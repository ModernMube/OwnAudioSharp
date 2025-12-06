using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultitrackPlayer.Models;
using MultitrackPlayer.Services;
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
    /// Indicates whether this instance has been disposed.
    /// </summary>
    private bool _disposed;

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
    /// </summary>
    [ObservableProperty]
    private float _tempoPercent = 100.0f;

    /// <summary>
    /// Called when the tempo percentage changes.
    /// Applies the tempo change to all tracks in the sync group.
    /// </summary>
    /// <param name="value">The new tempo percentage.</param>
    partial void OnTempoPercentChanged(float value)
    {
        // Convert percentage to multiplier (100% = 1.0x)
        float tempoMultiplier = value / 100.0f;

        // Apply tempo to sync group (this controls all tracks together)
        if (_audioService.Mixer != null)
        {
            try
            {
                _audioService.Mixer.SetSyncGroupTempo("MainTracks", tempoMultiplier);
            }
            catch
            {
                // Sync group doesn't exist yet, will be set when playing starts
            }
        }

        // Also update individual sources in case they're not in sync group yet
        foreach (var track in Tracks)
        {
            if (track.TrackInfo.Source is FileSource fileSource)
            {
                fileSource.Tempo = tempoMultiplier;
            }
        }
    }

    /// <summary>
    /// Gets or sets the pitch shift in semitones (0 = original pitch).
    /// </summary>
    [ObservableProperty]
    private int _pitchSemitones = 0;

    /// <summary>
    /// Called when the pitch semitones property changes.
    /// Applies the pitch shift to all loaded tracks.
    /// </summary>
    /// <param name="value">The new pitch shift value in semitones.</param>
    partial void OnPitchSemitonesChanged(int value)
    {
        // Apply pitch shift to all tracks (integer semitones)
        foreach (var track in Tracks)
        {
            if (track.TrackInfo.Source is FileSource fileSource)
            {
                fileSource.PitchShift = value;
            }
        }
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

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += Timer_Tick;

        // Initialize audio service
        Task.Run(InitializeAudioAsync);
    }

    #endregion

    #region Private Methods - Timer and Initialization

    /// <summary>
    /// Handles the playback timer tick event.
    /// Updates the current position display and checks for end of playback.
    /// </summary>
    /// <param name="sender">The timer that raised the event.</param>
    /// <param name="e">Event arguments.</param>
    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (!IsPlaying || _isSeeking || _audioService.Mixer == null)
            return;

        try
        {
            double position = _audioService.Mixer.GetSyncGroupPosition("MainTracks");
            CurrentPositionSeconds = position;

            if (TrackDurationSeconds > 0 && CurrentPositionSeconds >= TrackDurationSeconds)
            {
                // Use the StopCommand's logic to ensure clean state transition
                if (StopCommand.CanExecute(null))
                {
                    StopCommand.Execute(null);
                }
            }
            else
            {
                var currentTime = TimeSpan.FromSeconds(position);
                var totalTime = TimeSpan.FromSeconds(TrackDurationSeconds);
                PositionDisplay = $"{currentTime:mm\\:ss} / {totalTime:mm\\:ss}";
            }
        }
        catch
        {
            // Sync group might not exist anymore
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
        TempoPercent = 100.0f;
        PitchSemitones = 0;
        StatusMessage = "Reset controls to default values";
    }

    #endregion

    #region Commands - Playback Control

    /// <summary>
    /// Starts or resumes playback of all loaded tracks.
    /// Creates a synchronized playback group and applies all audio effects.
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

            await Dispatcher.UIThread.InvokeAsync(() => { StatusMessage = "Starting playback..."; });

            await _audioService.RestartAsync();

            // Handle solo and mute logic
            var soloTracks = Tracks.Where(t => t.IsSolo).ToList();
            foreach (var track in Tracks)
            {
                if (track.TrackInfo.Source != null)
                {
                    float volumeMultiplier = track.Volume / 100.0f;
                    if (soloTracks.Any())
                    {
                        track.TrackInfo.Source.Volume = track.IsSolo ? volumeMultiplier : 0f;
                    }
                    else
                    {
                        track.TrackInfo.Source.Volume = track.IsMuted ? 0f : volumeMultiplier;
                    }
                }
            }

            var sources = Tracks.Select(t => t.TrackInfo.Source).Where(s => s != null).Cast<IAudioSource>().ToArray();
            if (sources.Length == 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { StatusMessage = "No valid sources to play."; });
                return;
            }
            
            await Task.Run(() =>
            {
                // NEW ARCHITECTURE: Create sync group - automatically attaches sources to GhostTrack
                _audioService.Mixer.CreateSyncGroup("MainTracks", sources);

                // NEW ARCHITECTURE: Set tempo on GhostTrack - automatically propagates to all sources
                _audioService.Mixer.SetSyncGroupTempo("MainTracks", TempoPercent / 100.0f);

                // NEW ARCHITECTURE: No manual drift correction needed!
                // - Automatic continuous drift check in ReadSamples() every ~10ms
                // - Tight 10ms tolerance (vs old 100ms)
                // - Lock-free, zero overhead design

                _audioService.Mixer.StartSyncGroup("MainTracks");
            });

            var longestTrack = sources.OfType<FileSource>().Max(s => s?.Duration) ?? 0.0;
            TrackDurationSeconds = longestTrack;

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
    /// Stops the playback timer and restarts the audio engine to clear buffers.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand(CanExecute = nameof(CanPauseStop))]
    private async Task PauseAsync()
    {
        _playbackTimer.Stop();
        IsPlaying = false;
        await Dispatcher.UIThread.InvokeAsync(() => { StatusMessage = "Pausing..."; });
        await _audioService.RestartAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { StatusMessage = "Paused"; });
    }

    /// <summary>
    /// Stops the current playback and resets the position to the beginning.
    /// Clears all playback state and restarts the audio engine.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand(CanExecute = nameof(CanPauseStop))]
    private async Task StopAsync()
    {
        _playbackTimer.Stop();
        IsPlaying = false;
        CurrentPositionSeconds = 0;
        TrackDurationSeconds = 0;
        PositionDisplay = "00:00 / 00:00";
        await Dispatcher.UIThread.InvokeAsync(() => { StatusMessage = "Stopping..."; });
        await _audioService.RestartAsync();
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
                _audioService.Mixer?.SeekSyncGroup("MainTracks", positionInSeconds);
                CurrentPositionSeconds = positionInSeconds;
            }
            catch
            {
                // Sync group might not exist or other audio error
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
