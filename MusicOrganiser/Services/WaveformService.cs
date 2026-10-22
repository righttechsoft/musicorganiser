using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace MusicOrganiser.Services;

/// <summary>Loudness bars (<see cref="Peaks"/>, 0-255 heights) plus per-bucket spectral levels
/// (0-255 each, normalised per band) that colour the bars. <see cref="Tempo"/> is BPM per bucket (0 = unknown),
/// <see cref="Beat"/> the pulse strength (0 = ambient, 255 = strongly percussive), <see cref="Spectrum"/> the absolute
/// level of <see cref="WaveformService.SpectrumBands"/> log-spaced bands, band-major (band * Buckets + bucket, band 0 = bass).
/// Peaks/Low/Mid/High/Tempo/Beat are <see cref="WaveformService.Buckets"/> long.</summary>
public sealed record Waveform(byte[] Peaks, byte[] Low, byte[] Mid, byte[] High, byte[] Tempo, byte[] Beat, byte[] Spectrum);

// Whole-track loudness chart for the transport bar's seek control (and the phone via
// GET /file/waveform). Computed once per track by decoding with AudioFileReader (same decoder
// as playback, so anything that plays works) and cached in the DB keyed by path + size + mtime.
// ponytail: fixed 1000 buckets, RMS (not LUFS), one byte per bucket; ceiling is that a very
// long track gets coarse bars and a very short one gets repeated ones.
public static class WaveformService
{
    public const int Buckets = 1000;
    public const int SpectrumBands = 8;

    // ponytail: calibration knobs for the height mapping. Unweighted RMS in dB (no K/A weighting);
    // the upgrade path is perceptual weighting if bass-heavy quiet passages still read too tall.
    private const double FloorPercentile = 0.10;  // quietest-decile bucket sets the bottom of the scale...
    private const double MinRangeDb = 6;          // ...but at least this far below the loudest (flat tracks stay flat)
    private const double MaxRangeDb = 40;         // ...and at most this far (one quiet intro can't flatten the rest)
    private const double BaseHeight = 0.15;       // height of a bucket sitting at the floor
    private const double FadeDb = 20;             // below the floor, fade from BaseHeight to 0 over this many dB
    private const double SilenceDb = 60;          // buckets this far under the loudest are left out of the statistics

    // ponytail: one FFT band bank (8 log-spaced bands: everything below the first edge, six in between, everything above
    // the last) feeds everything spectral. Colour shows RELATIVE balance, not band level: Low = bands 0-1 (< ~225 Hz), Mid =
    // bands 2-5, High = bands 6-7 (> ~3 kHz); each group's smoothed dB is compared with its own median, then with the
    // other two groups' deviations, and a softmax (Gain per dB) turns that into weights (byte = sqrt(weight), clients
    // square it). Dense music keeps all groups near their medians, so absolute levels would paint everything one colour.
    // The Spectrum bytes are the opposite, absolute: dB against the loudest band value, SpectrumRangeDb below it = 0.
    private static readonly double[] BandEdgesHz = { 116, 225, 436, 845, 1638, 3176, 6158 };
    private const double SpectrumRangeDb = 40;
    private const int SmoothBuckets = 5;   // centred moving average over the band dB curves (~1 s)
    private const double Gain = 0.9;       // softmax gain per dB of relative excess (3 dB -> ~15x weight)

    // ponytail: tempo = autocorrelation of a bass-weighted onset envelope (20 ms hop), once per TempoHopSec over a
    // TempoWindowSec window, strongest lag in 50-220 BPM scored as r(l) + 0.5 r(2l) (leans to the faster reading
    // on a tie). Half/double-tempo ambiguity is accepted; the upgrade is a proper beat tracker.
    private const double DepthFloor = 0.03, DepthSpan = 0.12;   // onset modulation depth gate
    private const int OnsetMs = 20;
    private const int TempoHopSec = 2;
    private const int TempoWindowSec = 8;
    private const double MinBpm = 50, MaxBpm = 220;
    private const double BeatFloor = 0.15, BeatSpan = 0.6;   // normalised autocorrelation r -> 0..1: (r - floor) / span

