using System;
using System.Collections.ObjectModel;
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
/// Track management functionality for MainWindowViewModel.
/// Contains: Track collection, add/remove track operations, and track-related commands.
/// </summary>
public partial class MainWindowViewModel
{
    #region Properties

    /// <summary>
    /// Gets the collection of audio tracks loaded in the player.
    /// </summary>
    public ObservableCollection<TrackViewModel> Tracks { get; }

    #endregion

    #region Public Methods

    /// <summary>
    /// Adds a new audio track to the player from the specified file path.
    /// Creates a FileSource with appropriate sample rate and channel conversion.
    /// </summary>
    /// <param name="filePath">The full path to the audio file to load.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AddTrackAsync(string filePath)
    {
        try
        {
            // HARD LIMIT: Check track count before adding
            if (Tracks.Count >= AudioConstants.MaxAudioSources)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    StatusMessage = $"Cannot add track: Maximum limit of {AudioConstants.MaxAudioSources} tracks reached.";
                });
                return;
            }

            TrackInfo? trackInfo = null;
            TrackViewModel? trackViewModel = null;

            // Create TrackInfo and ViewModel on UI thread
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusMessage = $"Loading {System.IO.Path.GetFileName(filePath)}...";
                trackInfo = new TrackInfo(filePath);
                trackViewModel = new TrackViewModel(trackInfo);
            });

            if (trackInfo == null || trackViewModel == null)
                return;

            // Capture current tempo and pitch values before background task
            float currentTempo = TempoPercent / 100.0f; // Convert to multiplier
            int currentPitch = PitchSemitones;

            // Get engine format for resampling
            int targetSampleRate = OwnaudioNET.OwnaudioNet.Engine?.Config.SampleRate ?? 48000;
            int targetChannels = OwnaudioNET.OwnaudioNet.Engine?.Config.Channels ?? 2;

            // Create FileSource in background thread to avoid blocking
            await Task.Run(() =>
            {
                // CRITICAL: Create FileSource with matching sample rate and channels
                var fileSource = new FileSource(filePath, 8192,
                    targetSampleRate: targetSampleRate,
                    targetChannels: targetChannels);
                trackInfo.Source = fileSource;

                // Set initial settings
                fileSource.Volume = trackInfo.Volume;
                fileSource.Tempo = currentTempo;
                fileSource.PitchShift = currentPitch;
                fileSource.SyncTolerance = 0.01; // 10ms tolerance for sync
            });

            // Add to collection on UI thread
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Tracks.Add(trackViewModel);
                StatusMessage = $"Loaded {trackInfo.FileName} ({Tracks.Count}/{AudioConstants.MaxAudioSources} tracks)";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusMessage = $"Error loading track: {ex.Message}";
            });
        }
    }

    #endregion

    #region Commands

    /// <summary>
    /// Removes the specified track from the player.
    /// Stops playback if currently playing, disposes the track, and restarts playback if other tracks remain.
    /// </summary>
    /// <param name="track">The track to remove.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand]
    private async Task RemoveTrackAsync(TrackViewModel track)
    {
        if (track == null)
            return;

        // Stop playback first if playing
        bool wasPlaying = IsPlaying;
        if (IsPlaying)
        {
            await StopAsync();
        }

        // Remove from UI first
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Tracks.Remove(track);
            StatusMessage = $"Removing track: {track.FileName}...";
        });

        // Dispose track
        track.Dispose();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            StatusMessage = $"Removed track: {track.FileName}";
        });

        if (wasPlaying && Tracks.Any())
        {
            await PlayAsync();
        }
    }

    #endregion
}
