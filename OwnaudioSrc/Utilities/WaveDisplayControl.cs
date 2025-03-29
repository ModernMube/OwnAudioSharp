using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Reactive;
using System;

namespace Ownaudio.Utilities
{
    /// <summary>
    /// A control for displaying audio waveforms with zoom and scroll capabilities.
    /// Provides different display styles and interactive features for audio visualization.
    /// </summary>
    public class WaveAvaloniaDisplay : Avalonia.Controls.Control
    {
        private float[] _audioData;

        private Avalonia.Point[] _pointCache;
        private int _pointCacheSize = 0;

        private double _zoomFactor = 1.0;
        private double _scrollOffset = 0.0;
        private double _playbackPosition = 0.0;

        
        private Pen _waveformPen;
        private Pen _playbackPen;

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
            get => _zoomFactor;
            set
            {
                if (value < 1.0)
                    value = 1.0;

                if (value > 50.0)
                    value = 50.0;

                if (_zoomFactor != value)
                {
                    _zoomFactor = value;

                    ValidateScrollOffset();
                    InvalidateVisual();
                }
            }
        }

        /// <summary>
        /// Gets or sets the horizontal scroll offset (0.0 to 1.0).
        /// 0.0 represents the start of the audio data,
        /// 1.0 represents the end of the audio data.
        /// </summary>
        public double ScrollOffset
        {
            get => _scrollOffset;
            set
            {
                _scrollOffset = Math.Clamp(value, 0.0, 1.0);
                InvalidateVisual();
            }
        }

        /// <summary>
        /// Gets or sets the current playback position (0.0 to 1.0).
        /// 0.0 represents the start of the audio data,
        /// 1.0 represents the end of the audio data.
        /// </summary>
        public double PlaybackPosition
        {
            get => _playbackPosition;
            set
            {
                _playbackPosition = Math.Clamp(value, 0.0, 1.0);
                InvalidateVisual();
            }
        }
        #endregion

        /// <summary>
        /// Event triggered when the playback position changes.
        /// The event argument is the new position (0.0 to 1.0).
        /// </summary>
        public event EventHandler<double> PlaybackPositionChanged;

#nullable disable
        /// <summary>
        /// Initializes a new instance of the WaveAvaloniaDisplay class.
        /// Sets up default values and subscribes to property changes.
        /// </summary>
        public WaveAvaloniaDisplay()
        {
            // Default preferred size
            MinHeight = 50;

            // Legyen egy kezdeti méretű pont gyorsítótár
            _pointCache = new Avalonia.Point[1000];

            // Tollak inicializálása
            _waveformPen = new Pen(WaveformBrush);
            _playbackPen = new Pen(PlaybackPositionBrush, 2);

            // Observer újrafelhasználás a memória takarékosság érdekében
            var visualInvalidator = new AnonymousObserver<object>(_ => InvalidateVisual());

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

            this.PointerPressed += WaveformDisplay_PointerPressed;
            this.PointerMoved += WaveformDisplay_PointerMoved;
            this.PointerReleased += WaveformDisplay_PointerReleased;
            this.PointerWheelChanged += WaveformDisplay_PointerWheelChanged;
        }
#nullable restore

