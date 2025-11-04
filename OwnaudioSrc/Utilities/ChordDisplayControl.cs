using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Reactive;
using System;
using System.Collections.Generic;
using System.Linq;
using Ownaudio.Utilities.OwnChordDetect.Analysis;

namespace Ownaudio.Utilities
{
    /// <summary>
    /// A control for displaying chord progressions with zoom and scroll capabilities.
    /// Shows chords as labeled rectangles along a timeline.
    /// </summary>
    public class ChordDisplay : Control
    {
        private List<TimedChord> _chords = new List<TimedChord>();
        private float _totalDuration = 0f;
        private bool _autoFollow = true;
        private bool _isUserDragging = false;

        // Pre-allocated drawing objects to avoid GC pressure
        private readonly Pen _chordBorderPen = new Pen(Brushes.Gray, 1);
        private readonly Rect _tempRect = new Rect();

        #region Dependency Properties

        /// <summary>
        /// Background brush for chord rectangles.
        /// </summary>
        public static readonly StyledProperty<IBrush> ChordBackgroundProperty =
            AvaloniaProperty.Register<ChordDisplay, IBrush>(
                nameof(ChordBackground),
                Brushes.LightBlue);

        /// <summary>
        /// Border brush for chord rectangles.
        /// </summary>
        public static readonly StyledProperty<IBrush> ChordBorderProperty =
            AvaloniaProperty.Register<ChordDisplay, IBrush>(
                nameof(ChordBorder),
                Brushes.DarkBlue);

        /// <summary>
        /// Text brush for chord labels.
        /// </summary>
        public static readonly StyledProperty<IBrush> ChordTextProperty =
            AvaloniaProperty.Register<ChordDisplay, IBrush>(
                nameof(ChordText),
                Brushes.Black);

        /// <summary>
        /// Font family for chord text.
        /// </summary>
        public static readonly StyledProperty<FontFamily> ChordFontFamilyProperty =
            AvaloniaProperty.Register<ChordDisplay, FontFamily>(
                nameof(ChordFontFamily),
                FontFamily.Default);

        /// <summary>
        /// Font size for chord text.
        /// </summary>
        public static readonly StyledProperty<double> ChordFontSizeProperty =
            AvaloniaProperty.Register<ChordDisplay, double>(
                nameof(ChordFontSize),
                14.0);

        /// <summary>
        /// Font weight for chord text.
        /// </summary>
        public static readonly StyledProperty<FontWeight> ChordFontWeightProperty =
            AvaloniaProperty.Register<ChordDisplay, FontWeight>(
                nameof(ChordFontWeight),
                FontWeight.Normal);

        /// <summary>
        /// Zoom factor for the timeline (1.0 = entire duration visible).
        /// </summary>
        public static readonly StyledProperty<double> ZoomFactorProperty =
            AvaloniaProperty.Register<ChordDisplay, double>(
                nameof(ZoomFactor),
                1.0,
                validate: value => Math.Clamp(value, 1.0, 50.0) == value);

        /// <summary>
        /// Horizontal scroll offset (0.0 to 1.0).
        /// </summary>
        public static readonly StyledProperty<double> ScrollOffsetProperty =
            AvaloniaProperty.Register<ChordDisplay, double>(
                nameof(ScrollOffset),
                0.0,
                validate: value => Math.Clamp(value, 0.0, 1.0) == value);

        /// <summary>
        /// Current playback position (0.0 to 1.0).
        /// </summary>
        public static readonly StyledProperty<double> PlaybackPositionProperty =
            AvaloniaProperty.Register<ChordDisplay, double>(
                nameof(PlaybackPosition),
                0.0,
                validate: value => Math.Clamp(value, 0.0, 1.0) == value);

        /// <summary>
        /// Playback position indicator brush.
        /// </summary>
        public static readonly StyledProperty<IBrush> PlaybackPositionBrushProperty =
            AvaloniaProperty.Register<ChordDisplay, IBrush>(
                nameof(PlaybackPositionBrush),
                Brushes.Red);

        /// <summary>
        /// Auto-follow playback position.
        /// </summary>
        public static readonly StyledProperty<bool> AutoFollowProperty =
            AvaloniaProperty.Register<ChordDisplay, bool>(
                nameof(AutoFollow),
                true);

        /// <summary>
        /// Minimum chord width in pixels to ensure readability.
        /// </summary>
        public static readonly StyledProperty<double> MinimumChordWidthProperty =
            AvaloniaProperty.Register<ChordDisplay, double>(
                nameof(MinimumChordWidth),
                50.0);

