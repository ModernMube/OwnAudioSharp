using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.BufferManagement;
using OwnaudioNET.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Events;

namespace OwnaudioNET.Sources;

/// <summary>
/// Audio source that captures audio from an input device (microphone, line-in, etc.).
/// </summary>
/// <remarks>
/// Capture runs entirely on the native Rust side (plan L): a native input stream writes straight
/// into the track ring and the mixer renders it natively, so no audio data flows through managed
/// code and the GC never touches the capture path. The managed side is only a controller
/// (start/stop, metering). Input must be enabled in AudioConfig for this source to work.
/// </remarks>
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
        duration: TimeSpan.Zero); // Live input has no duration

    /// <inheritdoc/>
    public override double Position => Interlocked.CompareExchange(ref _currentPosition, 0, 0);

    /// <inheritdoc/>
    public override double Duration => 0.0; // Live input has infinite duration

    /// <inheritdoc/>
    public override bool IsEndOfStream => false; // Live input never ends

    /// <summary>
    /// Initializes a new instance of the InputSource class.
    /// </summary>
    /// <param name="engine">The audio engine wrapper for input capture.</param>
    /// <param name="bufferSizeInFrames">Size of the capture buffer in frames (default: 8192).</param>
    /// <exception cref="ArgumentNullException">Thrown when engine is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when input is not enabled in the engine.</exception>
    public InputSource(AudioEngineWrapper engine, int bufferSizeInFrames = 8192)
    {
        ArgumentNullException.ThrowIfNull(engine);

        _config = engine.Config;
        if (!engine.IsRunning)
        {
            throw new InvalidOperationException("Audio engine must be running to create InputSource. Call OwnaudioNet.Start() first.");
        }

        _currentPosition = 0.0;

        // Device capture runs entirely on the native side: no managed capture thread is created and
        // no audio data flows through managed memory. The mixer opens the native input capture and
        // attaches the native track when this source is added to it.
        _rustNative = OwnaudioNET.Engine.RustNativeChain.Enabled;
    }

    /// <inheritdoc/>
    public override int ReadSamples(Span<float> buffer, int frameCount)
    {
        ThrowIfDisposed();

        // Live input is captured entirely on the native side: the captured audio lives in the native
        // track ring and is mixed natively, so managed ReadSamples has no samples to hand back and
        // yields silence. Retained for API compatibility (the method is part of the public source
        // contract, and the native path never routes playback through it).
        FillWithSilence(buffer, frameCount * _config.Channels);
        return frameCount;
    }

    /// <inheritdoc/>
    public override bool Seek(double positionInSeconds)
    {
        return false;
    }

    /// <inheritdoc/>
    public override void Play()
    {
        ThrowIfDisposed();
        RustNativePlay();
    }

    /// <inheritdoc/>
    public override void Pause()
    {
        ThrowIfDisposed();
        RustNativePause();
    }

    /// <inheritdoc/>
    public override void Stop()
    {
        ThrowIfDisposed();
        RustNativeStop();
    }

    /// <summary>
    /// Gets the current input levels (peak levels) of the live capture.
    /// </summary>
    /// <returns>Tuple containing left and right channel peak levels (0.0 to 1.0), or (0, 0) if not available.</returns>
    /// <remarks>
    /// The peaks are read from the native capture's own metering and scaled by the Volume property to
    /// reflect the actual output level. For mono sources, both left and right values are identical.
    /// Returns (0, 0) when the source is not playing or before it is attached to a mixer.
    /// </remarks>
    public (float left, float right) GetInputLevels()
    {
        ThrowIfDisposed();
        return State == AudioState.Playing ? RustNativeInputLevels() : (0f, 0f);
    }

    /// <summary>
    /// Releases the resources used by the InputSource and detaches the native capture backend.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                DisposeRustBackend();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
