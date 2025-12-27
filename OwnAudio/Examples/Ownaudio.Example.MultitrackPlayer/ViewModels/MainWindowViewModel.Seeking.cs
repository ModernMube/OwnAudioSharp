using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MultitrackPlayer.ViewModels;

/// <summary>
/// Seeking and timeline functionality for MainWindowViewModel.
/// Contains: Position tracking, seeking operations, and playback timer updates.
/// </summary>
public partial class MainWindowViewModel
{
    #region Properties

    /// <summary>
    /// Gets or sets the total duration of the longest track in seconds.
    /// </summary>
    [ObservableProperty]
    private double _trackDurationSeconds;

    /// <summary>
    /// Gets or sets the current playback position in seconds.
    /// </summary>
    [ObservableProperty]
    private double _currentPositionSeconds;

    /// <summary>
    /// Gets or sets the formatted position display string (e.g., "01:23 / 04:56").
    /// </summary>
    [ObservableProperty]
    private string _positionDisplay = "00:00 / 00:00";

    #endregion

    #region Timer

    /// <summary>
    /// Handles the playback timer tick event.
    /// Updates the current position display and checks for end of playback.
    /// OPTIMIZATION: Reduced property updates and cached string formatting.
    /// </summary>
    /// <param name="sender">The timer that raised the event.</param>
    /// <param name="e">Event arguments.</param>
    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (!IsPlaying || _isSeeking || _audioService.Mixer == null)
            return;

        try
        {
            // NEW - v2.4.0+: Use MasterClock.CurrentTimestamp instead of GetSyncGroupPosition
            double position = _audioService.Mixer.MasterClock.CurrentTimestamp;

            // Check if we reached the end of playback
            if (TrackDurationSeconds > 0 && position >= TrackDurationSeconds)
            {
                // Use the StopCommand's logic to ensure clean state transition
                if (StopCommand.CanExecute(null))
                {
                    StopCommand.Execute(null);
                }
                return;
            }

            // OPTIMIZATION: Only update UI if position changed significantly
            if (Math.Abs(position - CurrentPositionSeconds) > 0.1)
            {
                CurrentPositionSeconds = position;

                // Update display when position changed
                int currentMinutes = (int)(position / 60);
                int currentSeconds = (int)(position % 60);
                int totalMinutes = (int)(TrackDurationSeconds / 60);
                int totalSeconds = (int)(TrackDurationSeconds % 60);

                PositionDisplay = $"{currentMinutes:D2}:{currentSeconds:D2} / {totalMinutes:D2}:{totalSeconds:D2}";
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Timer tick error: {ex.Message}");
        }
    }

    #endregion

    #region Commands

    /// <summary>
    /// Begins a seek operation by pausing the playback timer.
    /// Called when the user starts dragging the position slider.
    /// </summary>
    [RelayCommand]
    private void BeginSeek()
    {
        if (!IsPlaying) return;
        _isSeeking = true;
        _playbackTimer.Stop();
    }

    /// <summary>
    /// Completes a seek operation by updating the playback position.
    /// Called when the user releases the position slider.
    /// </summary>
    /// <param name="position">The new position in seconds.</param>
    [RelayCommand]
    private void EndSeek(object? position)
    {
        if (!IsPlaying || position == null)
        {
            _isSeeking = false;
            return;
        }

        if (double.TryParse(position.ToString(), out double positionInSeconds))
        {
            try
            {
                // NEW - v2.4.0+: Seek master clock directly
                _audioService.Mixer?.MasterClock.SeekTo(positionInSeconds);
                CurrentPositionSeconds = positionInSeconds;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Seek error: {ex.Message}");
            }
        }

        _isSeeking = false;
        if (IsPlaying)
        {
            _playbackTimer.Start();
        }
    }

    #endregion
}