        /// <summary>
        /// Corner radius for chord rectangles.
        /// </summary>
        public static readonly StyledProperty<double> CornerRadiusProperty =
            AvaloniaProperty.Register<ChordDisplay, double>(
                nameof(CornerRadius),
                4.0);

        /// <summary>
        /// Gap between chord rectangles in pixels.
        /// </summary>
        public static readonly StyledProperty<double> ChordGapProperty =
            AvaloniaProperty.Register<ChordDisplay, double>(
                nameof(ChordGap),
                1.0);

        #endregion

        #region Properties

        public IBrush ChordBackground
        {
            get => GetValue(ChordBackgroundProperty);
            set => SetValue(ChordBackgroundProperty, value);
        }

        public IBrush ChordBorder
        {
            get => GetValue(ChordBorderProperty);
            set => SetValue(ChordBorderProperty, value);
        }

        public IBrush ChordText
        {
            get => GetValue(ChordTextProperty);
            set => SetValue(ChordTextProperty, value);
        }

        public FontFamily ChordFontFamily
        {
            get => GetValue(ChordFontFamilyProperty);
            set => SetValue(ChordFontFamilyProperty, value);
        }

        public double ChordFontSize
        {
            get => GetValue(ChordFontSizeProperty);
            set => SetValue(ChordFontSizeProperty, value);
        }

