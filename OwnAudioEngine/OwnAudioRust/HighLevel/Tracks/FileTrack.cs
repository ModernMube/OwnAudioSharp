using System;
using System.Threading;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// File-backed track where the whole audio path stays native: a Rust prefetch thread
/// owns the decoder and feeds the track, no managed pump anywhere. Looping and EOS are
/// handled down there too; we only toggle Loop, ask for a Seek and watch for the end.
/// </summary>
public sealed class FileTrack : IDisposable
{
    #region Fields

    /// <summary>
    /// How often we poke the finished latch. It's a single native flag read, no audio.
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
    /// Wraps a native file source already opened on the track. sampleRate is only used
    /// to turn seek times into frames.
    /// </summary>
    internal FileTrack(AudioTrack track, FileSourceHandle sourceHandle, float sampleRate)
    {
        _track = track;
        _sourceHandle = sourceHandle;
        _sampleRate = sampleRate;

        _finishPoll = new Timer(_pollFinished, null, FinishPollMilliseconds, FinishPollMilliseconds);
    }

    #endregion

    #region Events

    /// <summary>
    /// Fires once on end-of-stream, on a timer thread. Not raised for a looping source
    /// and not on Dispose.
    /// </summary>
    public event EventHandler<TrackFeedCompletedEventArgs>? Completed;

    #endregion

    #region Propertyes

    /// <summary>
    /// The track we render into.
    /// </summary>
    public AudioTrack Track => _track;

    /// <summary>
    /// Loop playback: on EOS the native decoder rewinds and keeps feeding seamlessly.
    /// Skipped when unchanged, the sync tick assigns this every tick.
    /// </summary>
    public bool Loop
    {
        get { lock (_sync) { return _loop; } }
        set
        {
            lock (_sync)
            {
                if (value == _loop) { return; }
                _loop = value;
                if (_disposed) { return; }

                int code = OwnAudioNative.ownaudio_v1_file_source_set_loop(
                    _sourceHandle.DangerousGetHandle(),
                    (byte)(value ? 1 : 0));
                ErrorCodeMapper.ThrowIfError(code, nameof(Loop));
            }
        }
    }

    /// <summary>
    /// True when the source hit EOS without looping. Clears once audio flows again
    /// after a Seek.
    /// </summary>
    public bool IsFinished
    {
        get
        {
            lock (_sync)
            {
                if (_disposed) { return false; }

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
    /// Asks the native decoder to reposition. Non-blocking — the prefetch thread does it
    /// on its next round. This only moves the decoder; reset the track's rendered
    /// position separately with <see cref="AudioTrack.Seek"/>.
    /// </summary>
    /// <param name="position"></param>
    public void Seek(TimeSpan position)
    {
        double seconds = Math.Max(0.0, position.TotalSeconds);
        ulong frame = (ulong)Math.Round(seconds * _sampleRate);

        lock (_sync)
        {
            if (_disposed) { return; }

            int code = OwnAudioNative.ownaudio_v1_file_source_seek(_sourceHandle.DangerousGetHandle(), frame);
            ErrorCodeMapper.ThrowIfError(code, nameof(Seek));
            _completedRaised = false;
        }
    }

    #endregion

    #region Finished polling

    private void _pollFinished(object? state)
    {
        TrackFeedCompletedEventArgs? args = null;

        lock (_sync)
        {
            if (_disposed || _completedRaised || _loop) { return; }

            int code = OwnAudioNative.ownaudio_v1_file_source_is_finished(
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

    #region IDisposable

    /// <summary>
    /// Stops the poll timer and releases the native control handle. The track stays,
    /// it belongs to the session.
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
