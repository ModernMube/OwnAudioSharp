using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using System;
using System.Threading;

namespace OwnaudioNET.Sources;

/// <summary>
/// Fills a buffer with freshly generated frames. Called on the pump thread, never on the
/// audio thread, so locking is fine - but keep it allocation free.
/// framePosition is the absolute frame index of the first frame, so grid-locked
/// generators (metronome, LFO) can derive their phase from it.
/// </summary>
/// <param name="buffer"></param>
/// <param name="frameCount"></param>
/// <param name="framePosition"></param>
public delegate void AudioRenderCallback(Span<float> buffer, int frameCount, long framePosition);

/// <summary>
/// Endless source: a managed callback makes the audio, a pump thread shoves it into the
/// engine's lock-free feed. Unlike SampleSource it generates forever, so a param change
/// lands within the look-ahead without reloading anything.
/// </summary>
public sealed partial class StreamingSource : BaseAudioSource
{
    /// <summary>
    /// Pump nap time when it can't make progress (feed full, or we're not playing).
    /// </summary>
    private const int IdlePollMilliseconds = 4;

    /// <summary>
    /// How much audio we keep queued ahead. Way under the ring capacity on purpose:
    /// queued audio was rendered with the old params, so this is the param-change latency.
    /// </summary>
    private const double TargetLookAheadSeconds = 0.12;

    private readonly AudioRenderCallback _render;
    private readonly AudioConfig _config;
    private readonly float[] _chunk;
    private readonly int _chunkFrames;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly object _pumpLock = new();

    private Thread? _pumpThread;
    private volatile bool _stopRequested;

    /// <summary>
    /// Frame index the next render call gets. Pump thread only.
    /// </summary>
    private long _renderFrame;

    /// <summary>
    /// Rendered but not yet swallowed by the feed, carried over so a partial write never
    /// gets re-rendered. Pump thread only.
    /// </summary>
    private int _pendingSamples;
    private int _pendingOffset;

    /// <summary>
    /// Seek handoff: Seek() sets it, the pump consumes it, so only one thread moves the
    /// cursor. Under _pumpLock.
    /// </summary>
    private bool _seekRequested;
    private long _seekTargetFrame;

    /// <summary>
    /// Audio config this source runs at.
    /// </summary>
    public override AudioConfig Config => _config;

    /// <summary>
    /// Stream info (channels, rate, length).
    /// </summary>
    public override AudioStreamInfo StreamInfo => new AudioStreamInfo(
        channels: _config.Channels,
        sampleRate: _config.SampleRate,
        duration: TimeSpan.Zero);

    /// <summary>
    /// Playback pos in seconds. Native path asks the rust track.
    /// </summary>
    public override double Position => _rustNative ? _rustPosition : _renderFrame / (double)_config.SampleRate;

    /// <summary>
    /// Infinite - a generator has no end.
    /// </summary>
    public override double Duration => double.PositiveInfinity;

    /// <summary>
    /// Always false, we never run dry.
    /// </summary>
    public override bool IsEndOfStream => false;

    /// <summary>
    /// Wraps a generator callback. Nothing is called until playback starts, we only
    /// allocate the pump's reusable chunk here.
    /// </summary>
    /// <param name="render"></param>
    /// <param name="config"></param>
    public StreamingSource(AudioRenderCallback render, AudioConfig config)
    {
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        int _channels = Math.Max(1, _config.Channels);
        _chunkFrames = Math.Clamp(_config.SampleRate / 50, 128, 8192);
        _chunk = new float[_chunkFrames * _channels];

        _rustNative = OwnaudioNET.Engine.RustNativeChain.Enabled;
    }

    /// <summary>
    /// Starts playback and the feed pump.
    /// </summary>
    public override void Play()
    {
        ThrowIfDisposed();

        if (_rustNative) _rustPlay();
        else base.Play();

        _startPump();
        _wake.Set();
    }

    /// <summary>
    /// Pauses, pump goes to sleep on the wake handle.
    /// </summary>
    public override void Pause()
    {
        ThrowIfDisposed();

        if (_rustNative) _rustPause();
        else base.Pause();

        _wake.Set();
    }

    /// <summary>
    /// Stops and rewinds the render cursor to zero.
    /// </summary>
    public override void Stop()
    {
        ThrowIfDisposed();

        if (_rustNative) _rustStop();
        else base.Stop();

        _requestSeek(0);
        _wake.Set();
    }

    /// <summary>
    /// Jumps the generator. The pump does the actual move and drops the stale look-ahead,
    /// so audio after the seek is generated for the new spot. Negatives clamp to zero.
    /// </summary>
    /// <param name="positionInSeconds"></param>
    public override bool Seek(double positionInSeconds)
    {
        ThrowIfDisposed();

        double _target = Math.Max(0.0, positionInSeconds);
        _requestSeek((long)Math.Round(_target * _config.SampleRate));

        _wake.Set();
        return true;
    }

