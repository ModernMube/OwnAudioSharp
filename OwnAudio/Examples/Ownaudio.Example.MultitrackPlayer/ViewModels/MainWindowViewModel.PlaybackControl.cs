using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Sources;

namespace MultitrackPlayer.ViewModels;

/// <summary>
/// Playback control functionality for MainWindowViewModel.
/// Contains: Play, Pause, Stop commands and playback state management.
/// </summary>
public partial class MainWindowViewModel
{
    #region Commands

    /// <summary>
    /// Starts or resumes playback of all loaded tracks.
    /// Creates a synchronized playback group and applies all audio effects.
    /// OPTIMIZATION: No LINQ allocations - uses cached arrays.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        if (!IsInitialized || _audioService.Mixer == null)
            return;

        try
        {
            if (Tracks.Count == 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { StatusMessage = "No tracks loaded"; });
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => { StatusMessage = "Preparing audio..."; });

            // ENGINE: Audio service should run continuously to prevent cold start glitches

            // OPTIMIZATION: Update cached arrays if needed (zero allocation in steady state)
            if (_cacheNeedsUpdate || _cachedTrackArray.Length != Tracks.Count)
            {
                _cachedTrackArray = new TrackViewModel[Tracks.Count];
                Tracks.CopyTo(_cachedTrackArray, 0);
                _cacheNeedsUpdate = false;
            }

            // OPTIMIZATION: Handle solo and mute logic without LINQ
            bool hasSoloTracks = false;
            int validSourceCount = 0;

            // First pass: count solo tracks and valid sources
            for (int i = 0; i < _cachedTrackArray.Length; i++)
            {
                if (_cachedTrackArray[i].IsSolo)
                {
                    hasSoloTracks = true;
                }
                if (_cachedTrackArray[i].TrackInfo.Source != null)
                {
                    validSourceCount++;
                }
            }

