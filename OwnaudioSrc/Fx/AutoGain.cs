using System;
using Ownaudio.Processors;

namespace Ownaudio.Fx;

/// <summary>
/// Automatic Gain Control preset
/// </summary>
public enum AutoGainPreset
{
    /// <summary>
    /// Gentle AGC for music playback - preserves dynamics
    /// Ideal for: Background music, streaming, acoustic content
    /// </summary>
    Music,

    /// <summary>
    /// Voice optimized AGC - clear and consistent speech levels
    /// Ideal for: Podcasts, voice recordings, interviews
    /// </summary>
    Voice,

    /// <summary>
    /// Broadcast ready AGC - tight level control for professional use
    /// Ideal for: Radio, streaming, commercial audio
    /// </summary>
    Broadcast,

    /// <summary>
    /// Live performance AGC - fast response with feedback prevention
    /// Ideal for: Live sound, stage use, real-time processing
    /// </summary>
    Live
}

/// <summary>
/// Simple Automatic Gain Control processor
/// </summary>
public class AutoGain : SampleProcessorBase
{
    private float targetLevel = 0.25f;   // Linear target level
    private float attackCoeff = 0.99f;   // Attack coefficient
    private float releaseCoeff = 0.999f; // Release coefficient
    private float gateThreshold = 0.001f; // Gate threshold (linear)
    private float maxGain = 4.0f;        // Maximum gain
    private float minGain = 0.25f;       // Minimum gain

    private float currentGain = 1.0f;    // Current gain
    private float level = 0.0f;          // Signal level detector

    /// <summary>
    /// Creates AGC with default settings
    /// </summary>
    public AutoGain() { }

    /// <summary>
    /// Creates AGC with preset
    /// </summary>
    public AutoGain(AutoGainPreset preset)
    {
        SetPreset(preset);
    }

    /// <summary>
    /// Process audio samples
    /// </summary>
    public override void Process(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float input = Math.Abs(samples[i]);

            // Simple level detector
            level = (input > level) ?
                attackCoeff * level + (1.0f - attackCoeff) * input :
                releaseCoeff * level + (1.0f - releaseCoeff) * input;

            // Gate check
            if (level < gateThreshold)
            {
                samples[i] *= currentGain;
                continue;
            }

            // Calculate needed gain
            float targetGain = targetLevel / Math.Max(level, 0.0001f);
            targetGain = Math.Max(minGain, Math.Min(maxGain, targetGain));

            // Smooth gain changes
            currentGain = 0.995f * currentGain + 0.005f * targetGain;

            // Apply gain
            samples[i] *= currentGain;

            // Simple soft limiting
            if (samples[i] > 0.95f) samples[i] = 0.95f;
            else if (samples[i] < -0.95f) samples[i] = -0.95f;
        }
    }

    /// <summary>
    /// Apply preset settings
    /// </summary>
    public void SetPreset(AutoGainPreset preset)
    {
        switch (preset)
        {
            case AutoGainPreset.Music:
                targetLevel = 0.2f;      // Moderate level to preserve dynamics
                attackCoeff = 0.995f;    // Very slow attack preserves transients
                releaseCoeff = 0.9995f;  // Very slow release for natural feel
                maxGain = 2.0f;          // Limited gain boost (+6dB)
                minGain = 0.5f;          // Limited attenuation (-6dB)
                gateThreshold = 0.002f;  // Higher gate to ignore quiet passages
                break;

            case AutoGainPreset.Voice:
                targetLevel = 0.3f;      // Good speech level for clarity
                attackCoeff = 0.99f;     // Medium attack for speech dynamics
                releaseCoeff = 0.999f;   // Smooth release for natural speech
                maxGain = 3.0f;          // Higher gain for quiet speakers (+9.5dB)
                minGain = 0.3f;          // Moderate attenuation (-10dB)
                gateThreshold = 0.001f;  // Lower gate for breath detection
                break;

            case AutoGainPreset.Broadcast:
                targetLevel = 0.4f;      // Hot level for broadcast standards
                attackCoeff = 0.98f;     // Fast attack for quick response
                releaseCoeff = 0.995f;   // Medium release for punch
                maxGain = 4.0f;          // High gain capability (+12dB)
                minGain = 0.2f;          // Strong attenuation (-14dB)
                gateThreshold = 0.0005f; // Very low gate for full control
                break;

            case AutoGainPreset.Live:
                targetLevel = 0.5f;      // High level for live use
                attackCoeff = 0.97f;     // Very fast attack prevents feedback
                releaseCoeff = 0.99f;    // Fast release for responsiveness
                maxGain = 2.5f;          // Limited gain to prevent feedback (+8dB)
                minGain = 0.1f;          // Can heavily attenuate loud signals (-20dB)
                gateThreshold = 0.005f;  // Higher gate for noisy live environments
                break;
        }
    }

    /// <summary>
    /// Reset processor state
    /// </summary>
    public override void Reset()
    {
        currentGain = 1.0f;
        level = 0.0f;
    }

    /// <summary>
    /// Set target level (0.0 to 1.0)
    /// </summary>
    public void SetTargetLevel(float level)
    {
        targetLevel = Math.Max(0.01f, Math.Min(1.0f, level));
    }

    /// <summary>
    /// Set attack speed (0.0 to 1.0, higher = slower)
    /// </summary>
    public void SetAttackSpeed(float speed)
    {
        attackCoeff = Math.Max(0.9f, Math.Min(0.999f, speed));
    }

    /// <summary>
    /// Set release speed (0.0 to 1.0, higher = slower)
    /// </summary>
    public void SetReleaseSpeed(float speed)
    {
        releaseCoeff = Math.Max(0.9f, Math.Min(0.9999f, speed));
    }

    /// <summary>
    /// Current gain value
    /// </summary>
    public float CurrentGain => currentGain;

    /// <summary>
    /// Current input level
    /// </summary>
    public float InputLevel => level;
}