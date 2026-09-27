using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OwnaudioNET.Core;

namespace MultitrackPlayer.ViewModels;

/// <summary>
/// Master fader and the global tempo and pitch every track follows.
/// </summary>
public partial class MainWindowViewModel
{
    public double MinTempoPercent => AudioConstants.MinTempo * 100.0;

    public double MaxTempoPercent => AudioConstants.MaxTempo * 100.0;

    [ObservableProperty]
    private double _masterVolume = 100.0;

    /// <summary>
    /// 100 = original speed.
    /// </summary>
    [ObservableProperty]
    private double _tempoPercent = 100.0;

    [ObservableProperty]
    private int _pitchSemitones;

    partial void OnMasterVolumeChanged(double value)
    {
        if (_audio.Mixer != null) _audio.Mixer.MasterVolume = (float)(value / 100.0);
    }

    /// <summary>
    /// The smooth setter glides over, no buffer flush, so it is fine to call on every slider notch.
    /// </summary>
    partial void OnTempoPercentChanged(double value)
    {
        float tempo = (float)(value / 100.0);
        foreach (var t in Tracks) t.Source.SetTempoSmooth(tempo);

        _updateDuration();
    }

    partial void OnPitchSemitonesChanged(int value)
    {
        foreach (var t in Tracks) t.Source.SetPitchSmooth(value);
    }

    [RelayCommand]
    private void ResetControls()
    {
        MasterVolume = 100.0;
        TempoPercent = 100.0;
        PitchSemitones = 0;
    }
}
