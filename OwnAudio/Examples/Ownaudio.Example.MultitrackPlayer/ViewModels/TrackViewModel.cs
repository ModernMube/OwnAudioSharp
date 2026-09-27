using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using OwnaudioNET.Sources;

namespace MultitrackPlayer.ViewModels;

/// <summary>
/// One row of the track list: the file, its source and the fader, mute and solo on it.
/// </summary>
public partial class TrackViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Meter floor in dB, the meters never go under it.
    /// </summary>
    public const double FloorDb = -60.0;

    public TrackViewModel(string filePath, FileSource source)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Source = source;
    }

    public string FilePath { get; }

    public string FileName { get; }

    public FileSource Source { get; }

    /// <summary>
    /// Fader in percent, 100 = unity.
    /// </summary>
    [ObservableProperty]
    private double _volume = 100.0;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _isSolo;

    [ObservableProperty]
    private double _leftLevel = FloorDb;

    [ObservableProperty]
    private double _rightLevel = FloorDb;

    /// <summary>
    /// Somebody somewhere is soloed. Set by the main VM, the track works out the rest.
    /// </summary>
    public bool SoloActive { get; set; }

    public float Level => IsMuted || (SoloActive && !IsSolo) ? 0f : (float)(Volume / 100.0);

    /// <summary>
    /// Every fader, mute and solo change lands here, so none of them can write past the others.
    /// </summary>
    public void ApplyLevel() => Source.Volume = Level;

    partial void OnVolumeChanged(double value) => ApplyLevel();

    partial void OnIsMutedChanged(bool value) => ApplyLevel();

    public void ResetMeters()
    {
        LeftLevel = FloorDb;
        RightLevel = FloorDb;
    }

    public void Dispose() => Source.Dispose();
}
