using System;
using OwnaudioNET.Core;

namespace OwnaudioNET.Sources;

/// <summary>
/// Output level metering + throttled position events for BaseAudioSource.
/// </summary>
public partial class BaseAudioSource
{
    private (float left, float right) _outputLevels;
    private double _lastPositionReported;
    private DateTime _lastPositionUpdate = DateTime.UtcNow;

    /// <summary>
    /// Current L/R output levels, 0.0 - 1.0.
    /// </summary>
    public (float left, float right) OutputLevels => _outputLevels;

    /// <summary>
    /// Sets the levels directly, bypassing CalculateOutputLevels. The rust-native mixer uses this:
    /// audio renders on the native thread so OnSamplesRead never runs, levels come from the
    /// native track's metering on the control tick.
    /// </summary>
    /// <param name="levels"></param>
    internal void SetOutputLevels((float left, float right) levels) => _outputLevels = levels;

    /// <summary>
    /// Fires when the position moved noticeably. Throttled.
    /// </summary>
    public event EventHandler? PositionChanged;

    /// <summary>
    /// Averages abs sample values into OutputLevels. Call after ReadSamples.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="sampleCount"></param>
    protected void CalculateOutputLevels(Span<float> buffer, int sampleCount)
    {
        if (sampleCount == 0) { _outputLevels = (0f, 0f); return; }

        float _leftSum = 0f;
        float _rightSum = 0f;

        if (Config.Channels == 2)
        {
            int _frames = sampleCount / 2;
            for (int i = 0; i < _frames; i++)
            {
                _leftSum += Math.Abs(buffer[i * 2]);
                _rightSum += Math.Abs(buffer[i * 2 + 1]);
            }

            _outputLevels = (_leftSum / _frames, _rightSum / _frames);
        }
        else
        {
            for (int i = 0; i < sampleCount; i++) _leftSum += Math.Abs(buffer[i]);

            float _level = _leftSum / sampleCount;
            _outputLevels = (_level, _level);
        }
    }

    /// <summary>
    /// Fires PositionChanged if we moved >10ms and at least 50ms passed since the last event.
    /// </summary>
    protected void UpdatePositionChanged()
    {
        var _now = DateTime.UtcNow;

        if ((_now - _lastPositionUpdate).TotalMilliseconds >= 50 && Math.Abs(Position - _lastPositionReported) > 0.01)
        {
            _lastPositionReported = Position;
            _lastPositionUpdate = _now;
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Hook for derived classes - metering + position event after a read.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="samplesRead"></param>
    protected virtual void OnSamplesRead(Span<float> buffer, int samplesRead)
    {
        CalculateOutputLevels(buffer, samplesRead);
        UpdatePositionChanged();
    }
}
