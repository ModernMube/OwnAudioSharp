using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// Memory-backed track: the interleaved buffer lives in native memory and the audio
/// thread reads it directly. The memory counterpart of <see cref="FileTrack"/> — the
/// copy happens once at open time on the control thread, never on the audio path.
/// </summary>
public sealed class MemoryTrack : IDisposable
{
    #region Fields

    /// <summary>
    /// How often we poke the finished latch. Single native flag read, no audio.
    /// </summary>
    private const int FinishPollMilliseconds = 15;

    private readonly AudioTrack _track;
    private readonly IntPtr _mixerHandle;
    private readonly float _sampleRate;
    private readonly ushort _channels;
    private readonly object _sync = new();

    private MemorySourceHandle _sourceHandle;
    private Timer? _finishPoll;
    private bool _loop;
    private bool _completedRaised;
    private bool _disposed;

    #endregion

    #region Construction

    /// <summary>
    /// Wraps a native memory source already opened on the track. sampleRate converts
    /// seek times to frames, channels sizes reloaded buffers.
    /// </summary>
    internal MemoryTrack(
        AudioTrack track,
        MemorySourceHandle sourceHandle,
        IntPtr mixerHandle,
        float sampleRate,
        ushort channels)
    {
        _track = track;
        _sourceHandle = sourceHandle;
        _mixerHandle = mixerHandle;
        _sampleRate = sampleRate;
        _channels = channels;

        _finishPoll = new Timer(_pollFinished, null, FinishPollMilliseconds, FinishPollMilliseconds);
    }

    #endregion

    #region Events

    /// <summary>
    /// Fires once on end-of-buffer, on a timer thread. Not for a looping source and
    /// not on Dispose.
    /// </summary>
    public event EventHandler<TrackFeedCompletedEventArgs>? Completed;

    #endregion

    #region Propertyes

    /// <summary>
    /// The track we serve.
    /// </summary>
    public AudioTrack Track => _track;

    /// <summary>
    /// Loop playback: at end-of-buffer the native read position rewinds and serving
    /// carries on seamlessly.
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

                int code = OwnAudioNative.ownaudio_v1_memory_source_set_loop(
                    _sourceHandle.DangerousGetHandle(),
                    (byte)(value ? 1 : 0));
                ErrorCodeMapper.ThrowIfError(code, nameof(Loop));
            }
        }
    }

    /// <summary>
    /// True when we ran off the end without looping. Clears after a Seek or Reload.
    /// </summary>
    public bool IsFinished
    {
        get
        {
            lock (_sync)
            {
                if (_disposed) { return false; }

                int code = OwnAudioNative.ownaudio_v1_memory_source_is_finished(
                    _sourceHandle.DangerousGetHandle(),
                    out byte finished);
                ErrorCodeMapper.ThrowIfError(code, nameof(IsFinished));
                return finished != 0;
            }
        }
    }

    #endregion

    #region Seeking / reload

    /// <summary>
    /// Moves the native read position. Non-blocking, the audio thread picks it up on
    /// its next read. Only the source moves — reset the track's rendered position with
    /// <see cref="AudioTrack.Seek"/>.
    /// </summary>
    /// <param name="position"></param>
    public void Seek(TimeSpan position)
    {
        double seconds = Math.Max(0.0, position.TotalSeconds);
        ulong frame = (ulong)Math.Round(seconds * _sampleRate);

        lock (_sync)
        {
            if (_disposed) { return; }

            int code = OwnAudioNative.ownaudio_v1_memory_source_seek(_sourceHandle.DangerousGetHandle(), frame);
            ErrorCodeMapper.ThrowIfError(code, nameof(Seek));
            _completedRaised = false;
        }
    }

    /// <summary>
    /// Swaps the served buffer for a new one and restarts from the top. Copies once
    /// into native memory on this thread, keeps the Loop setting and re-arms the poll.
    /// </summary>
    /// <param name="samples">The new interleaved buffer.</param>
    public void Reload(ReadOnlySpan<float> samples)
    {
        lock (_sync)
        {
            if (_disposed) { return; }

            ref readonly float first = ref samples.IsEmpty
                ? ref Unsafe.NullRef<float>()
                : ref MemoryMarshal.GetReference(samples);

            int code = OwnAudioNative.ownaudio_v1_track_open_memory(
                _mixerHandle,
                _track.GetNativeHandle(),
                in first,
                (nuint)samples.Length,
                _channels,
                (byte)(_loop ? 1 : 0),
                out IntPtr rawSource);
            ErrorCodeMapper.ThrowIfError(code, nameof(Reload));

            MemorySourceHandle old = _sourceHandle;
            var handle = new MemorySourceHandle();
            Marshal.InitHandle(handle, rawSource);
            _sourceHandle = handle;
            old.Dispose();

            _completedRaised = false;
            _finishPoll?.Change(FinishPollMilliseconds, FinishPollMilliseconds);
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

            int code = OwnAudioNative.ownaudio_v1_memory_source_is_finished(
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
    /// Stops the poll timer and drops the native control handle. The track stays,
    /// it's the session's.
    /// </summary>
    public void Dispose()
    {
        Timer? timer;
        MemorySourceHandle handle;
        lock (_sync)
        {
            if (_disposed) { return; }

            _disposed = true;
            timer = _finishPoll;
            _finishPoll = null;
            handle = _sourceHandle;
        }

        timer?.Dispose();
        handle.Dispose();
    }

    #endregion
}
