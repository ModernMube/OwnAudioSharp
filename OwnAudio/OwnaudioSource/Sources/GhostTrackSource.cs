using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;

namespace OwnaudioNET.Sources;

/// <summary>
/// A special zero-allocation "ghost" audio source that acts as the master synchronization clock.
/// This source contains silent audio (0.0f samples) and is used to synchronize multiple tracks.
///
/// Key Features:
/// - Always outputs silence (no audible output)
/// - Automatically resizes to match the longest source in the sync group
/// - Uses minimal resources (mono channel, configurable sample rate)
/// - Provides the master sample position for drift correction
/// - Supports tempo changes that cascade to all synced sources
///
/// This is the "invisible conductor" that keeps all tracks perfectly in sync.
/// </summary>
public sealed class GhostTrackSource : BaseAudioSource
{
    private readonly AudioConfig _config;
    private long _totalFrames;
    private long _currentFrame;
    private double _tempo;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Gets the audio configuration for this ghost track.
    /// Ghost tracks use minimal resources: mono channel, standard sample rate.
    /// </summary>
    public override AudioConfig Config => _config;

    /// <summary>
    /// Gets the stream information for this ghost track.
    /// </summary>
    public override AudioStreamInfo StreamInfo => new AudioStreamInfo(
        channels: _config.Channels,
        sampleRate: _config.SampleRate,
        duration: TimeSpan.FromSeconds(Duration));

    /// <summary>
    /// Gets the current playback position in seconds.
    /// This is the master position that all other sources sync to.
    /// </summary>
    public override double Position
    {
        get
        {
            lock (_lock)
            {
                return (double)_currentFrame / _config.SampleRate;
            }
        }
    }

    /// <summary>
    /// Gets the total duration in seconds.
    /// This represents the length of the longest audio source in the sync group.
    /// </summary>
    public override double Duration
    {
        get
        {
            lock (_lock)
            {
                return (double)_totalFrames / _config.SampleRate;
            }
        }
    }

    /// <summary>
    /// Gets whether the ghost track has reached the end of its duration.
    /// </summary>
    public override bool IsEndOfStream
    {
        get
        {
            lock (_lock)
            {
                return _currentFrame >= _totalFrames && !Loop;
            }
        }
    }

    /// <summary>
    /// Gets or sets the tempo multiplier (1.0 = normal speed, 0.5 = half speed, 2.0 = double speed).
    /// Changes to tempo affect all synchronized sources.
    /// </summary>
    public override float Tempo
    {
        get
        {
            lock (_lock)
            {
                return (float)_tempo;
            }
        }
        set
        {
            lock (_lock)
            {
                _tempo = Math.Clamp(value, 0.1f, 4.0f); // Reasonable tempo range
            }
        }
    }

    /// <summary>
    /// Gets the total number of frames in the ghost track.
    /// </summary>
    public long TotalFrames
    {
        get
        {
            lock (_lock)
            {
                return _totalFrames;
            }
        }
    }

    /// <summary>
    /// Gets the current frame position.
    /// This is the master frame position used for synchronization.
    /// </summary>
    public long CurrentFrame
    {
        get
        {
            lock (_lock)
            {
                return _currentFrame;
            }
        }
    }

    /// <summary>
    /// Initializes a new ghost track with the specified duration.
    /// </summary>
    /// <param name="durationInSeconds">The initial duration in seconds.</param>
    /// <param name="sampleRate">Sample rate (default: 48000 Hz).</param>
    /// <param name="outputChannels">Number of output channels (default: 2 for stereo).</param>
    public GhostTrackSource(double durationInSeconds, int sampleRate = 48000, int outputChannels = 2)
    {
        if (durationInSeconds < 0)
            throw new ArgumentException("Duration must be non-negative.", nameof(durationInSeconds));

        if (sampleRate <= 0)
            throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

        if (outputChannels <= 0)
            throw new ArgumentException("Output channels must be positive.", nameof(outputChannels));

        // Ghost track uses minimal resources internally (mono)
        // but outputs in the target channel count
        _config = new AudioConfig
        {
            SampleRate = sampleRate,
            Channels = outputChannels,
            BufferSize = 512 // Standard buffer size
        };

        _totalFrames = (long)(durationInSeconds * sampleRate);
        _currentFrame = 0;
        _tempo = 1.0;
        _disposed = false;
    }

