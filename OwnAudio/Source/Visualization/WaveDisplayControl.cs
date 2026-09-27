using Avalonia;
using Avalonia.Input;
using Avalonia.Media;

namespace OwnaudioNET.Visualization
{
    /// <summary>
    /// Waveform view for Avalonia. Zoom + scroll + click/drag to seek. The wave is built into
    /// one geometry and kept until data, size or view changes, so a moving playhead is one line.
    /// </summary>
    public partial class WaveAvaloniaDisplay : Avalonia.Controls.Control
    {
        private float[]? _audioData;

        // the wave as drawn last time, null = rebuild on next render
        private StreamGeometry? _wave;
        private Size _waveSize;

        private Pen _waveformPen;
        private Pen _playbackPen;

        private bool _dragging;
        private bool _updatingProps;   // stops scroll<->playhead feedback loop

        #region styled props
        // wave color
        public static readonly StyledProperty<IBrush> WaveformBrushProperty =
            AvaloniaProperty.Register<WaveAvaloniaDisplay, IBrush>(nameof(WaveformBrush), Brushes.LimeGreen);

        // playhead color
        public static readonly StyledProperty<IBrush> PlaybackPositionBrushProperty =
            AvaloniaProperty.Register<WaveAvaloniaDisplay, IBrush>(nameof(PlaybackPositionBrush), Brushes.Red);

        // vertical gain of the drawing
        public static readonly StyledProperty<double> VerticalScaleProperty =
            AvaloniaProperty.Register<WaveAvaloniaDisplay, double>(nameof(VerticalScale), 1.0);

        // how the wave is drawn (minmax/positive/rms)
        public static readonly StyledProperty<WaveformDisplayStyle> DisplayStyleProperty =
            AvaloniaProperty.Register<WaveAvaloniaDisplay, WaveformDisplayStyle>(nameof(DisplayStyle), WaveformDisplayStyle.MinMax);

        // 1 = whole file, up to 50x
        public static readonly StyledProperty<double> ZoomFactorProperty =
            AvaloniaProperty.Register<WaveAvaloniaDisplay, double>(nameof(ZoomFactor), 1.0,
                validate: v => Math.Clamp(v, 1.0, 50.0) == v);

        // left edge of the view, 0..1
        public static readonly StyledProperty<double> ScrollOffsetProperty =
            AvaloniaProperty.Register<WaveAvaloniaDisplay, double>(nameof(ScrollOffset), 0.0,
                validate: v => Math.Clamp(v, 0.0, 1.0) == v);

        // playhead, 0..1
        public static readonly StyledProperty<double> PlaybackPositionProperty =
            AvaloniaProperty.Register<WaveAvaloniaDisplay, double>(nameof(PlaybackPosition), 0.0,
                validate: v => Math.Clamp(v, 0.0, 1.0) == v);

        // view chases the playhead when true
        public static readonly StyledProperty<bool> AutoFollowProperty =
            AvaloniaProperty.Register<WaveAvaloniaDisplay, bool>(nameof(AutoFollow), true);

        /// <summary>
        /// Waveform drawing styles.
        /// </summary>
        public enum WaveformDisplayStyle
        {
            // classic min/max column per pixel
            MinMax,
            // abs peak only, drawn from the bottom
            Positive,
            // energy view
            RMS
        }

        public IBrush WaveformBrush
        {
            get => GetValue(WaveformBrushProperty);
            set => SetValue(WaveformBrushProperty, value);
        }

        public IBrush PlaybackPositionBrush
        {
            get => GetValue(PlaybackPositionBrushProperty);
            set => SetValue(PlaybackPositionBrushProperty, value);
        }

        public double VerticalScale
        {
            get => GetValue(VerticalScaleProperty);
            set => SetValue(VerticalScaleProperty, value);
        }

        public WaveformDisplayStyle DisplayStyle
        {
            get => GetValue(DisplayStyleProperty);
            set => SetValue(DisplayStyleProperty, value);
        }

        public double ZoomFactor
        {
            get => GetValue(ZoomFactorProperty);
            set => SetValue(ZoomFactorProperty, value);
        }

        public double ScrollOffset
        {
            get => GetValue(ScrollOffsetProperty);
            set => SetValue(ScrollOffsetProperty, value);
        }

        public double PlaybackPosition
        {
            get => GetValue(PlaybackPositionProperty);
            set => SetValue(PlaybackPositionProperty, value);
        }

        public bool AutoFollow
        {
            get => GetValue(AutoFollowProperty);
            set => SetValue(AutoFollowProperty, value);
        }
        #endregion

