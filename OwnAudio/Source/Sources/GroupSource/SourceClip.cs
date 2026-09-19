using Ownaudio.Audio.Tracks;

namespace OwnaudioNET.Sources;

/// <summary>
/// One audio file placed on a <see cref="GroupSource"/>'s timeline. The audio is loaded once
/// when the clip is added; moving it only changes where it sits, playing or not.
/// </summary>
public sealed class SourceClip
{
    private GroupSource? _owner;
    private double _startSeconds;

    internal SourceClip(GroupSource owner, GroupClip data, double startSeconds)
    {
        _owner = owner;
        Data = data;
        _startSeconds = startSeconds;
    }

    /// <summary>
    /// The loaded audio, shared by every native group this clip gets placed on.
    /// </summary>
    internal GroupClip Data { get; }

    /// <summary>
    /// Id of the placement on the group's current native track, null while there is none.
    /// </summary>
    internal ulong? NativeId { get; set; }

    /// <summary>
    /// Where it was loaded from.
    /// </summary>
    public string FilePath => Data.FilePath;

    /// <summary>
    /// Clip start on the group's content timeline, in seconds. Setting it moves the clip; a
    /// negative value lands at zero.
    /// </summary>
    public double StartSeconds
    {
        get => _startSeconds;
        set
        {
            if (_owner is { } owner) owner.MoveClip(this, value);
            else _startSeconds = System.Math.Max(0.0, value);
        }
    }

    /// <summary>
    /// Length in content seconds.
    /// </summary>
    public double Duration => Data.Duration.TotalSeconds;

    /// <summary>
    /// Just past the last sample, in content seconds.
    /// </summary>
    public double EndSeconds => StartSeconds + Duration;

    /// <summary>
    /// Decoded into memory when added, otherwise streamed from disk while it plays.
    /// </summary>
    public bool IsInMemory => Data.InMemory;

    /// <summary>
    /// False once the clip was removed from its group.
    /// </summary>
    public bool IsOnGroup => _owner is not null;

    internal void SetStart(double seconds) => _startSeconds = seconds;

    internal void Orphan()
    {
        _owner = null;
        NativeId = null;
    }

    /// <inheritdoc/>
    public override string ToString() => $"{System.IO.Path.GetFileName(FilePath)} @ {StartSeconds:F3}s +{Duration:F3}s";
}
