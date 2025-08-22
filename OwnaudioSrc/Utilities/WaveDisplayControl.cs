using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Reactive;
using System;
using System.Buffers;

namespace Ownaudio.Utilities
{
    /// <summary>
    /// A control for displaying audio waveforms with zoom and scroll capabilities.
    /// Provides different display styles and interactive features for audio visualization.
    /// </summary>
    public partial class WaveAvaloniaDisplay : Avalonia.Controls.Control
    {
        private float[]? _audioData;

        // Using ArrayPool for more efficient memory usage
        private readonly ArrayPool<Point> _pointPool = ArrayPool<Point>.Shared;
        private Point[] _pointCache;
        private int _pointCacheSize = 0;
        private int _pointCacheCapacity = 1000;

        private Pen _waveformPen;
        private Pen _playbackPen;

        // Reuse existing point arrays for different rendering styles
        private readonly Point[] _linePoints = new Point[2];

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
        #endregion

        /// <summary>
        /// Event triggered when the playback position changes (e.g., by user interaction).
        /// The event argument is the new position (0.0 to 1.0).
        /// </summary>
        public event EventHandler<double>? PlaybackPositionChanged; // Mark as nullable

        /// <summary>
        /// Initializes a new instance of the WaveAvaloniaDisplay class.
        /// Sets up default values and subscribes to property changes.
        /// </summary>
        /// <remarks>
        /// This constructor initializes the control with default settings and
        /// subscribes to relevant property changes to update the visual display.
        /// It uses ArrayPool for efficient memory management of the point cache.
        /// </remarks>
        public WaveAvaloniaDisplay()
        {
            MinHeight = 50;

            // Initialize point cache from pool instead of direct allocation
            _pointCache = _pointPool.Rent(_pointCacheCapacity);

            _waveformPen = new Pen(WaveformBrush);
            _playbackPen = new Pen(PlaybackPositionBrush, 2);

            // Subscribe to all relevant property changes to InvalidateVisual
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

            // Subscribe to ZoomFactor and ScrollOffset for visual invalidation
            this.GetObservable(ZoomFactorProperty).Subscribe(new AnonymousObserver<double>(_ => {
                ValidateScrollOffset(); // Ensure scroll offset is valid after zoom changes
                InvalidateVisual();
            }));
            this.GetObservable(ScrollOffsetProperty).Subscribe(new AnonymousObserver<double>(_ => InvalidateVisual()));
            this.GetObservable(PlaybackPositionProperty).Subscribe(new AnonymousObserver<double>(_ => InvalidateVisual()));

            this.PointerPressed += WaveformDisplay_PointerPressed;
            this.PointerMoved += WaveformDisplay_PointerMoved;
            this.PointerReleased += WaveformDisplay_PointerReleased;
            this.PointerWheelChanged += WaveformDisplay_PointerWheelChanged;
        }

        /// <summary>
        /// Sets the audio data to be displayed and resets zoom and scroll state.
        /// </summary>
        /// <param name="audioData">The audio sample data to display.</param>
        /// <remarks>
        /// This method updates the internal audio data reference, resets the zoom factor
        /// to 1.0 (showing the entire waveform), resets the scroll offset to 0.0 (starting position),
        /// and triggers a visual update of the control.
        /// </remarks>
        public void SetAudioData(float[] audioData)
        {
            _audioData = audioData;

            // When new data is set, reset zoom and scroll (optional, but good default)
            ZoomFactor = 1.0;
            ScrollOffset = 0.0;
            PlaybackPosition = 0.0;

            InvalidateVisual();
        }

        /// <summary>
        /// Renders the waveform based on the current display settings.
        /// </summary>
        /// <param name="context">The drawing context.</param>
        /// <remarks>
        /// This method handles the rendering of the waveform according to the selected display style.
        /// It calculates the visible portion of the audio data based on zoom and scroll state,
        /// ensures the point cache has sufficient capacity, renders the waveform using the
        /// appropriate style method, and draws the playback position indicator.
        /// 
        /// Memory usage is optimized by reusing point arrays from a shared pool and
        /// drawing the waveform in batches to reduce GPU draw calls.
        /// </remarks>
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

