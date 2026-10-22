using System;
using System.Collections.Generic;
using System.Timers;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MusicOrganiser.Services;

public class AudioPlayerService : IDisposable
{
    private readonly MMDeviceEnumerator _enum = new();
    private NotificationClient? _notificationClient;
    private WasapiOut? _output;
    private MediaFoundationResampler? _resampler;
    // Target format of _resampler, kept so Seek can rebuild it with identical output.
    private WaveFormat? _resamplerFormat;
    private LockedSampleProvider? _lockedProvider;
    // Level tap (before the volume stage); recreated with every output, so its frame counter shares
    // the device clock's origin. See GetLiveLevels.
    private LevelTap? _tap;
    // In-app gain applied at the TAIL of the pipeline (after the resampler) so volume/mute
    // changes are audible within WasapiOut's buffer (the configured OutputBufferMs) instead of after the resampler's
    // ~1s source buffer. Do not move this back onto AudioFileReader.
    private VolumeSampleProvider? _gain;
    private WaveStream? _audioFile;
    private MMDevice? _selectedDevice;
    // The default-endpoint device resolved for the current playback (when no device is
    // explicitly selected). Owned here and kept alive for as long as _output uses it.
    private MMDevice? _ownedPlaybackDevice;
    private readonly System.Timers.Timer _positionTimer;
    private bool _disposed;
    private float _volume = 1.0f;
    private int _outputBufferMs = 300;
    private bool _localMuted;
    // Bluetooth A2DP endpoints often fail (throw at Init, or error mid-stream via PlaybackStopped)
    // in WASAPI shared *event-sync* mode; push mode is reliable. Sticky once tripped for the session.
    // ponytail: session-global, not per-device — push mode is universally safe (marginally higher
    // latency), so no per-device tracking. Add a device->mode map if that latency ever matters.
    private bool _pushModeFallback;
    // Held handle for the endpoint whose master volume we watch for external changes.
    private MMDevice? _monitoredDevice;
    private AudioEndpointVolume? _monitoredEndpointVolume;
    // Serializes device-change handling — a single Bluetooth connect fires several callbacks.
    private readonly object _deviceChangeLock = new();
    // Serializes pipeline reads against Seek's resampler swap. Flushing via the MFT's
    // Reposition() is NOT an option: its EndStreamAndDrain/ProcessOutput loop crashes
    // natively (0xc0000005) even single-threaded, so Seek replaces the resampler outright
    // and reads must not run mid-swap.
    private readonly object _pipelineLock = new();

    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? PlaybackStopped;
    // Raised when the OS master volume of the active device changes (incl. from outside the app).
    public event Action? SystemVolumeChanged;
    // Raised when render devices are added/removed/changed (e.g. Bluetooth connect/disconnect).
    public event Action? DevicesChanged;

    public string? CurrentFilePath { get; private set; }
    public TimeSpan CurrentPosition => _audioFile?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan TotalDuration => _audioFile?.TotalTime ?? TimeSpan.Zero;
    // Audible clock. CurrentPosition is the READ position of the source, which leads the speaker by
    // the resampler's buffer plus WASAPI's. WasapiOut.GetPosition() is bytes the device has played
    // since this output started (frozen while paused), so audible = file time at which this output's
    // stream began (_clockBaseSec) + played. The base is 0 after Play (every output starts at the
    // top of the file) and is re-derived in Seek, which keeps the output running.
    private double _clockBaseSec;
    // After a seek the old audio still drains from the WASAPI buffer; hold the playhead at the
    // seek target until the device clock catches up to it (null = no hold).
    private double? _seekHoldSec;

