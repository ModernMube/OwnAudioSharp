using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.BufferManagement;
using OwnaudioNET.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Events;

namespace OwnaudioNET.Sources;

/// <summary>
/// Audio source that captures audio from an input device (microphone, line-in, etc.).
/// Provides real-time audio capture with buffering to prevent dropouts.
/// </summary>
/// <remarks>
/// This source continuously captures audio from the input device when playing.
/// The captured audio is buffered in a circular buffer for smooth playback.
/// Input must be enabled in AudioConfig for this source to work.
/// </remarks>
public sealed class InputSource : BaseAudioSource
{
    private readonly AudioEngineWrapper _engine;
    private readonly CircularBuffer _captureBuffer;
    private readonly Thread _captureThread;
    private readonly ManualResetEventSlim _pauseEvent;
    private readonly AudioConfig _config;
    private readonly int _bufferSizeInFrames;

    private volatile bool _shouldStop;
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
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _bufferSizeInFrames = bufferSizeInFrames;

        // Get config from engine
        _config = engine.Config;

        // Verify input is enabled
        if (!engine.IsRunning)
        {
            throw new InvalidOperationException("Audio engine must be running to create InputSource. Call OwnaudioNet.Start() first.");
        }

        // Initialize circular buffer (samples = frames * channels)
        int bufferSizeInSamples = bufferSizeInFrames * _config.Channels;
        _captureBuffer = new CircularBuffer(bufferSizeInSamples);

        // Initialize synchronization primitives
        _pauseEvent = new ManualResetEventSlim(false);
        _shouldStop = false;
        _currentPosition = 0.0;

        // Create and start capture thread
        _captureThread = new Thread(CaptureThreadProc)
        {
            Name = $"InputSource-Capture-{Id}",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _captureThread.Start();
    }

    /// <inheritdoc/>
    public override int ReadSamples(Span<float> buffer, int frameCount)
    {
        ThrowIfDisposed();

        // If not playing, return silence
        if (State != AudioState.Playing)
        {
            FillWithSilence(buffer, frameCount * _config.Channels);
            return frameCount;
        }

        int samplesToRead = frameCount * _config.Channels;
        int samplesRead = _captureBuffer.Read(buffer.Slice(0, samplesToRead));
        int framesRead = samplesRead / _config.Channels;

        // Update position
        if (framesRead > 0)
        {
            double frameDuration = 1.0 / _config.SampleRate;
            double newPosition;
            double currentPosition;
            do
            {
                currentPosition = Interlocked.CompareExchange(ref _currentPosition, 0, 0);
                newPosition = currentPosition + (framesRead * frameDuration);
            } while (Math.Abs(Interlocked.CompareExchange(ref _currentPosition, newPosition, currentPosition) - currentPosition) > double.Epsilon);
        }

        // Check for buffer underrun
        if (framesRead < frameCount)
        {
            // Fill remaining with silence
            int remainingSamples = (frameCount - framesRead) * _config.Channels;
            FillWithSilence(buffer.Slice(samplesRead), remainingSamples);

            long currentFramePosition = (long)(Position * _config.SampleRate);
            OnBufferUnderrun(new BufferUnderrunEventArgs(
                frameCount - framesRead,
                currentFramePosition));
        }

        // Apply volume
        ApplyVolume(buffer, frameCount * _config.Channels);

        return framesRead;
    }

    /// <inheritdoc/>
    public override bool Seek(double positionInSeconds)
    {
        // Seeking is not supported for live input
        return false;
    }

    /// <inheritdoc/>
    public override void Play()
    {
        ThrowIfDisposed();
        base.Play();
        _pauseEvent.Set(); // Resume capture thread
    }

    /// <inheritdoc/>
    public override void Pause()
    {
        ThrowIfDisposed();
        base.Pause();
        _pauseEvent.Reset(); // Pause capture thread
    }

    /// <inheritdoc/>
    public override void Stop()
    {
        ThrowIfDisposed();
        base.Stop();
        _pauseEvent.Reset(); // Pause capture thread
        _captureBuffer.Clear();
    }

