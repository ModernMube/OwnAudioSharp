using System;
using System.Threading;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// A file-backed <see cref="AudioTrack"/> whose audio is decoded entirely by the
/// native engine: a Rust prefetch thread owns the decoder and feeds the track on
/// the audio thread, with no managed pump in the loop.
/// </summary>
/// <remarks>
/// <para>
/// This is the "zero bytes in C#" replacement for the <see cref="TrackFeeder"/>
/// on the file-playback path (plan 14 / D.3). Looping and end-of-stream are
/// handled natively; the control side only toggles <see cref="Loop"/>, requests a
/// <see cref="Seek"/>, and observes completion through <see cref="IsFinished"/> and
/// the <see cref="Completed"/> event.
/// </para>
/// <para>
/// Opening the file installs the decoding source on the track, replacing the
/// track's default ring-buffer source. Feeding does not start playback; drive
/// transport through the owning <see cref="MultiTrackSession"/> (for example
/// <see cref="MultiTrackSession.PlayAll"/>).
/// </para>
/// <para>
/// <b>Thread safety:</b> the members are safe to call from any thread but must
/// not run concurrently with <see cref="Dispose"/> on the same instance.
/// <see cref="Completed"/> is raised on a <see cref="System.Threading.Timer"/>
/// (thread-pool) thread.
/// </para>
/// </remarks>
public sealed class FileTrack : IDisposable
{
    #region Fields

    /// <summary>
    /// How often (milliseconds) the finished latch is polled to raise
    /// <see cref="Completed"/>. The poll reads a single native flag; it never
    /// touches audio data.
    /// </summary>
    private const int FinishPollMilliseconds = 15;

    private readonly AudioTrack _track;
    private readonly FileSourceHandle _sourceHandle;
    private readonly float _sampleRate;
    private readonly object _sync = new();

    private Timer? _finishPoll;
    private bool _loop;
    private bool _completedRaised;
    private bool _disposed;

    #endregion

    #region Construction

    /// <summary>
    /// Wraps a native file source that was opened on <paramref name="track"/>.
    /// </summary>
    /// <param name="track">The track the file source renders into.</param>
    /// <param name="sourceHandle">The native file-source control handle.</param>
    /// <param name="sampleRate">Output sample rate, used to convert seek times to frames.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="track"/> or <paramref name="sourceHandle"/> is null.
    /// </exception>
    internal FileTrack(AudioTrack track, FileSourceHandle sourceHandle, float sampleRate)
    {
        _track = track ?? throw new ArgumentNullException(nameof(track));
        _sourceHandle = sourceHandle ?? throw new ArgumentNullException(nameof(sourceHandle));
        _sampleRate = sampleRate;

        _finishPoll = new Timer(PollFinished, null, FinishPollMilliseconds, FinishPollMilliseconds);
    }

    #endregion

    #region Events

    /// <summary>
    /// Raised once when the source reaches end-of-stream without looping.
    /// </summary>
    /// <remarks>
    /// Fired on a <see cref="System.Threading.Timer"/> thread. Not raised for a
    /// looping source, nor when the track is torn down via <see cref="Dispose"/>.
    /// </remarks>
    public event EventHandler<TrackFeedCompletedEventArgs>? Completed;

    #endregion

    #region Properties

    /// <summary>Gets the track this file source renders into.</summary>
    public AudioTrack Track => _track;

    /// <summary>
    /// Gets or sets whether playback loops: on end-of-stream the native decoder is
    /// rewound to the start and feeding continues seamlessly, instead of finishing.
    /// </summary>
    public bool Loop
    {
        get
        {
            lock (_sync)
            {
                return _loop;
            }
        }
        set
        {
            lock (_sync)
            {
                // Skip the native call when unchanged: the control-rate sync tick assigns this every
                // tick from the source, and the mirror starts at the native default (false).
                if (value == _loop)
                {
                    return;
                }
                _loop = value;
                if (_disposed)
                {
                    return;
                }

                int code = OwnAudioNative.ownaudio_v1_file_source_set_loop(
                    _sourceHandle.DangerousGetHandle(),
                    (byte)(value ? 1 : 0));
                ErrorCodeMapper.ThrowIfError(code, nameof(Loop));
            }
        }
    }

    /// <summary>
    /// Gets whether the source has reached end-of-stream without looping. Clears
    /// once audio flows again after a <see cref="Seek"/>.
    /// </summary>
    public bool IsFinished
    {
        get
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return false;
                }

                int code = OwnAudioNative.ownaudio_v1_file_source_is_finished(
                    _sourceHandle.DangerousGetHandle(),
                    out byte finished);
                ErrorCodeMapper.ThrowIfError(code, nameof(IsFinished));
                return finished != 0;
            }
        }
    }

    #endregion

    #region Seeking

    /// <summary>
    /// Requests a reposition of the native decoder to <paramref name="position"/>.
    /// </summary>
    /// <remarks>
    /// Non-blocking: the native prefetch thread performs the reposition on its next
    /// iteration and the finished latch clears once audio flows from the new
    /// position. This seeks only the decoding source; reset the owning track's
    /// rendered position separately via <see cref="AudioTrack.Seek"/>.
    /// </remarks>
    /// <param name="position">The target playback position from the start of the stream.</param>
    public void Seek(TimeSpan position)
    {
        double seconds = Math.Max(0.0, position.TotalSeconds);
        ulong frame = (ulong)Math.Round(seconds * _sampleRate);

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            int code = OwnAudioNative.ownaudio_v1_file_source_seek(
                _sourceHandle.DangerousGetHandle(),
                frame);
            ErrorCodeMapper.ThrowIfError(code, nameof(Seek));
            _completedRaised = false;
        }
    }

    #endregion

    #region Finished polling

    private void PollFinished(object? state)
    {
        bool raise = false;
        TrackFeedCompletedEventArgs? args = null;

        lock (_sync)
        {
            if (_disposed || _completedRaised || _loop)
            {
                return;
            }

            int code = OwnAudioNative.ownaudio_v1_file_source_is_finished(
                _sourceHandle.DangerousGetHandle(),
                out byte finished);
            if (code != 0 || finished == 0)
            {
                return;
            }

            _completedRaised = true;
            raise = true;
            args = new TrackFeedCompletedEventArgs(TrackFeedEndReason.EndOfStream, null);

            // Stop polling; the source has finished and will not finish twice
            // without an intervening seek (which re-arms via Seek).
            _finishPoll?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        if (raise)
        {
            Completed?.Invoke(this, args!);
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Stops finished-polling and releases the native file-source control handle.
    /// The track is left intact (it is owned by the session).
    /// </summary>
    public void Dispose()
    {
        Timer? timer;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            timer = _finishPoll;
            _finishPoll = null;
        }

        timer?.Dispose();
        _sourceHandle.Dispose();
    }

    #endregion
}
