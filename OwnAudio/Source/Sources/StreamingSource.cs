using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using System;
using System.Threading;

namespace OwnaudioNET.Sources;

/// <summary>
/// Renders interleaved audio frames on demand for a <see cref="StreamingSource"/>.
/// Invoked on the source's pump thread, never on the audio thread, so it may take
/// locks - but it must stay allocation-free to keep the feed ahead of playback.
/// </summary>
/// <param name="buffer">Destination span, exactly <paramref name="frameCount"/> frames wide.</param>
/// <param name="frameCount">Number of frames to render.</param>
/// <param name="framePosition">
/// Absolute frame index of the first requested frame, counted from the start of the
/// timeline. Generators that must stay locked to a grid (a metronome, an LFO) derive
/// their phase from this rather than from an internal counter, so a seek repositions
/// them exactly.
/// </param>
public delegate void AudioRenderCallback(Span<float> buffer, int frameCount, long framePosition);

/// <summary>
/// An endless audio source whose samples are produced by a managed callback and pushed
/// into the engine's lock-free feed from a dedicated pump thread.
/// Unlike <see cref="SampleSource"/>, which serves a fixed buffer, this generates audio
/// continuously, so a parameter change takes effect within the feed look-ahead without
/// reloading or restarting anything.
/// </summary>
public sealed partial class StreamingSource : BaseAudioSource
{
    #region Fields

    /// <summary>
    /// How long the pump parks when it cannot make progress, either because the feed is
    /// topped up or because the source is not playing.
    /// </summary>
    private const int IdlePollMilliseconds = 4;

    /// <summary>
    /// How much audio the pump keeps queued ahead of the playhead. Deliberately far below
    /// the track's ring capacity: everything already queued was rendered with the old
    /// parameters, so this is the worst-case latency of a live parameter change.
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
    /// Absolute frame index handed to the next render call. Pump thread only.
    /// </summary>
    private long _renderFrame;

    /// <summary>
    /// Samples rendered but not yet accepted by the feed, carried to the next iteration
    /// so a partially accepted write is never re-rendered. Pump thread only.
    /// </summary>
    private int _pendingSamples;
    private int _pendingOffset;

    /// <summary>
    /// Set by <see cref="Seek"/>, consumed by the pump so only one thread ever moves the
    /// render cursor. Guarded by <see cref="_pumpLock"/>.
    /// </summary>
    private bool _seekRequested;
    private long _seekTargetFrame;

    #endregion

    #region Properties

    /// <inheritdoc/>
    public override AudioConfig Config => _config;

    /// <inheritdoc/>
    public override AudioStreamInfo StreamInfo => new AudioStreamInfo(
        channels: _config.Channels,
        sampleRate: _config.SampleRate,
        duration: TimeSpan.Zero);

    /// <inheritdoc/>
    public override double Position => _rustNative ? _rustPosition : _renderFrame / (double)_config.SampleRate;

    /// <summary>
    /// Always <see cref="double.PositiveInfinity"/>: a generator has no end.
    /// </summary>
    public override double Duration => double.PositiveInfinity;

    /// <summary>
    /// Always <see langword="false"/>: a generator never runs out of audio.
    /// </summary>
    public override bool IsEndOfStream => false;

    #endregion

    #region Construction

    /// <summary>
    /// Creates a source that pulls its audio from <paramref name="render"/>.
    /// The callback is not invoked until playback starts, so constructing a source is cheap
    /// and allocates only the pump's reusable chunk buffer.
    /// </summary>
    /// <param name="render">The generator invoked to fill the feed.</param>
    /// <param name="config">Sample rate and channel layout the generator produces.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="render"/> or <paramref name="config"/> is null.
    /// </exception>
    public StreamingSource(AudioRenderCallback render, AudioConfig config)
    {
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        int channels = Math.Max(1, _config.Channels);
        _chunkFrames = Math.Clamp(_config.SampleRate / 50, 128, 8192);
        _chunk = new float[_chunkFrames * channels];

        _rustNative = OwnaudioNET.Engine.RustNativeChain.Enabled;
    }

    #endregion

    #region Transport

    /// <inheritdoc/>
    public override void Play()
    {
        ThrowIfDisposed();

        if (_rustNative) _rustPlay();
        else base.Play();

        StartPump();
        _wake.Set();
    }

    /// <inheritdoc/>
    public override void Pause()
    {
        ThrowIfDisposed();

        if (_rustNative) _rustPause();
        else base.Pause();

        _wake.Set();
    }

    /// <inheritdoc/>
    public override void Stop()
    {
        ThrowIfDisposed();

        if (_rustNative) _rustStop();
        else base.Stop();

        RequestSeek(0);
        _wake.Set();
    }

    /// <summary>
    /// Moves the generator to <paramref name="positionInSeconds"/>. The render cursor is
    /// repositioned on the pump thread, which also drops the stale look-ahead so the audio
    /// after the seek is generated for the new position.
    /// </summary>
    /// <param name="positionInSeconds">Target position; negative values clamp to zero.</param>
    public override bool Seek(double positionInSeconds)
    {
        ThrowIfDisposed();

        double target = Math.Max(0.0, positionInSeconds);
        RequestSeek((long)Math.Round(target * _config.SampleRate));

        _wake.Set();
        return true;
    }