    public TimeSpan AudiblePosition
    {
        get
        {
            var cur = CurrentPosition;
            var o = _output;
            if (o == null || o.PlaybackState == PlaybackState.Stopped) return cur;
            try
            {
                double computed = _clockBaseSec + (double)o.GetPosition() / o.OutputWaveFormat.AverageBytesPerSecond;
                if (_seekHoldSec is double hold)
                {
                    if (computed >= hold) _seekHoldSec = null;
                    else computed = hold;
                }
                computed = Math.Clamp(computed, 0, Math.Max(0, TotalDuration.TotalSeconds));
                // Never ahead of what has been read.
                return TimeSpan.FromSeconds(Math.Min(computed, cur.TotalSeconds));
            }
            catch { return cur; }   // output disposed mid-read / clock unavailable
        }
    }

    // Envelope of the audio the DEVICE is playing right now: full-band level and a kick (bass onset)
    // strength, both raw (unnormalised). The tap's ring is indexed by frames pulled since this output
    // started and WasapiOut.GetPosition() counts the frames the device has played since the same
    // moment, so this is in sync with what is audible; a Seek keeps the output (and tap) running.
    public (float level, float kick) GetLiveLevels()
    {
        var o = _output;
        var tap = _tap;
        if (o == null || tap == null || o.PlaybackState != PlaybackState.Playing) return (0f, 0f);
        try
        {
            long frame = o.GetPosition() / o.OutputWaveFormat.BlockAlign;
            return tap.Lookup(frame);
        }
        catch { return (0f, 0f); }   // output disposed mid-read
    }

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
            if (_gain != null)
            {
                _gain.Volume = _localMuted ? 0f : _volume;
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
            if (_gain != null)
            {
                _gain.Volume = _localMuted ? 0f : _volume;
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

    // WASAPI buffer duration in ms, clamped 100–1000. Changing it live re-inits the
    // current track (same pattern as OutputDeviceId), preserving position/pause state.
    public int OutputBufferMs
    {
        get => _outputBufferMs;
        set
        {
            var clamped = Math.Clamp(value, 100, 1000);
            if (clamped == _outputBufferMs) return;
            _outputBufferMs = clamped;
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

    // Entry point for the CoreAudio notification callbacks. CRITICAL: the callback runs on an
    // MMDevice-internal thread that holds an audio-engine lock, and MSDN forbids calling any
    // enumerator/device/endpoint-volume method from inside it — doing so deadlocks, and a
    // concurrent Play() (WasapiOut/AudioClient) then hangs. So return immediately and do the
    // real work (which touches CoreAudio) on the thread pool, off the callback thread.
    private void OnDevicesChangedExternal()
    {
        System.Threading.ThreadPool.QueueUserWorkItem(_ => OnDevicesChangedInternal());
    }

    // Fired off the COM callback thread when the set of render devices changes.
    private void OnDevicesChangedInternal()
    {
        lock (_deviceChangeLock)
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
        }
        DevicesChanged?.Invoke();
    }

    // CoreAudio device-change callbacks. Must return fast without re-entering CoreAudio, so they
    // only hand off to OnDevicesChangedExternal (which defers the work to the thread pool).
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly AudioPlayerService _svc;
        public NotificationClient(AudioPlayerService svc) => _svc = svc;
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _svc.OnDevicesChangedExternal();
        public void OnDeviceAdded(string pwstrDeviceId) => _svc.OnDevicesChangedExternal();
        public void OnDeviceRemoved(string deviceId) => _svc.OnDevicesChangedExternal();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _svc.OnDevicesChangedExternal();
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
            _audioFile = OpenReader(filePath);

            var (device, ownsDevice) = GetActiveDevice();
            if (device == null)
                throw new InvalidOperationException("No active audio output device.");
            // WasapiOut keeps using this device while playing, so hold owned (default)
            // devices alive until StopAndReleaseFile rather than disposing here.
            if (ownsDevice) _ownedPlaybackDevice = device;

            // WasapiOut shared mode won't resample: match the device's mix format.
            // Resample sample-rate only, staying stereo (float, device rate). For a multichannel
            // endpoint (5.1/7.1 rear speakers) StereoToSurround then duplicates stereo across every
            // speaker; its WaveFormat IS the device mix format (WAVE_FORMAT_EXTENSIBLE). Feeding
            // that exact extensible format is what lets shared-mode AudioClient.Initialize accept
            // >2 channels — the stripped AsStandardWaveFormat() form fails with E_INVALIDARG
            // ("Value does not fall within the expected range").
            var mixFormat = device.AudioClient.MixFormat;
            _resamplerFormat = WaveFormat.CreateIeeeFloatWaveFormat(mixFormat.SampleRate, 2);
            _resampler = new MediaFoundationResampler(_audioFile, _resamplerFormat);
            _lockedProvider = new LockedSampleProvider(_resampler.ToSampleProvider(), _pipelineLock);
            _tap = new LevelTap(_lockedProvider);
            _gain = new VolumeSampleProvider(_tap)
            {
                Volume = _localMuted ? 0f : _volume
            };
            IWaveProvider outProvider = mixFormat.Channels > 2
                ? new StereoToSurround(_gain, mixFormat)
                : new SampleToWaveProvider(_gain);
            // Bigger buffer survives render-thread starvation at high CPU load (100ms glitched);
            // cost: tail-stage changes (in-app volume, audible seek) lag by ~buffer size.
            _output = new WasapiOut(device, AudioClientShareMode.Shared, !_pushModeFallback, _outputBufferMs);
            _output.Init(outProvider);
            _output.PlaybackStopped += OnPlaybackStopped;
            _output.Play();
            _clockBaseSec = 0;
            _seekHoldSec = null;
            CurrentFilePath = filePath;
            _positionTimer.Start();
        }
        catch (Exception ex)
        {
            StopAndReleaseFile();
            // Event-sync init failed (typical on Bluetooth) -> retry once in push mode.
            if (!_pushModeFallback)
            {
                _pushModeFallback = true;
                Play(filePath);
                return;
            }
            throw new InvalidOperationException($"Failed to play file: {ex.Message}", ex);
        }
    }

    // Media Foundation seeks inside FLAC land seconds off while reporting the requested position;
    // FlacSeekReader makes them exact. Everything else keeps AudioFileReader (measured exact).
    private static WaveStream OpenReader(string filePath) =>
        filePath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)
            ? new FlacSeekReader(filePath)
            : new AudioFileReader(filePath);

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
            // Discard the resampler's ~1s of buffered pre-seek audio by swapping in a fresh
            // resampler over the repositioned reader (Reposition() would drain-crash, see
            // _pipelineLock). The lock keeps the render thread's Read out during the swap.
            lock (_pipelineLock)
            {
                // Audible clock: the output keeps running, so re-base it. What it has played so far
                // plus what is still queued in its buffer (old audio, ~OutputBufferMs) is the stream
                // time before the new audio reaches the speaker.
                try
                {
                    var o = _output;
                    if (o != null && o.PlaybackState != PlaybackState.Stopped)
                    {
                        double played = (double)o.GetPosition() / o.OutputWaveFormat.AverageBytesPerSecond;
                        _clockBaseSec = position.TotalSeconds - (played + _outputBufferMs / 1000.0);
                        _seekHoldSec = position.TotalSeconds;
                    }
                }
                catch { _clockBaseSec = 0; _seekHoldSec = null; }
                _audioFile.CurrentTime = position;
                if (_resampler != null && _lockedProvider != null && _resamplerFormat != null)
                {
                    var old = _resampler;
                    _resampler = new MediaFoundationResampler(_audioFile, _resamplerFormat);
                    _lockedProvider.SetSource(_resampler.ToSampleProvider());
                    old.Dispose();
                }
            }
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
        _gain = null;
        _tap = null;
        _lockedProvider = null;
        _resamplerFormat = null;

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

