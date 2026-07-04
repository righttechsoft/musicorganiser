using System;
using System.Collections.Generic;
using System.Timers;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace MusicOrganiser.Services;

public class AudioPlayerService : IDisposable
{
    private readonly MMDeviceEnumerator _enum = new();
    private NotificationClient? _notificationClient;
    private WasapiOut? _output;
    private MediaFoundationResampler? _resampler;
    private AudioFileReader? _audioFile;
    private MMDevice? _selectedDevice;
    // The default-endpoint device resolved for the current playback (when no device is
    // explicitly selected). Owned here and kept alive for as long as _output uses it.
    private MMDevice? _ownedPlaybackDevice;
    private readonly System.Timers.Timer _positionTimer;
    private bool _disposed;
    private float _volume = 1.0f;
    private bool _localMuted;
    // Held handle for the endpoint whose master volume we watch for external changes.
    private MMDevice? _monitoredDevice;
    private AudioEndpointVolume? _monitoredEndpointVolume;

    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? PlaybackStopped;
    // Raised when the OS master volume of the active device changes (incl. from outside the app).
    public event Action? SystemVolumeChanged;
    // Raised when render devices are added/removed/changed (e.g. Bluetooth connect/disconnect).
    public event Action? DevicesChanged;

    public string? CurrentFilePath { get; private set; }
    public TimeSpan CurrentPosition => _audioFile?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan TotalDuration => _audioFile?.TotalTime ?? TimeSpan.Zero;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _output?.PlaybackState == PlaybackState.Paused;
    public bool IsStopped => _output == null || _output.PlaybackState == PlaybackState.Stopped;

