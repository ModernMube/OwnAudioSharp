using System;
using OwnaudioNET.Core;

namespace OwnaudioNET.Sources;

/// <summary>
/// Extension methods for BaseAudioSource to add output level monitoring.
/// This partial class extends BaseAudioSource without modifying the original implementation.
/// </summary>
public partial class BaseAudioSource
{
    private (float left, float right) _outputLevels;
    private double _lastPositionReported;
    private DateTime _lastPositionUpdate = DateTime.UtcNow;

    /// <summary>
    /// Gets the current output audio levels (left and right channels).
    /// Values range from 0.0 (silence) to 1.0 (maximum).
    /// </summary>
    public (float left, float right) OutputLevels => _outputLevels;

    /// <summary>
    /// Event that fires when the playback position changes significantly.
    /// Throttled to prevent excessive event firing.
    /// </summary>
    public event EventHandler? PositionChanged;

    /// <summary>
    /// Calculates the audio levels from the provided buffer.
    /// Should be called after ReadSamples to update OutputLevels.
    /// </summary>
    /// <param name="buffer">The audio buffer to analyze.</param>
    /// <param name="sampleCount">The number of samples in the buffer.</param>
    protected void CalculateOutputLevels(Span<float> buffer, int sampleCount)
    {
        if (sampleCount == 0)
        {
            _outputLevels = (0f, 0f);
            return;
        }

        float leftSum = 0f;
        float rightSum = 0f;
        int frameCount = sampleCount / Config.Channels;

        if (Config.Channels == 2)
        {
            // Stereo: calculate separate left and right levels
            for (int i = 0; i < frameCount; i++)
            {
                leftSum += Math.Abs(buffer[i * 2]);
                rightSum += Math.Abs(buffer[i * 2 + 1]);
            }

            _outputLevels = (
                leftSum / frameCount,
                rightSum / frameCount
            );
        }
        else // Mono
        {
            // Mono: use same value for both channels
            for (int i = 0; i < sampleCount; i++)
            {
                leftSum += Math.Abs(buffer[i]);
            }

            float level = leftSum / sampleCount;
            _outputLevels = (level, level);
        }
    }

    /// <summary>
    /// Updates the position change notification.
    /// Throttled to fire events only when position changes significantly (>50ms).
    /// </summary>
    protected void UpdatePositionChanged()
    {
        var now = DateTime.UtcNow;
        var timeSinceUpdate = (now - _lastPositionUpdate).TotalMilliseconds;

        // Only fire event every 50ms and when position actually changed
        if (timeSinceUpdate >= 50 && Math.Abs(Position - _lastPositionReported) > 0.01)
        {
            _lastPositionReported = Position;
            _lastPositionUpdate = now;
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Override this in derived classes to call CalculateOutputLevels after reading samples.
    /// </summary>
    protected virtual void OnSamplesRead(Span<float> buffer, int samplesRead)
    {
        CalculateOutputLevels(buffer, samplesRead);
        UpdatePositionChanged();
    }
}