        /// <summary>
        /// Sets the audio data to be displayed and resets zoom and scroll state.
        /// </summary>
        /// <param name="audioData">The audio sample data to display.</param>
        public void SetAudioData(float[] audioData)
        {
            _audioData = audioData;

            _zoomFactor = 1.0;
            _scrollOffset = 0.0;

            InvalidateVisual();
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

            double visibleSamplesFraction = 1.0 / _zoomFactor;
            int visibleSamples = (int)(totalSamples * visibleSamplesFraction);
            int startSampleIndex = (int)(totalSamples * _scrollOffset);

            if (startSampleIndex + visibleSamples > totalSamples)
            {
                startSampleIndex = totalSamples - visibleSamples;
            }

            if (startSampleIndex < 0)
                startSampleIndex = 0;

            int samplesPerPixel = Math.Max(1, visibleSamples / (int)width);

            // Make sure the point array is big enough
            int requiredSize = (int)width * 2;
            if (_pointCache.Length < requiredSize)
            {
                _pointCache = new Avalonia.Point[requiredSize];
            }

            _pointCacheSize = 0;

            var displayStyle = DisplayStyle;

            // Use precomputed buffers for different display modes
            if (displayStyle == WaveformDisplayStyle.MinMax)
            {
                RenderMinMaxStyle(width, centerY, vScale, startSampleIndex, samplesPerPixel);
            }
            else if (displayStyle == WaveformDisplayStyle.Positive)
            {
                RenderPositiveStyle(width, height, vScale, startSampleIndex, samplesPerPixel);
            }
            else // RMS
            {
                RenderRmsStyle(width, centerY, vScale, startSampleIndex, samplesPerPixel);
            }

            // Draw the lines in sections for better performance
            const int batchSize = 1000;
            for (int i = 0; i < _pointCacheSize; i += batchSize * 2)
            {
                int count = Math.Min(batchSize * 2, _pointCacheSize - i);
                if (count > 1)
                {
                    for (int j = 0; j < count; j += 2)
                    {
                        context.DrawLine(_waveformPen, _pointCache[i + j], _pointCache[i + j + 1]);
                    }
                }
            }

            // Draw playback position
            double pixelPosition = ConvertAbsolutePositionToPixel(_playbackPosition, width, startSampleIndex, visibleSamples, totalSamples);

            if (pixelPosition >= 0 && pixelPosition < width)
            {
                context.DrawLine(_playbackPen, new Avalonia.Point(pixelPosition, 0), new Avalonia.Point(pixelPosition, height));
            }
        }

        private void RenderMinMaxStyle(double width, double centerY, float vScale, int startSampleIndex, int samplesPerPixel)
        {
            for (int x = 0; x < width; x++)
            {
                int pixelStartSample = startSampleIndex + (int)(x * samplesPerPixel);

                float minValue = 1.0f;
                float maxValue = -1.0f;

                // Performance optimized loop
                int endSample = Math.Min(pixelStartSample + samplesPerPixel, _audioData.Length);
                for (int i = pixelStartSample; i < endSample; i++)
                {
                    if (i >= 0)
                    {
                        float sample = _audioData[i];
                        if (sample < minValue) minValue = sample;
                        if (sample > maxValue) maxValue = sample;
                    }
                }

                _pointCache[_pointCacheSize++] = new Avalonia.Point(x, centerY + minValue * vScale);
                _pointCache[_pointCacheSize++] = new Avalonia.Point(x, centerY + maxValue * vScale);
            }
        }

        private void RenderPositiveStyle(double width, double height, float vScale, int startSampleIndex, int samplesPerPixel)
        {
            for (int x = 0; x < width; x++)
            {
                int pixelStartSample = startSampleIndex + (int)(x * samplesPerPixel);

                float maxPositive = 0;

                int endSample = Math.Min(pixelStartSample + samplesPerPixel, _audioData.Length);
                for (int i = pixelStartSample; i < endSample; i++)
                {
                    if (i >= 0)
                    {
                        float sample = Math.Abs(_audioData[i]);
                        if (sample > maxPositive) maxPositive = sample;
                    }
                }

                _pointCache[_pointCacheSize++] = new Avalonia.Point(x, height);
                _pointCache[_pointCacheSize++] = new Avalonia.Point(x, height - maxPositive * vScale);
            }
        }

        private void RenderRmsStyle(double width, double centerY, float vScale, int startSampleIndex, int samplesPerPixel)
        {
            for (int x = 0; x < width; x++)
            {
                int pixelStartSample = startSampleIndex + (int)(x * samplesPerPixel);

                float sumSquares = 0;
                int count = 0;

                int endSample = Math.Min(pixelStartSample + samplesPerPixel, _audioData.Length);
                for (int i = pixelStartSample; i < endSample; i++)
                {
                    if (i >= 0)
                    {
                        float sample = _audioData[i];
                        sumSquares += sample * sample;
                        count++;
                    }
                }

                float rms = count > 0 ? (float)Math.Sqrt(sumSquares / count) : 0;

                _pointCache[_pointCacheSize++] = new Avalonia.Point(x, centerY + rms * vScale);
                _pointCache[_pointCacheSize++] = new Avalonia.Point(x, centerY - rms * vScale);
            }
        }

        // Mouse handling for setting playback position
        private bool _isDraggingPlayhead = false;

