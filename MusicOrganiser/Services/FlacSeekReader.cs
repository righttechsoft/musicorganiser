using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.MediaFoundation;
using NAudio.Utils;
using NAudio.Wave;

namespace MusicOrganiser.Services;

// Seek-exact FLAC reader. Adapted from NAudio's MediaFoundationReader (MIT,
// https://github.com/naudio/NAudio). Media Foundation's FLAC source seeks to an estimate and
// reports a timestamp derived from the REQUEST, so neither the landing point nor the timestamp can
// be trusted. It does decode correctly from the start of any stream it is given, so on a seek we
// find a FLAC frame ourselves (FlacFrameFinder), give MF a spliced stream that starts exactly
// there (STREAMINFO header + file bytes from that frame), and discard up to the target sample.
// Playback from the start, and everything after a seek, is ordinary MF decoding.
// Fallbacks, never throwing out of a seek: targets within 60 s decode from 0 and discard (exact),
// anything else uses the plain MF seek (approximate, like AudioFileReader).
// ponytail: the 60 s fallback ceiling is a decode of up to 60 s (~0.5 s); a file the finder
// cannot parse seeks approximately past that.
public sealed class FlacSeekReader : WaveStream
{
    private const double FallbackDecodeSeconds = 60;

    private readonly string _file;
    private readonly WaveFormat _waveFormat;
    private readonly long _length;
    private readonly FlacInfo? _info;
    private IMFSourceReader? _reader;
    private Stream? _spliced;
    private long _position;
    private long _repositionTo = -1;
    private byte[] _buf = Array.Empty<byte>();
    private int _bufOffset;
    private int _bufCount;