            double visibleSamplesFraction = 1.0 / ZoomFactor;
            int visibleSamples = (int)(totalSamples * visibleSamplesFraction);
            int startSampleIndex = (int)(totalSamples * ScrollOffset);

            // Adjust startSampleIndex to prevent going out of bounds
            if (startSampleIndex + visibleSamples > totalSamples)
            {
                startSampleIndex = totalSamples - visibleSamples;
            }
            if (startSampleIndex < 0) // Should not happen with clamping ScrollOffset, but for safety
                startSampleIndex = 0;

            int samplesPerPixel = Math.Max(1, visibleSamples / (int)width);

            // Ensure point cache is large enough
            int requiredSize = (int)width * 2; // Each pixel might draw 2 points (min/max or start/end of line)
            EnsurePointCacheCapacity(requiredSize);

            _pointCacheSize = 0;

            var displayStyle = DisplayStyle;

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

            // Draw lines in batches to reduce GPU draw calls
            // This loop iterates through the pointCache and draws pairs of points as lines.
            // _pointCacheSize holds the actual number of points added to the cache.
            // Each line requires 2 points, so total lines = _pointCacheSize / 2.
            for (int i = 0; i < _pointCacheSize; i += 2) // Increment by 2 points per line
            {
                // Ensure there are at least two points left to form a line
                if (i + 1 < _pointCacheSize)
                {
                    context.DrawLine(_waveformPen, _pointCache[i], _pointCache[i + 1]);
                }
            }


            double pixelPosition = ConvertAbsolutePositionToPixel(PlaybackPosition, width, startSampleIndex, visibleSamples, totalSamples);

            if (pixelPosition >= 0 && pixelPosition < width)
            {
                _linePoints[0] = new Point(pixelPosition, 0);
                _linePoints[1] = new Point(pixelPosition, height);
                context.DrawLine(_playbackPen, _linePoints[0], _linePoints[1]);
            }
        }

        /// <summary>
        /// Ensures the point cache has enough capacity for the required number of points.
        /// </summary>
        /// <param name="requiredCapacity">The minimum required capacity for the point cache.</param>
        /// <remarks>
        /// This method checks if the current point cache has sufficient capacity.
        /// If not, it returns the old buffer to the pool, calculates a new capacity,
        /// and rents a new buffer from the shared pool. This approach optimizes memory
        /// usage by reusing arrays instead of frequent allocations and deallocations.
        /// </remarks>
        private void EnsurePointCacheCapacity(int requiredCapacity)
        {
            if (_pointCacheCapacity < requiredCapacity)
            {
                // Return the old buffer to the pool
                _pointPool.Return(_pointCache, clearArray: false);

                // Calculate new capacity (round up to nearest multiple of 1000 for efficiency)
                _pointCacheCapacity = ((requiredCapacity + 999) / 1000) * 1000;
                if (_pointCacheCapacity < requiredCapacity) _pointCacheCapacity = requiredCapacity; // Ensure it's at least requiredCapacity
                if (_pointCacheCapacity == 0) _pointCacheCapacity = 1000; // Avoid zero capacity

                // Rent a new buffer from the pool
                _pointCache = _pointPool.Rent(_pointCacheCapacity);
            }
        }

