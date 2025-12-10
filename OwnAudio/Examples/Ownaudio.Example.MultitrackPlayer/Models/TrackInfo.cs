using System;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;

namespace MultitrackPlayer.Models;

/// <summary>
/// Represents information about an audio track in the multitrack player.
/// </summary>
public class TrackInfo : IDisposable
{
    #region Fields

    /// <summary>
    /// The volume level for this track (0.0 to 2.0, where 1.0 is normal volume).
    /// </summary>
    private float _volume = 1.0f;

    /// <summary>
    /// Indicates whether this track is muted.
    /// </summary>
    private bool _isMuted;

    /// <summary>
    /// Indicates whether this track is soloed.
    /// </summary>
    private bool _isSolo;

    /// <summary>
    /// Indicates whether this instance has been disposed.
    /// </summary>
    private bool _disposed;

    #endregion

    #region Properties

    /// <summary>
    /// Gets the full file path of the audio track.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Gets the file name (without path) of the audio track.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Gets or sets the audio source for this track.
    /// </summary>
    public FileSource? Source { get; set; }

    /// <summary>
    /// Gets or sets the volume level for this track.
    /// Valid range is 0.0 to 2.0, where 1.0 is normal volume.
    /// </summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 2f);
            if (Source != null)
                Source.Volume = _isMuted ? 0f : _volume;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether this track is muted.
    /// When muted, the volume is set to 0 regardless of the Volume property.
    /// </summary>
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            if (Source != null)
                Source.Volume = _isMuted ? 0f : _volume;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether this track is soloed.
    /// When any track is soloed, only soloed tracks will be audible.
    /// </summary>
    public bool IsSolo
    {
        get => _isSolo;
        set => _isSolo = value;
    }

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackInfo"/> class.
    /// </summary>
    /// <param name="filePath">The full path to the audio file.</param>
    /// <exception cref="ArgumentNullException">Thrown when filePath is null.</exception>
    public TrackInfo(string filePath)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        FileName = System.IO.Path.GetFileName(filePath);
    }

    #endregion

    #region IDisposable Implementation

    /// <summary>
    /// Releases all resources used by this TrackInfo.
    /// Disposes the audio source if it exists.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        // Only dispose if Source is not null and not already disposed
        if (Source != null)
        {
            try
            {
                Source.Dispose();
            }
            catch
            {
                // Source might already be disposed
            }
            finally
            {
                Source = null;
            }
        }

        _disposed = true;
    }

    #endregion
}