    private void RequestSeek(long frame)
    {
        lock (_pumpLock)
        {
            _seekTargetFrame = frame;
            _seekRequested = true;
        }
    }

    #endregion

    #region Managed Read Path

    /// <summary>
    /// Renders straight into the caller's buffer. Only used off the rust-native chain;
    /// with the native feed the pump owns the generator instead.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="frameCount">Frames to produce.</param>
    public override int ReadSamples(Span<float> buffer, int frameCount)
    {
        ThrowIfDisposed();

        int sampleCount = frameCount * _config.Channels;

        if (State != AudioState.Playing)
        {
            FillWithSilence(buffer, sampleCount);
            return frameCount;
        }

        _render(buffer.Slice(0, sampleCount), frameCount, _renderFrame);
        _renderFrame += frameCount;

        ApplyVolume(buffer, sampleCount);
        OnSamplesRead(buffer, sampleCount);

        return frameCount;
    }

    #endregion

    #region Pump

    /// <summary>
    /// Spins up the feed pump if it is not already running. Idempotent, so every
    /// <see cref="Play"/> can call it without tracking whether the thread exists.
    /// </summary>
    private void StartPump()
    {
        lock (_pumpLock)
        {
            if (_pumpThread != null || _stopRequested) return;

            _pumpThread = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "OwnAudio.StreamingSource"
            };
            _pumpThread.Start();
        }
    }

    /// <summary>
    /// Signals the pump to exit and waits for it. Idempotent and safe from any thread
    /// other than the pump itself.
    /// </summary>
    private void StopPump()
    {
        Thread? thread;
        lock (_pumpLock)
        {
            thread = _pumpThread;
            _stopRequested = true;
        }

        _wake.Set();

        if (thread != null && thread != Thread.CurrentThread)
            thread.Join();

        lock (_pumpLock) { _pumpThread = null; }
    }

    /// <summary>
    /// Keeps the native feed topped up to <see cref="TargetLookAheadSeconds"/> while playing.
    /// While not playing it blocks on the wake handle indefinitely rather than polling: only
    /// Play, Seek and Stop can change that state and all of them signal the handle, so a
    /// paused or stopped source costs no CPU at all. The short timed wait is reserved for the
    /// one case that resolves on its own, a feed that is momentarily topped up.
    /// </summary>
    private void PumpLoop()
    {
        int channels = Math.Max(1, _config.Channels);
        int targetSamples = (int)(TargetLookAheadSeconds * _config.SampleRate) * channels;

        while (!_stopRequested)
        {
            if (ConsumeSeekRequest()) continue;

            var track = RustTrack;
            if (track == null || State != AudioState.Playing)
            {
                _wake.Wait();
                _wake.Reset();
                continue;
            }

            int accepted;
            try
            {
                if (_pendingSamples == 0)
                {
                    int free = track.FreeSampleCount;
                    int queued = FeedCapacitySamples - free;
                    int room = Math.Min(targetSamples - queued, free);
                    if (room < channels)
                    {
                        _wake.Wait(IdlePollMilliseconds);
                        _wake.Reset();
                        continue;
                    }

                    int frames = Math.Min(_chunkFrames, room / channels);
                    _render(_chunk.AsSpan(0, frames * channels), frames, _renderFrame);
                    _renderFrame += frames;
                    _pendingSamples = frames * channels;
                    _pendingOffset = 0;
                }

                accepted = track.Write(_chunk.AsSpan(_pendingOffset, _pendingSamples));
            }
            catch (Exception ex)
            {
                OnError(new OwnaudioNET.Events.AudioErrorEventArgs($"Streaming feed failed: {ex.Message}", ex));
                return;
            }

            _pendingOffset += accepted;
            _pendingSamples -= accepted;

            if (accepted == 0)
            {
                _wake.Wait(IdlePollMilliseconds);
                _wake.Reset();
            }
        }
    }

    /// <summary>
    /// Applies a pending seek on the pump thread: drops the queued look-ahead and moves the
    /// render cursor. Returns true when the loop should restart to re-evaluate its state.
    /// </summary>
    private bool ConsumeSeekRequest()
    {
        long target;
        lock (_pumpLock)
        {
            if (!_seekRequested) return false;

            target = _seekTargetFrame;
            _seekRequested = false;
        }

        _renderFrame = target;
        _pendingSamples = 0;
        _pendingOffset = 0;

        try
        {
            RustTrack?.ResetFeed();
            if (_rustNative) _rustRebaseAfterFeedReset(target / (double)_config.SampleRate);
        }
        catch { }

        return true;
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Stops the pump thread, releases the native backend and the wake handle.
    /// </summary>
    /// <param name="disposing">True when called from <see cref="IDisposable.Dispose"/>.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopPump();
            _disposeRustBackend();
            _wake.Dispose();
        }

        base.Dispose(disposing);
    }

    #endregion
}
