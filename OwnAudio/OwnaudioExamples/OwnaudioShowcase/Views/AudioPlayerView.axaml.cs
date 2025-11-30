using Avalonia.Controls;
using Avalonia.Input;
using Avalonia;
using OwnaudioShowcase.ViewModels;

namespace OwnaudioShowcase.Views;

public partial class AudioPlayerView : UserControl
{
    private AudioPlayerViewModel? _viewModel;

    public AudioPlayerView()
    {
        InitializeComponent();

        // Attach this view to the ViewModel for direct UI updates (zero GC)
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is AudioPlayerViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.AttachView(this);
        }
    }

    /// <summary>
    /// Updates peak meters directly - NO binding, NO PropertyChanged, ZERO GC!
    /// Called from ViewModel timer callback.
    /// </summary>
    public void UpdatePeakMeters(float leftPeak, float rightPeak)
    {
        // Direct property assignment - no allocation!
        LeftPeakProgressBar.Value = leftPeak;
        RightPeakProgressBar.Value = rightPeak;
    }

    /// <summary>
    /// ✅ FIX #2: Updates playback state (position + peak meters) directly - ZERO PropertyChanged allocations!
    /// Called from ViewModel timer callback at 15-30 Hz.
    /// This completely bypasses MVVM binding for hot-path updates.
    /// </summary>
    public void UpdatePlaybackState(double position, float leftPeak, float rightPeak)
    {
        // Update position slider - direct property set, no binding
        PositionSlider.Value = position;

        // Update position text with optimized formatting
        PositionTextBlock.Text = FormatTime(position);

        // Update peak meters
        LeftPeakProgressBar.Value = leftPeak;
        RightPeakProgressBar.Value = rightPeak;
    }

    // Cache for time formatting to reduce allocations
    private readonly char[] _timeBuffer = new char[8]; // "mm:ss" format
    private int _lastFormattedSeconds = -1;
    private string? _cachedTimeString;

    /// <summary>
    /// Formats time in mm:ss format with minimal allocations.
    /// Uses caching to avoid string allocation if the second hasn't changed.
    /// </summary>
    private string FormatTime(double seconds)
    {
        int totalSeconds = (int)seconds;

        // Return cached string if second hasn't changed
        if (totalSeconds == _lastFormattedSeconds && _cachedTimeString != null)
        {
            return _cachedTimeString;
        }

        _lastFormattedSeconds = totalSeconds;

        int minutes = totalSeconds / 60;
        int secs = totalSeconds % 60;

        // Build string using char buffer (single allocation)
        _timeBuffer[0] = (char)('0' + minutes / 10);
        _timeBuffer[1] = (char)('0' + minutes % 10);
        _timeBuffer[2] = ':';
        _timeBuffer[3] = (char)('0' + secs / 10);
        _timeBuffer[4] = (char)('0' + secs % 10);

        _cachedTimeString = new string(_timeBuffer, 0, 5);
        return _cachedTimeString;
    }

    // ✅ FIX #1: Event handlers for user-initiated seeking

    /// <summary>
    /// Called when user starts dragging the position slider.
    /// Sets the dragging flag to prevent timer updates during seek.
    /// </summary>
    private void OnPositionSliderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.IsPositionSliderDragging = true;
        }
    }

    /// <summary>
    /// Called when user releases the position slider.
    /// Clears the dragging flag to resume timer updates.
    /// </summary>
    private void OnPositionSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.IsPositionSliderDragging = false;
        }
    }

    /// <summary>
    /// Called when the position slider value changes.
    /// If the user is dragging, this triggers a seek operation.
    /// ✅ FIX #1: Only seeks when user is actively dragging
    /// </summary>
    private void OnPositionSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_viewModel == null)
            return;

        // Only seek if user is dragging AND the Value property changed
        if (e.Property.Name == nameof(Slider.Value) &&
            _viewModel.IsPositionSliderDragging &&
            e.NewValue is double newValue)
        {
            _viewModel.SeekToPosition(newValue);
        }
    }
}
