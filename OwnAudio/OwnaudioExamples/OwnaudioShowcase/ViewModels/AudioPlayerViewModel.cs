using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Sources;
using OwnaudioShowcase.Models;
using OwnaudioShowcase.Views;

namespace OwnaudioShowcase.ViewModels;

/// <summary>
/// ViewModel for the Audio Player and Multitrack Mixer tab.
/// Handles audio file loading, playback control, and mixer operations.
/// </summary>
public partial class AudioPlayerViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer _updateTimer;
    private bool _disposed;
    private bool _isUpdating; // Flag to prevent reentrant updates
    private WeakReference<AudioPlayerView>? _viewRef; // Weak reference to avoid memory leaks
    private int _timerTickCount = 0; // ✅ FIX #3: Counter for throttling position updates
    private const string SYNC_GROUP_NAME = "MainSyncGroup"; // Sync group name for synchronized playback

    [ObservableProperty]
    private ObservableCollection<AudioTrackModel> tracks = new();

    [ObservableProperty]
    private AudioTrackModel? selectedTrack;

    [ObservableProperty]
    private bool isPlaying;

    [ObservableProperty]
    private bool isInitialized;

    [ObservableProperty]
    private float masterVolume = 1.0f;

    // NOTE: LeftPeak and RightPeak properties REMOVED!
    // We now update peak meters DIRECTLY via View.UpdatePeakMeters()
    // This completely bypasses the MVVM notification system for ZERO GC!

    private double _position;
    public double Position
    {
        get => _position;
        private set // ✅ FIX #1: Private setter - csak olvasásra a timer-ből
        {
            // Only notify if value actually changed (reduce notification overhead)
            if (Math.Abs(_position - value) > 0.001)
            {
                _position = value;
                OnPropertyChanged();

                // ✅ SEEK ELTÁVOLÍTVA! Most már NEM seek-el automatikusan
                // Seek csak a SeekToPosition() metódusban történik
            }
        }
    }

    /// <summary>
    /// Seeks to a specific position in all tracks.
    /// This should ONLY be called when the user manually changes the position slider.
    /// ✅ MULTITRACK MODE: Seeks all tracks simultaneously and resyncs the sync group.
    /// </summary>
    public void SeekToPosition(double newPosition)
    {
        if (!IsInitialized || Tracks.Count == 0)
            return;

        // Update internal position immediately
        _position = newPosition;
        OnPropertyChanged(nameof(Position));

        // Seek all tracks to the new position (multitrack synchronized seek)
        for (int i = 0; i < Tracks.Count; i++)
        {
            var track = Tracks[i];
            if (track.AudioSource != null)
            {
                track.AudioSource.Seek(newPosition);
            }
        }

        // IMPORTANT: Resync the sync group after seeking to ensure perfect sync
        try
        {
            App.AudioEngine.CheckAndResyncAllGroups(toleranceInFrames: 30);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Resync after seek error: {ex}");
        }
    }

    [ObservableProperty]
    private double duration;

    [ObservableProperty]
    private bool isPositionSliderDragging;

    [ObservableProperty]
    private string statusText = "Ready";

    public AudioPlayerViewModel()
    {
        // ✅ FIX #3: Optimized timer frequency
        // Peak meters: 30 Hz (smooth visual updates)
        // Position: 10 Hz (sufficient for time display, updated every 3rd tick)
        // Using DispatcherTimer to avoid cross-thread marshalling and ElapsedEventArgs allocation
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33.33) // 30 Hz for peak meters (was 66.67ms = 15 Hz)
        };
        _updateTimer.Tick += OnUpdateTimerTick;
    }

    /// <summary>
    /// Attaches the View for direct UI updates (zero GC approach).
    /// Called automatically when DataContext is set.
    /// </summary>
    public void AttachView(AudioPlayerView view)
    {
        _viewRef = new WeakReference<AudioPlayerView>(view);
    }

    /// <summary>
    /// Initializes the audio engine asynchronously.
    /// Called when the view is loaded.
    /// Sets up multitrack synchronized playback mode from the start.
    /// </summary>
    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (IsInitialized)
            return;

        try
        {
            StatusText = "Initializing audio engine (Multitrack Mode)...";

            // Initialize audio engine with default config
            await App.AudioEngine.InitializeAsync();
            App.AudioEngine.Start();

            // Enable automatic drift correction from the start
            App.AudioEngine.EnableAutoDriftCorrection = true;

            IsInitialized = true;
            StatusText = "Audio engine initialized - Ready for Multitrack Playback";

            // Start update timer for UI updates
            _updateTimer.Start();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to initialize audio engine: {ex.Message}";
            Debug.WriteLine($"Audio initialization error: {ex}");
        }
    }

    /// <summary>
    /// Loads one or more audio files and adds them to the mixer.
    /// All files are automatically added to the multitrack synchronized group.
    /// </summary>
    [RelayCommand]
    private async Task LoadAudioFilesAsync()
    {
        if (!IsInitialized)
        {
            await InitializeAsync();
        }

        try
        {
            var audioFilters = new[] { "Audio Files:*.mp3;*.wav;*.flac" };
            var filePaths = await App.FileDialog.OpenFilesAsync("Select Audio Files", audioFilters);

            if (filePaths == null || filePaths.Length == 0)
                return;

            StatusText = $"Loading {filePaths.Length} file(s) into Multitrack Mode...";

            foreach (var filePath in filePaths)
            {
                await LoadSingleFileAsync(filePath);
            }

            // Display status with track count and multitrack mode indicator
            var totalTracks = Tracks.Count;
            StatusText = totalTracks == 1
                ? "Loaded 1 track - Ready for Multitrack Playback"
                : $"Loaded {totalTracks} tracks - Synchronized Multitrack Mode Active";
        }
        catch (Exception ex)
        {
            StatusText = $"Error loading files: {ex.Message}";
            Debug.WriteLine($"File loading error: {ex}");
        }
    }

    /// <summary>
    /// Loads a single audio file and adds it as a track.
    /// </summary>
    private async Task LoadSingleFileAsync(string filePath)
    {
        // Load audio file via AudioEngineService
        var audioSource = await App.AudioEngine.LoadAudioFileAsync(filePath);

        // Get file info
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var duration = audioSource.Duration; // Duration is available directly on IAudioSource

        // Create track model
        var track = new AudioTrackModel
        {
            Id = Guid.NewGuid(),
            Name = fileName,
            FilePath = filePath,
            Duration = duration,
            AudioSource = audioSource
        };

        // Register property change handlers BEFORE adding to collection
        RegisterTrackHandlers(track);

        // Add to tracks collection
        Tracks.Add(track);

        // Add to mixer
        App.AudioEngine.AddSourceToMixer(audioSource);

        // Update total duration (use longest track)
        if (duration > Duration)
        {
            Duration = duration;
        }

        // Update sync group with all current tracks
        UpdateSyncGroup();
    }

    /// <summary>
    /// Updates the sync group with all current tracks for synchronized playback.
    /// IMPORTANT: Always creates a sync group, even for a single track, to ensure
    /// consistent multitrack behavior across all scenarios.
    /// </summary>
    private void UpdateSyncGroup()
    {
        // Collect all audio sources
        var sources = Tracks
            .Where(t => t.AudioSource != null)
            .Select(t => t.AudioSource!)
            .ToArray();

        // ALWAYS create sync group, even with 0 or 1 tracks
        // This ensures consistent multitrack behavior
        if (sources.Length > 0)
        {
            // Create or update sync group
            App.AudioEngine.CreateSyncGroup(SYNC_GROUP_NAME, sources);

            // Set tempo to 1.0 (normal speed)
            App.AudioEngine.SetSyncGroupTempo(SYNC_GROUP_NAME, 1.0f);

            // Enable automatic drift correction for perfect synchronization
            App.AudioEngine.EnableAutoDriftCorrection = true;

            // Check and resync with 30 frames tolerance
            App.AudioEngine.CheckAndResyncAllGroups(toleranceInFrames: 30);
        }
    }

    /// <summary>
    /// Removes the selected track from the mixer.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveTrack))]
    private void RemoveTrack()
    {
        if (SelectedTrack == null)
            return;

        try
        {
            // Remove from mixer
            if (SelectedTrack.AudioSource != null)
            {
                App.AudioEngine.RemoveSourceFromMixer(SelectedTrack.AudioSource);
            }

            // Remove from collection
            Tracks.Remove(SelectedTrack);

            StatusText = $"Removed track: {SelectedTrack.Name}";

            // Update sync group after removal
            UpdateSyncGroup();
        }
        catch (Exception ex)
        {
            StatusText = $"Error removing track: {ex.Message}";
            Debug.WriteLine($"Track removal error: {ex}");
        }
    }

    private bool CanRemoveTrack() => SelectedTrack != null;

    /// <summary>
    /// Toggles play/pause state using synchronized group playback.
    /// ALWAYS uses multitrack sync mode, even for a single track.
    /// </summary>
    [RelayCommand]
    private void PlayPause()
    {
        if (!IsInitialized || Tracks.Count == 0)
            return;

        IsPlaying = !IsPlaying;

        try
        {
            if (IsPlaying)
            {
                // Start mixer first (required for multitrack sync)
                if (App.AudioEngine.Mixer != null && !App.AudioEngine.Mixer.IsRunning)
                {
                    App.AudioEngine.Mixer.Start();
                }

                // ALWAYS use synchronized playback, even for single track
                // This ensures consistent behavior and perfect timing
                App.AudioEngine.StartSyncGroup(SYNC_GROUP_NAME);

                // Update track states
                for (int i = 0; i < Tracks.Count; i++)
                {
                    Tracks[i].IsPlaying = true;
                }

                // Status shows track count for clarity
                StatusText = Tracks.Count == 1
                    ? "Playing (Multitrack Mode - 1 track)"
                    : $"Playing (Multitrack Mode - {Tracks.Count} tracks)";
            }
            else
            {
                // Pause all tracks individually (sync group doesn't have pause, only stop)
                for (int i = 0; i < Tracks.Count; i++)
                {
                    var track = Tracks[i];
                    if (track.AudioSource != null)
                    {
                        track.AudioSource.Pause();
                        track.IsPlaying = false;
                    }
                }

                StatusText = "Paused";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error during play/pause: {ex.Message}";
            Debug.WriteLine($"PlayPause error: {ex}");
        }
    }

    /// <summary>
    /// Stops playback and resets position to beginning using synchronized group.
    /// ALWAYS uses multitrack sync mode, even for a single track.
    /// </summary>
    [RelayCommand]
    private void Stop()
    {
        if (!IsInitialized)
            return;

        IsPlaying = false;
        Position = 0;

        try
        {
            // ALWAYS stop using sync group, even for single track
            // This ensures consistent multitrack behavior
            if (Tracks.Count > 0)
            {
                App.AudioEngine.StopSyncGroup(SYNC_GROUP_NAME);
            }

            // Update track states
            for (int i = 0; i < Tracks.Count; i++)
            {
                var track = Tracks[i];
                track.IsPlaying = false;
                track.Position = 0;
            }

            StatusText = "Stopped (Multitrack Mode)";
        }
        catch (Exception ex)
        {
            StatusText = $"Error during stop: {ex.Message}";
            Debug.WriteLine($"Stop error: {ex}");
        }
    }

    /// <summary>
    /// Updates master volume on the mixer.
    /// </summary>
    partial void OnMasterVolumeChanged(float value)
    {
        if (!IsInitialized || App.AudioEngine.Mixer == null)
            return;

        App.AudioEngine.Mixer.MasterVolume = value;
    }

    /// <summary>
    /// Updates track volume when changed.
    /// </summary>
    private void UpdateTrackVolume(AudioTrackModel track)
    {
        if (track.AudioSource != null)
        {
            track.AudioSource.Volume = track.Volume;
        }
    }

    /// <summary>
    /// Updates track tempo when changed.
    /// </summary>
    private void UpdateTrackTempo(AudioTrackModel track)
    {
        if (track.AudioSource != null)
        {
            track.AudioSource.Tempo = track.Tempo;
        }
    }

    /// <summary>
    /// Updates track pitch shift when changed.
    /// </summary>
    private void UpdateTrackPitch(AudioTrackModel track)
    {
        if (track.AudioSource != null)
        {
            track.AudioSource.PitchShift = track.PitchShift;
        }
    }

    /// <summary>
    /// Timer callback for updating UI with current playback state.
    /// ✅ FIX #2 & #3: Direct UI updates + throttled position updates!
    /// CRITICAL: Zero-allocation implementation to prevent GC pressure!
    /// Uses DispatcherTimer.Tick instead of System.Timers.Timer.Elapsed to avoid:
    /// - ElapsedEventArgs allocation
    /// - Cross-thread marshalling overhead
    /// Runs at 30 Hz for peak meters, position updated every 3rd tick (10 Hz)
    /// </summary>
    private void OnUpdateTimerTick(object? sender, EventArgs e)
    {
        // Prevent reentrant calls
        if (_isUpdating)
            return;

        _isUpdating = true;

        try
        {
            if (!IsInitialized || Tracks.Count == 0)
                return;

            // ✅ FIX #3: Increment tick counter for throttling
            _timerTickCount++;

            // Update position from first track (avoid LINQ - zero allocation)
            var firstTrack = Tracks[0]; // Direct indexer access instead of FirstOrDefault()
            if (firstTrack?.AudioSource != null && !IsPositionSliderDragging)
            {
                var mixer = App.AudioEngine.Mixer;

                // ✅ FIX #3: Update position every 3rd tick (10 Hz), but peak meters every tick (30 Hz)
                bool shouldUpdatePosition = (_timerTickCount % 3 == 0);

                if (shouldUpdatePosition)
                {
                    var currentPosition = firstTrack.AudioSource.Position;

                    // Only update if changed significantly (reduce UI updates)
                    if (Math.Abs(_position - currentPosition) > 0.1)
                    {
                        // Update internal state (no PropertyChanged)
                        _position = currentPosition;

                        // Direct UI update - bypasses entire MVVM notification system
                        if (_viewRef?.TryGetTarget(out var view) == true)
                        {
                            view.UpdatePlaybackState(
                                currentPosition,
                                mixer?.LeftPeak ?? 0f,
                                mixer?.RightPeak ?? 0f
                            );
                        }
                    }
                    else
                    {
                        // Position hasn't changed, but still update peak meters
                        if (_viewRef?.TryGetTarget(out var view) == true)
                        {
                            view.UpdatePeakMeters(mixer?.LeftPeak ?? 0f, mixer?.RightPeak ?? 0f);
                        }
                    }
                }
                else
                {
                    // Not a position update tick, only update peak meters (30 Hz smooth visual)
                    if (_viewRef?.TryGetTarget(out var view) == true)
                    {
                        view.UpdatePeakMeters(mixer?.LeftPeak ?? 0f, mixer?.RightPeak ?? 0f);
                    }
                }
            }

            // Check if playback finished
            if (IsPlaying && _position >= Duration && Duration > 0)
            {
                IsPlaying = false;
                StatusText = "Playback finished";

                // Use for loop instead of foreach (less allocation)
                for (int i = 0; i < Tracks.Count; i++)
                {
                    Tracks[i].IsPlaying = false;
                }
            }
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    /// Registers property change handlers for a track.
    /// </summary>
    private void RegisterTrackHandlers(AudioTrackModel track)
    {
        track.PropertyChanged += (s, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(AudioTrackModel.Volume):
                    UpdateTrackVolume(track);
                    break;
                case nameof(AudioTrackModel.Tempo):
                    UpdateTrackTempo(track);
                    break;
                case nameof(AudioTrackModel.PitchShift):
                    UpdateTrackPitch(track);
                    break;
            }
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        // DispatcherTimer doesn't implement IDisposable, just stop it
        _updateTimer?.Stop();

        _disposed = true;
    }
}