    // One decode per path; concurrent requests (playback + prewarm + phone) share it.
    private sealed class Job
    {
        public CancellationTokenSource Cts = new();
        public Task<Waveform?> Task = null!;
        public int Waiters;
        public bool Sticky;   // started by Prewarm: keep decoding even with nobody waiting
        public readonly List<IProgress<Waveform>> Progress = new();
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Job> Jobs = new(StringComparer.OrdinalIgnoreCase);

#if DEBUG
    static WaveformService()
    {
        SelfCheck();
    }
#endif

    public static async Task<Waveform?> GetAsync(string path, CancellationToken ct, IProgress<Waveform>? progress = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var cached = await Task.Run(() => TryCache(path), ct).ConfigureAwait(false);
            if (cached != null) return cached;

            var job = Join(path, progress, sticky: false);
            try
            {
                return await job.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Nobody left who wants it (fast track skipping): stop burning CPU on a long decode.
                lock (Gate)
                {
                    if (--job.Waiters <= 0 && !job.Sticky) job.Cts.Cancel();
                }
                return null;
            }
        }
        catch { return null; }
    }

    // Fire-and-forget compute-and-cache of a track we expect to play soon.
    public static void Prewarm(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Join(path, null, sticky: true);
        }
        catch { /* best effort */ }
    }

    // Cancels decodes of this file (or anything under this folder) and waits briefly for them to
    // let go of their handle: AudioFileReader opens with FileShare.Read, which blocks a
    // move/delete. Called by FileOperationsService before it touches the path.
    public static async Task CancelUnderAsync(string pathOrFolder)
    {
        if (string.IsNullOrEmpty(pathOrFolder)) return;
        var prefix = pathOrFolder.TrimEnd('\\') + "\\";
        List<Task> pending = new();
        lock (Gate)
        {
            foreach (var (key, job) in Jobs)
            {
                if (key.Equals(pathOrFolder, StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    job.Cts.Cancel();
                    pending.Add(job.Task);
                }
            }
        }
        if (pending.Count == 0) return;
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* best effort */ }
    }

    private static Job Join(string path, IProgress<Waveform>? progress, bool sticky)
    {
        lock (Gate)
        {
            if (!Jobs.TryGetValue(path, out var job) || job.Cts.IsCancellationRequested)
            {
                var fresh = new Job();
                // Dedicated low-priority thread: a long FLAC decode must not starve the pool or
                // the playback render thread.
                fresh.Task = Task.Factory.StartNew(() => Decode(path, fresh), TaskCreationOptions.LongRunning);
                Jobs[path] = fresh;
                job = fresh;
            }
            if (sticky) job.Sticky = true;
            else { job.Waiters++; if (progress != null) job.Progress.Add(progress); }
            return job;
        }
    }

    private static Waveform? TryCache(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            var row = DatabaseService.Instance.GetWaveform(path);
            if (row is { } r && r.Size == fi.Length && r.Mtime == fi.LastWriteTimeUtc.Ticks && r.Data.Length == (6 + SpectrumBands) * Buckets)
                return new Waveform(r.Data[..Buckets], r.Data[Buckets..(2 * Buckets)],
                                    r.Data[(2 * Buckets)..(3 * Buckets)], r.Data[(3 * Buckets)..(4 * Buckets)],
                                    r.Data[(4 * Buckets)..(5 * Buckets)], r.Data[(5 * Buckets)..(6 * Buckets)],
                                    r.Data[(6 * Buckets)..]);
        }
        catch { }
        return null;
    }

    private static Waveform? Decode(string path, Job job)
    {
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            var ct = job.Cts.Token;
            var cached = TryCache(path);
            if (cached != null) return cached;   // lost a race with a job that just finished

            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            long size = fi.Length, mtime = fi.LastWriteTimeUtc.Ticks;

            Waveform result;
            using (var reader = new AudioFileReader(path))
            {
                // Provisional placement only (progress reports); the final result uses the decoded count.
                long estSamples = reader.Length / (reader.WaveFormat.BitsPerSample / 8);
                if (estSamples <= 0) return null;
                var acc = new Accumulator(Buckets, reader.WaveFormat.SampleRate * reader.WaveFormat.Channels * 30 / 1000,
                                      reader.WaveFormat.Channels, reader.WaveFormat.SampleRate);
                var buf = new float[65536];
                long nextReport = reader.Length / 20;
                int read;
                while ((read = reader.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    acc.Add(buf, read);
                    if (reader.Position >= nextReport)
                    {
                        nextReport += Math.Max(1, reader.Length / 20);
                        IProgress<Waveform>[] subs;
                        lock (Gate) subs = job.Progress.ToArray();
                        if (subs.Length > 0)
                        {
                            var partial = acc.ToBytes(estSamples);
                            foreach (var s in subs) s.Report(partial);
                        }
                    }
                }
                ct.ThrowIfCancellationRequested();
                result = acc.ToBytes(0);   // final: placed by the samples actually decoded
            }   // reader (and the file handle) released before the DB write

            DatabaseService.Instance.SaveWaveform(path, size, mtime,
                result.Peaks.Concat(result.Low).Concat(result.Mid).Concat(result.High)
                    .Concat(result.Tempo).Concat(result.Beat).Concat(result.Spectrum).ToArray());
            return result;
        }
        catch { return null; }
        finally
        {
            lock (Gate)
            {
                if (Jobs.TryGetValue(path, out var cur) && cur == job) Jobs.Remove(path);
            }
        }
    }

    // Onset envelope (band energy per OnsetMs) -> per-bucket tempo (BPM) and beat strength. One autocorrelation per
    // hop, then median-3 on tempo and linear interpolation to the buckets.
    internal static (byte[] tempo, byte[] beat) TempoBeat(List<double> energy, int buckets)
    {
        var tempo = new byte[buckets];
        var beat = new byte[buckets];
        int n = energy.Count;
        if (n < 400) return (tempo, beat);
        // Positive flux of the lightly smoothed amplitude.
        var amp = new double[n];
        for (int k = 0; k < n; k++) amp[k] = Math.Sqrt(energy[k]);
        var flux = new double[n];
        double prev = 0;
        for (int k = 1; k < n - 1; k++)
        {
            double sm = (amp[k - 1] + amp[k] + amp[k + 1]) / 3;
            flux[k] = Math.Max(0, sm - prev);
            prev = sm;
        }
        int perSec = 1000 / OnsetMs, hop = TempoHopSec * perSec, half = TempoWindowSec * perSec / 2;
        int minLag = (int)(60.0 * perSec / MaxBpm), maxLag = (int)(60.0 * perSec / MinBpm);
        int hops = n / hop + 1;
        var bpm = new double[hops];
        var str = new double[hops];
        var x = new double[2 * half];
        var r = new double[2 * maxLag + 2];
        for (int j = 0; j < hops; j++)
        {
            int c = Math.Min(j * hop, n - 1), lo = Math.Max(0, c - half), hi = Math.Min(n, c + half), len = hi - lo;
            if (len < 4 * perSec) continue;
            double mean = 0;
            for (int k = lo; k < hi; k++) mean += flux[k];
            mean /= len;
            double r0 = 0;
            for (int k = 0; k < len; k++) { x[k] = flux[lo + k] - mean; r0 += x[k] * x[k]; }
            if (r0 < 1e-18) continue;
            // Modulation depth of the onsets relative to the level: a steady tone or pad has a tiny flux whose
            // normalised autocorrelation would still look "strong", so weak modulation gates the beat strength.
            double ampMean = 0;
            for (int k = lo; k < hi; k++) ampMean += amp[k];
            ampMean /= len;
            double depth = Math.Sqrt(r0 / len) / Math.Max(ampMean, 1e-9);
            int top = Math.Min(2 * maxLag, len / 2);
            for (int l = minLag - 1; l <= top + 1; l++)
            {
                double acc = 0;
                for (int k = 0; k + l < len; k++) acc += x[k] * x[k + l];
                r[l] = acc / r0 * len / (len - l);   // unbiased, normalised
            }
            double best = double.NegativeInfinity; int bl = 0;
            // +-1 lag tolerance on the peak and its 2x harmonic (the true period falls between samples), and a mild
            // preference for the shorter lag so a 3x sub-harmonic with an equal peak does not win.
            double Near(int l) => Math.Max(r[l - 1], Math.Max(r[l], r[l + 1]));
            for (int l = minLag; l <= Math.Min(maxLag, top); l++)
            {
                double sc = (Near(l) + (2 * l + 1 <= top + 1 ? 0.5 * Near(2 * l) : 0)) * (1 + 0.15 * (1 - (double)l / maxLag));
                if (sc > best) { best = sc; bl = l; }
            }
            if (bl == 0) continue;
            if (r[bl - 1] > r[bl] && r[bl - 1] >= r[bl + 1]) bl--; else if (r[bl + 1] > r[bl]) bl++;   // snap to the local peak
            double lag = bl;
            if (bl > minLag && bl < top)
            {
                double a = r[bl - 1], b = r[bl], d = r[bl + 1], den = a - 2 * b + d;
                if (den < -1e-12) lag += Math.Clamp(0.5 * (a - d) / den, -0.5, 0.5);
            }
            bpm[j] = Math.Clamp(60.0 * perSec / lag, MinBpm, MaxBpm);
            str[j] = Math.Clamp((r[bl] - BeatFloor) / BeatSpan, 0, 1) * Math.Clamp((depth - DepthFloor) / DepthSpan, 0, 1);
        }
        // Median-3 on tempo (a lone wrong hop does not make a region), then interpolate hop -> bucket.
        var med = (double[])bpm.Clone();
        for (int j = 1; j < hops - 1; j++)
        {
            double a = bpm[j - 1], b = bpm[j], c = bpm[j + 1];
            med[j] = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
        }
        double hopsPerBucket = (n / (double)perSec) / buckets / TempoHopSec;
        for (int i = 0; i < buckets; i++)
        {
            double pos = (i + 0.5) * hopsPerBucket;
            int j0 = Math.Min((int)pos, hops - 1), j1 = Math.Min(j0 + 1, hops - 1);
            double f = pos - j0;
            double t0 = med[j0], t1 = med[j1];
            if (t0 <= 0) t0 = t1; else if (t1 <= 0) t1 = t0;   // an unknown hop does not drag the tempo toward 0
            tempo[i] = (byte)Math.Clamp(Math.Round(t0 + (t1 - t0) * f), 0, 255);
            beat[i] = (byte)Math.Round(255 * (str[j0] + (str[j1] - str[j0]) * f));
        }
        return (tempo, beat);
    }

    // The pure part: RMS over interleaved float samples, then normalised to the loudest bucket
    // with a sqrt curve so quiet passages stay visible. Samples go into fixed-duration windows
    // (~30 ms) as they are decoded, and only at the end are the windows spread over the buckets by
    // the count actually decoded, so bucket placement cannot depend on a length estimate.
    private sealed class Accumulator
    {
        private static readonly double[] OnsetWeights = { 1, 1, 1, 0.6, 0.4, 0.3, 0.3, 0.3 };   // bass-weighted
        private const int FftBits = 10, FftSize = 1 << FftBits;
        private readonly int _buckets, _window, _ch, _onWin;
        private readonly List<double> _sumSq = new();
        private readonly List<double[]> _bandSq = new();
        private readonly List<double> _onset = new();   // bass-weighted band energy per OnsetMs
        private readonly List<long> _count = new();
        private long _pos;
        private double _mono;
        private int _chPos;
        // Band bank: a Hann-windowed FFT of the latest FftSize mono frames every OnsetMs, power summed into SpectrumBands
        // (an FFT, not biquads: NAudio's FFT is optimised library code, the per-sample filter loop cost minutes on an hour-long track).
        private readonly float[] _ring = new float[FftSize], _hann = new float[FftSize];
        private readonly NAudio.Dsp.Complex[] _fft = new NAudio.Dsp.Complex[FftSize];
        private readonly int[] _binBand = new int[FftSize / 2];
        private int _ringPos;

        public Accumulator(int buckets, int windowSamples, int channels = 1, int sampleRate = 44100)
        {
            _buckets = buckets;
            _window = Math.Max(1, windowSamples);
            _ch = Math.Max(1, channels);
            _onWin = Math.Max(1, sampleRate * OnsetMs / 1000);
            for (int k = 0; k < FftSize; k++) _hann[k] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * k / (FftSize - 1)));
            for (int k = 0; k < _binBand.Length; k++)
            {
                double hz = (double)k * sampleRate / FftSize;
                int band = 0;
                while (band < BandEdgesHz.Length && hz >= BandEdgesHz[band]) band++;
                _binBand[k] = band;
            }
        }

        // Hot loop: the current window's sums live in fields and are written to the lists when the window
        // closes (and, for a progress copy, by SyncCurrent).
        private int _curW = -1;
        private long _winEnd;
        private double _curSum, _curCount;
        private double[] _curBand = new double[SpectrumBands];
        private int _onCnt;
        private readonly double[] _hop = new double[SpectrumBands];

        private void SyncCurrent()
        {
            if (_curW < 0) return;
            _sumSq[_curW] = _curSum; _count[_curW] = (long)_curCount;
        }

        private void Hop()
        {
            for (int k = 0; k < FftSize; k++)
            {
                _fft[k].X = _ring[(_ringPos + k) & (FftSize - 1)] * _hann[k];
                _fft[k].Y = 0;
            }
            NAudio.Dsp.FastFourierTransform.FFT(true, FftBits, _fft);
            Array.Clear(_hop);
            for (int k = 0; k < _binBand.Length; k++) _hop[_binBand[k]] += _fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y;
            double on = 0, scale = (double)_onWin * _ch / FftSize;   // so a window's sum behaves like energy over its samples
            for (int b = 0; b < SpectrumBands; b++)
            {
                _curBand[b] += _hop[b] * scale;
                on += OnsetWeights[b] * _hop[b];
            }
            _onset.Add(on);
        }

        public void Add(float[] buf, int n)
        {
            for (int i = 0; i < n; i++, _pos++)
            {
                if (_pos == _winEnd)
                {
                    SyncCurrent();
                    _sumSq.Add(0); _count.Add(0);
                    _curBand = new double[SpectrumBands]; _bandSq.Add(_curBand);
                    _curW++; _winEnd += _window; _curSum = 0; _curCount = 0;
                }
                double v = buf[i];
                _curSum += v * v;
                _curCount++;
                _mono += v;
                if (++_chPos == _ch)
                {
                    _ring[_ringPos] = (float)(_mono / _ch);
                    _ringPos = (_ringPos + 1) & (FftSize - 1);
                    _mono = 0; _chPos = 0;
                    if (++_onCnt == _onWin) { _onCnt = 0; Hop(); }
                }
            }
        }

        // Spreads per-window values over the buckets (summing; short tracks repeat windows).
        // estTotal > 0: provisional, windows placed against the estimated total (progress reports).
        // estTotal == 0: final, windows spread across all buckets by the actual decoded count.
        // Buckets nothing landed in stay 0.
        private double[] Place(Func<int, double> get, long estTotal)
        {
            var res = new double[_buckets];
            int W = _sumSq.Count;
            if (estTotal > 0)
            {
                for (int w = 0; w < W; w++)
                {
                    long b = (long)w * _window * _buckets / estTotal;
                    res[b >= _buckets ? _buckets - 1 : (int)b] += get(w);
                }
            }
            else if (W >= _buckets)
            {
                for (int w = 0; w < W; w++) res[(int)((long)w * _buckets / W)] += get(w);
            }
            else if (W > 0)
            {
                // Fewer windows than buckets (very short track): repeat windows to fill the width.
                for (int b = 0; b < _buckets; b++) res[b] = get((int)((long)b * W / _buckets));
            }
            return res;
        }

        public Waveform ToBytes(long estTotal)
        {
            SyncCurrent();
            var sum = Place(w => _sumSq[w], estTotal);
            var cnt = Place(w => _count[w], estTotal);

            // Adaptive dB contrast stretch: heavily mastered tracks have only 3-4 dB between their
            // loud and quiet sections, which a linear/sqrt amplitude scale cannot show.
            var db = new double[_buckets];
            double maxDb = double.NegativeInfinity;
            for (int i = 0; i < db.Length; i++)
            {
                db[i] = double.NegativeInfinity;
                if (cnt[i] == 0) continue;
                double rms = Math.Sqrt(sum[i] / cnt[i]);
                if (rms <= 1e-9) continue;   // digital silence
                db[i] = 20 * Math.Log10(rms);
                if (db[i] > maxDb) maxDb = db[i];
            }
            var bytes = new byte[_buckets];
            if (double.IsNegativeInfinity(maxDb))
                return new Waveform(bytes, new byte[_buckets], new byte[_buckets], new byte[_buckets],
                                    new byte[_buckets], new byte[_buckets], new byte[SpectrumBands * _buckets]);

            var live = new List<double>();
            foreach (var d in db) if (d > maxDb - SilenceDb) live.Add(d);
            live.Sort();
            double floorDb = live[(int)(FloorPercentile * (live.Count - 1))];
            floorDb = Math.Min(floorDb, maxDb - MinRangeDb);
            floorDb = Math.Max(floorDb, maxDb - MaxRangeDb);

            for (int i = 0; i < db.Length; i++)
            {
                double h;
                if (double.IsNegativeInfinity(db[i])) h = 0;
                else if (db[i] >= floorDb)
                    h = BaseHeight + (1 - BaseHeight) * Math.Clamp((db[i] - floorDb) / (maxDb - floorDb), 0, 1);
                else
                    h = BaseHeight * Math.Clamp(1 - (floorDb - db[i]) / FadeDb, 0, 1);
                bytes[i] = (byte)Math.Clamp(Math.Round(255 * h), 0, 255);
            }
            var bandSum = new double[SpectrumBands][];
            for (int b = 0; b < SpectrumBands; b++) { int bb = b; bandSum[b] = Place(w => _bandSq[w][bb], estTotal); }
            double[] Group(int from, int to)
            {
                var g = new double[_buckets];
                for (int b = from; b <= to; b++) for (int i = 0; i < g.Length; i++) g[i] += bandSum[b][i];
                return g;
            }
            var bands = Bands(Group(0, 1), Group(2, 5), Group(6, 7), cnt, db, maxDb);

            // Absolute band levels for the spectrum strip: dB against the loudest band value in the track.
            var spec = new byte[SpectrumBands * _buckets];
            double specMax = double.NegativeInfinity;
            var specDb = new double[SpectrumBands][];
            for (int b = 0; b < SpectrumBands; b++)
            {
                specDb[b] = new double[_buckets];
                for (int i = 0; i < _buckets; i++)
                {
                    double p = cnt[i] > 0 ? bandSum[b][i] / cnt[i] : 0;
                    specDb[b][i] = p > 1e-20 ? 10 * Math.Log10(p) : double.NegativeInfinity;
                    if (specDb[b][i] > specMax) specMax = specDb[b][i];
                }
            }
            if (!double.IsNegativeInfinity(specMax))
                for (int b = 0; b < SpectrumBands; b++)
                    for (int i = 0; i < _buckets; i++)
                        spec[b * _buckets + i] = (byte)Math.Round(255 * Math.Clamp((specDb[b][i] - (specMax - SpectrumRangeDb)) / SpectrumRangeDb, 0, 1));

            // Tempo only on the final result (progress copies would pay the autocorrelation every 5%).
            byte[] tempo, beat;
            if (estTotal == 0) (tempo, beat) = TempoBeat(_onset, _buckets);
            else { tempo = new byte[_buckets]; beat = new byte[_buckets]; }
            return new Waveform(bytes, bands[0], bands[1], bands[2], tempo, beat, spec);
        }

        // Per-bucket colour weights (as sqrt bytes) from the bands' relative balance. See the knobs comment.
        private byte[][] Bands(double[] lo, double[] mi, double[] hi, double[] cnt, double[] fullDb, double maxDb)
        {
            int n = _buckets;
            var sums = new[] { lo, mi, hi };
            var res = new[] { new byte[n], new byte[n], new byte[n] };
            var ok = new bool[n];
            for (int i = 0; i < n; i++) ok[i] = cnt[i] > 0 && fullDb[i] > maxDb - SilenceDb;
            var d = new double[3][];
            for (int b = 0; b < 3; b++)
            {
                var raw = new double[n];
                for (int i = 0; i < n; i++)
                    raw[i] = ok[i] ? 10 * Math.Log10(Math.Max(sums[b][i] / cnt[i], 1e-20)) : double.NaN;
                var sm = new double[n];
                var live = new List<double>();
                for (int i = 0; i < n; i++)
                {
                    if (!ok[i]) { sm[i] = double.NaN; continue; }
                    double acc = 0; int k = 0;
                    for (int q = Math.Max(0, i - SmoothBuckets / 2); q <= Math.Min(n - 1, i + SmoothBuckets / 2); q++)
                        if (ok[q]) { acc += raw[q]; k++; }
                    sm[i] = acc / k;
                    live.Add(sm[i]);
                }
                live.Sort();
                double med = live.Count > 0 ? live[live.Count / 2] : 0;
                d[b] = new double[n];
                for (int i = 0; i < n; i++) d[b][i] = sm[i] - med;
            }
            for (int i = 0; i < n; i++)
            {
                if (!ok[i]) { res[0][i] = res[1][i] = res[2][i] = 255; continue; }   // silent: equal weights
                double mean = (d[0][i] + d[1][i] + d[2][i]) / 3;
                var w = new double[3];
                double top = 0;
                for (int b = 0; b < 3; b++) { w[b] = Math.Exp(Gain * (d[b][i] - mean)); top = Math.Max(top, w[b]); }
                for (int b = 0; b < 3; b++) res[b][i] = (byte)Math.Round(255 * Math.Sqrt(w[b] / top));
            }
            return res;
        }
    }

