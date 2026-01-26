using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultitrackPlayer.Models;
using MultitrackPlayer.Services;
using OwnaudioNET.Synchronization;
using OwnaudioNET.Core;
using OwnaudioNET.Effects.SmartMaster;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Sources;

namespace MultitrackPlayer.ViewModels;

/// <summary>
/// Core infrastructure for MainWindowViewModel.
/// Contains: Fields, constructor, initialization logic, and core observable properties.
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

    /// <summary>
    /// SmartMaster effect instance.
    /// </summary>
    private SmartMasterEffect? _smartMaster;

    /// <summary>
    /// Timer for updating measurement progress.
    /// </summary>
    private DispatcherTimer? _measurementTimer;

    #endregion

    #region Core Properties

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
    /// Gets or sets the status message displayed in the UI.
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = "Ready";

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

    #endregion

    #region Initialization

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

            // Initialize SmartMaster effect
            if (OwnaudioNET.OwnaudioNet.Engine != null)
            {
                _smartMaster = new SmartMasterEffect();
                _smartMaster.Initialize(OwnaudioNET.OwnaudioNet.Engine.Config);
            }

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
                Logger.Log.Debug($"Full exception: {ex}");
                Logger.Log.Debug($"OwnaudioNet.IsInitialized: {OwnaudioNET.OwnaudioNet.IsInitialized}");
            });
        }
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

        _measurementTimer?.Stop();
        _measurementTimer = null;

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

        _smartMaster?.Dispose();
        _audioService.Dispose();

        _disposed = true;
    }

    #endregion
}
