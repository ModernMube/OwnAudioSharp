using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Reactive;
using System.Buffers;

namespace OwnaudioNET.RustNext.Visualization
{
    /// <summary>
    /// A control for displaying audio waveforms with zoom and scroll capabilities.
    /// Provides different display styles and interactive features for audio visualization.
    /// </summary>
    public partial class WaveAvaloniaDisplay : Avalonia.Controls.Control
    {
        private float[]? _audioData;

        private readonly ArrayPool<Point> _pointPool = ArrayPool<Point>.Shared;
        private Point[] _pointCache;
        private int _pointCacheSize = 0;
        private int _pointCacheCapacity = 1000;

        private Pen _waveformPen;
        private Pen _playbackPen;

        private readonly Point[] _linePoints = new Point[2];

#pragma warning disable CS0414 // Field is assigned but its value is never used
        private bool _autoFollow = true;
#pragma warning restore CS0414
        private bool _isUserDragging = false;
        private bool _isUpdatingProperties = false; // Prevent recursive updates

        #region Display properties
        /// <summary>
        /// Defines the WaveformBrush dependency property.
        /// This brush is used to render the waveform.
        /// </summary>
        public static readonly StyledProperty<IBrush> WaveformBrushProperty =
            AvaloniaProperty.Register<WaveAvaloniaDisplay, IBrush>(
                nameof(WaveformBrush),
                Brushes.LimeGreen);

        /// <summary>
        /// Defines the PlaybackPositionBrush dependency property.
        /// This brush is used to render the playback position indicator.
        /// </summary>
        public static readonly StyledProperty<IBrush> PlaybackPositionBrushProperty =
            AvaloniaProperty.Register<WaveAvaloniaDisplay, IBrush>(
                nameof(PlaybackPositionBrush),
                Brushes.Red);

        /// <summary>
        /// Defines the VerticalScale dependency property.
        /// This property controls the vertical scaling of the waveform.
        /// </summary>
        public static readonly StyledProperty<double> VerticalScaleProperty =
            AvaloniaProperty.Register<WaveAvaloniaDisplay, double>(
                nameof(VerticalScale),
                1.0);

        /// <summary>
        /// Defines the DisplayStyle dependency property.
        /// This property determines how the waveform is visualized.
        /// </summary>
        public static readonly StyledProperty<WaveformDisplayStyle> DisplayStyleProperty =
            AvaloniaProperty.Register<WaveAvaloniaDisplay, WaveformDisplayStyle>(
                nameof(DisplayStyle),
                WaveformDisplayStyle.MinMax);

        /// <summary>
        /// Defines the ZoomFactor dependency property.
        /// </summary>
        public static readonly StyledProperty<double> ZoomFactorProperty =
            AvaloniaProperty.Register<WaveAvaloniaDisplay, double>(
                nameof(ZoomFactor),
                1.0, // default value
                validate: value => Math.Clamp(value, 1.0, 50.0) == value); // validation directly in property system

        /// <summary>
        /// Defines the ScrollOffset dependency property.
        /// </summary>
        public static readonly StyledProperty<double> ScrollOffsetProperty =
            AvaloniaProperty.Register<WaveAvaloniaDisplay, double>(
                nameof(ScrollOffset),
                0.0, // default value
                validate: value => Math.Clamp(value, 0.0, 1.0) == value);

        /// <summary>
        /// Defines the PlaybackPosition dependency property.
        /// </summary>
        public static readonly StyledProperty<double> PlaybackPositionProperty =
            AvaloniaProperty.Register<WaveAvaloniaDisplay, double>(
                nameof(PlaybackPosition),
                0.0, // default value
                validate: value => Math.Clamp(value, 0.0, 1.0) == value);

        /// <summary>
        /// Defines the AutoFollow dependency property.
        /// Controls whether the view automatically follows the playback position.
        /// </summary>
        public static readonly StyledProperty<bool> AutoFollowProperty =
            AvaloniaProperty.Register<WaveAvaloniaDisplay, bool>(
                nameof(AutoFollow),
                true);

