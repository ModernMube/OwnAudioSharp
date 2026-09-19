using Logger;
using Ownaudio;
using Ownaudio.Audio.Tracks;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Synchronization;

namespace OwnaudioNET.Sources;

/// <summary>
/// Several audio files laid out on one timeline and played as a single source — a DAW lane with
/// its clips. They share one tempo and pitch stage, one effect chain (wrap it in a
/// <see cref="SourceWithEffects"/>), one volume, pan and route, and effect tails ring on across
/// clip boundaries.
/// </summary>
/// <remarks>
/// Clips can be added, moved and removed while the source plays. Each file is loaded once when it
/// is added: short ones decode into memory, longer ones stream from disk. Taking the source off a
/// mixer and adding it back — the usual stop/play cycle — decodes nothing again.
/// Playback runs on the native chain only; <see cref="ReadSamples"/> hands back silence.
/// </remarks>
public sealed partial class GroupSource : BaseAudioSource, IMasterClockSource, IRustClockedSource
{
    #region Fields

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly double _memoryMaxSeconds;
    private readonly AudioConfig _config;

    /// <summary>
    /// Guards the clip list and every native clip call, so a clip edit and a re-attach never
    /// interleave.
    /// </summary>
    private readonly object _clipLock = new object();
    private readonly List<SourceClip> _clips = new List<SourceClip>();

    private float _tempo = 1.0f;
    private float _pitchShift = 0.0f;
    private MasterClock? _masterClock;
    private double _startOffset;
    private volatile bool _isEndOfStream;
    private bool _disposed;

    #endregion

    #region Constructor

    /// <summary>
    /// An empty group. Every clip decodes to sampleRate and channels — pass the mixer's rate.
    /// Files up to memoryMaxSeconds long load into memory, longer ones stream.
    /// </summary>
    /// <param name="sampleRate"></param>
    /// <param name="channels"></param>
    /// <param name="memoryMaxSeconds"></param>
    public GroupSource(int sampleRate = 48000, int channels = 2, double memoryMaxSeconds = 30.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        _sampleRate = sampleRate;
        _channels = channels;
        _memoryMaxSeconds = Math.Max(0.0, memoryMaxSeconds);
        _rustNative = OwnaudioNET.Engine.RustNativeChain.Enabled;

        _config = new AudioConfig
        {
            SampleRate = sampleRate,
            Channels = channels,
            BufferSize = 8192
        };
    }

    #endregion

    #region Propertyes

    /// <inheritdoc/>
    public override AudioConfig Config => _config;

    /// <inheritdoc/>
    public override AudioStreamInfo StreamInfo => new AudioStreamInfo(_channels, _sampleRate, TimeSpan.FromSeconds(Duration));

    /// <summary>
    /// Width every clip decodes to.
    /// </summary>
    public int Channels => _channels;

    /// <summary>
    /// The clips on the timeline, in the order they were added.
    /// </summary>
    public IReadOnlyList<SourceClip> Clips
    {
        get { lock (_clipLock) { return _clips.ToArray(); } }
    }

    /// <summary>
    /// Content position in seconds.
    /// </summary>
    public override double Position => _rustNativePosition;

    /// <summary>
    /// End of the last clip in content seconds, zero while the group is empty.
    /// </summary>
    public override double Duration
    {
        get
        {
            lock (_clipLock)
            {
                double _end = 0.0;
                foreach (SourceClip clip in _clips)
                    _end = Math.Max(_end, clip.EndSeconds);
                return _end;
            }
        }
    }

    /// <inheritdoc/>
    public override bool IsEndOfStream => _isEndOfStream;

    /// <summary>
    /// Tempo multiplier, 1.0 = normal. Clamped to 0.8x..1.2x.
    /// </summary>
    public override float Tempo
    {
        get => _tempo;
        set
        {
            _tempo = Math.Clamp(value, AudioConstants.MinTempo, AudioConstants.MaxTempo);
            lock (_rustBackendLock)
            {
                if (_rustTrack is not null) _rustTrack.Tempo = _tempo;
            }
        }
    }

    /// <summary>
    /// Pitch shift in semitones, -12..+12.
    /// </summary>
    public override float PitchShift
    {
        get => _pitchShift;
        set
        {
            _pitchShift = Math.Clamp(value, -12.0f, 12.0f);
            lock (_rustBackendLock)
            {
                if (_rustTrack is not null) _rustTrack.PitchSemitones = _pitchShift;
            }
        }
    }

    /// <inheritdoc/>
    public double StartOffset
    {
        get => _startOffset;
        set => _startOffset = value;
    }

    /// <inheritdoc/>
    public bool IsAttachedToClock => _masterClock != null;

    #endregion

    #region Clips