    /// <summary>
    /// Resizes the ghost track to a new duration.
    /// This is called automatically when sources are added/removed from the sync group.
    /// </summary>
    /// <param name="newDurationInSeconds">The new duration in seconds.</param>
    public void Resize(double newDurationInSeconds)
    {
        ThrowIfDisposed();

        if (newDurationInSeconds < 0)
            throw new ArgumentException("Duration must be non-negative.", nameof(newDurationInSeconds));

        lock (_lock)
        {
            long newTotalFrames = (long)(newDurationInSeconds * _config.SampleRate);

            // If we're shrinking and current position is beyond new length, clamp it
            if (_currentFrame > newTotalFrames)
            {
                _currentFrame = newTotalFrames;
            }

            _totalFrames = newTotalFrames;
        }
    }

    /// <summary>
    /// Reads samples from the ghost track.
    /// This always outputs silence (0.0f) but advances the position correctly.
    /// </summary>
    /// <param name="buffer">The buffer to fill with silence.</param>
    /// <param name="frameCount">The number of frames to read.</param>
    /// <returns>The number of frames actually read.</returns>
    public override int ReadSamples(Span<float> buffer, int frameCount)
    {
        ThrowIfDisposed();

        if (State != AudioState.Playing)
        {
            FillWithSilence(buffer, frameCount * _config.Channels);
            return frameCount;
        }

        lock (_lock)
        {
            // Calculate how many frames we can actually read
            long framesRemaining = _totalFrames - _currentFrame;
            int framesToRead = (int)Math.Min(frameCount, framesRemaining);

            if (framesToRead <= 0)
            {
                if (Loop)
                {
                    // Loop back to start
                    _currentFrame = 0;
                    framesToRead = (int)Math.Min(frameCount, _totalFrames);
                }
                else
                {
                    // End of stream
                    State = AudioState.EndOfStream;
                    FillWithSilence(buffer, frameCount * _config.Channels);
                    return 0;
                }
            }

            // Always output silence (this is a ghost track)
            FillWithSilence(buffer, frameCount * _config.Channels);

            // Advance position accounting for tempo
            long frameAdvance = (long)(framesToRead * _tempo);
            _currentFrame += frameAdvance;

            // Update sample position for sync tracking
            UpdateSamplePosition(framesToRead);

            // Apply volume (though it's all zeros anyway)
            ApplyVolume(buffer, frameCount * _config.Channels);

            return framesToRead;
        }
    }

    /// <summary>
    /// Seeks to a specific position in the ghost track.
    /// </summary>
    /// <param name="positionInSeconds">The target position in seconds.</param>
    /// <returns>True if seek was successful, false otherwise.</returns>
    public override bool Seek(double positionInSeconds)
    {
        ThrowIfDisposed();

        if (positionInSeconds < 0)
            return false;

        lock (_lock)
        {
            long targetFrame = (long)(positionInSeconds * _config.SampleRate);

            // Clamp to valid range
            targetFrame = Math.Clamp(targetFrame, 0, _totalFrames);

            _currentFrame = targetFrame;

            // Update sample position for sync tracking
            SetSamplePosition(targetFrame);

            return true;
        }
    }

    /// <summary>
    /// Starts playback of the ghost track.
    /// </summary>
    public override void Play()
    {
        ThrowIfDisposed();
        base.Play();
    }

    /// <summary>
    /// Pauses the ghost track.
    /// </summary>
    public override void Pause()
    {
        ThrowIfDisposed();
        base.Pause();
    }

    /// <summary>
    /// Stops the ghost track and resets position to zero.
    /// </summary>
    public override void Stop()
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            _currentFrame = 0;
            ResetSamplePosition();
        }

        base.Stop();
    }

    /// <summary>
    /// Gets the current position as a frame number.
    /// This is the master position used for synchronization.
    /// </summary>
    /// <returns>The current frame position.</returns>
    public long GetCurrentFramePosition()
    {
        lock (_lock)
        {
            return _currentFrame;
        }
    }

    /// <summary>
    /// Sets the current frame position directly.
    /// Used by the synchronizer for precise positioning.
    /// </summary>
    /// <param name="framePosition">The frame position to set.</param>
    public void SetCurrentFramePosition(long framePosition)
    {
        lock (_lock)
        {
            _currentFrame = Math.Clamp(framePosition, 0, _totalFrames);
            SetSamplePosition(framePosition);
        }
    }

    /// <summary>
    /// Gets statistics about the ghost track.
    /// </summary>
    /// <returns>A string representation of the ghost track state.</returns>
    public override string ToString()
    {
        lock (_lock)
        {
            return $"GhostTrack: {Duration:F2}s, Position: {Position:F2}s, " +
                   $"Frame: {_currentFrame}/{_totalFrames}, Tempo: {_tempo:F2}x, State: {State}";
        }
    }

    /// <summary>
    /// Disposes the ghost track and releases resources.
    /// </summary>
    /// <param name="disposing">True if disposing managed resources.</param>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                Stop();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