        /// <summary>
        /// Enum defining different waveform visualization styles.
        /// </summary>
        public enum WaveformDisplayStyle
        {
            /// <summary>
            /// Shows both minimum and maximum values (classic waveform).
            /// </summary>
            MinMax,

            /// <summary>
            /// Shows only positive values (half-wave rectified).
            /// </summary>
            Positive,

            /// <summary>
            /// Shows RMS values (energy representation).
            /// </summary>
            RMS
        }

        /// <summary>
        /// Gets or sets the brush used to render the waveform.
        /// </summary>
        public IBrush WaveformBrush
        {
            get => GetValue(WaveformBrushProperty);
            set => SetValue(WaveformBrushProperty, value);
        }

        /// <summary>
        /// Gets or sets the brush used to render the playback position indicator.
        /// </summary>
        public IBrush PlaybackPositionBrush
        {
            get => GetValue(PlaybackPositionBrushProperty);
            set => SetValue(PlaybackPositionBrushProperty, value);
        }

        /// <summary>
        /// Gets or sets the vertical scale of the waveform.
        /// Higher values make the waveform taller.
        /// </summary>
        public double VerticalScale
        {
            get => GetValue(VerticalScaleProperty);
            set => SetValue(VerticalScaleProperty, value);
        }

        /// <summary>
        /// Gets or sets the display style of the waveform.
        /// </summary>
        public WaveformDisplayStyle DisplayStyle
        {
            get => GetValue(DisplayStyleProperty);
            set => SetValue(DisplayStyleProperty, value);
        }

        /// <summary>
        /// Gets or sets the zoom factor for the waveform.
        /// Value of 1.0 means no zoom (showing the entire waveform),
        /// larger values zoom in to show more detail.
        /// Valid range: 1.0 to 50.0.
        /// </summary>
        public double ZoomFactor
        {
            get => GetValue(ZoomFactorProperty);
            set => SetValue(ZoomFactorProperty, value);
        }

        /// <summary>
        /// Gets or sets the horizontal scroll offset (0.0 to 1.0).
        /// 0.0 represents the start of the audio data,
        /// 1.0 represents the end of the audio data.
        /// </summary>
        public double ScrollOffset
        {
            get => GetValue(ScrollOffsetProperty);
            set => SetValue(ScrollOffsetProperty, value);
        }

        /// <summary>
        /// Gets or sets the current playback position (0.0 to 1.0).
        /// 0.0 represents the start of the audio data,
        /// 1.0 represents the end of the audio data.
        /// </summary>
        public double PlaybackPosition
        {
            get => GetValue(PlaybackPositionProperty);
            set => SetValue(PlaybackPositionProperty, value);
        }

        /// <summary>
        /// Gets or sets whether the view should automatically follow the playback position.
        /// When enabled, the waveform scrolls to keep the playback indicator visible.
        /// </summary>
        public bool AutoFollow
        {
            get => GetValue(AutoFollowProperty);
            set => SetValue(AutoFollowProperty, value);
        }
        #endregion

        /// <summary>
        /// Event triggered when the playback position changes (e.g., by user interaction).
        /// The event argument is the new position (0.0 to 1.0).
        /// </summary>
        public event EventHandler<double>? PlaybackPositionChanged;

