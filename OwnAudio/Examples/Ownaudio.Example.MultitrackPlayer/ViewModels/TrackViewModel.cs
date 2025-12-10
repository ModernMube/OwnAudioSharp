using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MultitrackPlayer.Models;

namespace MultitrackPlayer.ViewModels;

/// <summary>
/// ViewModel for an individual audio track with volume, mute, and solo controls.
/// </summary>
public partial class TrackViewModel : ObservableObject, IDisposable
{
    #region Fields

    /// <summary>
    /// The underlying track information and audio source.
    /// </summary>
    private readonly TrackInfo _trackInfo;

    /// <summary>
    /// Indicates whether this instance has been disposed.
    /// </summary>
    private bool _disposed;

    #endregion

    #region Properties

    /// <summary>
    /// Gets the file name of the audio track.
    /// </summary>
    public string FileName => _trackInfo.FileName;

    /// <summary>
    /// Gets the full file path of the audio track.
    /// </summary>
    public string FilePath => _trackInfo.FilePath;

    /// <summary>
    /// Gets or sets the volume level for this track (0-100).
    /// </summary>
    [ObservableProperty]
    private float _volume;

    /// <summary>
    /// Called when the volume property changes.
    /// Converts the 0-100 range to 0.0-1.0 for the audio engine.
    /// </summary>
    /// <param name="value">The new volume value.</param>
    partial void OnVolumeChanged(float value)
    {
        // Convert 0-100 to 0.0-1.0 for the audio engine
        _trackInfo.Volume = value / 100.0f;
    }

    /// <summary>
    /// Gets or sets a value indicating whether this track is muted.
    /// </summary>
    [ObservableProperty]
    private bool _isMuted;

    /// <summary>
    /// Called when the mute state changes.
    /// </summary>
    /// <param name="value">True if the track should be muted; otherwise, false.</param>
    partial void OnIsMutedChanged(bool value)
    {
        _trackInfo.IsMuted = value;
    }

    /// <summary>
    /// Gets or sets a value indicating whether this track is soloed.
    /// When any track is soloed, only soloed tracks will be audible.
    /// </summary>
    [ObservableProperty]
    private bool _isSolo;

    /// <summary>
    /// Called when the solo state changes.
    /// </summary>
    /// <param name="value">True if the track should be soloed; otherwise, false.</param>
    partial void OnIsSoloChanged(bool value)
    {
        _trackInfo.IsSolo = value;
    }

    /// <summary>
    /// Gets the underlying track information model.
    /// </summary>
    public TrackInfo TrackInfo => _trackInfo;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackViewModel"/> class.
    /// </summary>
    /// <param name="trackInfo">The track information to wrap.</param>
    /// <exception cref="ArgumentNullException">Thrown when trackInfo is null.</exception>
    public TrackViewModel(TrackInfo trackInfo)
    {
        _trackInfo = trackInfo ?? throw new ArgumentNullException(nameof(trackInfo));
        // Convert 0.0-1.0 to 0-100 for display
        _volume = trackInfo.Volume * 100.0f;
        _isMuted = trackInfo.IsMuted;
        _isSolo = trackInfo.IsSolo;
    }

    #endregion

    #region IDisposable Implementation

    /// <summary>
    /// Releases all resources used by this ViewModel.
    /// Disposes the underlying track information and audio source.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _trackInfo.Dispose();
        _disposed = true;
    }

    #endregion
}
