using System;
using System.Threading;
using Ownaudio.Safe;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// Pumps a <see cref="StreamingAudioDecoder"/> into an <see cref="AudioTrack"/> on its own
/// background thread. Writes are non-blocking, so on a full ring or a starved decoder the
/// pump just parks briefly instead of spinning. Filling the buffer doesn't start playback —
/// that's the session's job. Decoder rate/channels must match the session.
/// </summary>
public sealed class TrackFeeder : IDisposable
{
    #region Fields

    /// <summary>
    /// Park time (ms) when we can't make progress either way.
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
    /// Rewind on EOS and keep going instead of finishing.
    /// </summary>
    private volatile bool _loop;

    /// <summary>
    /// Seek request handed over to the pump thread — the decoder is only ever touched
    /// from there. Both this and the target are guarded by _sync.
    /// </summary>
    private bool _seekRequested;
    private double _seekTargetSeconds;

    #endregion

    #region Construction

    /// <summary>
    /// Builds a feeder draining decoder into track. leaveDecoderOpen keeps the decoder's
    /// ownership with the caller, otherwise we dispose it with ourselves.
    /// </summary>
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
    /// Fires once when the pump loop ends — EOS, stopped or faulted. Raised on a pool
    /// thread, so a handler may call Stop or Dispose safely.
    /// </summary>
    public event EventHandler<TrackFeedCompletedEventArgs>? Completed;

    #endregion

    #region Propertyes

    /// <summary>
    /// The track we fill.
    /// </summary>
    public AudioTrack Track => _track;

    /// <summary>
    /// True while the pump thread is alive.
    /// </summary>
    public bool IsRunning => _running;

    /// <summary>
    /// Loop playback: on EOS we rewind the decoder and keep feeding, no gap.
    /// </summary>
    public bool Loop
    {
        get => _loop;
        set => _loop = value;
    }

    #endregion

    #region Seeking

    /// <summary>
    /// Asks for a reposition. Done on the pump thread, which also dumps the track's
    /// stale look-ahead so the pre-seek audio doesn't play first. If the pump isn't
    /// running it lands at the next start.
    /// </summary>
    /// <param name="position"></param>
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
    /// Fires up the pump thread.
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_pumpThread is not null)
                throw new InvalidOperationException("The feeder has already been started.");

            _running = true;
            _pumpThread = new Thread(_pumpLoop)
            {
                IsBackground = true,
                Name = "OwnAudio.TrackFeeder",
            };
            _pumpThread.Start();
        }
    }

    /// <summary>
    /// Signals the pump to quit and waits it out. Idempotent.
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
            thread.Join();

        lock (_sync) { _pumpThread = null; }
    }

    #endregion

    #region Pump loop

    private void _pumpLoop()
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
                            if (!_loop) break;

                            _decoder.Seek(TimeSpan.Zero);
                            continue;
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

                if (accepted == 0) _wake.Wait(IdlePollMilliseconds);
            }

            if (_stopRequested) reason = TrackFeedEndReason.Stopped;
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

        _raiseCompleted(reason, error);
    }

    private void _raiseCompleted(TrackFeedEndReason reason, Exception? error)
    {
        EventHandler<TrackFeedCompletedEventArgs>? handler = Completed;
        if (handler is null) { return; }

        var args = new TrackFeedCompletedEventArgs(reason, error);
        ThreadPool.QueueUserWorkItem(_ => handler(this, args));
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Stops the pump and, unless told to leave it open, disposes the decoder too. The
    /// track stays, it's the session's.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            _disposed = true;
        }

        Stop();
        _wake.Dispose();

        if (!_leaveDecoderOpen) _decoder.Dispose();
    }

    #endregion
}