        /// <summary>
        /// Initializes a new instance of the WaveAvaloniaDisplay class.
        /// Sets up default values and subscribes to property changes.
        /// </summary>
        public WaveAvaloniaDisplay()
        {
            MinHeight = 50;
            _pointCache = _pointPool.Rent(_pointCacheCapacity);

            _waveformPen = new Pen(WaveformBrush);
            _playbackPen = new Pen(PlaybackPositionBrush, 2);

            this.GetObservable(WaveformBrushProperty).Subscribe(new AnonymousObserver<IBrush>(brush => {
                _waveformPen = new Pen(brush);
                InvalidateVisual();
            }));

            this.GetObservable(PlaybackPositionBrushProperty).Subscribe(new AnonymousObserver<IBrush>(brush => {
                _playbackPen = new Pen(brush, 2);
                InvalidateVisual();
            }));

            this.GetObservable(VerticalScaleProperty).Subscribe(new AnonymousObserver<double>(_ => InvalidateVisual()));
            this.GetObservable(DisplayStyleProperty).Subscribe(new AnonymousObserver<WaveformDisplayStyle>(_ => InvalidateVisual()));

            this.GetObservable(ZoomFactorProperty).Subscribe(new AnonymousObserver<double>(_ => {
                ValidateScrollOffset();
                InvalidateVisual();
            }));
            this.GetObservable(ScrollOffsetProperty).Subscribe(new AnonymousObserver<double>(_ => InvalidateVisual()));

            this.GetObservable(PlaybackPositionProperty).Subscribe(new AnonymousObserver<double>(_ => {
                if (AutoFollow && !_isUserDragging && !_isUpdatingProperties)
                {
                    UpdateAutoFollow();
                }
                InvalidateVisual();
            }));

            this.GetObservable(AutoFollowProperty).Subscribe(new AnonymousObserver<bool>(_ => InvalidateVisual()));

#nullable disable
            this.PointerPressed += WaveformDisplay_PointerPressed;
            this.PointerMoved += WaveformDisplay_PointerMoved;
            this.PointerReleased += WaveformDisplay_PointerReleased;
            this.PointerWheelChanged += WaveformDisplay_PointerWheelChanged;
#nullable restore
        }

        /// <summary>
        /// Updates scroll position to follow playback position automatically.
        /// The playback indicator stays in the center until it reaches the end of the visible area.
        /// </summary>
        private void UpdateAutoFollow()
        {
            if (_audioData == null || _audioData.Length == 0 || _isUpdatingProperties)
                return;

            try
            {
                _isUpdatingProperties = true;

                double playbackPos = PlaybackPosition;
                double zoomFactor = Math.Max(1.0, ZoomFactor);
                double visibleRange = 1.0 / zoomFactor;
                double currentScrollOffset = ScrollOffset;

                visibleRange = Math.Clamp(visibleRange, 0.02, 1.0);

                double maxScrollOffset = Math.Max(0.0, 1.0 - visibleRange);

                currentScrollOffset = Math.Clamp(currentScrollOffset, 0.0, maxScrollOffset);

                double visibleStart = currentScrollOffset;
                double visibleEnd = Math.Min(1.0, currentScrollOffset + visibleRange);

                double centerThreshold = visibleStart + (visibleRange * 0.5);

                bool needsScroll = false;
                double newScrollOffset = currentScrollOffset;

                if (playbackPos > centerThreshold && visibleEnd < 0.999) // Use 0.999 to avoid floating point precision issues
                {
                    newScrollOffset = playbackPos - (visibleRange * 0.5);
                    needsScroll = true;
                }
                else if (playbackPos < visibleStart)
                {
                    newScrollOffset = Math.Max(0.0, playbackPos - (visibleRange * 0.25));
                    needsScroll = true;
                }
                else if (playbackPos > visibleEnd)
                {
                    newScrollOffset = playbackPos - (visibleRange * 0.25);
                    needsScroll = true;
                }

                if (needsScroll)
                {
                    newScrollOffset = Math.Clamp(newScrollOffset, 0.0, maxScrollOffset);

                    if (Math.Abs(newScrollOffset - currentScrollOffset) > 0.001)
                    {
                        ScrollOffset = newScrollOffset;
                    }
                }
            }
            finally
            {
                _isUpdatingProperties = false;
            }
        }

        /// <summary>
        /// Sets the audio data to be displayed and resets zoom and scroll state.
        /// </summary>
        /// <param name="audioData">The audio sample data to display.</param>
        public void SetAudioData(float[] audioData)
        {
            _audioData = audioData;

            ZoomFactor = 1.0;
            ScrollOffset = 0.0;
            PlaybackPosition = 0.0;

            InvalidateVisual();
        }

