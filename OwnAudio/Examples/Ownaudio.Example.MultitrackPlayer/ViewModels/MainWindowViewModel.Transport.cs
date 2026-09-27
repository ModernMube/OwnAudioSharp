using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MultitrackPlayer.ViewModels;

/// <summary>
/// Play, pause, stop and seek. Every track rides the mixer's master clock, so the transport
/// talks to the mixer and never moves a single source on its own.
/// </summary>
public partial class MainWindowViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isPaused;

    /// <summary>
    /// Project time in seconds - what the master clock counts, tempo already in it.
    /// </summary>
    [ObservableProperty]
    private double _currentPositionSeconds;

    /// <summary>
    /// Longest track at the current tempo.
    /// </summary>
    [ObservableProperty]
    private double _trackDurationSeconds;

    [ObservableProperty]
    private string _positionDisplay = "00:00 / 00:00";

    [ObservableProperty]
    private string _engineLoadText = string.Empty;

    private bool _isSeeking;
    private int _lastShownSecond = -1;
    private int _uiTicks;

    /// <summary>
    /// A fresh start registers every track prepared and lets the mixer start them on one clock
    /// position. A resume just picks up what the pause left on the mixer.
    /// </summary>
    [RelayCommand(CanExecute = nameof(_canPlay))]
    private async Task Play()
    {
        var mixer = _audio.Mixer;
        if (mixer == null) return;

        if (Tracks.Count == 0)
        {
            StatusMessage = "No tracks loaded";
            return;
        }

        var tracks = Tracks.ToArray();
        bool resume = IsPaused;
        double start = CurrentPositionSeconds;

        _applySolo();

        IsPlaying = true;
        IsPaused = false;

        await Task.Run(() =>
        {
            if (resume)
            {
                foreach (var t in tracks) t.Source.Play();
                mixer.Start();
                return;
            }

            mixer.Seek(start);
            foreach (var t in tracks) mixer.AddSourcePrepared(t.Source);

            //Paused while the prepared ones get started, so they all land on the same block
            mixer.Pause();
            mixer.StartPreparedSources(start);
            mixer.Start();
        });

        _pluginProcessor?.SetTransportPlaying(true);

        _uiTicks = 0;
        _uiTimer.Start();
        StatusMessage = "Playing";
    }

    private bool _canPlay() => IsInitialized && !IsPlaying;

    /// <summary>
    /// Everything stays on the mixer, only the clock and the sources hold.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsPlaying))]
    private async Task Pause()
    {
        var mixer = _audio.Mixer;
        if (mixer == null) return;

        _uiTimer.Stop();
        IsPlaying = false;
        IsPaused = true;

        var tracks = Tracks.ToArray();
        await Task.Run(() =>
        {
            mixer.Pause();
            foreach (var t in tracks) t.Source.Pause();
        });

        _pluginProcessor?.SetTransportPlaying(false);

        CurrentPositionSeconds = mixer.MasterClock.CurrentTimestamp;
        _resetMeters();
        StatusMessage = "Paused";
    }

    /// <summary>
    /// Back to zero, sources off the mixer. They stay loaded on their tracks for the next Play.
    /// </summary>
    [RelayCommand(CanExecute = nameof(_canStop))]
    private async Task Stop()
    {
        var mixer = _audio.Mixer;
        if (mixer == null) return;

        _uiTimer.Stop();
        IsPlaying = false;
        IsPaused = false;

        var tracks = Tracks.ToArray();
        await Task.Run(() =>
        {
            foreach (var t in tracks) t.Source.Stop();

            mixer.Seek(0.0);
            foreach (var t in tracks) mixer.RemoveSource(t.Source);
        });

        _pluginProcessor?.SetTransportPlaying(false);
        _pluginProcessor?.ResetPosition();

        CurrentPositionSeconds = 0.0;
        _resetMeters();
        _refreshPositionDisplay();
        StatusMessage = "Stopped";
    }

    private bool _canStop() => IsPlaying || IsPaused;

    /// <summary>
    /// The user grabbed the slider, the timer must not pull it back meanwhile.
    /// </summary>
    [RelayCommand]
    private void BeginSeek() => _isSeeking = true;

    /// <summary>
    /// Slider let go. position is the slider value; stopped, it only moves where the next Play starts.
    /// </summary>
    [RelayCommand]
    private void EndSeek(object? position)
    {
        _isSeeking = false;

        if (position == null || !double.TryParse(Convert.ToString(position, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            return;

        seconds = Math.Clamp(seconds, 0.0, TrackDurationSeconds);

        if (IsPlaying || IsPaused) _audio.Mixer?.Seek(seconds);

        CurrentPositionSeconds = seconds;
        _refreshPositionDisplay();
    }

    /// <summary>
    /// Clock readout every tick, meters too, the load once a second.
    /// </summary>
    private void _onUiTick(object? sender, EventArgs e)
    {
        var mixer = _audio.Mixer;
        if (mixer == null || !IsPlaying) return;

        if (!_isSeeking)
        {
            CurrentPositionSeconds = mixer.MasterClock.CurrentTimestamp;
            _refreshPositionDisplay();
        }

        _updateMeters();

        if (++_uiTicks % 20 == 0) _refreshEngineLoad();
    }

    /// <summary>
    /// The render callback's own cost - the one dropout signal there is. The first readings
    /// after a start never count, the device is still settling.
    /// </summary>
    private void _refreshEngineLoad()
    {
        var mixer = _audio.Mixer;
        var load = mixer?.SessionLoad;
        if (load == null) return;

        if (_uiTicks <= 40)
        {
            mixer!.ResetSessionLoad();
            return;
        }

        string text = $"DSP load {load.Value.AverageLoad:P0} avg / {load.Value.PeakLoad:P0} peak" +
                      (load.Value.HasOverrun ? " - DROPOUT" : string.Empty);

        if (text != EngineLoadText) EngineLoadText = text;
    }

    private void _refreshPositionDisplay()
    {
        int second = (int)CurrentPositionSeconds;
        if (second == _lastShownSecond) return;

        _lastShownSecond = second;
        PositionDisplay = $"{_mmss(CurrentPositionSeconds)} / {_mmss(TrackDurationSeconds)}";
    }

    private static string _mmss(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");

    /// <summary>
    /// Longest file, stretched by the tempo. Called when a track comes or goes and on a tempo move.
    /// </summary>
    private void _updateDuration()
    {
        double longest = 0.0;
        foreach (var t in Tracks) longest = Math.Max(longest, t.Source.Duration);

        TrackDurationSeconds = longest / (TempoPercent / 100.0);
        _lastShownSecond = -1;
        _refreshPositionDisplay();
    }
}