        // ponytail: WasapiOut can intermittently fire a non-null Exception on a NATURAL end (seen
        // on the Realtek endpoint), not just a genuine mid-stream drop. Without this guard that
        // re-inits playback and seeks to CurrentPosition — which at a natural end is the very end
        // of the track — landing on zero remaining samples with no further PlaybackStopped, so the
        // track sits "playing" forever with no auto-advance. 2s is enough slack for the position
        // timer's last tick to lag the real end without also matching a real mid-stream error
        // (those happen well before the end).
        var nearEnd = TotalDuration > TimeSpan.Zero
                      && CurrentPosition >= TotalDuration - TimeSpan.FromSeconds(2);

        // Device errored mid-stream (common on Bluetooth event-sync) rather than the track
        // ending cleanly. Retry the same track/position in push mode before giving up, so the
        // ViewModel doesn't mistake it for end-of-track and skip through the whole queue.
        // Deferred off this callback thread: re-initing WASAPI here would dispose _output from
        // inside its own PlaybackStopped callback and deadlock (see device-change handling).
        if (e.Exception != null && !_pushModeFallback && CurrentFilePath != null && !nearEnd)
        {
            _pushModeFallback = true;
            var path = CurrentFilePath;
            var pos = CurrentPosition;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Play(path); Seek(pos); }
                catch { PlaybackStopped?.Invoke(this, EventArgs.Empty); }
            });
            return;
        }

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

    // Takes _pipelineLock around every Read so Seek can safely swap the resampler
    // (see _pipelineLock comment). Sits between the resampler and the volume stage.
    private sealed class LockedSampleProvider : ISampleProvider
    {
        private ISampleProvider _src;
        private readonly object _lock;
        public LockedSampleProvider(ISampleProvider src, object sync) { _src = src; _lock = sync; }
        public WaveFormat WaveFormat => _src.WaveFormat;
        // Call only while holding the lock (Seek does) so Read never sees a half-swapped source.
        public void SetSource(ISampleProvider src) => _src = src;
        public int Read(float[] buffer, int offset, int count)
        {
            lock (_lock) return _src.Read(buffer, offset, count);
        }
    }

    // Pass-through that measures ~20 ms blocks of the (stereo) stream into a ring: RMS level and a
    // kick value (150 Hz one-pole lowpass energy, positive change vs a short running average).
    // Audio thread: no allocation, no locks. The single writer publishes _done after filling an
    // entry; the UI reader only trusts entries inside the last RingSize blocks (a torn read of a
    // slot being overwritten just shows a slightly wrong pulse).
    // ponytail: first-order kick detector on a mono mix, not a beat tracker; the ring (~10 s) must
    // outlast the WASAPI buffer + the lookup lag, which holds up to the 1000 ms buffer cap.
    private sealed class LevelTap : ISampleProvider
    {
        private const int RingSize = 512;
        private readonly ISampleProvider _src;
        private readonly int _blockFrames, _ch;
        private readonly double _kickCoef;
        private readonly float[] _level = new float[RingSize], _kick = new float[RingSize];
        private long _done;               // completed blocks
        private int _fill;                // frames in the block under construction
        private double _sumSq, _kickSumSq, _lp, _avg;

        public LevelTap(ISampleProvider src)
        {
            _src = src;
            _ch = src.WaveFormat.Channels;
            _blockFrames = Math.Max(1, src.WaveFormat.SampleRate / 50);
            _kickCoef = 1 - Math.Exp(-2 * Math.PI * 150 / src.WaveFormat.SampleRate);
        }

        public WaveFormat WaveFormat => _src.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = _src.Read(buffer, offset, count);
            for (int i = 0; i + _ch <= n; i += _ch)
            {
                double x = 0;
                for (int c = 0; c < _ch; c++) x += buffer[offset + i + c];
                x /= _ch;
                _sumSq += x * x;
                _lp += _kickCoef * (x - _lp);
                _kickSumSq += _lp * _lp;
                if (++_fill == _blockFrames)
                {
                    float rms = (float)Math.Sqrt(_sumSq / _fill);
                    double kickRms = Math.Sqrt(_kickSumSq / _fill);
                    int slot = (int)(_done % RingSize);
                    _level[slot] = rms;
                    _kick[slot] = (float)Math.Max(0, kickRms - _avg);
                    _avg += 0.2 * (kickRms - _avg);
                    _sumSq = _kickSumSq = 0; _fill = 0;
                    Volatile.Write(ref _done, _done + 1);
                }
            }
            return n;
        }

        public (float level, float kick) Lookup(long frame)
        {
            long block = frame / _blockFrames;
            long done = Volatile.Read(ref _done);
            if (frame < 0 || block >= done || done - block >= RingSize - 2) return (0f, 0f);
            int slot = (int)(block % RingSize);
            return (_level[slot], _kick[slot]);
        }
    }

    // Duplicates a stereo source across all channels of a multichannel endpoint so stereo music
    // is audible on every speaker (fronts + rears + sides). Front L/R feed the left/right side
    // speakers, centre gets the (L+R) mix, LFE stays silent.
    // ponytail: routing assumes the standard KSAUDIO channel order for 4/6/8ch; an endpoint with
    // an exotic channel mask could map oddly — read WaveFormatExtensible.ChannelMask if that shows up.
    // IWaveProvider (not ISampleProvider) so it can expose the device's WAVE_FORMAT_EXTENSIBLE
    // float format directly to WasapiOut. Wrapping via SampleToWaveProvider fails here — it
    // demands Encoding == IeeeFloat, but the extensible mix format reports Encoding == Extensible
    // ("Must be already floating point").
    private sealed class StereoToSurround : IWaveProvider
    {
        private readonly ISampleProvider _src;       // stereo, float, at WaveFormat.SampleRate
        private readonly (float l, float r)[] _map;  // per-output-channel gains
        private readonly int _outCh;
        private float[] _in = Array.Empty<float>();

        public StereoToSurround(ISampleProvider stereoSource, WaveFormat deviceMixFormat)
        {
            _src = stereoSource;
            WaveFormat = deviceMixFormat;            // expose the exact mix format to WasapiOut
            _outCh = deviceMixFormat.Channels;
            _map = BuildMap(_outCh);
            System.Diagnostics.Debug.Assert(_map.Length == _outCh);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            var outFrames = count / (_outCh * 4);    // 4 bytes per 32-bit float sample
            var need = outFrames * 2;                 // stereo input samples
            if (_in.Length < need) _in = new float[need];

            var got = _src.Read(_in, 0, need);        // stereo float samples
            var frames = got / 2;
            for (var f = 0; f < frames; f++)
            {
                var l = _in[f * 2];
                var r = _in[f * 2 + 1];
                var frameBase = offset + f * _outCh * 4;
                for (var c = 0; c < _outCh; c++)
                {
                    var s = l * _map[c].l + r * _map[c].r;
                    BitConverter.TryWriteBytes(buffer.AsSpan(frameBase + c * 4, 4), s);
                }
            }
            return frames * _outCh * 4;
        }

        // Per-output gains for the standard KSAUDIO speaker order; even/odd fallback for odd counts.
        private static (float, float)[] BuildMap(int channels) => channels switch
        {
            4 => new (float, float)[] { (1, 0), (0, 1), (1, 0), (0, 1) },                                       // FL FR BL BR
            6 => new (float, float)[] { (1, 0), (0, 1), (0.5f, 0.5f), (0, 0), (1, 0), (0, 1) },                 // FL FR FC LFE BL BR
            8 => new (float, float)[] { (1, 0), (0, 1), (0.5f, 0.5f), (0, 0), (1, 0), (0, 1), (1, 0), (0, 1) }, // + SL SR
            _ => BuildEvenOdd(channels),
        };

        private static (float, float)[] BuildEvenOdd(int channels)
        {
            var m = new (float, float)[channels];
            for (var i = 0; i < channels; i++) m[i] = i % 2 == 0 ? (1f, 0f) : (0f, 1f);
            return m;
        }
    }
}