        /// <summary>
        /// Renders the waveform based on the current display settings.
        /// </summary>
        /// <param name="context">The drawing context.</param>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static double SampleToPixel(double sampleIndex, double startSample, double samplesPerPixel)
        {
            return (sampleIndex - startSample) / samplesPerPixel;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static double PixelToSample(double pixel, double startSample, double samplesPerPixel)
        {
            return startSample + pixel * samplesPerPixel;
        }

        /// <summary>
        /// Renders the waveform based on the current display settings.
        /// </summary>
        /// <param name="context">The drawing context.</param>
        public override void Render(DrawingContext context)
        {
            base.Render(context);

            if (_audioData == null || _audioData.Length == 0)
                return;

            var bounds = Bounds;
            double width = bounds.Width;
            double height = bounds.Height;
            double centerY = height / 2;

            var vScale = (float)(height / 2 * VerticalScale);

            int totalSamples = _audioData.Length;
            double zoom = Math.Max(1.0, ZoomFactor);

            double startSample = totalSamples * ScrollOffset;
            double samplesPerPixel = totalSamples / (zoom * width);

            double maxStartSample = totalSamples - samplesPerPixel * width;
            if (startSample > maxStartSample) startSample = Math.Max(0, maxStartSample);
            if (startSample < 0) startSample = 0;

            int requiredSize = (int)width * 2;
            EnsurePointCacheCapacity(requiredSize);

            _pointCacheSize = 0;

            if (samplesPerPixel < 1.0)
            {
                RenderZoomedInLineStyle(centerY, vScale, startSample, samplesPerPixel);
            }
            else
            {
                var displayStyle = DisplayStyle;

                if (displayStyle == WaveformDisplayStyle.MinMax)
                    RenderMinMaxStyle(width, centerY, vScale, startSample, samplesPerPixel);
                else if (displayStyle == WaveformDisplayStyle.Positive)
                    RenderPositiveStyle(width, height, vScale, startSample, samplesPerPixel);
                else
                    RenderRmsStyle(width, centerY, vScale, startSample, samplesPerPixel);
            }

            if (_pointCacheSize > 1)
            {
                for (int i = 0; i < _pointCacheSize - 1; i += 2)
                {
                    context.DrawLine(_waveformPen, _pointCache[i], _pointCache[i + 1]);
                }
            }

            double pixelPosition = SampleToPixel(PlaybackPosition * totalSamples, startSample, samplesPerPixel);

            if ((pixelPosition >= 0 && pixelPosition <= width) || AutoFollow)
            {
                double visualPosition = Math.Clamp(pixelPosition, 0, width);

                double snappedX = (_playbackPen.Thickness % 2 != 0)
                    ? Math.Floor(visualPosition) + 0.5
                    : Math.Round(visualPosition);

                _linePoints[0] = new Point(snappedX, 0);
                _linePoints[1] = new Point(snappedX, height);
                context.DrawLine(_playbackPen, _linePoints[0], _linePoints[1]);
            }
        }

        /// <summary>
        /// Ensures the point cache has enough capacity for the required number of points.
        /// </summary>
        private void EnsurePointCacheCapacity(int requiredCapacity)
        {
            if (_pointCacheCapacity < requiredCapacity)
            {
                _pointPool.Return(_pointCache, clearArray: false);
                _pointCacheCapacity = ((requiredCapacity + 999) / 1000) * 1000;
                if (_pointCacheCapacity < requiredCapacity) _pointCacheCapacity = requiredCapacity;
                if (_pointCacheCapacity == 0) _pointCacheCapacity = 1000;
                _pointCache = _pointPool.Rent(_pointCacheCapacity);
            }
        }

        private void RenderZoomedInLineStyle(double centerY, float vScale, double startSample, double samplesPerPixel)
        {
            int dataLen = _audioData!.Length;
            int firstVisibleSample = (int)Math.Max(0, Math.Floor(startSample));
            int lastVisibleSample = (int)Math.Min(dataLen, Math.Ceiling(startSample + Bounds.Width * samplesPerPixel) + 1);

            if (firstVisibleSample >= dataLen) return;

            double prevX = SampleToPixel(firstVisibleSample, startSample, samplesPerPixel);
            double prevY = centerY + _audioData[firstVisibleSample] * vScale;

            for (int i = firstVisibleSample + 1; i < lastVisibleSample; i++)
            {
                double curX = SampleToPixel(i, startSample, samplesPerPixel);
                double curY = centerY + _audioData[i] * vScale;

                _pointCache[_pointCacheSize++] = new Point(prevX, prevY);
                _pointCache[_pointCacheSize++] = new Point(curX, curY);

                prevX = curX;
                prevY = curY;
            }
        }

        private void RenderMinMaxStyle(double width, double centerY, float vScale, double startSample, double samplesPerPixel)
        {
            int dataLen = _audioData!.Length;
            float minValue, maxValue, sample;

            for (int x = 0; x < width; x++)
            {
                int sampleStart = (int)Math.Max(0, Math.Floor(PixelToSample(x, startSample, samplesPerPixel)));
                int sampleEnd   = (int)Math.Min(dataLen, Math.Ceiling(PixelToSample(x + 1, startSample, samplesPerPixel)));
                if (sampleStart >= dataLen) break;
                if (sampleEnd <= sampleStart) sampleEnd = sampleStart + 1;

                minValue = 1.0f;
                maxValue = -1.0f;

                for (int i = sampleStart; i < sampleEnd; i++)
                {
                    sample = _audioData[i];
                    if (sample < minValue) minValue = sample;
                    if (sample > maxValue) maxValue = sample;
                }

                _pointCache[_pointCacheSize++] = new Point(x, centerY + minValue * vScale);
                _pointCache[_pointCacheSize++] = new Point(x, centerY + maxValue * vScale);
            }
        }

        private void RenderPositiveStyle(double width, double height, float vScale, double startSample, double samplesPerPixel)
        {
            int dataLen = _audioData!.Length;
            float maxPositive, sample;

            for (int x = 0; x < width; x++)
            {
                int sampleStart = (int)Math.Max(0, Math.Floor(PixelToSample(x, startSample, samplesPerPixel)));
                int sampleEnd   = (int)Math.Min(dataLen, Math.Ceiling(PixelToSample(x + 1, startSample, samplesPerPixel)));
                if (sampleStart >= dataLen) break;
                if (sampleEnd <= sampleStart) sampleEnd = sampleStart + 1;

                maxPositive = 0;

                for (int i = sampleStart; i < sampleEnd; i++)
                {
                    sample = Math.Abs(_audioData[i]);
                    if (sample > maxPositive) maxPositive = sample;
                }

                _pointCache[_pointCacheSize++] = new Point(x, height);
                _pointCache[_pointCacheSize++] = new Point(x, height - maxPositive * vScale);
            }
        }

        private void RenderRmsStyle(double width, double centerY, float vScale, double startSample, double samplesPerPixel)
        {
            int dataLen = _audioData!.Length;
            float sumSquares, sample, rms;
            int count;

            for (int x = 0; x < width; x++)
            {
                int sampleStart = (int)Math.Max(0, Math.Floor(PixelToSample(x, startSample, samplesPerPixel)));
                int sampleEnd   = (int)Math.Min(dataLen, Math.Ceiling(PixelToSample(x + 1, startSample, samplesPerPixel)));
                if (sampleStart >= dataLen) break;
                if (sampleEnd <= sampleStart) sampleEnd = sampleStart + 1;

                sumSquares = 0;
                count = 0;

                for (int i = sampleStart; i < sampleEnd; i++)
                {
                    sample = _audioData[i];
                    sumSquares += sample * sample;
                    count++;
                }

                rms = count > 0 ? (float)Math.Sqrt(sumSquares / count) : 0;

                _pointCache[_pointCacheSize++] = new Point(x, centerY + rms * vScale);
                _pointCache[_pointCacheSize++] = new Point(x, centerY - rms * vScale);
            }
        }

        private bool _isDraggingPlayhead = false;

        private void WaveformDisplay_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            var point = e.GetPosition(this);

            if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed ||
                e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                e.Pointer.Capture(this);
                return;
            }

            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _isUserDragging = true; // Disable auto-follow during user interaction
                var position = CalculatePlaybackPositionFromMousePosition(point.X);
                PlaybackPosition = position;
                PlaybackPositionChanged?.Invoke(this, position);