#if DEBUG
    // Self-check: silence then full-scale gives low then 255. Runs once, on first touch.
    private static void SelfCheck()
    {
        // Loud third, a third 3 dB lower (x0.708), then digital silence.
        var samples = new float[60000];
        for (int i = 0; i < 20000; i++) samples[i] = (i % 2 == 0) ? 0.5f : -0.5f;
        for (int i = 20000; i < 40000; i++) samples[i] = (i % 2 == 0) ? 0.354f : -0.354f;
        var acc = new Accumulator(Buckets, 10);
        acc.Add(samples, samples.Length);
        var b = acc.ToBytes(0).Peaks;
        Debug.Assert(b.Length == Buckets, "WaveformService self-check: bucket count");
        Debug.Assert(b[100] == 255, "WaveformService self-check: loudest section not full height");
        Debug.Assert(b[500] < b[100] * 0.75, "WaveformService self-check: 3 dB quieter section not clearly shorter");
        Debug.Assert(b[900] == 0 && b[Buckets - 1] == 0, "WaveformService self-check: silence not 0");
        // Estimate larger than actual (the final result ignores it): provisional leaves a tail.
        var est = acc.ToBytes(samples.Length * 2L).Peaks;
        Debug.Assert(est[Buckets - 1] == 0, "WaveformService self-check: provisional should leave a tail");
        // Very short track (fewer windows than buckets) still fills every bucket.
        var tiny = new Accumulator(Buckets, 10);
        tiny.Add(new float[] { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f }, 20);
        Debug.Assert(tiny.ToBytes(0).Peaks[Buckets - 1] == 255, "WaveformService self-check: short track not full width");
        // Band split is relative: a constant tone has no deviation from its own median -> neutral;
        // tone A then A + 8 kHz makes 'high' clearly the heaviest in the second half.
        var tones = new float[4 * 88200];
        for (int i = 0; i < tones.Length; i++)
        {
            double t = i / 44100.0;
            double v = 0.3 * Math.Sin(2 * Math.PI * 1000 * t);
            if (i >= tones.Length / 2) v += 0.3 * Math.Sin(2 * Math.PI * 8000 * t);
            tones[i] = (float)v;
        }
        var bandAcc = new Accumulator(Buckets, 44100 * 30 / 1000, 1, 44100);
        bandAcc.Add(tones, tones.Length);
        var wf = bandAcc.ToBytes(0);
        Debug.Assert(wf.High[800] > wf.Low[800] && wf.High[800] > wf.Mid[800] && wf.High[800] == 255,
            "WaveformService self-check: added 8 kHz not high-dominant");
        Debug.Assert(wf.High[100] < 128 && wf.High[100] < wf.Mid[100], "WaveformService self-check: plain 1 kHz half not low-treble");
        var flat = new float[2 * 88200];
        for (int i = 0; i < flat.Length; i++) flat[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 80 * i / 44100.0));
        var flatAcc = new Accumulator(Buckets, 44100 * 30 / 1000, 1, 44100);
        flatAcc.Add(flat, flat.Length);
        var fw = flatAcc.ToBytes(0);
        TempoCheck();
        Debug.Assert(Math.Abs(fw.Low[500] - fw.Mid[500]) < 32 && Math.Abs(fw.Low[500] - fw.High[500]) < 32,
            "WaveformService self-check: constant tone not neutral");
    }

    // Click tracks at 120 and 70 BPM must read within a few BPM (or an exact half/double); a steady tone has no beat.
    private static void TempoCheck()
    {
        static (double tempo, double beat) Run(Func<int, float> gen, int seconds)
        {
            int sr = 44100;
            var buf = new float[seconds * sr];
            for (int i = 0; i < buf.Length; i++) buf[i] = gen(i);
            var acc = new Accumulator(Buckets, sr * 30 / 1000, 1, sr);
            acc.Add(buf, buf.Length);
            var w = acc.ToBytes(0);
            var t = w.Tempo.Skip(200).Take(600).OrderBy(v => v).ToArray();
            var bt = w.Beat.Skip(200).Take(600).OrderBy(v => v).ToArray();
            return (t[t.Length / 2], bt[bt.Length / 2]);
        }
        Func<double, Func<int, float>> clicks = bpm => i =>
        {
            double period = 44100 * 60.0 / bpm, ph = i % period;
            return ph < 2000 ? (float)(0.8 * Math.Exp(-ph / 400) * Math.Sin(2 * Math.PI * 60 * ph / 44100)) : 0f;
        };
        var a = Run(clicks(120), 40);
        var b = Run(clicks(70), 40);
        var c = Run(i => (float)(0.4 * Math.Sin(2 * Math.PI * 440 * i / 44100.0)), 40);
        static bool Near(double v, double bpm) => Math.Abs(v - bpm) <= 4 || Math.Abs(v - bpm / 2) <= 3 || Math.Abs(v - bpm * 2) <= 6;
        Debug.Assert(Near(a.tempo, 120) && a.beat > 128, $"WaveformService self-check: 120 BPM clicks read {a.tempo}/{a.beat}");
        Debug.Assert(Near(b.tempo, 70) && b.beat > 128, $"WaveformService self-check: 70 BPM clicks read {b.tempo}/{b.beat}");
        Debug.Assert(c.beat < 20, $"WaveformService self-check: steady tone has beat {c.beat}");
    }
#endif
}
