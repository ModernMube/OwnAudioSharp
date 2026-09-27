using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OwnaudioNET;
using OwnaudioNET.Core;
using OwnaudioNET.Sources;

namespace MultitrackPlayer.ViewModels;

/// <summary>
/// Loading and removing tracks, solo, and the level meters.
/// </summary>
public partial class MainWindowViewModel
{
    [ObservableProperty]
    private double _masterLeftLevel = TrackViewModel.FloorDb;

    [ObservableProperty]
    private double _masterRightLevel = TrackViewModel.FloorDb;

    /// <summary>
    /// Decodes at the engine's format, takes the current tempo and pitch, and joins the mix
    /// straight away when the transport is already going.
    /// </summary>
    public async Task AddTrackAsync(string filePath)
    {
        var mixer = _audio.Mixer;
        if (mixer == null) return;

        if (Tracks.Count >= AudioConstants.MaxAudioSources)
        {
            StatusMessage = $"Track limit reached ({AudioConstants.MaxAudioSources})";
            return;
        }

        StatusMessage = $"Loading {Path.GetFileName(filePath)}...";

        int sampleRate = OwnaudioNet.Engine!.Config.SampleRate;
        int channels = OwnaudioNet.Engine!.Config.Channels;
        float tempo = (float)(TempoPercent / 100.0);
        int pitch = PitchSemitones;

        FileSource source;
        try
        {
            source = await Task.Run(() => new FileSource(filePath, 8192, sampleRate, channels)
            {
                Tempo = tempo,
                PitchShift = pitch
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load {Path.GetFileName(filePath)}: {ex.Message}";
            return;
        }

        var track = new TrackViewModel(filePath, source);
        track.PropertyChanged += _onTrackPropertyChanged;
        Tracks.Add(track);
        _applySolo();

        //AddSource puts it on the clock first, so it enters where the others are
        if (IsPlaying) await Task.Run(() => mixer.AddSource(source));
        else if (IsPaused) await Task.Run(() => mixer.AddSourcePrepared(source));

        _updateDuration();
        StatusMessage = $"Loaded {track.FileName}";
    }

    /// <summary>
    /// Off the mixer before the dispose - a disposed source left registered breaks the next stop.
    /// </summary>
    [RelayCommand]
    private async Task RemoveTrack(TrackViewModel? track)
    {
        if (track == null) return;

        var mixer = _audio.Mixer;
        if (mixer != null) await Task.Run(() => mixer.RemoveSource(track.Source));

        track.PropertyChanged -= _onTrackPropertyChanged;
        Tracks.Remove(track);
        track.Dispose();

        _applySolo();
        _updateDuration();

        if (Tracks.Count == 0 && (IsPlaying || IsPaused)) await Stop();

        StatusMessage = $"Removed {track.FileName}";
    }

    private void _onTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackViewModel.IsSolo)) _applySolo();
    }

    /// <summary>
    /// One solo anywhere silences the rest, the tracks work out their own level from that.
    /// </summary>
    private void _applySolo()
    {
        bool anySolo = false;
        foreach (var t in Tracks) anySolo |= t.IsSolo;

        foreach (var t in Tracks)
        {
            t.SoloActive = anySolo;
            t.ApplyLevel();
        }
    }

    /// <summary>
    /// Peaks to dBFS, written only on a half dB move so the bindings stay quiet.
    /// </summary>
    private void _updateMeters()
    {
        foreach (var t in Tracks)
        {
            if (t.Source.GetOutputLevels() is not { } levels) continue;

            double l = _toDb(levels.left), r = _toDb(levels.right);
            if (Math.Abs(l - t.LeftLevel) >= 0.5) t.LeftLevel = l;
            if (Math.Abs(r - t.RightLevel) >= 0.5) t.RightLevel = r;
        }

        var mixer = _audio.Mixer!;
        double ml = _toDb(mixer.LeftPeak), mr = _toDb(mixer.RightPeak);
        if (Math.Abs(ml - MasterLeftLevel) >= 0.5) MasterLeftLevel = ml;
        if (Math.Abs(mr - MasterRightLevel) >= 0.5) MasterRightLevel = mr;
    }

    private void _resetMeters()
    {
        foreach (var t in Tracks) t.ResetMeters();
        MasterLeftLevel = TrackViewModel.FloorDb;
        MasterRightLevel = TrackViewModel.FloorDb;
    }

    private static double _toDb(float linear)
        => linear > 0f ? Math.Max(20.0 * Math.Log10(linear), TrackViewModel.FloorDb) : TrackViewModel.FloorDb;
}