        /// <summary>
        /// Renders the waveform using the MinMax display style.
        /// </summary>
        /// <param name="width">The width of the control in pixels.</param>
        /// <param name="centerY">The vertical center position of the waveform.</param>
        /// <param name="vScale">The vertical scale factor to apply to the waveform.</param>
        /// <param name="startSampleIndex">The starting sample index in the audio data.</param>
        /// <param name="samplesPerPixel">The number of audio samples represented by each horizontal pixel.</param>
        /// <remarks>
        /// This method renders the waveform by finding the minimum and maximum sample values
        /// for each pixel column, creating a classic oscilloscope-style waveform display.
        /// It populates the point cache with vertical line segments for each pixel column.
        /// Local variables are reused to reduce stack pressure and memory allocations.
        /// </remarks>
        private void RenderMinMaxStyle(double width, double centerY, float vScale, int startSampleIndex, int samplesPerPixel)
        {
            // Reuse local variables to reduce stack allocations
            int pixelStartSample, endSample;
            float minValue, maxValue, sample;

            for (int x = 0; x < width; x++)
            {
                pixelStartSample = startSampleIndex + (int)(x * samplesPerPixel);

                minValue = 1.0f;
                maxValue = -1.0f;

                endSample = Math.Min(pixelStartSample + samplesPerPixel, _audioData!.Length);
                for (int i = pixelStartSample; i < endSample; i++)
                {
                    // No need for 'if (i >= 0)' since startSampleIndex is clamped.
                    sample = _audioData[i];
                    if (sample < minValue) minValue = sample;
                    if (sample > maxValue) maxValue = sample;
                }

                _pointCache[_pointCacheSize++] = new Point(x, centerY + minValue * vScale);
                _pointCache[_pointCacheSize++] = new Point(x, centerY + maxValue * vScale);
            }
        }

        /// <summary>
        /// Renders the waveform using the Positive display style.
        /// </summary>
        /// <param name="width">The width of the control in pixels.</param>
        /// <param name="height">The height of the control in pixels.</param>
        /// <param name="vScale">The vertical scale factor to apply to the waveform.</param>
        /// <param name="startSampleIndex">The starting sample index in the audio data.</param>
        /// <param name="samplesPerPixel">The number of audio samples represented by each horizontal pixel.</param>
        /// <remarks>
        /// This method renders the waveform by finding the maximum absolute value
        /// for each pixel column, creating a half-wave rectified display that shows
        /// only the positive amplitude of the audio signal. The waveform is drawn
        /// from the bottom of the control upward.
        /// Local variables are reused to reduce stack pressure and memory allocations.
        /// </remarks>
        private void RenderPositiveStyle(double width, double height, float vScale, int startSampleIndex, int samplesPerPixel)
        {
            // Reuse local variables to reduce stack allocations
            int pixelStartSample, endSample;
            float maxPositive, sample;

            for (int x = 0; x < width; x++)
            {
                pixelStartSample = startSampleIndex + (int)(x * samplesPerPixel);

                maxPositive = 0;

                endSample = Math.Min(pixelStartSample + samplesPerPixel, _audioData!.Length);
                for (int i = pixelStartSample; i < endSample; i++)
                {
                    sample = Math.Abs(_audioData[i]);
                    if (sample > maxPositive) maxPositive = sample;
                }

                _pointCache[_pointCacheSize++] = new Point(x, height); // Base of the line
                _pointCache[_pointCacheSize++] = new Point(x, height - maxPositive * vScale); // Top of the line
            }
        }

