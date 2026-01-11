using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultitrackPlayer.Models;
using OwnaudioNET.Synchronization;
using OwnaudioNET.Core;
using OwnaudioNET.Sources;

namespace MultitrackPlayer.ViewModels;

/// <summary>
/// Audio effects functionality for MainWindowViewModel.
/// Contains: Master volume, tempo, pitch shift controls and reset functionality.
/// </summary>
public partial class MainWindowViewModel
{
    #region Properties

    /// <summary>
    /// Gets or sets the master volume level (0-100).
    /// </summary>
    [ObservableProperty]
    private float _masterVolume = 100.0f;

    /// <summary>
    /// Called when the master volume property changes.
    /// Converts the 0-100 range to 0.0-1.0 for the audio engine.
    /// </summary>
    /// <param name="value">The new master volume value.</param>
    partial void OnMasterVolumeChanged(float value)
    {
        if (_audioService.Mixer != null)
            _audioService.Mixer.MasterVolume = value / 100.0f; // Convert 0-100 to 0.0-1.0
    }

    /// <summary>
    /// Gets or sets the tempo percentage (100% = normal speed).
    /// NOTE: This property is updated by code-behind AFTER PointerReleased, not during drag.
    /// </summary>
    [ObservableProperty]
    private float _tempoPercent = 100.0f;

    /// <summary>
    /// Called when the tempo percentage changes programmatically (NOT during drag).
    /// This is only triggered by code-behind after PointerReleased or by Reset button.
    /// </summary>
    /// <param name="value">The new tempo percentage.</param>
    partial void OnTempoPercentChanged(float value)
    {
        // This is now called ONLY after PointerReleased or programmatic changes
        // No debounce needed - the change is already final
        // NOTE: ApplyTempoChange() is called directly from code-behind, not here
    }

    /// <summary>
    /// Gets or sets the pitch shift in semitones (0 = original pitch).
    /// </summary>
    [ObservableProperty]
    private int _pitchSemitones = 0;

    /// <summary>
    /// Called when the pitch semitones property changes programmatically.
    /// </summary>
    /// <param name="value">The new pitch shift value in semitones.</param>
    partial void OnPitchSemitonesChanged(int value)
    {
        // Final change after PointerReleased, no debounce needed
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Applies tempo change to all tracks.
    /// PUBLIC: Called from code-behind on PointerReleased.
    /// </summary>
    public async void ApplyTempoChange(float tempoPercent)
    {
        // HARD LIMIT: Clamp tempo to valid range (80% - 120%)
        float minPercent = AudioConstants.MinTempo * 100.0f; // 80%
        float maxPercent = AudioConstants.MaxTempo * 100.0f; // 120%
        float clampedPercent = Math.Clamp(tempoPercent, minPercent, maxPercent);

        // Update UI if clamped
        if (Math.Abs(clampedPercent - tempoPercent) > 0.01f)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                TempoPercent = clampedPercent;
                StatusMessage = $"Tempo limited to {minPercent:F0}%-{maxPercent:F0}% range";
            });
        }

        // Convert percentage to multiplier (100% = 1.0x)
        float tempoMultiplier = clampedPercent / 100.0f;

        // OPTIMIZED: Use SetTempoSmooth() and cache to array to avoid collection issues
        var trackArray = Tracks.ToArray();

        await Task.Run(() =>
        {
            // OPTIMIZED: Parallel processing for tracks to improve performance
            System.Threading.Tasks.Parallel.ForEach(trackArray, track =>
            {
                if (track.TrackInfo.Source is FileSource fileSource)
                {
                    // Use smooth tempo change without buffer clearing
                    fileSource.SetTempoSmooth(tempoMultiplier);
                }
            });
        });
    }

    /// <summary>
    /// Applies pitch change to all tracks.
    /// PUBLIC: Called from code-behind on PointerReleased.
    /// </summary>
    public async void ApplyPitchChange(int pitchSemitones)
    {
        // Cache to array for parallel processing
        var trackArray = Tracks.ToArray();

        await Task.Run(() =>
        {
            // OPTIMIZED: Parallel processing for improved performance
            System.Threading.Tasks.Parallel.ForEach(trackArray, track =>
            {
                if (track.TrackInfo.Source is FileSource fileSource)
                {
                    // Use smooth pitch change without buffer clearing
                    fileSource.SetPitchSmooth(pitchSemitones);
                }
            });
        });
    }

    #endregion

    #region Commands

    /// <summary>
    /// Resets all audio control parameters to their default values.
    /// Sets master volume to 100%, tempo to 100%, and pitch shift to 0 semitones.
    /// CRITICAL: Triggers full track resynchronization to prevent drift after extreme tempo changes.
    /// </summary>
    [RelayCommand]
    private async void ResetControls()
    {
        //MasterVolume = 100.0f;

        // Update UI properties
        TempoPercent = 100.0f;
        PitchSemitones = 0;

        // CRITICAL FIX: Resynchronize all tracks after buffer clearing
        bool wasPlaying = IsPlaying;
        double currentPosition = CurrentPositionSeconds;

        await Task.Run(() =>
        {
            // Cache tracks to array for thread-safe iteration
            var trackArray = Tracks.ToArray();

            // Step 1: Reset tempo and pitch on all tracks (this clears SoundTouch buffers)
            System.Threading.Tasks.Parallel.ForEach(trackArray, track =>
            {
                if (track.TrackInfo.Source is FileSource fileSource)
                {
                    fileSource.Tempo = 1.0f;  // 100% = 1.0x multiplier
                    fileSource.PitchShift = 0;
                }
            });

            // Step 2: If playing, resync all tracks to current position to rebuild buffers in sync
            if (wasPlaying && _audioService.Mixer != null)
            {
                // Resync MasterClock
                _audioService.Mixer.MasterClock.SeekTo(currentPosition);

                // Seek all tracks to current position to force buffer rebuild
                System.Threading.Tasks.Parallel.ForEach(trackArray, track =>
                {
                    if (track.TrackInfo.Source is FileSource fileSource)
                    {
                        fileSource.Seek(currentPosition);
                    }
                });
            }
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            StatusMessage = "Reset controls to default values";
        });
    }

    #endregion
}