        /// <summary>
        /// Handles pointer press events.
        /// Left click sets playback position, middle or right click prepares for zoom/scroll operations.
        /// </summary>
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
                var position = CalculatePlaybackPositionFromMousePosition(point.X);
                PlaybackPosition = position;
                PlaybackPositionChanged?.Invoke(this, position);

                _isDraggingPlayhead = true;
                e.Pointer.Capture(this);
            }
        }

        /// <summary>
        /// Handles pointer move events.
        /// Updates playback position during drag operations.
        /// </summary>
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

        /// <summary>
        /// Handles pointer release events.
        /// Ends drag operations.
        /// </summary>
        private void WaveformDisplay_PointerReleased(object sender, PointerReleasedEventArgs e)
        {
            _isDraggingPlayhead = false;
            e.Pointer.Capture(null);
        }

        /// <summary>
        /// Handles mouse wheel events.
        /// CTRL+Wheel: Zoom in/out
        /// SHIFT+Wheel or horizontal wheel: Horizontal scroll
        /// Regular wheel: Adjust vertical scale
        /// </summary>
        private void WaveformDisplay_PointerWheelChanged(object sender, PointerWheelEventArgs e)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var point = e.GetPosition(this);
                double pointRatio = point.X / Bounds.Width;

                double oldZoom = _zoomFactor;
                double zoomChange = e.Delta.Y > 0 ? 1.2 : 0.8;

                double newZoom = _zoomFactor * zoomChange;
                ZoomFactor = newZoom;

                if (newZoom != 1.0)
                {
                    double visibleRange = 1.0 / oldZoom;
                    double absPosition = _scrollOffset + (pointRatio * visibleRange);

                    double newVisibleRange = 1.0 / _zoomFactor;

                    _scrollOffset = Math.Clamp(absPosition - (pointRatio * newVisibleRange), 0.0, 1.0 - newVisibleRange);

                    InvalidateVisual();
                }
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.X) > 0)
            {
                double scrollDelta = (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * 0.01 / _zoomFactor;
                ScrollOffset = Math.Clamp(_scrollOffset - scrollDelta, 0.0, 1.0 - (1.0 / _zoomFactor));
            }
            else
            {
                double newScale = VerticalScale * (e.Delta.Y > 0 ? 1.1 : 0.9);
                VerticalScale = Math.Clamp(newScale, 0.1, 10.0);
            }
        }

        // Helper methods
        /// <summary>
        /// Calculates playback position from mouse X coordinate.
        /// </summary>
        /// <param name="x">X coordinate in control space.</param>
        /// <returns>Playback position between 0.0 and 1.0.</returns>
        private double CalculatePlaybackPositionFromMousePosition(double x)
        {
            if (_audioData == null || _audioData.Length == 0)
                return 0.0;

            int totalSamples = _audioData.Length;
            double visibleSamplesFraction = 1.0 / _zoomFactor;
            int visibleSamples = (int)(totalSamples * visibleSamplesFraction);
            int startSampleIndex = (int)(totalSamples * _scrollOffset);

            double relativePosition = x / Bounds.Width;

            int samplePosition = startSampleIndex + (int)(relativePosition * visibleSamples);

            return Math.Clamp((double)samplePosition / totalSamples, 0.0, 1.0);
        }

        /// <summary>
        /// Converts absolute position (0.0-1.0) to pixel coordinate.
        /// </summary>
        /// <param name="position">Absolute position (0.0-1.0).</param>
        /// <param name="width">Control width in pixels.</param>
        /// <param name="startSampleIndex">Start sample index of visible range.</param>
        /// <param name="visibleSamples">Number of visible samples.</param>
        /// <param name="totalSamples">Total number of samples.</param>
        /// <returns>X coordinate in control space.</returns>
        private double ConvertAbsolutePositionToPixel(double position, double width, int startSampleIndex, int visibleSamples, int totalSamples)
        {
            int samplePosition = (int)(position * totalSamples);

            double relativePos = (double)(samplePosition - startSampleIndex) / visibleSamples;

            return relativePos * width;
        }

        /// <summary>
        /// Ensures scroll offset is within valid range based on current zoom factor.
        /// </summary>
        private void ValidateScrollOffset()
        {
            double maxOffset = Math.Max(0.0, 1.0 - (1.0 / _zoomFactor));
            _scrollOffset = Math.Clamp(_scrollOffset, 0.0, maxOffset);
        }
    }
}
