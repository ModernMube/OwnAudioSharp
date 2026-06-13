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

            if (_cacheNeedsUpdate || _cachedTrackArray.Length != Tracks.Count)
            {
                _cachedTrackArray = new TrackViewModel[Tracks.Count];
                Tracks.CopyTo(_cachedTrackArray, 0);
                _cacheNeedsUpdate = false;
            }

            bool hasSoloTracks = false;
            int validSourceCount = 0;

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

            if (_cachedSourceArray.Length != validSourceCount)
            {
                _cachedSourceArray = new IAudioSource[validSourceCount];
            }

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
                _audioService.Mixer.MasterClock.SeekTo(startPosition);

                for (int i = 0; i < _cachedSourceArray.Length; i++)
                {
                    if (_cachedSourceArray[i] is IMasterClockSource clockSource)
                    {
                        clockSource.AttachToClock(_audioService.Mixer.MasterClock);
                    }
                }

                // System.Threading.Tasks.Parallel.ForEach(_cachedSourceArray, source =>
                // {
                //     source.Seek(startPosition);
                // });

                System.Threading.Tasks.Parallel.ForEach(_cachedSourceArray, source =>
                {
                    source.Play();
                });

                System.Threading.Tasks.Parallel.ForEach(_cachedSourceArray, source =>
                {
                    _audioService.Mixer.AddSource(source);
                });

                if (_smartMaster != null && IsSmartMasterEnabled)
                {
                    _audioService.Mixer.AddMasterEffect(_smartMaster);
                }

                _masterEffect?.SetTransportPlaying(true);
            });

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

        await Task.Run(() =>
        {
            for (int i = 0; i < _cachedSourceArray.Length; i++)
            {
                try { _cachedSourceArray[i].Pause(); }
                catch { }
            }

            // Pause VST transport — lock-free, safe from background thread.
            _masterEffect?.SetTransportPlaying(false);
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

        await Task.Run(() =>
        {
            // Stop all tracks
            for (int i = 0; i < _cachedSourceArray.Length; i++)
            {
                try
                {
                    _cachedSourceArray[i].Stop();
                }
                catch { }
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
                    catch { }
                }
            }

            if (_audioService.Mixer != null && _smartMaster != null)
            {
                _audioService.Mixer.RemoveMasterEffect(_smartMaster);
            }

            if (_smartMaster != null)
            {
                _smartMaster.OnPlaybackStopped();
            }

            // Stop VST transport and reset position/buffers.
            // Reset() calls SetTransportState(false) + ResetTransportPosition() + clears buffers.
            // Lock-free enqueues — safe from this background thread.
            _masterEffect?.Reset();
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
