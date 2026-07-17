using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.BufferManagement;
using OwnaudioNET.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Events;

namespace OwnaudioNET.Sources;

/// <summary>
/// Live capture source (mic, line-in). Capture runs fully on the native rust side - the input
/// stream writes straight into the track ring, managed side is just a controller (start/stop,
/// metering). Input must be enabled in AudioConfig.
/// </summary>
public sealed partial class InputSource : BaseAudioSource
{
    private readonly AudioConfig _config;

    private double _currentPosition;
    private bool _disposed;

    /// <inheritdoc/>
    public override AudioConfig Config => _config;

    /// <inheritdoc/>
    public override AudioStreamInfo StreamInfo => new AudioStreamInfo(
        channels: _config.Channels,
        sampleRate: _config.SampleRate,
        duration: TimeSpan.Zero);

    /// <inheritdoc/>
    public override double Position => Interlocked.CompareExchange(ref _currentPosition, 0, 0);

    /// <inheritdoc/>
    public override double Duration => 0.0;

    /// <inheritdoc/>
    public override bool IsEndOfStream => false;

    /// <summary>
    /// Needs a running engine. No managed capture thread - the mixer opens the native capture
    /// and attaches the track when this source is added.
    /// </summary>
    /// <param name="engine"></param>
    /// <param name="bufferSizeInFrames"></param>
    public InputSource(AudioEngineWrapper engine, int bufferSizeInFrames = 8192)
    {
        ArgumentNullException.ThrowIfNull(engine);

        _config = engine.Config;
        if(!engine.IsRunning)
            throw new InvalidOperationException("Audio engine must be running to create InputSource. Call OwnaudioNet.Start() first.");

        _rustNative = OwnaudioNET.Engine.RustNativeChain.Enabled;
    }

    /// <summary>
    /// Always silence - the captured audio lives in the native track ring and gets mixed natively,
    /// managed ReadSamples has nothing to hand back. Kept for API compat.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="frameCount"></param>
    /// <returns></returns>
    public override int ReadSamples(Span<float> buffer, int frameCount)
    {
        ThrowIfDisposed();

        FillWithSilence(buffer, frameCount * _config.Channels);
        return frameCount;
    }

    /// <summary>
    /// No seeking on live input.
    /// </summary>
    /// <param name="positionInSeconds"></param>
    /// <returns></returns>
    public override bool Seek(double positionInSeconds) => false;

    /// <inheritdoc/>
    public override void Play()
    {
        ThrowIfDisposed();
        _rustPlay();
    }

    /// <inheritdoc/>
    public override void Pause()
    {
        ThrowIfDisposed();
        _rustPause();
    }

    /// <inheritdoc/>
    public override void Stop()
    {
        ThrowIfDisposed();
        _rustStop();
    }

    /// <summary>
    /// L/R peak levels of the live capture, from the native metering, scaled by Volume.
    /// (0,0) when not playing or not yet attached to a mixer. Mono gives identical L/R.
    /// </summary>
    /// <returns></returns>
    public (float left, float right) GetInputLevels()
    {
        ThrowIfDisposed();
        return State == AudioState.Playing ? _rustInputLevels() : (0f, 0f);
    }

    /// <summary>
    /// Detaches the native capture backend.
    /// </summary>
    /// <param name="disposing"></param>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing) _disposeRustBackend();
            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
