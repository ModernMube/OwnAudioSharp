using System;
using System.Threading;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// Track fed by a native group: audio files laid out on one content timeline and summed into
/// this single track, so they share its stretch, effect chain, gain, pan and route. Clips go on,
/// move and come off while it plays; the timeline, the gaps and EOS all live on the native side.
/// </summary>
public sealed class GroupTrack : IDisposable
{
    #region Fields

    /// <summary>
    /// How often we poke the finished latch. A single native flag read, no audio.
    /// </summary>
    private const int FinishPollMilliseconds = 15;

    private readonly AudioTrack _track;
    private readonly GroupSourceHandle _sourceHandle;
    private readonly float _sampleRate;
    private readonly object _sync = new();

    private Timer? _finishPoll;
    private bool _completedRaised;
    private bool _disposed;

    #endregion

    #region Construction

    /// <summary>
    /// Wraps a native group already installed on the track. sampleRate turns times into frames.
    /// </summary>
    internal GroupTrack(AudioTrack track, GroupSourceHandle sourceHandle, float sampleRate)
    {
        _track = track;
        _sourceHandle = sourceHandle;
        _sampleRate = sampleRate;

        _finishPoll = new Timer(_pollFinished, null, FinishPollMilliseconds, FinishPollMilliseconds);
    }

    #endregion

    #region Events

    /// <summary>
    /// Fires once when the cursor runs past the last clip end, on a timer thread. Not on Dispose.
    /// </summary>
    public event EventHandler<TrackFeedCompletedEventArgs>? Completed;

    #endregion

    #region Propertyes

    /// <summary>
    /// The track we render into.
    /// </summary>
    public AudioTrack Track => _track;

    /// <summary>
    /// Session rate, the unit every frame count here is in.
    /// </summary>
    public float SampleRate => _sampleRate;

    /// <summary>
    /// True once the cursor ran past the last clip end. Clears on a Seek.
    /// </summary>
    public bool IsFinished
    {
        get
        {
            lock (_sync)
            {
                if (_disposed) { return false; }

                int code = OwnAudioNative.ownaudio_v1_group_source_is_finished(
                    _sourceHandle.DangerousGetHandle(),
                    out byte finished);
                ErrorCodeMapper.ThrowIfError(code, nameof(IsFinished));
                return finished != 0;
            }
        }
    }

    /// <summary>
    /// Just past the last clip end, zero for an empty group.
    /// </summary>
    public TimeSpan End
    {
        get
        {
            lock (_sync)
            {
                if (_disposed) { return TimeSpan.Zero; }

                int code = OwnAudioNative.ownaudio_v1_group_source_get_end_frame(
                    _sourceHandle.DangerousGetHandle(),
                    out ulong frames);
                ErrorCodeMapper.ThrowIfError(code, nameof(End));
                return TimeSpan.FromSeconds(frames / (double)_sampleRate);
            }
        }
    }

    #endregion

    #region Clips

    /// <summary>
    /// Places a loaded clip on the timeline at start and returns the id to move and remove it
    /// by. A memory clip only shares its buffer, a streamed one opens its file here.
    /// </summary>
    /// <param name="clip">loaded at the session rate and this group's width</param>
    /// <param name="start">on the content timeline</param>
    public ulong AddClip(GroupClip clip, TimeSpan start)
    {
        ArgumentNullException.ThrowIfNull(clip);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            int code = OwnAudioNative.ownaudio_v1_group_source_add_clip(
                _sourceHandle.DangerousGetHandle(),
                clip.DangerousGetHandle(),
                _toFrames(start),
                out ulong id);
            ErrorCodeMapper.ThrowIfError(code, nameof(AddClip));

            _rearmFinishPoll();
            return id;
        }
    }

    /// <summary>
    /// Takes a clip off the timeline. False for an id the group doesn't hold.
    /// </summary>
    /// <param name="clipId"></param>
    public bool RemoveClip(ulong clipId)
    {
        lock (_sync)
        {
            if (_disposed) { return false; }

            int code = OwnAudioNative.ownaudio_v1_group_source_remove_clip(
                _sourceHandle.DangerousGetHandle(),
                clipId,
                out byte removed);
            ErrorCodeMapper.ThrowIfError(code, nameof(RemoveClip));
            return removed != 0;
        }
    }

    /// <summary>
    /// Moves a clip, heard from the next render block. False for an id the group doesn't hold.
    /// </summary>
    /// <param name="clipId"></param>
    /// <param name="start">on the content timeline</param>
    public bool MoveClip(ulong clipId, TimeSpan start)
    {
        lock (_sync)
        {
            if (_disposed) { return false; }

            int code = OwnAudioNative.ownaudio_v1_group_source_set_clip_start(
                _sourceHandle.DangerousGetHandle(),
                clipId,
                _toFrames(start),
                out byte found);
            ErrorCodeMapper.ThrowIfError(code, nameof(MoveClip));

            //A clip moved past where the cursor stopped gives the group something to play again
            _rearmFinishPoll();
            return found != 0;
        }
    }

    #endregion

    #region Seeking

    /// <summary>
    /// Asks the group to move its content cursor. Non-blocking. Only moves the group; reset the
    /// track's rendered position separately with <see cref="AudioTrack.Seek"/>.
    /// </summary>
    /// <param name="position"></param>
    public void Seek(TimeSpan position)
    {
        lock (_sync)
        {
            if (_disposed) { return; }

            int code = OwnAudioNative.ownaudio_v1_group_source_seek(
                _sourceHandle.DangerousGetHandle(),
                _toFrames(position));
            ErrorCodeMapper.ThrowIfError(code, nameof(Seek));

            _rearmFinishPoll();
        }
    }

    #endregion

    #region Finished polling

    /// <summary>
    /// The poll stops itself once it fired, anything that can make the group play again winds it
    /// back up. Call under _sync.
    /// </summary>
    private void _rearmFinishPoll()
    {
        _completedRaised = false;
        _finishPoll?.Change(FinishPollMilliseconds, FinishPollMilliseconds);
    }

    private void _pollFinished(object? state)
    {
        TrackFeedCompletedEventArgs? args = null;

        lock (_sync)
        {
            if (_disposed || _completedRaised) { return; }

            int code = OwnAudioNative.ownaudio_v1_group_source_is_finished(
                _sourceHandle.DangerousGetHandle(),
                out byte finished);
            if (code != 0 || finished == 0) { return; }

            _completedRaised = true;
            args = new TrackFeedCompletedEventArgs(TrackFeedEndReason.EndOfStream, null);

            _finishPoll?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        Completed?.Invoke(this, args!);
    }

    #endregion

    private ulong _toFrames(TimeSpan time)
        => (ulong)Math.Round(Math.Max(0.0, time.TotalSeconds) * _sampleRate);

    #region IDisposable

    /// <summary>
    /// Stops the poll timer and releases the native control handle. The track stays, it belongs
    /// to the session.
    /// </summary>
    public void Dispose()
    {
        Timer? timer;
        lock (_sync)
        {
            if (_disposed) { return; }

            _disposed = true;
            timer = _finishPoll;
            _finishPoll = null;
        }

        timer?.Dispose();
        _sourceHandle.Dispose();
    }

    #endregion
}
