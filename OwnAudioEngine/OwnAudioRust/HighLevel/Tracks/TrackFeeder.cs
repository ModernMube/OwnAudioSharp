using System;
using System.Threading;
using Ownaudio.Safe;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// Bridges a <see cref="StreamingAudioDecoder"/> to an <see cref="AudioTrack"/> by
/// pumping decoded samples into the track's lock-free audio feed on a dedicated
/// background thread.
/// </summary>
/// <remarks>
/// <para>
/// The pump reads interleaved <c>float</c> samples from the decoder and pushes them
/// into the track via <see cref="AudioTrack.Write(ReadOnlySpan{float})"/>.  Writing is
/// non-blocking: when the track's ring buffer is full the feeder applies back-pressure
/// (it parks briefly and retries the unwritten remainder), and when the decoder is
/// momentarily starved by its prefetch thread the feeder waits without busy-spinning.
/// </para>
/// <para>
/// Feeding only fills the track's buffer; it does not start playback.  Drive transport
/// through the owning <see cref="MultiTrackSession"/> (for example
/// <see cref="MultiTrackSession.PlayAll"/>) — until then the buffer simply fills to
/// capacity and the feeder back-pressures.
/// </para>
/// <para>
/// The decoder's output channel count and sample rate must match the session the track
/// belongs to; mismatched layouts produce misaligned audio.
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="Start"/>, <see cref="Stop"/> and
/// <see cref="Dispose"/> are safe to call from any thread but must not run concurrently
/// on the same instance.  <see cref="Completed"/> is raised on a
/// <see cref="System.Threading.ThreadPool"/> thread.
/// </para>
/// </remarks>
public sealed class TrackFeeder : IDisposable
{
    #region Fields

    /// <summary>
    /// How long the pump parks (milliseconds) when it cannot make progress because
    /// the track buffer is full or the decoder is transiently starved.
    /// </summary>
    private const int IdlePollMilliseconds = 5;

    private readonly StreamingAudioDecoder _decoder;
    private readonly AudioTrack _track;
    private readonly bool _leaveDecoderOpen;
    private readonly float[] _chunk;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly object _sync = new();

    private Thread? _pumpThread;
    private volatile bool _stopRequested;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>
    /// When <see langword="true"/>, the pump seeks the decoder back to the start on
    /// end-of-stream and keeps feeding, so playback loops seamlessly.
    /// </summary>
    private volatile bool _loop;

    /// <summary>
    /// Set by <see cref="Seek"/> to request a reposition; consumed on the pump thread
    /// so the decoder is only ever touched by one thread. Guarded by <see cref="_sync"/>.
    /// </summary>
    private bool _seekRequested;

    /// <summary>
    /// The pending seek target in seconds, valid only while <see cref="_seekRequested"/>
    /// is set. Guarded by <see cref="_sync"/>.
    /// </summary>
    private double _seekTargetSeconds;

    #endregion

    #region Construction

    /// <summary>
    /// Creates a feeder that pumps <paramref name="decoder"/> output into
    /// <paramref name="track"/>.
    /// </summary>
    /// <param name="decoder">The source decoder to drain.</param>
    /// <param name="track">The destination track to fill.</param>
    /// <param name="leaveDecoderOpen">
    /// When <see langword="false"/> (the default) the feeder disposes
    /// <paramref name="decoder"/> together with itself; pass <see langword="true"/>
    /// to keep ownership of the decoder with the caller.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="decoder"/> or <paramref name="track"/> is
    /// <see langword="null"/>.
    /// </exception>
    public TrackFeeder(StreamingAudioDecoder decoder, AudioTrack track, bool leaveDecoderOpen = false)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _track = track ?? throw new ArgumentNullException(nameof(track));
        _leaveDecoderOpen = leaveDecoderOpen;

