using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MultitrackPlayer.Services;
using OwnaudioNET.Events;

namespace MultitrackPlayer.ViewModels;

/// <summary>
/// The whole player. Split by job: transport, tracks, master controls, plugin, Smart Master.
/// </summary>
public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly AudioService _audio = AudioService.Instance;

    /// <summary>
    /// Position, meters and the load readout. Runs only while playing.
    /// </summary>
    private readonly DispatcherTimer _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };

    private bool _disposed;

    public MainWindowViewModel()
    {
        _uiTimer.Tick += _onUiTick;
        _ = _initializeAsync();
    }

    public ObservableCollection<TrackViewModel> Tracks { get; } = new ObservableCollection<TrackViewModel>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    private bool _isInitialized;

    [ObservableProperty]
    private string _statusMessage = "Starting the audio engine...";

    /// <summary>
    /// Engine comes up off the UI thread, the plugin scan follows once it is there.
    /// </summary>
    private async Task _initializeAsync()
    {
        try
        {
            await Task.Run(_audio.InitializeAsync);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Audio engine failed to start: {ex.Message}";
            return;
        }

        _audio.StreamFaulted += _onStreamFaulted;
        _audio.PlaybackEnded += _onPlaybackEnded;
        _audio.Mixer!.MasterVolume = (float)(MasterVolume / 100.0);

        IsInitialized = true;
        StatusMessage = "Ready";

        await ScanPluginsAsync();
    }

    /// <summary>
    /// Device gone or backend error: the transport stops instead of spinning over a dead mixer.
    /// </summary>
    private void _onStreamFaulted(object? sender, AudioStreamFaultEventArgs e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (IsPlaying || IsPaused) await Stop();
            StatusMessage = $"Audio output lost: {e.Kind}";
        });
    }

    private void _onPlaybackEnded(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (IsPlaying) await Stop();
        });
    }

    /// <summary>
    /// The mixer disposes the master effects it holds, so the plugin host only goes after it.
    /// Tracks last, by then nothing holds their sources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _uiTimer.Stop();
        _audio.StreamFaulted -= _onStreamFaulted;
        _audio.PlaybackEnded -= _onPlaybackEnded;

        _pluginHost?.CloseEditor();
        _audio.Dispose();
        _pluginHost?.Dispose();

        foreach (var track in Tracks) track.Dispose();
        Tracks.Clear();

        _disposed = true;
    }
}