    /// <summary>
    /// Capture thread procedure - continuously captures audio from the input device.
    /// </summary>
    private void CaptureThreadProc()
    {
        try
        {
            while (!_shouldStop)
            {
                // Wait if paused
                if (State != AudioState.Playing)
                {
                    _pauseEvent.Wait(100);
                    continue;
                }

                // Receive audio from engine
                float[]? capturedData = _engine.Receive(out int sampleCount);

                if (capturedData != null && sampleCount > 0)
                {
                    // Write captured data to circular buffer
                    int samplesWritten = _captureBuffer.Write(capturedData.AsSpan(0, sampleCount));

                    // Return buffer to pool
                    _engine.ReturnInputBuffer(capturedData);

                    // Check if buffer is full (write failed)
                    if (samplesWritten < sampleCount)
                    {
                        // Buffer full, we're dropping samples
                        // This shouldn't happen often if buffer is sized correctly
                        int droppedSamples = sampleCount - samplesWritten;
                        int droppedFrames = droppedSamples / _config.Channels;

                        long currentFramePosition = (long)(Position * _config.SampleRate);
                        OnBufferUnderrun(new BufferUnderrunEventArgs(
                            droppedFrames,
                            currentFramePosition));
                    }
                }
                else
                {
                    // No data available, wait a bit
                    Thread.Sleep(5);
                }
            }
        }
        catch (Exception ex)
        {
            // Report error to main thread
            OnError(new AudioErrorEventArgs($"Capture thread error: {ex.Message}", ex));
        }
    }

    /// <summary>
    /// Gets the current input levels (peak levels) from the capture buffer.
    /// This monitors the captured audio for peak sample values.
    /// </summary>
    /// <returns>Tuple containing left and right channel peak levels (0.0 to 1.0), or (0, 0) if not available.</returns>
    /// <remarks>
    /// This method peeks at the current capture buffer without removing samples.
    /// The returned values are scaled by the Volume property to reflect the actual output level.
    /// For mono sources, both left and right values will be identical.
    /// </remarks>
    public (float left, float right) GetInputLevels()
    {
        ThrowIfDisposed();

        if (State != AudioState.Playing || _captureBuffer.IsEmpty)
        {
            return (0f, 0f);
        }

        try
        {
            // Create a temporary buffer to peek at current audio data
            int peekSamples = Math.Min(512 * _config.Channels, _captureBuffer.Available);
            if (peekSamples == 0)
            {
                return (0f, 0f);
            }

            Span<float> peekBuffer = stackalloc float[peekSamples];
            int actualSamples = _captureBuffer.Peek(peekBuffer);

            if (actualSamples == 0)
            {
                return (0f, 0f);
            }

            // Calculate peak levels for each channel
            float leftPeak = 0f;
            float rightPeak = 0f;
            int channels = _config.Channels;

            for (int i = 0; i < actualSamples; i += channels)
            {
                float leftSample = Math.Abs(peekBuffer[i]);
                leftPeak = Math.Max(leftPeak, leftSample);

                if (channels > 1)
                {
                    float rightSample = Math.Abs(peekBuffer[i + 1]);
                    rightPeak = Math.Max(rightPeak, rightSample);
                }
            }

            // If mono, use same value for both channels
            if (channels == 1)
            {
                rightPeak = leftPeak;
            }

            // Apply volume scaling
            leftPeak *= Volume;
            rightPeak *= Volume;

            return (leftPeak, rightPeak);
        }
        catch
        {
            return (0f, 0f);
        }
    }

    /// <summary>
    /// Releases the unmanaged resources used by the InputSource and optionally releases the managed resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // Signal capture thread to stop
                _shouldStop = true;
                _pauseEvent.Set(); // Wake up thread if waiting

                // Wait for capture thread to exit (with timeout)
                if (_captureThread.IsAlive)
                {
                    if (!_captureThread.Join(TimeSpan.FromSeconds(2)))
                    {
                        // Thread didn't exit in time, force interrupt
                        try
                        {
                            _captureThread.Interrupt();
                        }
                        catch
                        {
                            // Ignore interrupt errors
                        }
                    }
                }

                // Dispose managed resources
                _pauseEvent?.Dispose();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