        public FontWeight ChordFontWeight
        {
            get => GetValue(ChordFontWeightProperty);
            set => SetValue(ChordFontWeightProperty, value);
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

        public IBrush PlaybackPositionBrush
        {
            get => GetValue(PlaybackPositionBrushProperty);
            set => SetValue(PlaybackPositionBrushProperty, value);
        }

        public bool AutoFollow
        {
            get => GetValue(AutoFollowProperty);
            set => SetValue(AutoFollowProperty, value);
        }

        public double MinimumChordWidth
        {
            get => GetValue(MinimumChordWidthProperty);
            set => SetValue(MinimumChordWidthProperty, value);
        }

        public double CornerRadius
        {
            get => GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        public double ChordGap
        {
            get => GetValue(ChordGapProperty);
            set => SetValue(ChordGapProperty, value);
        }

        #endregion

        /// <summary>
        /// Event triggered when playback position changes via user interaction.
        /// </summary>
        public event EventHandler<double>? PlaybackPositionChanged;

        public ChordDisplay()
        {
            MinHeight = 60;
            ChordBackground = Brushes.White;

            // Subscribe to property changes for visual updates
            this.GetObservable(ChordBackgroundProperty).Subscribe(new AnonymousObserver<IBrush>(_ => InvalidateVisual()));
            this.GetObservable(ChordBorderProperty).Subscribe(new AnonymousObserver<IBrush>(brush => {
                _chordBorderPen.Brush = brush;
                InvalidateVisual();
            }));
            this.GetObservable(ChordTextProperty).Subscribe(new AnonymousObserver<IBrush>(_ => InvalidateVisual()));
            this.GetObservable(ChordFontFamilyProperty).Subscribe(new AnonymousObserver<FontFamily>(_ => InvalidateVisual()));
            this.GetObservable(ChordFontSizeProperty).Subscribe(new AnonymousObserver<double>(_ => InvalidateVisual()));
            this.GetObservable(ChordFontWeightProperty).Subscribe(new AnonymousObserver<FontWeight>(_ => InvalidateVisual()));

            this.GetObservable(ZoomFactorProperty).Subscribe(new AnonymousObserver<double>(_ => {
                ValidateScrollOffset();
                InvalidateVisual();
            }));
            this.GetObservable(ScrollOffsetProperty).Subscribe(new AnonymousObserver<double>(_ => InvalidateVisual()));

            this.GetObservable(PlaybackPositionProperty).Subscribe(new AnonymousObserver<double>(_ => {
                if (AutoFollow && !_isUserDragging)
                {
                    UpdateAutoFollow();
                }
                InvalidateVisual();
            }));

            this.GetObservable(AutoFollowProperty).Subscribe(new AnonymousObserver<bool>(_ => InvalidateVisual()));
            this.GetObservable(MinimumChordWidthProperty).Subscribe(new AnonymousObserver<double>(_ => InvalidateVisual()));
            this.GetObservable(CornerRadiusProperty).Subscribe(new AnonymousObserver<double>(_ => InvalidateVisual()));
            this.GetObservable(ChordGapProperty).Subscribe(new AnonymousObserver<double>(_ => InvalidateVisual()));

            // Event handlers
            PointerPressed += ChordDisplay_PointerPressed;
            PointerMoved += ChordDisplay_PointerMoved;
            PointerReleased += ChordDisplay_PointerReleased;
            PointerWheelChanged += ChordDisplay_PointerWheelChanged;
        }

        /// <summary>
        /// Sets the chord data to display.
        /// </summary>
        /// <param name="chords">List of timed chords</param>
        public void SetChordData(List<TimedChord> chords)
        {
            _chords = chords ?? new List<TimedChord>();
            _totalDuration = _chords.Any() ? _chords.Max(c => c.EndTime) : 0f;

            // Reset zoom and scroll for new data
            ZoomFactor = 1.0;
            ScrollOffset = 0.0;
            PlaybackPosition = 0.0;

            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            if (!_chords.Any() || _totalDuration <= 0)
                return;

            var bounds = Bounds;
            double width = bounds.Width;
            double height = bounds.Height;

            // Calculate visible time range
            double visibleDuration = _totalDuration / ZoomFactor;
            double startTime = _totalDuration * ScrollOffset;
            double endTime = Math.Min(startTime + visibleDuration, _totalDuration);

            // Render chords
            RenderChords(context, width, height, startTime, endTime);

            // Render playback position
            RenderPlaybackPosition(context, width, height, startTime, visibleDuration);
        }

        private void RenderChords(DrawingContext context, double width, double height, double startTime, double endTime)
        {
            double visibleDuration = endTime - startTime;
            double pixelsPerSecond = width / visibleDuration;

            foreach (var chord in _chords)
            {
                // Skip chords outside visible range
                if (chord.EndTime < startTime || chord.StartTime > endTime)
                    continue;

                // Calculate chord position and size
                double chordStart = Math.Max(chord.StartTime - startTime, 0);
                double chordEnd = Math.Min(chord.EndTime - startTime, visibleDuration);
                double chordDuration = chordEnd - chordStart;

                double x = chordStart * pixelsPerSecond;
                double chordWidth = Math.Max(chordDuration * pixelsPerSecond, MinimumChordWidth);

                // Apply gap - reduce width and adjust position for gaps
                chordWidth = Math.Max(chordWidth - ChordGap, 1.0);
                x += ChordGap * 0.5;

                // Adjust x position if chord is expanded due to minimum width
                if (chordWidth < (chordDuration * pixelsPerSecond - ChordGap))
                {
                    double centerX = (chordStart + chordDuration / 2) * pixelsPerSecond;
                    x = centerX - chordWidth / 2;
                    x = Math.Max(ChordGap * 0.5, Math.Min(x, width - chordWidth - ChordGap * 0.5));
                }

                // Create chord rectangle with some padding from top/bottom
                double padding = 2.0;
                var rect = new Rect(x, padding, chordWidth, height - padding * 2);

                // Draw chord background with rounded corners
                if (CornerRadius > 0)
                {
                    context.FillRectangle(ChordBackground, rect, (float)CornerRadius);
                    context.DrawRectangle(_chordBorderPen, rect, (float)CornerRadius);
                }
                else
                {
                    context.FillRectangle(ChordBackground, rect);
                    context.DrawRectangle(_chordBorderPen, rect);
                }

                // Draw chord text
                RenderChordText(context, chord.ChordName, rect);
            }
        }

        private void RenderChordText(DrawingContext context, string chordName, Rect rect)
        {
            if (string.IsNullOrWhiteSpace(chordName))
                return;

            var typeface = new Typeface(ChordFontFamily, FontStyle.Normal, ChordFontWeight);
            var formattedText = new FormattedText(
                chordName,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                ChordFontSize,
                ChordText);

            // Center text in rectangle
            double textX = rect.X + (rect.Width - formattedText.Width) / 2;
            double textY = rect.Y + (rect.Height - formattedText.Height) / 2;

            // Ensure text stays within bounds
            textX = Math.Max(rect.X + 2, Math.Min(textX, rect.Right - formattedText.Width - 2));
            textY = Math.Max(rect.Y + 2, Math.Min(textY, rect.Bottom - formattedText.Height - 2));

            var textOrigin = new Point(textX, textY);
            context.DrawText(formattedText, textOrigin);
        }

        private void RenderPlaybackPosition(DrawingContext context, double width, double height, double startTime, double visibleDuration)
        {
            if (_totalDuration <= 0)
                return;

            double currentTime = PlaybackPosition * _totalDuration;
            double relativeTime = currentTime - startTime;

            // Check if playback position is in visible range or auto-follow is enabled
            if ((relativeTime >= 0 && relativeTime <= visibleDuration) || AutoFollow)
            {
                double pixelsPerSecond = width / visibleDuration;
                double x = Math.Clamp(relativeTime * pixelsPerSecond, 0, width);

                var playbackPen = new Pen(PlaybackPositionBrush, 2);
                context.DrawLine(playbackPen, new Point(x, 0), new Point(x, height));
            }
        }

        private void UpdateAutoFollow()
        {
            if (_totalDuration <= 0)
                return;

            double currentTime = PlaybackPosition * _totalDuration;
            double visibleDuration = _totalDuration / ZoomFactor;
            double currentScrollOffset = ScrollOffset;

            double visibleStart = currentScrollOffset * _totalDuration;
            double visibleEnd = visibleStart + visibleDuration;

            double centerThreshold = visibleStart + (visibleDuration * 0.5);

            bool needsScroll = false;
            double newScrollTime = visibleStart;

            if (currentTime > centerThreshold && visibleEnd < _totalDuration)
            {
                newScrollTime = currentTime - (visibleDuration * 0.5);
                needsScroll = true;
            }
            else if (currentTime < visibleStart)
            {
                newScrollTime = Math.Max(0, currentTime - (visibleDuration * 0.25));
                needsScroll = true;
            }
            else if (currentTime > visibleEnd)
            {
                newScrollTime = Math.Min(_totalDuration - visibleDuration, currentTime - (visibleDuration * 0.25));
                needsScroll = true;
            }

            if (needsScroll)
            {
                double maxScrollTime = Math.Max(0, _totalDuration - visibleDuration);
                newScrollTime = Math.Clamp(newScrollTime, 0, maxScrollTime);

                double newScrollOffset = _totalDuration > 0 ? newScrollTime / _totalDuration : 0;
                if (Math.Abs(newScrollOffset - currentScrollOffset) > 0.001)
                {
                    ScrollOffset = newScrollOffset;
                }
            }
        }

        private void ValidateScrollOffset()
        {
            if (_totalDuration <= 0)
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

        #region Event Handlers

        private bool _isDraggingPlayhead = false;

        private void ChordDisplay_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            var point = e.GetPosition(this);

            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _isUserDragging = true;
                var position = CalculatePlaybackPositionFromMousePosition(point.X);
                PlaybackPosition = position;
                PlaybackPositionChanged?.Invoke(this, position);

                _isDraggingPlayhead = true;
                e.Pointer.Capture(this);
            }
        }

        private void ChordDisplay_PointerMoved(object sender, PointerEventArgs e)
        {
            if (_isDraggingPlayhead)
            {
                var point = e.GetPosition(this);
                var position = CalculatePlaybackPositionFromMousePosition(point.X);
                PlaybackPosition = position;
                PlaybackPositionChanged?.Invoke(this, position);
            }
        }

        private void ChordDisplay_PointerReleased(object sender, PointerReleasedEventArgs e)
        {
            _isDraggingPlayhead = false;
            _isUserDragging = false;
            e.Pointer.Capture(null);
        }

        private void ChordDisplay_PointerWheelChanged(object sender, PointerWheelEventArgs e)
        {
            if (_totalDuration <= 0) return;

            bool wasAutoFollow = AutoFollow;
            if (wasAutoFollow) AutoFollow = false;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                // Zoom
                var point = e.GetPosition(this);
                double pointRatio = point.X / Bounds.Width;

                double oldZoom = ZoomFactor;
                double zoomChange = e.Delta.Y > 0 ? 1.2 : 0.8;
                double newZoom = oldZoom * zoomChange;
                ZoomFactor = newZoom;

                // Adjust scroll to zoom around mouse position
                double visibleRange = 1.0 / oldZoom;
                double timeAtMouse = ScrollOffset + (pointRatio * visibleRange);
                double newVisibleRange = 1.0 / ZoomFactor;
                ScrollOffset = timeAtMouse - (pointRatio * newVisibleRange);
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.X) > 0)
            {
                // Horizontal scroll
                double scrollDelta = (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * 0.01 / ZoomFactor;
                ScrollOffset = ScrollOffset - scrollDelta;
            }

            if (wasAutoFollow)
            {
                AutoFollow = true;
            }
        }

        private double CalculatePlaybackPositionFromMousePosition(double x)
        {
            if (_totalDuration <= 0)
                return 0.0;

            double visibleDuration = _totalDuration / ZoomFactor;
            double startTime = _totalDuration * ScrollOffset;

            double relativePosition = x / Bounds.Width;
            double timePosition = startTime + (relativePosition * visibleDuration);

            return Math.Clamp(timePosition / _totalDuration, 0.0, 1.0);
        }

        #endregion

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);

            PointerPressed -= ChordDisplay_PointerPressed;
            PointerMoved -= ChordDisplay_PointerMoved;
            PointerReleased -= ChordDisplay_PointerReleased;
            PointerWheelChanged -= ChordDisplay_PointerWheelChanged;
        }
    }
}