                _isDraggingPlayhead = true;
                e.Pointer.Capture(this);
            }
        }

        private void WaveformDisplay_PointerMoved(object sender, PointerEventArgs e)
        {
            if (_isDraggingPlayhead)
            {
                var point = e.GetPosition(this);
                var position = CalculatePlaybackPositionFromMousePosition(point.X);
                PlaybackPosition = position;
                PlaybackPositionChanged?.Invoke(this, position);
            }
        }

        private void WaveformDisplay_PointerReleased(object sender, PointerReleasedEventArgs e)
        {
            _isDraggingPlayhead = false;
            _isUserDragging = false; // Re-enable auto-follow
            e.Pointer.Capture(null);
        }

        private void WaveformDisplay_PointerWheelChanged(object sender, PointerWheelEventArgs e)
        {
            if (_audioData == null || _audioData.Length == 0) return;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                bool wasAutoFollow = AutoFollow;
                if (wasAutoFollow) AutoFollow = false;

                var point = e.GetPosition(this);
                double pointRatio = Math.Clamp(point.X / Math.Max(1.0, Bounds.Width), 0.0, 1.0);

                double oldZoom = ZoomFactor;
                double zoomChange = e.Delta.Y > 0 ? 1.2 : 0.8;

                double newZoom = Math.Clamp(oldZoom * zoomChange, 1.0, 50.0);

                double oldVisibleRange = 1.0 / oldZoom;
                double newVisibleRange = 1.0 / newZoom;
                double absPositionAtMouse = ScrollOffset + (pointRatio * oldVisibleRange);
                double newScrollOffset = absPositionAtMouse - (pointRatio * newVisibleRange);

                ZoomFactor = newZoom;

                double maxScrollOffset = Math.Max(0.0, 1.0 - newVisibleRange);
                ScrollOffset = Math.Clamp(newScrollOffset, 0.0, maxScrollOffset);

                if (wasAutoFollow)
                {
                    AutoFollow = true;
                    if (!_isUserDragging)
                    {
                        UpdateAutoFollow();
                    }
                }
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.X) > 0)
            {
                double scrollDelta = (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * 0.01 / Math.Max(1.0, ZoomFactor);
                double newScrollOffset = ScrollOffset - scrollDelta;
                double maxScrollOffset = Math.Max(0.0, 1.0 - (1.0 / Math.Max(1.0, ZoomFactor)));
                ScrollOffset = Math.Clamp(newScrollOffset, 0.0, maxScrollOffset);
            }
            else
            {
                double newScale = VerticalScale * (e.Delta.Y > 0 ? 1.1 : 0.9);
                VerticalScale = Math.Clamp(newScale, 0.1, 10.0);
            }
        }

        private double CalculatePlaybackPositionFromMousePosition(double x)
        {
            if (_audioData == null || _audioData.Length == 0)
                return 0.0;

            double absPos = ScrollOffset + (x / Bounds.Width) / Math.Max(1.0, ZoomFactor);
            return Math.Clamp(absPos, 0.0, 1.0);
        }

        private void ValidateScrollOffset()
        {
            if (_audioData == null || _audioData.Length == 0)
            {
                if (ScrollOffset != 0.0) ScrollOffset = 0.0;
                return;
            }

            double maxOffset = Math.Max(0.0, 1.0 - (1.0 / ZoomFactor));
            if (ScrollOffset > maxOffset)
            {
                ScrollOffset = maxOffset;
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);

            if (_pointCache != null)
            {
                _pointPool.Return(_pointCache, clearArray: false);
                _pointCache = null!;
            }

#nullable disable
            this.PointerPressed -= WaveformDisplay_PointerPressed;
            this.PointerMoved -= WaveformDisplay_PointerMoved;
            this.PointerReleased -= WaveformDisplay_PointerReleased;
            this.PointerWheelChanged -= WaveformDisplay_PointerWheelChanged;
#nullable restore
        }
    }
}