        /// <summary>
        /// Renders the waveform using the RMS display style.
        /// </summary>
        /// <param name="width">The width of the control in pixels.</param>
        /// <param name="centerY">The vertical center position of the waveform.</param>
        /// <param name="vScale">The vertical scale factor to apply to the waveform.</param>
        /// <param name="startSampleIndex">The starting sample index in the audio data.</param>
        /// <param name="samplesPerPixel">The number of audio samples represented by each horizontal pixel.</param>
        /// <remarks>
        /// This method renders the waveform by calculating the root mean square (RMS) value
        /// for each pixel column, creating an energy-based visualization of the audio signal.
        /// The RMS values are rendered as vertical lines extending equally above and below the center line.
        /// Local variables are reused to reduce stack pressure and memory allocations.
        /// </remarks>
        private void RenderRmsStyle(double width, double centerY, float vScale, int startSampleIndex, int samplesPerPixel)
        {
            // Reuse local variables to reduce stack allocations
            int pixelStartSample, endSample, count;
            float sumSquares, sample, rms;

            for (int x = 0; x < width; x++)
            {
                pixelStartSample = startSampleIndex + (int)(x * samplesPerPixel);

                sumSquares = 0;
                count = 0;

                endSample = Math.Min(pixelStartSample + samplesPerPixel, _audioData!.Length);
                for (int i = pixelStartSample; i < endSample; i++)
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

        /// <summary>
        /// Handles pointer press events.
        /// </summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        /// <remarks>
        /// This method handles different mouse button interactions:
        /// - Left click sets the playback position and initiates playhead dragging
        /// - Middle or right click prepares for zoom/scroll operations
        /// When the playback position changes, it raises the PlaybackPositionChanged event.
        /// </remarks>
        private void WaveformDisplay_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            var point = e.GetPosition(this);

            if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed ||
                e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                // For middle/right click, capture for potential future dragging/panning (not implemented here)
                e.Pointer.Capture(this);
                return;
            }

            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                var position = CalculatePlaybackPositionFromMousePosition(point.X);
                PlaybackPosition = position; // Set StyledProperty
                PlaybackPositionChanged?.Invoke(this, position); // Raise event

                _isDraggingPlayhead = true;
                e.Pointer.Capture(this);
            }
        }

        /// <summary>
        /// Handles pointer move events.
        /// </summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        /// <remarks>
        /// This method updates the playback position during playhead drag operations.
        /// When the playback position changes, it raises the PlaybackPositionChanged event.
        /// </remarks>
        private void WaveformDisplay_PointerMoved(object sender, PointerEventArgs e)
        {
            if (_isDraggingPlayhead)
            {
                var point = e.GetPosition(this);
                var position = CalculatePlaybackPositionFromMousePosition(point.X);
                PlaybackPosition = position; // Set StyledProperty
                PlaybackPositionChanged?.Invoke(this, position); // Raise event
            }
        }

        /// <summary>
        /// Handles pointer release events.
        /// </summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        /// <remarks>
        /// This method ends drag operations and releases pointer capture.
        /// </remarks>
        private void WaveformDisplay_PointerReleased(object sender, PointerReleasedEventArgs e)
        {
            _isDraggingPlayhead = false;
            e.Pointer.Capture(null);
        }

        /// <summary>
        /// Handles mouse wheel events.
        /// </summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        /// <remarks>
        /// This method provides several interactive features:
        /// - CTRL+Wheel: Zoom in/out centered on the mouse position
        /// - SHIFT+Wheel or horizontal wheel: Horizontal scroll through the waveform
        /// - Regular wheel: Adjust vertical scale of the waveform
        /// Each operation updates the appropriate property and triggers a visual update.
        /// </remarks>
        private void WaveformDisplay_PointerWheelChanged(object sender, PointerWheelEventArgs e)
        {
            if (_audioData == null || _audioData.Length == 0) return;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var point = e.GetPosition(this);
                double pointRatio = point.X / Bounds.Width;

                double oldZoom = ZoomFactor;
                double zoomChange = e.Delta.Y > 0 ? 1.2 : 0.8;

                double newZoom = oldZoom * zoomChange;
                ZoomFactor = newZoom; // This setter already clamps

                // Adjust scroll offset to keep the mouse point fixed relative to content
                double visibleRange = 1.0 / oldZoom;
                double absPositionAtMouse = ScrollOffset + (pointRatio * visibleRange);

                double newVisibleRange = 1.0 / ZoomFactor; // Use the potentially clamped ZoomFactor

                // Calculate new scroll offset and clamp it
                ScrollOffset = absPositionAtMouse - (pointRatio * newVisibleRange);
                // The ScrollOffset setter's validate callback will handle the final clamping.
                // InvalidateVisual is called via the property subscriptions.
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.X) > 0)
            {
                double scrollDelta = (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * 0.01 / ZoomFactor;
                ScrollOffset = ScrollOffset - scrollDelta; // The setter's validate callback will handle the clamping.
                // InvalidateVisual is called via the property subscriptions.
            }
            else
            {
                double newScale = VerticalScale * (e.Delta.Y > 0 ? 1.1 : 0.9);
                VerticalScale = Math.Clamp(newScale, 0.1, 10.0);
            }
        }

        /// <summary>
        /// Calculates playback position from mouse X coordinate.
        /// </summary>
        /// <param name="x">X coordinate in control space.</param>
        /// <returns>Playback position between 0.0 and 1.0.</returns>
        /// <remarks>
        /// This method converts a pixel coordinate within the control to an absolute
        /// playback position in the audio data, taking into account the current
        /// zoom factor and scroll offset.
        /// </remarks>
        private double CalculatePlaybackPositionFromMousePosition(double x)
        {
            if (_audioData == null || _audioData.Length == 0)
                return 0.0;

            int totalSamples = _audioData.Length;
            double visibleSamplesFraction = 1.0 / ZoomFactor;
            int visibleSamples = (int)(totalSamples * visibleSamplesFraction);
            int startSampleIndex = (int)(totalSamples * ScrollOffset);

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
        /// <remarks>
        /// This method converts an absolute playback position in the audio data
        /// to a pixel coordinate within the control, taking into account the current
        /// zoom factor and scroll offset.
        /// </remarks>
        private double ConvertAbsolutePositionToPixel(double position, double width, int startSampleIndex, int visibleSamples, int totalSamples)
        {
            int samplePosition = (int)(position * totalSamples);

            double relativePos = (double)(samplePosition - startSampleIndex) / visibleSamples;

            return relativePos * width;
        }

        /// <summary>
        /// Ensures scroll offset is within valid range based on current zoom factor.
        /// This method is called automatically by the ZoomFactorProperty subscription.
        /// </summary>
        /// <remarks>
        /// This method restricts the scroll offset to ensure it remains within
        /// valid bounds based on the current zoom factor, preventing scrolling
        /// beyond the end of the waveform.
        /// </remarks>
        private void ValidateScrollOffset()
        {
            if (_audioData == null || _audioData.Length == 0)
            {
                // If no data, scrollOffset should be 0.0
                if (ScrollOffset != 0.0) ScrollOffset = 0.0;
                return;
            }

            double maxOffset = Math.Max(0.0, 1.0 - (1.0 / ZoomFactor));
            // This will trigger the ScrollOffsetProperty's validate callback, clamping it.
            // No need to directly set _scrollOffset, let the property system handle it.
            if (ScrollOffset > maxOffset)
            {
                ScrollOffset = maxOffset;
            }
        }

        /// <summary>
        /// Cleans up resources when the control is detached from the visual tree.
        /// </summary>
        /// <param name="e">The event arguments.</param>
        /// <remarks>
        /// This method ensures proper cleanup of resources when the control is removed:
        /// - Returns the point cache to the shared pool
        /// - Disposes of the pens used for rendering
        /// - Unsubscribes from event handlers to prevent memory leaks
        /// Proper resource cleanup is essential for memory optimization.
        /// </remarks>
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);

            if (_pointCache != null)
            {
                _pointPool.Return(_pointCache, clearArray: false);
                _pointCache = null!; // Use null-forgiving operator after returning
            }

            // Unsubscribe from events to prevent memory leaks
            this.PointerPressed -= WaveformDisplay_PointerPressed;
            this.PointerMoved -= WaveformDisplay_PointerMoved;
            this.PointerReleased -= WaveformDisplay_PointerReleased;
            this.PointerWheelChanged -= WaveformDisplay_PointerWheelChanged;

            // Note: The subscriptions to StyledProperties (GetObservable) are typically handled
            // by Avalonia's internal dispose mechanisms when the control is detached/disposed.
            // Manual unsubscription is usually not needed for GetObservable subscriptions
            // within the control's own lifetime managing its StyledProperties.
        }
    }
}