        // fires on user seek, arg = new position 0..1
        public event EventHandler<double>? PlaybackPositionChanged;

        public WaveAvaloniaDisplay()
        {
            MinHeight = 50;
            _waveformPen = WavePen(WaveformBrush);
            _playbackPen = new Pen(PlaybackPositionBrush, 2);
        }

        // bevel, a miter join spikes out on the sharp turns of a zoomed-in wave
        static Pen WavePen(IBrush brush) => new Pen(brush, lineJoin: PenLineJoin.Bevel);

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            var p = change.Property;

            if (p == WaveformBrushProperty) _waveformPen = WavePen(WaveformBrush);
            else if (p == PlaybackPositionBrushProperty) _playbackPen = new Pen(PlaybackPositionBrush, 2);
            else if (p == ZoomFactorProperty) { ValidateScrollOffset(); _wave = null; }
            else if (p == PlaybackPositionProperty)
            {
                if (AutoFollow && !_dragging && !_updatingProps) UpdateAutoFollow();
            }
            else if (p == VerticalScaleProperty || p == DisplayStyleProperty || p == ScrollOffsetProperty) _wave = null;
            else if (p != AutoFollowProperty) return;

            InvalidateVisual();
        }

        // keeps the playhead in view: recenter once it passes mid-screen
        private void UpdateAutoFollow()
        {
            if (_audioData == null || _audioData.Length == 0 || _updatingProps) return;

            _updatingProps = true;
            try {
                double pos = PlaybackPosition;
                double range = Math.Clamp(1.0 / Math.Max(1.0, ZoomFactor), 0.02, 1.0);
                double maxOff = Math.Max(0.0, 1.0 - range);
                double off = Math.Clamp(ScrollOffset, 0.0, maxOff);
                double end = Math.Min(1.0, off + range);

                double target = off;
                if (pos > off + range * 0.5 && end < 0.999) target = pos - range * 0.5;
                else if (pos < off || pos > end) target = Math.Max(0.0, pos - range * 0.25);

                target = Math.Clamp(target, 0.0, maxOff);
                if (Math.Abs(target - off) > 0.001) ScrollOffset = target;
            }
            finally { _updatingProps = false; }
        }

        // hand over the samples to show; resets zoom/scroll/playhead
        public void SetAudioData(float[] audioData)
        {
            _audioData = audioData;
            _wave = null;
            ZoomFactor = 1.0;
            ScrollOffset = 0.0;
            PlaybackPosition = 0.0;
            InvalidateVisual();
        }

        static double SampleToPixel(double sample, double start, double spp) => (sample - start) / spp;
        static double PixelToSample(double px, double start, double spp) => start + px * spp;