    /// <summary>
    /// Loads a file and puts it on the timeline at startSeconds (content time). A file short
    /// enough for memory is decoded right here, so keep this off the UI thread.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="startSeconds"></param>
    public SourceClip AddClip(string filePath, double startSeconds)
    {
        ThrowIfDisposed();

        GroupClip _data = GroupClip.Open(filePath, _sampleRate, _channels, _memoryMaxSeconds);
        var _clip = new SourceClip(this, _data, Math.Max(0.0, startSeconds));

        lock (_clipLock)
        {
            _clips.Add(_clip);
            _placeOnTrack(_clip);
        }

        return _clip;
    }

    /// <summary>
    /// Takes a clip off the timeline and releases its audio. False when it isn't on this group.
    /// </summary>
    /// <param name="clip"></param>
    public bool RemoveClip(SourceClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

        lock (_clipLock)
        {
            if (!_clips.Remove(clip)) return false;

            lock (_rustBackendLock)
            {
                if (clip.NativeId is ulong _id) _rustGroupTrack?.RemoveClip(_id);
            }

            clip.Orphan();
            clip.Data.Dispose();
            return true;
        }
    }

    /// <summary>
    /// Moves a clip; <see cref="SourceClip.StartSeconds"/> lands here.
    /// </summary>
    internal void MoveClip(SourceClip clip, double startSeconds)
    {
        double _start = Math.Max(0.0, startSeconds);

        lock (_clipLock)
        {
            clip.SetStart(_start);

            lock (_rustBackendLock)
            {
                if (clip.NativeId is ulong _id) _rustGroupTrack?.MoveClip(_id, TimeSpan.FromSeconds(_start));
            }
        }
    }

    /// <summary>
    /// Puts one clip on the current native group, if there is one. A clip the group refuses
    /// stays in the list, silent, rather than taking the whole group down.
    /// Call under _clipLock.
    /// </summary>
    private void _placeOnTrack(SourceClip clip)
    {
        lock (_rustBackendLock)
        {
            if (_rustGroupTrack is null) return;

            try
            {
                clip.NativeId = _rustGroupTrack.AddClip(clip.Data, TimeSpan.FromSeconds(clip.StartSeconds));
            }
            catch (Exception ex)
            {
                clip.NativeId = null;
                Log.Error($"[GroupSource] Clip '{Path.GetFileName(clip.FilePath)}' could not be placed", ex);
            }
        }
    }

    #endregion

    #region Playback

    /// <summary>
    /// Silence: a group plays on the native chain only, there is no managed decode behind it.
    /// </summary>
    public override int ReadSamples(Span<float> buffer, int frameCount)
    {
        ThrowIfDisposed();
        FillWithSilence(buffer, frameCount * _channels);
        return frameCount;
    }

    /// <summary>
    /// Moves to a content position. False past the last clip end.
    /// </summary>
    public override bool Seek(double positionInSeconds)
    {
        ThrowIfDisposed();

        if (positionInSeconds < 0 || positionInSeconds > Duration) return false;

        _isEndOfStream = false;
        return _rustNativeSeek(positionInSeconds);
    }

    /// <inheritdoc/>
    public override void Play()
    {
        ThrowIfDisposed();
        _rustNativePlay();
    }

    /// <inheritdoc/>
    public override void Pause()
    {
        ThrowIfDisposed();
        lock (_rustBackendLock) { _rustTrack?.Pause(); }
        base.Pause();
    }

    /// <inheritdoc/>
    public override void Stop()
    {
        ThrowIfDisposed();
        lock (_rustBackendLock) { _rustTrack?.Stop(); }
        base.Stop();
    }

    #endregion

    #region Master clock

    /// <inheritdoc/>
    public void AttachToClock(MasterClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (ReferenceEquals(_masterClock, clock)) return;

        DetachFromClock();
        _masterClock = clock;

        double _target = _masterClock.CurrentTimestamp - _startOffset;
        if (!(_target > 0 && Seek(_target))) Seek(0);
    }

    /// <inheritdoc/>
    public void DetachFromClock() => _masterClock = null;

    /// <summary>
    /// Silence, same reason as <see cref="ReadSamples"/>.
    /// </summary>
    public bool ReadSamplesAtTime(double masterTimestamp, Span<float> buffer, int frameCount, out ReadResult result)
    {
        ThrowIfDisposed();
        FillWithSilence(buffer, frameCount * _channels);
        result = ReadResult.CreateSuccess(frameCount);
        return true;
    }

    #endregion

    /// <summary>
    /// Drops the native backend and the loaded audio of every clip.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;

        _disposed = true;
        base.Dispose(disposing);

        if (!disposing) return;

        _disposeRustBackend();

        lock (_clipLock)
        {
            foreach (SourceClip clip in _clips)
            {
                clip.Orphan();
                clip.Data.Dispose();
            }
            _clips.Clear();
        }
    }
}
