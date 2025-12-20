using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultitrackPlayer.Models;
using MultitrackPlayer.Services;
using Ownaudio.Synchronization;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Sources;

namespace MultitrackPlayer.ViewModels;

/// <summary>
/// Main ViewModel for the multitrack player application.
/// Manages tracks, playback controls, and audio effects.
/// </summary>
public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    #region Fields

    /// <summary>
    /// Singleton audio service managing the audio engine and mixer.
    /// </summary>
    private readonly AudioService _audioService;

    /// <summary>
    /// Timer for updating playback position display.
    /// </summary>
    private readonly DispatcherTimer _playbackTimer;

    /// <summary>
    /// Indicates whether the user is currently seeking through the timeline.
    /// </summary>
    private bool _isSeeking;

    /// <summary>
    /// Cached arrays to avoid LINQ allocations during playback.
    /// </summary>
    private TrackViewModel[] _cachedTrackArray = Array.Empty<TrackViewModel>();
    private IAudioSource[] _cachedSourceArray = Array.Empty<IAudioSource>();
    private bool _cacheNeedsUpdate = true;

    /// <summary>
    /// Indicates whether this instance has been disposed.
    /// </summary>
    private bool _disposed;


    /// <summary>
    /// Total dropout count for all tracks (NEW - v2.4.0+).
    /// </summary>
    private int _totalDropoutCount;

    #endregion

    #region Properties

    /// <summary>
    /// Gets the collection of audio tracks loaded in the player.
    /// </summary>
    public ObservableCollection<TrackViewModel> Tracks { get; }

    /// <summary>
    /// Gets or sets a value indicating whether playback is currently active.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isPlaying;

    /// <summary>
    /// Gets or sets a value indicating whether the audio engine has been initialized.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    private bool _isInitialized;

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
    /// Applies tempo change to all tracks.
    /// PUBLIC: Called from code-behind on PointerReleased.
    /// </summary>
    public async void ApplyTempoChange(float tempoPercent)
    {
        // Convert percentage to multiplier (100% = 1.0x)
        float tempoMultiplier = tempoPercent / 100.0f;

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

    /// <summary>
    /// Gets or sets the status message displayed in the UI.
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = "Ready";

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

    /// <summary>
    /// Gets or sets the total dropout count (NEW - v2.4.0+).
    /// </summary>
    [ObservableProperty]
    private int _dropoutCount;

    /// <summary>
    /// Gets or sets the last dropout message (NEW - v2.4.0+).
    /// </summary>
    [ObservableProperty]
    private string? _lastDropoutMessage;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindowViewModel"/> class.
    /// Sets up the audio service, track collection, and playback timer.
    /// </summary>
    public MainWindowViewModel()
    {
        _audioService = AudioService.Instance;
        Tracks = new ObservableCollection<TrackViewModel>();

        // OPTIMIZATION: Reduced update frequency to 4/sec to lower GC pressure
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playbackTimer.Tick += Timer_Tick;

        // Subscribe to collection changes to invalidate cache
        Tracks.CollectionChanged += (s, e) => _cacheNeedsUpdate = true;

        // Initialize audio service
        Task.Run(InitializeAudioAsync);
    }

    /// <summary>
    /// Initializes event handlers for the mixer (NEW - v2.4.0+).
    /// </summary>
    private void InitializeMixerEvents()
    {
        if (_audioService.Mixer == null)
            return;

        // Subscribe to TrackDropout events
        _audioService.Mixer.TrackDropout += OnTrackDropout;
    }

    /// <summary>
    /// Handles track dropout events (NEW - v2.4.0+).
    /// </summary>
    private void OnTrackDropout(object? sender, TrackDropoutEventArgs e)
    {
        _totalDropoutCount++;

        Dispatcher.UIThread.InvokeAsync(() =>
        {
            DropoutCount = _totalDropoutCount;
            LastDropoutMessage = $"{e.TrackName}: {e.MissedFrames} frames @ {e.MasterTimestamp:F2}s";

            // Optionally show status if dropout count is low
            if (_totalDropoutCount < 10) 
            {
                StatusMessage = $"Dropout: {e.Reason}";
            }
        });
    }

    #endregion

    #region Private Methods - Timer and Initialization

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

    /// <summary>
    /// Initializes the audio engine asynchronously to avoid blocking the UI thread.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task InitializeAudioAsync()
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusMessage = "Initializing audio engine...";
            });

            await _audioService.InitializeAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsInitialized = true;
                StatusMessage = "Ready";

                // Initialize mixer event handlers (NEW - v2.4.0+)
                InitializeMixerEvents();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusMessage = $"Error initializing audio: {ex.Message}";
            });
        }
    }

    #endregion

    #region Public Methods - Track Management

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
                StatusMessage = $"Loaded {trackInfo.FileName}";
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

    #region Commands - Track Management

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

    /// <summary>
    /// Resets all audio control parameters to their default values.
    /// Sets master volume to 100%, tempo to 100%, and pitch shift to 0 semitones.
    /// </summary>
    [RelayCommand]
    private void ResetControls()
    {
        MasterVolume = 100.0f;

        // CRITICAL FIX: Apply changes immediately to prevent sync issues during reset
        TempoPercent = 100.0f;
        PitchSemitones = 0;

        // Apply changes immediately to all tracks (bypass debounce)
        foreach (var track in Tracks)
        {
            if (track.TrackInfo.Source is FileSource fileSource)
            {
                fileSource.Tempo = 1.0f;  // 100% = 1.0x multiplier
                fileSource.PitchShift = 0;
            }
        }

        StatusMessage = "Reset controls to default values";
    }

    #endregion

    #region Commands - Playback Control

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

    #region Commands - Seeking

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

    #region IDisposable Implementation

    /// <summary>
    /// Releases all resources used by this ViewModel.
    /// Stops the timer, disposes all tracks, and cleans up the audio service.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _playbackTimer.Tick -= Timer_Tick;
        _playbackTimer.Stop();

        // Unsubscribe from mixer events (NEW - v2.4.0+)
        if (_audioService.Mixer != null)
        {
            _audioService.Mixer.TrackDropout -= OnTrackDropout;
        }

        foreach (var track in Tracks)
        {
            track.Dispose();
        }
        Tracks.Clear();

        _audioService.Dispose();

        _disposed = true;
    }

    #endregion
}