        // wave + playhead line
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_audioData == null || _audioData.Length == 0) return;

            double width = Bounds.Width, height = Bounds.Height;

            int total = _audioData.Length;
            double spp = total / (Math.Max(1.0, ZoomFactor) * width);
            double start = Math.Clamp(total * ScrollOffset, 0.0, Math.Max(0.0, total - spp * width));

            if (_wave == null || _waveSize != Bounds.Size)
            {
                _wave = BuildWave(width, height, start, spp);
                _waveSize = Bounds.Size;
            }
            context.DrawGeometry(null, _waveformPen, _wave);

            double px = SampleToPixel(PlaybackPosition * total, start, spp);
            if ((px >= 0 && px <= width) || AutoFollow)
            {
                double x = Math.Clamp(px, 0, width);
                x = _playbackPen.Thickness % 2 != 0 ? Math.Floor(x) + 0.5 : Math.Round(x);
                context.DrawLine(_playbackPen, new Point(x, 0), new Point(x, height));
            }
        }

        // the whole visible wave as one figure set, only redone when the view moves
        StreamGeometry BuildWave(double width, double height, double start, double spp)
        {
            double centerY = height / 2;
            float vScale = (float)(centerY * VerticalScale);

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                if (spp < 1.0) RenderSamples(ctx, width, centerY, vScale, start, spp);
                else RenderBlocks(ctx, width, height, centerY, vScale, start, spp);
            }
            return geometry;
        }

        // zoomed in past 1 sample/px: connect the actual samples
        void RenderSamples(StreamGeometryContext ctx, double width, double centerY, float vScale, double start, double spp)
        {
            var data = _audioData!;
            int first = (int)Math.Max(0, Math.Floor(start));
            int last = (int)Math.Min(data.Length, Math.Ceiling(start + width * spp) + 1);
            if (first >= data.Length) return;

            ctx.BeginFigure(new Point(SampleToPixel(first, start, spp), centerY + data[first] * vScale), false);
            for (int i = first + 1; i < last; i++)
                ctx.LineTo(new Point(SampleToPixel(i, start, spp), centerY + data[i] * vScale));
            ctx.EndFigure(false);
        }

        static void Column(StreamGeometryContext ctx, double x, double y0, double y1)
        {
            ctx.BeginFigure(new Point(x, y0), false);
            ctx.LineTo(new Point(x, y1));
            ctx.EndFigure(false);
        }

        // one column per pixel, style decides what the column shows
        void RenderBlocks(StreamGeometryContext ctx, double width, double height, double centerY, float vScale, double start, double spp)
        {
            var data = _audioData!;
            var style = DisplayStyle;

            for (int x = 0; x < width; x++)
            {
                int s0 = (int)Math.Max(0, Math.Floor(PixelToSample(x, start, spp)));
                int s1 = (int)Math.Min(data.Length, Math.Ceiling(PixelToSample(x + 1, start, spp)));
                if (s0 >= data.Length) break;
                if (s1 <= s0) s1 = s0 + 1;

                float min = 1f, max = -1f, sum = 0f;
                for (int i = s0; i < s1; i++)
                {
                    float s = data[i];
                    if (s < min) min = s;
                    if (s > max) max = s;
                    sum += s * s;
                }

                if (style == WaveformDisplayStyle.MinMax)
                    Column(ctx, x, centerY + min * vScale, centerY + max * vScale);
                else if (style == WaveformDisplayStyle.Positive)
                    Column(ctx, x, height, height - Math.Max(Math.Abs(min), max) * vScale);
                else {
                    float rms = (float)Math.Sqrt(sum / (s1 - s0));
                    Column(ctx, x, centerY + rms * vScale, centerY - rms * vScale);
                }
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _dragging = true;
                SeekTo(e.GetPosition(this).X);
            }
            e.Pointer.Capture(this);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_dragging) SeekTo(e.GetPosition(this).X);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _dragging = false;
            e.Pointer.Capture(null);
        }

        // ctrl+wheel = zoom at cursor, shift/horiz wheel = scroll, plain wheel = vertical gain
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (_audioData == null || _audioData.Length == 0) return;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                bool follow = AutoFollow;
                if (follow) AutoFollow = false;

                double ratio = Math.Clamp(e.GetPosition(this).X / Math.Max(1.0, Bounds.Width), 0.0, 1.0);
                double oldZoom = ZoomFactor;
                double newZoom = Math.Clamp(oldZoom * (e.Delta.Y > 0 ? 1.2 : 0.8), 1.0, 50.0);

                double anchor = ScrollOffset + ratio / oldZoom;
                ZoomFactor = newZoom;
                ScrollOffset = Math.Clamp(anchor - ratio / newZoom, 0.0, Math.Max(0.0, 1.0 - 1.0 / newZoom));

                if (follow)
                {
                    AutoFollow = true;
                    if (!_dragging) UpdateAutoFollow();
                }
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.X != 0)
            {
                double delta = (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * 0.01 / Math.Max(1.0, ZoomFactor);
                double maxOff = Math.Max(0.0, 1.0 - 1.0 / Math.Max(1.0, ZoomFactor));
                ScrollOffset = Math.Clamp(ScrollOffset - delta, 0.0, maxOff);
            }
            else
            {
                VerticalScale = Math.Clamp(VerticalScale * (e.Delta.Y > 0 ? 1.1 : 0.9), 0.1, 10.0);
            }
        }

        void SeekTo(double x)
        {
            var pos = CalcPositionFromX(x);
            PlaybackPosition = pos;
            PlaybackPositionChanged?.Invoke(this, pos);
        }

        double CalcPositionFromX(double x)
        {
            if (_audioData == null || _audioData.Length == 0) return 0.0;
            return Math.Clamp(ScrollOffset + x / Bounds.Width / Math.Max(1.0, ZoomFactor), 0.0, 1.0);
        }

        void ValidateScrollOffset()
        {
            if (_audioData == null || _audioData.Length == 0)
            {
                if (ScrollOffset != 0.0) ScrollOffset = 0.0;
                return;
            }
            double maxOff = Math.Max(0.0, 1.0 - 1.0 / ZoomFactor);
            if (ScrollOffset > maxOff) ScrollOffset = maxOff;
        }
    }
}