        int channels = Math.Max(1, decoder.StreamInfo.Channels);
        int sampleRate = Math.Max(1, decoder.StreamInfo.SampleRate);
        int frames = Math.Clamp(sampleRate / 10, 256, 65_536);
        _chunk = new float[frames * channels];
    }

    #endregion

    #region Events

    /// <summary>
    /// Raised once when the pump loop terminates, whether by reaching the end of the
    /// stream, being stopped, or faulting.
    /// </summary>
    /// <remarks>
    /// Fired on a <see cref="System.Threading.ThreadPool"/> thread, never on the
    /// caller's thread or the pump thread.  Handlers may safely call
    /// <see cref="Stop"/> or <see cref="Dispose"/>.
    /// </remarks>
    public event EventHandler<TrackFeedCompletedEventArgs>? Completed;

    #endregion

    #region Properties

    /// <summary>Gets the track this feeder fills.</summary>
    public AudioTrack Track => _track;

    /// <summary>
    /// Gets a value indicating whether the pump thread is currently running.
    /// </summary>
    public bool IsRunning => _running;

    /// <summary>
    /// Gets or sets whether playback loops: on end-of-stream the decoder is rewound to
    /// the start and feeding continues seamlessly (no gap), instead of completing.
    /// </summary>
    public bool Loop
    {
        get => _loop;
        set => _loop = value;
    }

    #endregion

    #region Seeking

    /// <summary>
    /// Requests a reposition of the underlying decoder to <paramref name="position"/>.
    /// </summary>
    /// <remarks>
    /// The seek is performed on the pump thread (the only thread that touches the
    /// decoder), which also clears the track's buffered look-ahead so the stale
    /// pre-seek audio does not play first. When the pump is not running the request is
    /// applied at its next start.
    /// </remarks>
    /// <param name="position">The target playback position from the start of the stream.</param>
    public void Seek(TimeSpan position)
    {
        lock (_sync)
        {
            _seekTargetSeconds = Math.Max(0.0, position.TotalSeconds);
            _seekRequested = true;
            _wake.Set();
        }
    }

    #endregion

    #region Control

    /// <summary>
    /// Starts the background pump thread.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the feeder is disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when already started.</exception>
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_pumpThread is not null)
            {
                throw new InvalidOperationException("The feeder has already been started.");
            }

            _running = true;
            _pumpThread = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "OwnAudio.TrackFeeder",
            };
            _pumpThread.Start();
        }
    }

    /// <summary>
    /// Signals the pump thread to stop and waits for it to finish.  Idempotent.
    /// </summary>
    public void Stop()
    {
        Thread? thread;
        lock (_sync)
        {
            thread = _pumpThread;
            _stopRequested = true;
            _wake.Set();
        }

        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join();
        }

        lock (_sync)
        {
            _pumpThread = null;
        }
    }

    #endregion

    #region Pump loop

    private void PumpLoop()
    {
        TrackFeedEndReason reason = TrackFeedEndReason.EndOfStream;
        Exception? error = null;

        try
        {
            int pending = 0;
            int offset = 0;

            while (!_stopRequested)
            {
                if (_seekRequested)
                {
                    double target;
                    lock (_sync)
                    {
                        target = _seekTargetSeconds;
                        _seekRequested = false;
                    }

                    _decoder.Seek(TimeSpan.FromSeconds(target));
                    _track.ClearSource();
                    pending = 0;
                    offset = 0;
                }

                if (pending == 0)
                {
                    int read = _decoder.Read(_chunk, 0, _chunk.Length);
                    if (read <= 0)
                    {
                        if (_decoder.IsEndOfStream)
                        {
                            if (_loop)
                            {
                                _decoder.Seek(TimeSpan.Zero);
                                continue;
                            }

                            break;
                        }

                        _wake.Wait(IdlePollMilliseconds);
                        continue;
                    }

                    pending = read;
                    offset = 0;
                }

                int accepted = _track.Write(_chunk.AsSpan(offset, pending));
                offset += accepted;
                pending -= accepted;

                if (accepted == 0)
                {
                    _wake.Wait(IdlePollMilliseconds);
                }
            }

            if (_stopRequested)
            {
                reason = TrackFeedEndReason.Stopped;
            }
        }
        catch (Exception ex)
        {
            error = ex;
            reason = TrackFeedEndReason.Faulted;
        }
        finally
        {
            _running = false;
        }

        RaiseCompleted(reason, error);
    }

    private void RaiseCompleted(TrackFeedEndReason reason, Exception? error)
    {
        EventHandler<TrackFeedCompletedEventArgs>? handler = Completed;
        if (handler is null)
        {
            return;
        }

        var args = new TrackFeedCompletedEventArgs(reason, error);
        ThreadPool.QueueUserWorkItem(_ => handler(this, args));
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Stops the pump thread and, unless constructed with
    /// <c>leaveDecoderOpen: true</c>, disposes the underlying decoder.  The track is
    /// left intact (it is owned by the session).
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
        _wake.Dispose();

        if (!_leaveDecoderOpen)
        {
            _decoder.Dispose();
        }
    }

    #endregion
}
