using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MusicOrganiser.Models;
using MusicOrganiser.Services;

namespace MusicOrganiser.Controls;

/// <summary>
/// Seek bar that draws the whole track as a mirrored loudness chart (one byte per bucket in
/// <see cref="Waveform"/>), the played part in full colour. Bars are coloured by spectral balance
/// (bass = pink, mids = violet, treble = cyan) when the waveform carries band levels, else by
/// <see cref="PlayedBrush"/>/<see cref="UnplayedBrush"/>. While playing, the bars just left of the
/// playhead pulse with the live level and the playhead glows with the kick (pulled via
/// <see cref="LiveSource"/> each frame; the render hook only exists while playing and visible).
/// Without a waveform it degrades to a thin progress bar. Drag protocol matches the VM's slider
/// mechanism: <see cref="IsDragging"/> goes true on mouse down, <see cref="Value"/> previews while
/// scrubbing, and <see cref="IsDragging"/> going false on mouse up is what makes the view model
/// commit the seek.
/// </summary>
public class WaveformSeekBar : FrameworkElement
{
    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static readonly DependencyProperty WaveformProperty = DependencyProperty.Register(
        nameof(Waveform), typeof(Waveform), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying), typeof(bool), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(false, (d, e) => ((WaveformSeekBar)d).UpdateRenderHook()));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(0.0,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsDraggingProperty = DependencyProperty.Register(
        nameof(IsDragging), typeof(bool), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    // Defaults: the play button's accent (#2F8FE0) and a light grey.
    public static readonly DependencyProperty PlayedBrushProperty = DependencyProperty.Register(
        nameof(PlayedBrush), typeof(Brush), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(Frozen("#2F8FE0"), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnplayedBrushProperty = DependencyProperty.Register(
        nameof(UnplayedBrush), typeof(Brush), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(Frozen("#C4CAD1"), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DrawStyleProperty = DependencyProperty.Register(
        nameof(DrawStyle), typeof(WaveformStyle), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(WaveformStyle.Bars, FrameworkPropertyMetadataOptions.AffectsRender));

    // (Not "Style": FrameworkElement already has one.)
    public WaveformStyle DrawStyle { get => (WaveformStyle)GetValue(DrawStyleProperty); set => SetValue(DrawStyleProperty, value); }
    public Waveform? Waveform { get => (Waveform?)GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public bool IsDragging { get => (bool)GetValue(IsDraggingProperty); set => SetValue(IsDraggingProperty, value); }
    public Brush PlayedBrush { get => (Brush)GetValue(PlayedBrushProperty); set => SetValue(PlayedBrushProperty, value); }
    public Brush UnplayedBrush { get => (Brush)GetValue(UnplayedBrushProperty); set => SetValue(UnplayedBrushProperty, value); }

    // Pulled each rendered frame while playing: raw (level, kick) of what is audible now.
    public Func<(float level, float kick)>? LiveSource
    {
        get => _liveSource;
        set { _liveSource = value; UpdateRenderHook(); }
    }
    private Func<(float level, float kick)>? _liveSource;
    // Audible playhead as a percentage of Maximum (null = unknown); replaces the 100 ms Value while animating.
    public Func<double?>? PositionSource { get; set; }

    // Bar colours by spectral band; unplayed bars are the same colour pushed toward the panel
    // behind the control (#F0F0F0). The phone uses the identical formula.
    private static readonly (double r, double g, double b) CLow = (0xFF, 0x2D, 0x95), CMid = (0x7C, 0x4D, 0xFF), CHigh = (0x00, 0xC8, 0xFF);
    private const double PanelGray = 0xF0;
    private const double UnplayedBlend = 0.65;
    private const int PulseBars = 10;
    private const double PulseGrow = 0.4;

    // ponytail: the taste knobs of the four fingerprint styles (Ribbons, Strands, BeatBars, Spectrum). Wavelengths are
    // in pixels, not time: a 70 minute track has well under one beat per pixel, so tempo sets the texture, not the beat grid.
    private const double RippleK = 840, RippleMinPx = 4, RippleMaxPx = 16;   // wavelength px = K / BPM: 60 BPM ~14, 180 BPM ~5
    private const double RippleDepth = 0.25;                                  // A: edge dip, fraction of local half-height at full beat
    private const double StrandAmp = 0.9, StrandMinPx = 5, StrandShareGain = 2;                  // B: amplitude (x band weight x half-height), shortest wavelength
    private const double StrandBassPx = 2.2, StrandMidPx = 1.4, StrandHighPx = 0.9;   // B: line thickness
    private const double BeatPitchSlow = 9, BeatPitchFast = 3;                // C: bar pitch px at <= 60 BPM / >= 170 BPM
    private const double BeatSlowBpm = 60, BeatFastBpm = 170;
    private const int SpecRows = WaveformService.SpectrumBands;               // D: rows, bass at the bottom
    private const double TickWidthPx = 1, TickHeightPx = 4;                   // D: tempo ticks on the bottom edge
    private const double TiltMid = 1.25, TiltHigh = 1.6, SharePower = 1.5, ShareAbsent = 0.08, ShareMin = 0.06;   // band shares from absolute spectrum level
    private const double SpecFloor = 0.2, SpecGamma = 0.6;                    // D: cell alpha floor (level > 0) and level gamma
    private const double DefaultBpm = 90;                                     // when a section has no tempo

    // Visual layers: the chart at full strength and the washed (unplayed) chart are each recorded once per
    // (waveform, size, style, dpi, brushes) and only clipped per playhead move; the overlay (lift, halo, playhead,
    // Bars swell) is the only thing re-recorded while playing, throttled to ~30 fps.
    private sealed class Layer : DrawingVisual
    {
        public readonly RectangleGeometry ClipRect = new(new Rect());
        public Layer(bool chart) { if (chart) { VisualClip = ClipRect; VisualCacheMode = new BitmapCache(1) { SnapsToDevicePixels = true }; } }
        public void Scale(double dpi) { if (VisualCacheMode is BitmapCache bc && bc.RenderAtScale != dpi) VisualCacheMode = new BitmapCache(dpi) { SnapsToDevicePixels = true }; }
    }
    private readonly Layer _full = new(true), _wash = new(true), _overlay = new(false);
    private enum Mode { None, Progress, Fingerprint, Bars }
    private Mode _mode;
    private (Waveform? wf, double w, double h, double dpi, WaveformStyle style) _failedKey;
    private (Waveform?, double, double, double, WaveformStyle, Brush, Brush) _builtKey;
    private double _pitch, _barW, _usable, _mid;   // Bars layout of the current build
    private int _bars;
    private bool _barBands;
    private const long FrameMs = 33;
    private long _lastFrameMs;
    private double _lastX = double.NaN, _lastLevel, _lastKick;
    private bool _lastHooked;

    private readonly Dictionary<int, Brush> _brushes = new();
    private bool _hooked;
    private double _level, _kick;                          // smoothed, 0..1
    private double _levelPeak = 0.02, _kickPeak = 0.003;   // adaptive normalisation of the raw envelopes
    private double? _livePos;

    public WaveformSeekBar()
    {
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
        AddVisualChild(_full); AddVisualChild(_wash); AddVisualChild(_overlay);
        Loaded += (s, e) => UpdateRenderHook();
        Unloaded += (s, e) => UpdateRenderHook();
        IsVisibleChanged += (s, e) => UpdateRenderHook();
    }

    protected override int VisualChildrenCount => 3;
    protected override Visual GetVisualChild(int index) => index switch { 0 => _full, 1 => _wash, 2 => _overlay, _ => throw new ArgumentOutOfRangeException(nameof(index)) };

    // Subscribe to the per-frame render event only while playing on screen; costs nothing otherwise.
    private void UpdateRenderHook()
    {
        bool want = IsPlaying && IsLoaded && IsVisible && LiveSource != null;
        if (want == _hooked) return;
        _hooked = want;
        if (want) CompositionTarget.Rendering += OnFrame;
        else
        {
            CompositionTarget.Rendering -= OnFrame;
            _level = _kick = 0; _livePos = null;
            InvalidateVisual();   // settle the bars back to rest
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var src = LiveSource;
        if (src == null) return;
        long now = Environment.TickCount64;
        if (now - _lastFrameMs < FrameMs) return;   // 30 fps cap
        _lastFrameMs = now;
        var (lv, kk) = src();
        _levelPeak = Math.Max(Math.Max(_levelPeak * 0.998, lv), 0.02);
        _kickPeak = Math.Max(Math.Max(_kickPeak * 0.998, kk), 0.003);
        double tl = Math.Clamp(lv / _levelPeak, 0, 1), tk = Math.Clamp(kk / _kickPeak, 0, 1);
        _level += (tl - _level) * (tl > _level ? 0.6 : 0.12);   // fast attack, slower decay
        _kick += (tk - _kick) * (tk > _kick ? 0.7 : 0.15);
        _livePos = IsDragging ? null : PositionSource?.Invoke();
        UpdateVisuals(false);
    }

    private Brush Cached(int key, byte r, byte g, byte b)
    {
        if (_brushes.TryGetValue(key, out var br)) return br;
        if (_brushes.Count > 8192) _brushes.Clear();
        var n = new SolidColorBrush(Color.FromRgb(r, g, b));
        n.Freeze();
        return _brushes[key] = n;
    }

    // Band levels (0-255) -> brush, quantised to 5 bits per channel.
    private static (double r, double g, double b) Mix(int l, int m, int h)
    {
        double wl = (l / 255.0) * (l / 255.0), wm = (m / 255.0) * (m / 255.0), wh = (h / 255.0) * (h / 255.0);
        double sum = wl + wm + wh;
        if (sum < 1e-6) { wl = wm = wh = 1; sum = 3; }
        double r = (wl * CLow.r + wm * CMid.r + wh * CHigh.r) / sum;
        double g = (wl * CLow.g + wm * CMid.g + wh * CHigh.g) / sum;
        double b = (wl * CLow.b + wm * CMid.b + wh * CHigh.b) / sum;
        return (r, g, b);
    }

    private Brush BandBrush(int l, int m, int h, bool played)
    {
        var (r, g, b) = Mix(l, m, h);
        if (!played)
        {
            r += (PanelGray - r) * UnplayedBlend; g += (PanelGray - g) * UnplayedBlend; b += (PanelGray - b) * UnplayedBlend;
        }
        int r5 = (int)r >> 3, g5 = (int)g >> 3, b5 = (int)b >> 3;
        return Cached((r5 << 10) | (g5 << 5) | b5, (byte)(r5 << 3 | r5 >> 2), (byte)(g5 << 3 | g5 >> 2), (byte)(b5 << 3 | b5 >> 2));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        Fill(dc, Brushes.Transparent, 0, 0, w, h);   // hit-testable everywhere
        UpdateVisuals(true);
    }

    private void UpdateVisuals(bool fromRender)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX, px = 1.0 / dpi;
        bool rebuilt = EnsureStatic(w, h, dpi, px);
        UpdateDynamic(w, h, px, rebuilt);
    }

    private void BarData(Waveform wf, byte[] peaks, int i, out int v, out int l, out int m, out int hi)
    {
        // Aggregate by max when there are more buckets than bars.
        int from = (int)((long)i * peaks.Length / _bars);
        int to = Math.Max(from + 1, (int)((long)(i + 1) * peaks.Length / _bars));
        v = l = m = hi = 0;
        for (int k = from; k < to && k < peaks.Length; k++)
        {
            if (peaks[k] > v) v = peaks[k];
            if (_barBands)
            {
                if (wf.Low[k] > l) l = wf.Low[k];
                if (wf.Mid[k] > m) m = wf.Mid[k];
                if (wf.High[k] > hi) hi = wf.High[k];
            }
        }
    }

    private double BarHeight(int v) => Math.Max(_barW, v / 255.0 * _usable);   // silence still shows a line (2 px)

    private void DrawBarsStatic(DrawingContext dc, Waveform wf, byte[] peaks, bool playedColours)
    {
        for (int i = 0; i < _bars; i++)
        {
            BarData(wf, peaks, i, out int v, out int l, out int m, out int hi);
            double bh = BarHeight(v);
            Brush br = _barBands ? BandBrush(l, m, hi, playedColours) : (playedColours ? PlayedBrush : UnplayedBrush);
            Fill(dc, br, i * _pitch, _mid - bh / 2, _barW, bh);
        }
    }

    // Records the two static layers when anything they depend on changed. Returns true when it did.
    private bool EnsureStatic(double w, double h, double dpi, double px)
    {
        var wf = Waveform;
        var key = (wf, w, h, dpi, DrawStyle, PlayedBrush, UnplayedBrush);
        if (key == _builtKey) return false;
        _builtKey = key;
        var peaks = wf?.Peaks;
        double mid = h / 2;
        bool fingerprint = DrawStyle != WaveformStyle.Bars && wf != null && peaks != null && peaks.Length == WaveformService.Buckets
            && wf.Tempo.Length == peaks.Length && wf.Beat.Length == peaks.Length && wf.Spectrum.Length == SpecRows * peaks.Length
            && wf.Low.Length == peaks.Length && wf.Mid.Length == peaks.Length && wf.High.Length == peaks.Length;
        DrawingGroup? dg = null;
        if (fingerprint)
        {
            var fkey = (wf, w, h, dpi, DrawStyle);
            if (_failedKey == fkey) fingerprint = false;   // this combination threw before: plain bars
            else
            {
                try
                {
                    dg = new DrawingGroup();
                    using (var g = dg.Open())
                    {
                        switch (DrawStyle)
                        {
                            case WaveformStyle.Ribbons: BuildRibbons(g, wf!, w, h, px); break;
                            case WaveformStyle.Strands: BuildStrands(g, wf!, w, h, px); break;
                            case WaveformStyle.BeatBars: BuildBeatBars(g, wf!, w, h, px); break;
                            default: BuildSpectrum(g, wf!, w, h, px); break;
                        }
                    }
                    dg.Freeze();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"WaveformSeekBar {DrawStyle} failed, falling back to Bars: {ex}");
                    _failedKey = fkey; dg = null; fingerprint = false;
                }
            }
        }
        _full.Scale(dpi); _wash.Scale(dpi);
        if (fingerprint)
        {
            _mode = Mode.Fingerprint;
            using (var dc = _full.RenderOpen()) dc.DrawDrawing(dg);
            using (var dc = _wash.RenderOpen()) { dc.PushOpacity(1 - UnplayedBlend); dc.DrawDrawing(dg); dc.Pop(); }
        }
        else if (wf == null || peaks == null || peaks.Length == 0)
        {
            _mode = Mode.Progress;
            double th = 4 * px;
            using (var dc = _full.RenderOpen()) Fill(dc, PlayedBrush, 0, mid - th / 2, w, th);
            using (var dc = _wash.RenderOpen()) Fill(dc, UnplayedBrush, 0, mid - th / 2, w, th);
        }
        else
        {
            _mode = Mode.Bars;
            _pitch = 3 * px; _barW = 2 * px; _usable = h - 2 * px; _mid = mid;
            _bars = Math.Max(1, (int)(w / _pitch));
            _barBands = wf.Low.Length == peaks.Length && wf.Mid.Length == peaks.Length && wf.High.Length == peaks.Length;
            using (var dc = _full.RenderOpen()) DrawBarsStatic(dc, wf, peaks, true);
            using (var dc = _wash.RenderOpen()) DrawBarsStatic(dc, wf, peaks, false);
        }
        return true;
    }

    // Playhead-dependent part: two clip rects (cheap property sets) and the small overlay visual.
    private void UpdateDynamic(double w, double h, double px, bool force)
    {
        double max = Maximum;
        double val = _hooked && _livePos is double lp ? lp / 100.0 * max : Value;
        double frac = max > 0 ? Math.Clamp(val / max, 0, 1) : 0;
        double playX = frac * w;
        if (!force && _hooked == _lastHooked && Math.Abs(playX - _lastX) < px / 4
            && Math.Abs(_level - _lastLevel) < 0.01 && Math.Abs(_kick - _lastKick) < 0.01) return;
        _lastX = playX; _lastLevel = _level; _lastKick = _kick; _lastHooked = _hooked;

        double clipX = playX;
        if (_mode == Mode.Bars)   // split between bars, not through one: a bar is played when its centre is left of the playhead
        {
            int count = playX < _barW / 2 ? 0 : Math.Min(_bars, (int)((playX - _barW / 2) / _pitch) + 1);
            clipX = count > 0 ? count * _pitch - px / 2 : 0;
        }
        _full.ClipRect.Rect = R(0, 0, clipX, h);
        _wash.ClipRect.Rect = R(clipX, 0, w - clipX, h);

        var wf = Waveform;
        Brush? headBrush = null;   // colour of the bar at the playhead, for the glow
        using (var dc = _overlay.RenderOpen())
        {
            if (_mode == Mode.Fingerprint)
            {
                int hk = Math.Min((int)(frac * wf!.Peaks.Length), wf.Peaks.Length - 1);
                headBrush = BandBrush(wf.Low[hk], wf.Mid[hk], wf.High[hk], true);
                if (_hooked && _level > 0.02 && frac > 0)
                {
                    // Subtle lift of the region just left of the playhead.
                    dc.PushOpacity(0.3 * _level);
                    Fill(dc, Brushes.White, Math.Max(0, playX - 40), 0, Math.Min(40, playX), h);
                    dc.Pop();
                }
            }
            else if (_mode == Mode.Bars)
            {
                var peaks = wf!.Peaks;
                int headBar = Math.Min((int)(playX / _pitch), _bars - 1);
                for (int i = Math.Max(0, headBar - PulseBars + 1); i <= headBar; i++)
                {
                    BarData(wf, peaks, i, out int v, out int l, out int m, out int hi);
                    bool isPlayed = i * _pitch + _barW / 2 <= playX;
                    if (i == headBar) headBrush = _barBands ? BandBrush(l, m, hi, true) : PlayedBrush;
                    if (!(_hooked && _level > 0.001)) continue;
                    double bh = Math.Min(_usable, BarHeight(v) * (1 + PulseGrow * _level * (1 - (double)(headBar - i) / PulseBars)));
                    Brush br = _barBands ? BandBrush(l, m, hi, isPlayed) : (isPlayed ? PlayedBrush : UnplayedBrush);
                    Fill(dc, br, i * _pitch, _mid - bh / 2, _barW, bh);
                }
            }

            if (_hooked && _kick > 0.02 && frac > 0 && headBrush is SolidColorBrush hb)
            {
                // Soft halo: a transparent-colour-transparent gradient (one small allocation per update).
                double half = 8 + _kick * 40;
                var c = hb.Color;
                var glow = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0),
                        new GradientStop(Color.FromArgb((byte)(_kick * 225), c.R, c.G, c.B), 0.5),
                        new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
                    }, new Point(0, 0), new Point(1, 0));
                glow.Freeze();
                Fill(dc, glow, playX - half, 0, half * 2, h);
            }

            if (frac > 0)
                Fill(dc, PlayedBrush, Math.Min(playX, w - 1.5 * px), 0, 1.5 * px, h);
        }
    }

    // ---- fingerprint styles: static drawings, built once per (waveform, size, style) ----

    // Linear interpolation of a per-bucket byte array at pixel x (bucket centres), so shapes are smooth and x stays linear in time.
    private static double Sample(byte[] a, double x, double w)
    {
        double pos = x / w * a.Length - 0.5;
        int i0 = Math.Clamp((int)Math.Floor(pos), 0, a.Length - 1), i1 = Math.Min(i0 + 1, a.Length - 1);
        double f = Math.Clamp(pos - i0, 0, 1);
        return a[i0] + (a[i1] - a[i0]) * f;
    }

    private static double Wavelength(double bpm) => Math.Clamp(RippleK / (bpm > 0 ? bpm : DefaultBpm), RippleMinPx, RippleMaxPx);

    // Every computed-size rectangle goes through these: NaN/Infinity/negative sizes are clamped and zero-size fills are skipped.
    private static double Fin(double v) => double.IsFinite(v) ? v : 0;
    private static Rect R(double x, double y, double w, double h) => new Rect(Fin(x), Fin(y), Math.Max(0, Fin(w)), Math.Max(0, Fin(h)));
    private static void Fill(DrawingContext g, Brush? b, double x, double y, double w, double h)
    {
        if (b == null || !(Fin(w) > 0) || !(Fin(h) > 0)) return;
        g.DrawRectangle(b, null, R(x, y, w, h));
    }

    private static Brush Solid((double r, double g, double b) c, double a = 1)
    {
        var br = new SolidColorBrush(Color.FromArgb((byte)(a * 255), (byte)c.r, (byte)c.g, (byte)c.b));
        br.Freeze();
        return br;
    }

    private static Pen FrozenPen(Brush b, double thickness)
    {
        var p = new Pen(b, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    // Mirrored filled shape: +-half[i] around mid, sampled every step px.
    private static Geometry Mirror(double[] half, double step, double w, double mid)
    {
        var sg = new StreamGeometry();
        using (var c = sg.Open())
        {
            c.BeginFigure(new Point(0, mid - half[0]), true, true);
            for (int i = 1; i < half.Length; i++) c.LineTo(new Point(Math.Min(i * step, w), mid - half[i]), true, false);
            for (int i = half.Length - 1; i >= 0; i--) c.LineTo(new Point(Math.Min(i * step, w), mid + half[i]), true, false);
        }
        sg.Freeze();
        return sg;
    }

    // Band shares (0-1, sum 1) over the drawn column x0..x1: absolute level from Spectrum (bass rows 0-1, mid 2-5, high 6-7, max over
    // the column's buckets), tilted, sharpened, times the squared relative weight; absent bands (< 8%) get 0, present ones at least 6%.
    // Without a Spectrum: the old tempered relative weights.
    private static (double lo, double mi, double hi) Shares(Waveform wf, double x0, double x1, double w)
    {
        double xm = (x0 + x1) / 2;
        double rl = Sample(wf.Low, xm, w) / 255, rm = Sample(wf.Mid, xm, w) / 255, rh = Sample(wf.High, xm, w) / 255;
        int nb = wf.Peaks.Length;
        if (wf.Spectrum.Length != SpecRows * nb)
        {
            double l0 = rl + 0.15, m0 = rm + 0.15, h0 = rh + 0.15, s0 = l0 + m0 + h0;
            return (l0 / s0, m0 / s0, h0 / s0);
        }
        int b0 = Math.Clamp((int)(x0 / w * nb), 0, nb - 1), b1 = Math.Clamp((int)Math.Ceiling(x1 / w * nb), b0 + 1, nb);
        int[] mx = new int[SpecRows];
        for (int r = 0; r < SpecRows; r++)
            for (int k = b0; k < b1; k++) mx[r] = Math.Max(mx[r], wf.Spectrum[r * nb + k]);
        double[] a = { Math.Max(mx[0], mx[1]) / 255.0, Math.Max(Math.Max(mx[2], mx[3]), Math.Max(mx[4], mx[5])) / 255.0, Math.Max(mx[6], mx[7]) / 255.0 };
        double[] rel = { rl * rl, rm * rm, rh * rh };
        double[] tilt = { 1, TiltMid, TiltHigh };
        var v = new double[3];
        double sum = 0;
        for (int k = 0; k < 3; k++)
        {
            v[k] = a[k] < ShareAbsent ? 0 : Math.Pow(a[k] * tilt[k], SharePower) * rel[k];
            sum += v[k];
        }
        if (sum < 1e-9)   // balance weights all zero: fall back to the absolute levels alone
        {
            for (int k = 0; k < 3; k++) { v[k] = a[k] < ShareAbsent ? 0 : Math.Pow(a[k] * tilt[k], SharePower); sum += v[k]; }
            if (sum < 1e-9) return (1 / 3.0, 1 / 3.0, 1 / 3.0);
        }
        double t = 0;
        for (int k = 0; k < 3; k++) { v[k] /= sum; if (v[k] > 0 || a[k] >= ShareAbsent) v[k] = Math.Max(v[k], ShareMin); t += v[k]; }
        return (v[0] / t, v[1] / t, v[2] / t);
    }

    // A. Silhouette = loudness; three stacked colour bands by share (bass innermost); the edge ripples with the tempo, deeper with the beat.
    private static void BuildRibbons(DrawingContext g, Waveform wf, double w, double h, double px)
    {
        double mid = h / 2, usable = h - 2 * px, step = Math.Max(px, 1.0);
        int n = (int)Math.Ceiling(w / step) + 1;
        var outer = new double[n]; var upper = new double[n]; var inner = new double[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double x = Math.Min(i * step, w);
            double hp = Math.Max(px, Sample(wf.Peaks, x, w) / 255 * usable / 2);
            phase += 2 * Math.PI * step / Wavelength(Sample(wf.Tempo, x, w));
            double d = RippleDepth * Sample(wf.Beat, x, w) / 255;
            double e = hp * (1 - d * 0.5 * (1 - Math.Sin(phase)));
            var (lo, mi, _) = Shares(wf, x, x + step, w);
            outer[i] = e; upper[i] = e * (lo + mi); inner[i] = e * lo;
        }
        g.DrawGeometry(Solid(CHigh), null, Mirror(outer, step, w, mid));
        g.DrawGeometry(Solid(CMid), null, Mirror(upper, step, w, mid));
        g.DrawGeometry(Solid(CLow), null, Mirror(inner, step, w, mid));
    }

    // B. Faint loudness outline; three oscillating lines (bass at the tempo wavelength, mids twice as fast, treble four times),
    // amplitude = band weight x loudness, phase carried across buckets so lines do not jump where the tempo changes.
    private static void BuildStrands(DrawingContext g, Waveform wf, double w, double h, double px)
    {
        double mid = h / 2, usable = h - 2 * px, step = Math.Max(px, 1.0) / 2;
        int n = (int)Math.Ceiling(w / step) + 1;
        var env = new double[n];
        var pts = new Point[3][];
        for (int k = 0; k < 3; k++) pts[k] = new Point[n];
        var phase = new double[3];
        var on = new bool[3][];
        for (int k = 0; k < 3; k++) on[k] = new bool[n];
        for (int i = 0; i < n; i++)
        {
            double x = Math.Min(i * step, w);
            double hp = Math.Max(px, Sample(wf.Peaks, x, w) / 255 * usable / 2);
            env[i] = hp;
            double wl = Wavelength(Sample(wf.Tempo, x, w));
            var (sl, sm, sh) = Shares(wf, x, x + step, w);
            double[] share = { sl, sm, sh };
            double[] weight = { Math.Min(1, sl * StrandShareGain), Math.Min(1, sm * StrandShareGain), Math.Min(1, sh * StrandShareGain) };
            for (int k = 0; k < 3; k++)
            {
                phase[k] += 2 * Math.PI * step / Math.Max(StrandMinPx, wl / (1 << k));
                pts[k][i] = new Point(x, mid + Math.Sin(phase[k]) * hp * StrandAmp * weight[k] * (0.5 + 0.5 * weight[k]));
                on[k][i] = share[k] > 0;
            }
        }
        g.DrawGeometry(Solid((0x90, 0x98, 0xA4), 0.22), null, Mirror(env, step, w, mid));
        (double r, double g, double b)[] col = { CLow, CMid, CHigh };
        double[] th = { StrandBassPx, StrandMidPx, StrandHighPx };
        for (int k = 2; k >= 0; k--)
        {
            var sg = new StreamGeometry();
            using (var c = sg.Open())
            {
                bool open = false;
                for (int i = 0; i < n; i++)
                {
                    if (!on[k][i]) { open = false; continue; }
                    if (!open) { c.BeginFigure(pts[k][i], false, false); open = true; }
                    else c.LineTo(pts[k][i], true, true);
                }
            }
            sg.Freeze();
            g.DrawGeometry(null, FrozenPen(Solid(col[k]), th[k]), sg);
        }
    }

    // C. Bars as in the default style, each split into three colour segments by share (bass at the bottom); the pitch follows the
    // tempo (3 px fast .. 9 px slow). Bars are laid along x, so each still covers exactly its own time span.
    private static void BuildBeatBars(DrawingContext g, Waveform wf, double w, double h, double px)
    {
        double mid = h / 2, usable = h - 2 * px, gap = px;
        Brush bHi = Solid(CHigh), bMi = Solid(CMid), bLo = Solid(CLow);
        double x = 0;
        while (x < w)
        {
            double bpm = Sample(wf.Tempo, x, w);
            double f = Math.Clamp((BeatFastBpm - bpm) / (BeatFastBpm - BeatSlowBpm), 0, 1);
            double pitch = bpm <= 0 ? 3 * px : BeatPitchFast + (BeatPitchSlow - BeatPitchFast) * f;
            double x1 = Math.Min(x + pitch, w);
            int from = Math.Clamp((int)(x / w * wf.Peaks.Length), 0, wf.Peaks.Length - 1);
            int to = Math.Clamp((int)Math.Ceiling(x1 / w * wf.Peaks.Length), from + 1, wf.Peaks.Length);
            int v = 0;
            for (int k = from; k < to; k++) v = Math.Max(v, wf.Peaks[k]);
            double bh = Math.Max(2 * px, v / 255.0 * usable);
            var (_, mi, hi) = Shares(wf, x, x1, w);
            double bw = Math.Max(px, x1 - x - gap), top = mid - bh / 2, hHi = bh * hi, hMi = bh * mi;
            Fill(g, bHi, x, top, bw, hHi);
            Fill(g, bMi, x, top + hHi, bw, hMi);
            Fill(g, bLo, x, top + hHi + hMi, bw, bh - hHi - hMi);
            x += pitch;
        }
    }

    // D. Inside the loudness silhouette, 8 rows (bass at the bottom) of cells coloured along the pink-violet-cyan ramp, brightness
    // = band level; tempo ticks on the bottom edge, opacity from the beat strength.
    private static void BuildSpectrum(DrawingContext g, Waveform wf, double w, double h, double px)
    {
        double mid = h / 2, usable = h - 2 * px, pitch = 3 * px, cellW = 2 * px;
        var rowBrush = new Brush[SpecRows][];
        for (int r = 0; r < SpecRows; r++)
        {
            double t = r / (double)(SpecRows - 1);
            var (a, b, f) = t < 0.5 ? (CLow, CMid, t * 2) : (CMid, CHigh, (t - 0.5) * 2);
            var c = (a.r + (b.r - a.r) * f, a.g + (b.g - a.g) * f, a.b + (b.b - a.b) * f);
            rowBrush[r] = new Brush[256];
            for (int q = 1; q < 256; q++) rowBrush[r][q] = Solid(c, SpecFloor + (1 - SpecFloor) * Math.Pow(q / 255.0, SpecGamma));   // gamma'd strength over the panel
        }
        int bars = Math.Max(1, (int)(w / pitch));
        int nb = wf.Peaks.Length;
        for (int i = 0; i < bars; i++)
        {
            double x = i * pitch, cx = x + cellW / 2;
            double hh = Math.Max(px, Sample(wf.Peaks, cx, w) / 255 * usable / 2);
            double cellH = 2 * hh / SpecRows;
            int bucket = Math.Clamp((int)(cx / w * nb), 0, nb - 1);
            for (int r = 0; r < SpecRows; r++)
            {
                var cb = rowBrush[r][wf.Spectrum[r * nb + bucket]];
                Fill(g, cb, x, mid + hh - (r + 1) * cellH, cellW, cellH);
            }
        }
        double phase = 0;
        for (double x = 0; x < w; x += px)
        {
            phase += px / Wavelength(Sample(wf.Tempo, x, w));
            if (phase < 1) continue;
            phase -= 1;
            double a = 0.8 * Sample(wf.Beat, x, w) / 255;
            if (a >= 0.03) Fill(g, Solid((0x40, 0x48, 0x58), a), x, h - TickHeightPx, TickWidthPx * Math.Max(px, 1), TickHeightPx);
        }
    }

    private void SeekTo(Point p)
    {
        if (ActualWidth <= 0) return;
        SetCurrentValue(ValueProperty, Math.Clamp(p.X / ActualWidth, 0, 1) * Maximum);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        SetCurrentValue(IsDraggingProperty, true);   // before Value: the VM only previews while dragging
        SeekTo(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && IsDragging) SeekTo(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        SeekTo(e.GetPosition(this));
        ReleaseMouseCapture();
        SetCurrentValue(IsDraggingProperty, false);  // the VM commits the seek on this edge
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // Capture stolen mid-drag (e.g. alt-tab): don't leave the VM stuck in dragging state.
        if (IsDragging) SetCurrentValue(IsDraggingProperty, false);
    }
}