    /// <summary>
    /// Parks a seek target for the pump to pick up.
    /// </summary>
    /// <param name="frame"></param>
    private void _requestSeek(long frame)
    {
        lock (_pumpLock)
        {
            _seekTargetFrame = frame;
            _seekRequested = true;
        }
    }

    /// <summary>
    /// Renders straight into the caller's buffer. Managed path only, the native chain lets
    /// the pump drive the generator instead.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="frameCount"></param>
    public override int ReadSamples(Span<float> buffer, int frameCount)
    {
        ThrowIfDisposed();

        int _sampleCount = frameCount * _config.Channels;

        if (State != AudioState.Playing)
        {
            FillWithSilence(buffer, _sampleCount);
            return frameCount;
        }

        _render(buffer.Slice(0, _sampleCount), frameCount, _renderFrame);
        _renderFrame += frameCount;

        ApplyVolume(buffer, _sampleCount);
        OnSamplesRead(buffer, _sampleCount);

        return frameCount;
    }

    /// <summary>
    /// Spins up the pump if it isn't running. Idempotent, Play() can just call it.
    /// </summary>
    private void _startPump()
    {
        lock (_pumpLock)
        {
            if (_pumpThread != null || _stopRequested) return;

            _pumpThread = new Thread(_pumpLoop)
            {
                IsBackground = true,
                Name = "OwnAudio.StreamingSource"
            };
            _pumpThread.Start();
        }
    }

    /// <summary>
    /// Tells the pump to quit and waits for it. Safe from any thread but the pump itself.
    /// </summary>
    private void _stopPump()
    {
        Thread? _thread;
        lock (_pumpLock)
        {
            _thread = _pumpThread;
            _stopRequested = true;
        }

        _wake.Set();

        if (_thread != null && _thread != Thread.CurrentThread)
            _thread.Join();

        lock (_pumpLock) { _pumpThread = null; }
    }

    /// <summary>
    /// Keeps the native feed topped up to TargetLookAheadSeconds while playing. Not playing
    /// means an untimed wait, not polling - Play/Seek/Stop all signal the handle, so an idle
    /// source burns no CPU. The timed wait is only for a feed that's momentarily full.
    /// </summary>
    private void _pumpLoop()
    {
        int _channels = Math.Max(1, _config.Channels);
        int _targetSamples = (int)(TargetLookAheadSeconds * _config.SampleRate) * _channels;

        while (!_stopRequested)
        {
            if (_consumeSeekRequest()) continue;

            var _track = RustTrack;
            if (_track == null || State != AudioState.Playing)
            {
                _wake.Wait();
                _wake.Reset();
                continue;
            }

            int _accepted;
            try
            {
                if (_pendingSamples == 0)
                {
                    int _free = _track.FreeSampleCount;
                    int _room = Math.Min(_targetSamples - (FeedCapacitySamples - _free), _free);
                    if (_room < _channels)
                    {
                        _wake.Wait(IdlePollMilliseconds);
                        _wake.Reset();
                        continue;
                    }

                    int _frames = Math.Min(_chunkFrames, _room / _channels);
                    _render(_chunk.AsSpan(0, _frames * _channels), _frames, _renderFrame);
                    _renderFrame += _frames;
                    _pendingSamples = _frames * _channels;
                    _pendingOffset = 0;
                }

                _accepted = _track.Write(_chunk.AsSpan(_pendingOffset, _pendingSamples));
            }
            catch (Exception ex)
            {
                OnError(new OwnaudioNET.Events.AudioErrorEventArgs($"Streaming feed failed: {ex.Message}", ex));
                return;
            }

            _pendingOffset += _accepted;
            _pendingSamples -= _accepted;

            if (_accepted == 0)
            {
                _wake.Wait(IdlePollMilliseconds);
                _wake.Reset();
            }
        }
    }

    /// <summary>
    /// Applies a pending seek on the pump thread: dumps the queued look-ahead, moves the
    /// cursor. True means restart the loop and re-check state.
    /// </summary>
    private bool _consumeSeekRequest()
    {
        long _target;
        lock (_pumpLock)
        {
            if (!_seekRequested) return false;

            _target = _seekTargetFrame;
            _seekRequested = false;
        }

        _renderFrame = _target;
        _pendingSamples = 0;
        _pendingOffset = 0;

        try
        {
            RustTrack?.ResetFeed();
            if (_rustNative) _rustRebaseAfterFeedReset(_target / (double)_config.SampleRate);
        }
        catch { }

        return true;
    }

    /// <summary>
    /// Kills the pump, drops the native backend and the wake handle.
    /// </summary>
    /// <param name="disposing"></param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stopPump();
            _disposeRustBackend();
            _wake.Dispose();
        }

        base.Dispose(disposing);
    }
}
