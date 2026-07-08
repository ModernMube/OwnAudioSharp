using System;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// A memory-backed <see cref="AudioTrack"/> whose interleaved sample buffer is
/// owned and served entirely by the native engine: the audio thread reads the
/// buffer directly, with no managed pump in the loop.
/// </summary>
/// <remarks>
/// <para>
/// This is the "zero bytes in C#" backend for <c>SampleSource</c> — the memory
/// counterpart of <see cref="FileTrack"/>. The buffer is copied into native
/// memory once at open time (a control-thread copy, never on the audio path);
/// afterwards the managed side is only a controller: it toggles <see cref="Loop"/>,
/// requests a <see cref="Seek"/>, observes completion through <see cref="IsFinished"/>
/// / <see cref="Completed"/>, and can replace the buffer via <see cref="Reload"/>.
/// </para>
/// <para>
/// Opening installs the serving source on the track, replacing the track's default
/// ring-buffer source. It does not start playback; drive transport through the
/// owning <see cref="MultiTrackSession"/> (for example
/// <see cref="MultiTrackSession.PlayAll"/>).
/// </para>
/// <para>
/// <b>Thread safety:</b> the members are safe to call from any thread but must not
/// run concurrently with <see cref="Dispose"/> on the same instance.
/// <see cref="Completed"/> is raised on a <see cref="System.Threading.Timer"/>
/// (thread-pool) thread.
/// </para>
/// </remarks>
public sealed class MemoryTrack : IDisposable
{
    #region Fields

    /// <summary>
    /// How often (milliseconds) the finished latch is polled to raise
    /// <see cref="Completed"/>. The poll reads a single native flag; it never
    /// touches audio data.
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
    /// Wraps a native memory source that was opened on <paramref name="track"/>.
    /// </summary>
    /// <param name="track">The track the memory source serves.</param>
    /// <param name="sourceHandle">The native memory-source control handle.</param>
    /// <param name="mixerHandle">The owning mixer handle (for <see cref="Reload"/>).</param>
    /// <param name="sampleRate">Output sample rate, used to convert seek times to frames.</param>
    /// <param name="channels">Interleaved channel count, used to size reloaded buffers.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="track"/> or <paramref name="sourceHandle"/> is null.
    /// </exception>
    internal MemoryTrack(
        AudioTrack track,
        MemorySourceHandle sourceHandle,
        IntPtr mixerHandle,
        float sampleRate,
        ushort channels)
    {
        _track = track ?? throw new ArgumentNullException(nameof(track));
        _sourceHandle = sourceHandle ?? throw new ArgumentNullException(nameof(sourceHandle));
        _mixerHandle = mixerHandle;
        _sampleRate = sampleRate;
        _channels = channels;

        _finishPoll = new Timer(PollFinished, null, FinishPollMilliseconds, FinishPollMilliseconds);
    }

    #endregion

    #region Events

    /// <summary>
    /// Raised once when the source reaches end-of-buffer without looping.
    /// </summary>
    /// <remarks>
    /// Fired on a <see cref="System.Threading.Timer"/> thread. Not raised for a
    /// looping source, nor when the track is torn down via <see cref="Dispose"/>.
    /// </remarks>
    public event EventHandler<TrackFeedCompletedEventArgs>? Completed;

    #endregion

    #region Properties

    /// <summary>Gets the track this memory source serves.</summary>
    public AudioTrack Track => _track;

    /// <summary>
    /// Gets or sets whether playback loops: on end-of-buffer the native read
    /// position rewinds to the start and serving continues seamlessly, instead of
    /// finishing.
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
                if (value == _loop)
                {
                    return;
                }
                _loop = value;
                if (_disposed)
                {
                    return;
                }

                int code = OwnAudioNative.ownaudio_v1_memory_source_set_loop(
                    _sourceHandle.DangerousGetHandle(),
                    (byte)(value ? 1 : 0));
                ErrorCodeMapper.ThrowIfError(code, nameof(Loop));
            }
        }
    }

    /// <summary>
    /// Gets whether the source has reached end-of-buffer without looping. Clears
    /// once audio flows again after a <see cref="Seek"/> or <see cref="Reload"/>.
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
    /// Requests a reposition of the native read position to <paramref name="position"/>.
    /// </summary>
    /// <remarks>
    /// Non-blocking: the audio thread applies the reposition on its next read and the
    /// finished latch clears once audio flows from the new position. This seeks only
    /// the serving source; reset the owning track's rendered position separately via
    /// <see cref="AudioTrack.Seek"/>.
    /// </remarks>
    /// <param name="position">The target playback position from the start of the buffer.</param>
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

            int code = OwnAudioNative.ownaudio_v1_memory_source_seek(
                _sourceHandle.DangerousGetHandle(),
                frame);
            ErrorCodeMapper.ThrowIfError(code, nameof(Seek));
            _completedRaised = false;
        }
    }

    /// <summary>
    /// Replaces the served buffer with <paramref name="samples"/>, installing a fresh
    /// native memory source on the track (the previous source is retired off the
    /// audio thread) and restarting from the beginning.
    /// </summary>
    /// <remarks>
    /// The samples are copied once into native memory; this is a control-thread
    /// operation, not an audio-path one. Preserves the current <see cref="Loop"/>
    /// setting and re-arms completion polling.
    /// </remarks>
    /// <param name="samples">The new interleaved sample buffer.</param>
    /// <exception cref="Ownaudio.Safe.Exceptions.OwnAudioException">
    /// Thrown when the native source could not be installed.
    /// </exception>
    public void Reload(ReadOnlySpan<float> samples)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            ref readonly float first = ref samples.IsEmpty
                ? ref System.Runtime.CompilerServices.Unsafe.NullRef<float>()
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

            // Swap in the fresh control handle and release the old one (this retires
            // the previous serving source's control block; the audio-thread source is
            // retired by the command queue when the new one is installed).
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

            int code = OwnAudioNative.ownaudio_v1_memory_source_is_finished(
                _sourceHandle.DangerousGetHandle(),
                out byte finished);
            if (code != 0 || finished == 0)
            {
                return;
            }

            _completedRaised = true;
            raise = true;
            args = new TrackFeedCompletedEventArgs(TrackFeedEndReason.EndOfStream, null);

            // Stop polling; the source has finished and will not finish twice without
            // an intervening seek or reload (which re-arm the poll).
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
    /// Stops finished-polling and releases the native memory-source control handle.
    /// The track is left intact (it is owned by the session).
    /// </summary>
    public void Dispose()
    {
        Timer? timer;
        MemorySourceHandle handle;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

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