    // In-app volume: attenuates the decoded sample stream (does not touch the OS mixer).
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_audioFile != null)
            {
                _audioFile.Volume = _localMuted ? 0f : _volume;
            }
        }
    }

    // Silences local desktop output (used while the phone is the audio sink) without
    // changing the stored Volume, so the app-volume slider value is preserved.
    public bool LocalMuted
    {
        get => _localMuted;
        set
        {
            _localMuted = value;
            if (_audioFile != null)
            {
                _audioFile.Volume = _localMuted ? 0f : _volume;
            }
        }
    }

    // Windows master (endpoint) volume of the active output device — same as the
    // taskbar volume slider; affects every app on the PC.
    // ponytail: read-on-load / on-set only; no OnVolumeNotification tracking of
    // external changes. Add a notification handler if the slider must follow the OS live.
    public float SystemVolume
    {
        get
        {
            try
            {
                var (device, owned) = GetActiveDevice();
                if (device == null) return 1f;
                try { return device.AudioEndpointVolume.MasterVolumeLevelScalar; }
                finally { if (owned) device.Dispose(); }
            }
            catch { return 1f; }
        }
        set
        {
            try
            {
                var (device, owned) = GetActiveDevice();
                if (device == null) return;
                try { device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f); }
                finally { if (owned) device.Dispose(); }
            }
            catch { /* ignore */ }
        }
    }

    // Stable CoreAudio device id, or null for the system default endpoint.
    public string? OutputDeviceId
    {
        get => _selectedDevice?.ID;
        set
        {
            _selectedDevice?.Dispose();
            _selectedDevice = null;
            if (!string.IsNullOrEmpty(value))
            {
                try
                {
                    var dev = _enum.GetDevice(value);
                    // Accept only an ACTIVE endpoint. A disconnected BT device is still
                    // "present" (Unplugged/NotPresent) and GetDevice returns it, but it's
                    // not in the active list -> would show as an empty selection. Fall to default.
                    if (dev.State == DeviceState.Active) _selectedDevice = dev;
                    else { dev.Dispose(); _selectedDevice = null; }
                }
                catch { _selectedDevice = null; } // stale/removed id -> fall back to default
            }

            UpdateVolumeMonitor(); // watch the newly-selected endpoint's master volume

            // Live-switch the current track to the new device, preserving position/state.
            if (CurrentFilePath != null)
            {
                var path = CurrentFilePath;
                var pos = CurrentPosition;
                var wasPaused = IsPaused;
                Play(path);
                Seek(pos);
                if (wasPaused) Pause();
            }
        }
    }

    // Id of the system default render endpoint, or null if none/unavailable.
    public string? GetDefaultDeviceId()
    {
        try
        {
            using var dev = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return dev.ID;
        }
        catch { return null; }
    }

    public IReadOnlyList<(string Id, string Name)> GetOutputDevices()
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var dev in _enum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try { list.Add((dev.ID, dev.FriendlyName)); }
                finally { dev.Dispose(); }
            }
        }
        catch { /* ignore enumeration failures */ }
        return list;
    }

    public AudioPlayerService()
    {
        _positionTimer = new System.Timers.Timer(100);
        _positionTimer.Elapsed += (s, e) => PositionChanged?.Invoke(this, CurrentPosition);
        UpdateVolumeMonitor(); // watch the default endpoint until a device is selected

        // Live device add/remove/default-change tracking (Bluetooth, USB, etc.).
        try
        {
            _notificationClient = new NotificationClient(this);
            _enum.RegisterEndpointNotificationCallback(_notificationClient);
        }
        catch { _notificationClient = null; }
    }

    // Fired (on a COM thread) when the set of render devices changes.
    private void OnDevicesChangedInternal()
    {
        try
        {
            // If the selected device vanished (e.g. BT disconnected), fall back to default.
            if (_selectedDevice != null)
            {
                var active = false;
                try { using var d = _enum.GetDevice(_selectedDevice.ID); active = d.State == DeviceState.Active; }
                catch { active = false; }
                if (!active) { _selectedDevice.Dispose(); _selectedDevice = null; }
            }
        }
        catch { }

        UpdateVolumeMonitor();   // re-point the volume listener at the current active/default endpoint
        DevicesChanged?.Invoke();
    }

    // CoreAudio device-change callbacks. All just funnel to OnDevicesChangedInternal.
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly AudioPlayerService _svc;
        public NotificationClient(AudioPlayerService svc) => _svc = svc;
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _svc.OnDevicesChangedInternal();
        public void OnDeviceAdded(string pwstrDeviceId) => _svc.OnDevicesChangedInternal();
        public void OnDeviceRemoved(string deviceId) => _svc.OnDevicesChangedInternal();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _svc.OnDevicesChangedInternal();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    // (Re)attaches the master-volume change listener to the active endpoint (selected or default).
    // ponytail: monitors the selected/default device; if the *Windows default* changes while
    // we're on default, re-select a device to refresh. Add IMMNotificationClient if that matters.
    private void UpdateVolumeMonitor()
    {
        if (_monitoredEndpointVolume != null)
        {
            try { _monitoredEndpointVolume.OnVolumeNotification -= OnEndpointVolumeNotification; } catch { }
            _monitoredEndpointVolume = null;
        }
        _monitoredDevice?.Dispose();
        _monitoredDevice = null;

        try
        {
            // Own a dedicated handle (don't alias _selectedDevice, which the setter disposes).
            _monitoredDevice = _selectedDevice != null
                ? _enum.GetDevice(_selectedDevice.ID)
                : _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _monitoredEndpointVolume = _monitoredDevice.AudioEndpointVolume;
            _monitoredEndpointVolume.OnVolumeNotification += OnEndpointVolumeNotification;
        }
        catch { /* no device / unavailable */ }
    }

    // Fires on a COM thread whenever the endpoint's volume changes (external or in-app).
    private void OnEndpointVolumeNotification(AudioVolumeNotificationData data) => SystemVolumeChanged?.Invoke();

    // Returns the device to act on plus whether the caller owns (must dispose) it.
    private (MMDevice? device, bool owned) GetActiveDevice()
    {
        if (_selectedDevice != null) return (_selectedDevice, false);
        try { return (_enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia), true); }
        catch { return (null, false); }
    }

    public void Play(string filePath)
    {
        StopAndReleaseFile();

        try
        {
            _audioFile = new AudioFileReader(filePath);
            _audioFile.Volume = _localMuted ? 0f : _volume;

            var (device, ownsDevice) = GetActiveDevice();
            if (device == null)
                throw new InvalidOperationException("No active audio output device.");
            // WasapiOut keeps using this device while playing, so hold owned (default)
            // devices alive until StopAndReleaseFile rather than disposing here.
            if (ownsDevice) _ownedPlaybackDevice = device;

            // WasapiOut shared mode won't resample: convert to the device mix format first.
            _resampler = new MediaFoundationResampler(_audioFile, device.AudioClient.MixFormat.AsStandardWaveFormat());
            _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            _output.Init(_resampler);
            _output.PlaybackStopped += OnPlaybackStopped;
            _output.Play();
            CurrentFilePath = filePath;
            _positionTimer.Start();
        }
        catch (Exception ex)
        {
            StopAndReleaseFile();
            throw new InvalidOperationException($"Failed to play file: {ex.Message}", ex);
        }
    }

    public void Resume()
    {
        if (_output?.PlaybackState == PlaybackState.Paused)
        {
            _output.Play();
            _positionTimer.Start();
        }
    }

    public void Pause()
    {
        if (_output?.PlaybackState == PlaybackState.Playing)
        {
            _output.Pause();
            _positionTimer.Stop();
        }
    }

    public void Stop()
    {
        _positionTimer.Stop();
        _output?.Stop();
        if (_audioFile != null)
        {
            _audioFile.Position = 0;
        }
        PositionChanged?.Invoke(this, TimeSpan.Zero);
    }

    public void Seek(TimeSpan position)
    {
        if (_audioFile != null)
        {
            _audioFile.CurrentTime = position;
            PositionChanged?.Invoke(this, position);
        }
    }

    public void StopAndReleaseFile()
    {
        _positionTimer.Stop();

        if (_output != null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Stop();
            _output.Dispose();
            _output = null;
        }

        if (_resampler != null)
        {
            _resampler.Dispose();
            _resampler = null;
        }

        if (_audioFile != null)
        {
            _audioFile.Dispose();
            _audioFile = null;
        }

        if (_ownedPlaybackDevice != null)
        {
            _ownedPlaybackDevice.Dispose();
            _ownedPlaybackDevice = null;
        }

        CurrentFilePath = null;
    }

    public bool IsPlayingFile(string filePath)
    {
        return CurrentFilePath != null &&
               CurrentFilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        _positionTimer.Stop();
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _positionTimer.Stop();
        _positionTimer.Dispose();
        StopAndReleaseFile();
        if (_notificationClient != null)
        {
            try { _enum.UnregisterEndpointNotificationCallback(_notificationClient); } catch { }
            _notificationClient = null;
        }
        if (_monitoredEndpointVolume != null)
        {
            try { _monitoredEndpointVolume.OnVolumeNotification -= OnEndpointVolumeNotification; } catch { }
            _monitoredEndpointVolume = null;
        }
        _monitoredDevice?.Dispose();
        _selectedDevice?.Dispose();
        _enum.Dispose();
    }
}