            if (validSourceCount == 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { StatusMessage = "No valid sources to play."; });
                return;
            }

            // OPTIMIZATION: Allocate source array only once per play
            if (_cachedSourceArray.Length != validSourceCount)
            {
                _cachedSourceArray = new IAudioSource[validSourceCount];
            }

            // Second pass: apply volume/mute/solo and fill source array
            int sourceIndex = 0;
            for (int i = 0; i < _cachedTrackArray.Length; i++)
            {
                var track = _cachedTrackArray[i];
                if (track.TrackInfo.Source != null)
                {
                    float volumeMultiplier = track.Volume / 100.0f;
                    if (hasSoloTracks)
                    {
                        track.TrackInfo.Source.Volume = track.IsSolo ? volumeMultiplier : 0f;
                    }
                    else
                    {
                        track.TrackInfo.Source.Volume = track.IsMuted ? 0f : volumeMultiplier;
                    }

                    _cachedSourceArray[sourceIndex++] = track.TrackInfo.Source;
                }
            }

            // Capture start position on UI thread to ensure thread safety
            double startPosition = CurrentPositionSeconds;

            await Task.Run(() =>
            {
                // SYNC: MasterClock Timeline-Based Synchronization (v2.4.0+)

                // 1. Sync MasterClock to match the UI's transport position
                _audioService.Mixer.MasterClock.SeekTo(startPosition);

                // 2. Attach all tracks to the MasterClock
                for (int i = 0; i < _cachedSourceArray.Length; i++)
                {
                    if (_cachedSourceArray[i] is IMasterClockSource clockSource)
                    {
                        clockSource.AttachToClock(_audioService.Mixer.MasterClock);
                    }
                }

                // 2.5. CRITICAL: Seek all tracks to start position before pre-buffering
                System.Threading.Tasks.Parallel.ForEach(_cachedSourceArray, source =>
                {
                    source.Seek(startPosition);
                });

                // 3. CRITICAL: Pre-buffer all tracks in parallel to prevent noise
                System.Threading.Tasks.Parallel.ForEach(_cachedSourceArray, source =>
                {
                    source.Play();
                });

                // 4. CRITICAL: Add all tracks to mixer in parallel for atomic-like timing
                System.Threading.Tasks.Parallel.ForEach(_cachedSourceArray, source =>
                {
                    _audioService.Mixer.AddSource(source);
                });

                // 5. Apply SmartMaster effect to mixer output if enabled
                if (_smartMaster != null && IsSmartMasterEnabled)
                {
                    // Add SmartMaster as a master effect on the mixer
                    _audioService.Mixer.AddMasterEffect(_smartMaster);
                }
            });

            // OPTIMIZATION: Calculate longest track duration without LINQ
            double longestDuration = 0.0;
            for (int i = 0; i < _cachedSourceArray.Length; i++)
            {
                if (_cachedSourceArray[i] is FileSource fileSource)
                {
                    if (fileSource.Duration > longestDuration)
                    {
                        longestDuration = fileSource.Duration;
                    }
                }
            }
            TrackDurationSeconds = longestDuration;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsPlaying = true;
                StatusMessage = "Playing";
                _playbackTimer.Start();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { StatusMessage = $"Error starting playback: {ex.Message}"; });
        }
    }

    /// <summary>
    /// Determines whether the Play command can execute.
    /// </summary>
    /// <returns>True if the audio engine is initialized and not currently playing; otherwise, false.</returns>
    private bool CanPlay() => IsInitialized && !IsPlaying;

    /// <summary>
    /// Pauses the current playback.
    /// This currently functions as a stop, preserving the position for the next play.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand(CanExecute = nameof(CanPauseStop))]
    private async Task PauseAsync()
    {
        if (!IsPlaying) return;
        
        _playbackTimer.Stop();
        IsPlaying = false;
        await Dispatcher.UIThread.InvokeAsync(() => { StatusMessage = "Pausing..."; });

        // NEW - v2.4.0+: Pause all tracks individually
        await Task.Run(() =>
        {
            for (int i = 0; i < _cachedSourceArray.Length; i++)
            {
                try
                {
                    _cachedSourceArray[i].Pause();
                }
                catch
                {
                    // Ignore errors
                }
            }
        });

        await Dispatcher.UIThread.InvokeAsync(() => { StatusMessage = "Paused"; });
    }

    /// <summary>
    /// Stops the current playback and resets the position to the beginning.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand(CanExecute = nameof(CanPauseStop))]
    private async Task StopAsync()
    {
        if (!IsPlaying) return;

        _playbackTimer.Stop();
        IsPlaying = false;
        await Dispatcher.UIThread.InvokeAsync(() => { StatusMessage = "Stopping..."; });

        // NEW - v2.4.0+: Stop all tracks and reset master clock
        await Task.Run(() =>
        {
            // Stop all tracks
            for (int i = 0; i < _cachedSourceArray.Length; i++)
            {
                try
                {
                    _cachedSourceArray[i].Stop();
                }
                catch
                {
                    // Ignore errors
                }
            }

            // Reset master clock to beginning
            if (_audioService.Mixer != null)
            {
                _audioService.Mixer.MasterClock.SeekTo(0.0);
            }

            // Remove all tracks from mixer
            if (_audioService.Mixer != null)
            {
                for (int i = 0; i < _cachedSourceArray.Length; i++)
                {
                    try
                    {
                        _audioService.Mixer.RemoveSource(_cachedSourceArray[i].Id);
                    }
                    catch
                    {
                        // Ignore errors
                    }
                }
            }
            
            // CRITICAL: Remove SmartMaster from mixer BEFORE notifying it
            // This prevents it from processing empty buffers after tracks are removed
            if (_audioService.Mixer != null && _smartMaster != null)
            {
                _audioService.Mixer.RemoveMasterEffect(_smartMaster);
            }
            
            // CRITICAL: Notify SmartMaster that playback stopped
            // This clears filter states to prevent corruption on next Play
            if (_smartMaster != null)
            {
                _smartMaster.OnPlaybackStopped();
            }
        });

        // Reset UI state after stopping
        CurrentPositionSeconds = 0;
        TrackDurationSeconds = 0;
        PositionDisplay = "00:00 / 00:00";
        
        await Dispatcher.UIThread.InvokeAsync(() => { StatusMessage = "Stopped"; });
    }

    /// <summary>
    /// Determines whether the Pause and Stop commands can execute.
    /// </summary>
    /// <returns>True if playback is currently active; otherwise, false.</returns>
    private bool CanPauseStop() => IsPlaying;

    #endregion
}