    public FlacSeekReader(string file)
    {
        _file = file;
        MediaFoundationApi.Startup();
        using (var fs = OpenFile())
            _info = FlacFrameFinder.Parse(fs);
        // Probe reader only for format + duration; Read creates its own on the reading thread
        // (same threading approach as NAudio's default, non-SingleReaderObject mode).
        var probe = CreateUrlReader();
        long mfLength;
        try
        {
            _waveFormat = GetCurrentWaveFormat(probe);
            probe.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM, true);
            mfLength = GetLength(probe);
        }
        finally { Marshal.ReleaseComObject(probe); }
        if (_info != null && _info.TotalSamples > 0)
            _length = _info.TotalSamples * _waveFormat.BlockAlign;
        else
        {
            _length = mfLength;
            if (_info != null) _info.TotalSamples = mfLength / _waveFormat.BlockAlign;   // for the byte-offset estimate
        }
    }

    private FileStream OpenFile() => new(_file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    private IMFSourceReader CreateUrlReader()
    {
        MediaFoundationInterop.MFCreateSourceReaderFromURL(_file, null, out var reader);
        return Configure(reader);
    }

    private static IMFSourceReader Configure(IMFSourceReader reader)
    {
        reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_ALL_STREAMS, false);
        reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM, true);
        var partial = new MediaType
        {
            MajorType = MediaTypes.MFMediaType_Audio,
            SubType = AudioSubtypes.MFAudioFormat_PCM
        };
        var current = GetCurrentMediaType(reader);
        // mono, low sample rate files can go wrong on Windows 10 unless we specify here
        partial.ChannelCount = current.ChannelCount;
        partial.SampleRate = current.SampleRate;
        reader.SetCurrentMediaType(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM, IntPtr.Zero, partial.MediaFoundationObject);
        Marshal.ReleaseComObject(current.MediaFoundationObject);
        return reader;
    }

    private static MediaType GetCurrentMediaType(IMFSourceReader reader)
    {
        reader.GetCurrentMediaType(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM, out var mediaType);
        return new MediaType(mediaType);
    }

    private static WaveFormat GetCurrentWaveFormat(IMFSourceReader reader)
    {
        var t = GetCurrentMediaType(reader);
        var sub = t.SubType;
        if (sub == AudioSubtypes.MFAudioFormat_PCM) return new WaveFormat(t.SampleRate, t.BitsPerSample, t.ChannelCount);
        if (sub == AudioSubtypes.MFAudioFormat_Float) return WaveFormat.CreateIeeeFloatWaveFormat(t.SampleRate, t.ChannelCount);
        throw new InvalidDataException($"Unsupported audio sub Type {FieldDescriptionHelper.Describe(typeof(AudioSubtypes), sub)}");
    }

    private long GetLength(IMFSourceReader reader)
    {
        var variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
        try
        {
            int hr = reader.GetPresentationAttribute(MediaFoundationInterop.MF_SOURCE_READER_MEDIASOURCE,
                MediaFoundationAttributes.MF_PD_DURATION, variantPtr);
            if (hr == MediaFoundationErrors.MF_E_ATTRIBUTENOTFOUND) return 0;
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            var variant = Marshal.PtrToStructure<PropVariant>(variantPtr);
            return ((long)variant.Value * _waveFormat.AverageBytesPerSecond) / 10_000_000L;
        }
        finally
        {
            PropVariant.Clear(variantPtr);
            Marshal.FreeHGlobal(variantPtr);
        }
    }

    public override WaveFormat WaveFormat => _waveFormat;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Position cannot be less than 0");
            // Block-align so the target is a whole frame; the real reposition happens in Read.
            value -= value % _waveFormat.BlockAlign;
            _repositionTo = value;
            _position = value;
        }
    }

    // Reads the next decoded sample into _buf. false = end of stream.
    private bool ReadNextSample()
    {
        _reader!.ReadSample(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0,
            out _, out MF_SOURCE_READER_FLAG flags, out _, out IMFSample sample);
        if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) return false;
        if ((flags & ~MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
            throw new InvalidOperationException($"MediaFoundationReadError {flags}");

        sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        buffer.Lock(out IntPtr data, out _, out int cb);
        if (_buf.Length < cb) _buf = new byte[cb];
        Marshal.Copy(data, _buf, 0, cb);
        _bufOffset = 0;
        _bufCount = cb;
        buffer.Unlock();
        Marshal.ReleaseComObject(buffer);
        Marshal.ReleaseComObject(sample);
        return true;
    }

    private void ReleaseReader()
    {
        if (_reader != null)
        {
            Marshal.ReleaseComObject(_reader);
            _reader = null;
        }
        _spliced?.Dispose();
        _spliced = null;
        _bufCount = 0;
        _bufOffset = 0;
    }

    // Decodes and drops `bytes` of PCM from the current reader. false = hit end of stream first.
    private bool Discard(long bytes)
    {
        while (bytes > 0)
        {
            if (_bufCount == 0 && !ReadNextSample()) return false;
            int n = (int)Math.Min(bytes, _bufCount);
            _bufOffset += n;
            _bufCount -= n;
            bytes -= n;
        }
        return true;
    }

    private void Reposition(long targetBytes)
    {
        ReleaseReader();
        long sample = targetBytes / _waveFormat.BlockAlign;
        if (sample > 0 && _info != null)
        {
            try { if (TryOpenSpliced(sample)) { _position = targetBytes; return; } }
            catch { /* fall through to the fallbacks */ }
            ReleaseReader();
        }

        _reader = CreateUrlReader();
        if (sample == 0) { _position = 0; return; }
        if (sample <= FallbackDecodeSeconds * _waveFormat.SampleRate)
        {
            try { Discard(targetBytes); _position = targetBytes; return; }
            catch { ReleaseReader(); _reader = CreateUrlReader(); }
        }
        // Last resort: the plain MF seek lands on an estimate; the position claimed is the request.
        SetSourcePosition(targetBytes * 10_000_000L / _waveFormat.AverageBytesPerSecond);
        _position = targetBytes;
    }

    private bool TryOpenSpliced(long sample)
    {
        var fs = OpenFile();
        long frameSample;
        try
        {
            if (!FlacFrameFinder.TryFind(fs, _info!, _info!.TotalSamples, sample, out long frameOffset, out frameSample))
            {
                fs.Dispose();
                return false;
            }
            _spliced = new SplicedStream(FlacFrameFinder.BuildHeader(_info), fs, frameOffset);
        }
        catch { fs.Dispose(); throw; }

        var byteStream = MediaFoundationApi.CreateByteStream(new StreamAdapter(_spliced));
        _reader = Configure(MediaFoundationApi.CreateSourceReaderFromByteStream(byteStream));
        var fmt = GetCurrentWaveFormat(_reader);
        if (!fmt.Equals(_waveFormat)) return false;   // a different decode format would corrupt the pipeline
        // First decoded sample is frameSample; drop up to the target (end of stream = a target past EOF).
        Discard((sample - frameSample) * _waveFormat.BlockAlign);
        return true;
    }

    private void SetSourcePosition(long pos100ns)
    {
        var pv = PropVariant.FromLong(pos100ns);
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(pv));
        try
        {
            Marshal.StructureToPtr(pv, ptr, false);
            _reader!.SetCurrentPosition(Guid.Empty, ptr);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_repositionTo != -1)
        {
            long to = _repositionTo;
            _repositionTo = -1;
            Reposition(to);
        }
        _reader ??= CreateUrlReader();

        int written = 0;
        while (written < count)
        {
            if (_bufCount == 0 && !ReadNextSample()) break;
            int n = Math.Min(count - written, _bufCount);
            Array.Copy(_buf, _bufOffset, buffer, offset + written, n);
            _bufOffset += n;
            _bufCount -= n;
            written += n;
        }
        _position += written;
        return written;
    }

    protected override void Dispose(bool disposing)
    {
        ReleaseReader();
        base.Dispose(disposing);
    }

    // Read-only seekable view of [header bytes] + [file bytes from `fileOffset` to EOF].
    private sealed class SplicedStream : Stream
    {
        private readonly byte[] _header;
        private readonly FileStream _fs;
        private readonly long _fileOffset;
        private long _pos;

        public SplicedStream(byte[] header, FileStream fs, long fileOffset)
        {
            _header = header; _fs = fs; _fileOffset = fileOffset;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _header.Length + (_fs.Length - _fileOffset);
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin)
        {
            _pos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                _ => Length + offset
            };
            return _pos;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int done = 0;
            if (_pos < _header.Length)
            {
                int n = (int)Math.Min(count, _header.Length - _pos);
                Array.Copy(_header, _pos, buffer, offset, n);
                _pos += n; done += n;
            }
            if (done < count)
            {
                _fs.Position = _fileOffset + (_pos - _header.Length);
                int n = _fs.Read(buffer, offset + done, count - done);
                _pos += n; done += n;
            }
            return done;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _fs.Dispose();
            base.Dispose(disposing);
        }
    }

    // Minimal COM IStream over a read-only managed Stream, for MFCreateMFByteStreamOnStream.
    // MF may call from several work-queue threads, hence the lock.
    private sealed class StreamAdapter : IStream
    {
        private readonly Stream _s;
        public StreamAdapter(Stream s) => _s = s;

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int n;
            lock (_s) n = _s.Read(pv, 0, cb);
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, n);
        }

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            long p;
            lock (_s) p = _s.Seek(dlibMove, (SeekOrigin)dwOrigin);
            if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, p);
        }

        public void Stat(out STATSTG pstatstg, int grfStatFlag)
        {
            pstatstg = new STATSTG { type = 2, cbSize = _s.Length };   // STGTY_STREAM
        }

        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new NotSupportedException();
        public void SetSize(long libNewSize) => throw new NotSupportedException();
        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new NotSupportedException();
        public void Commit(int grfCommitFlags) { }
        public void Revert() { }
        public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
        public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
        public void Clone(out IStream ppstm) => throw new NotSupportedException();
    }
}
