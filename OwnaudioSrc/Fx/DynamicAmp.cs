using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// An adaptive volume control class that can dynamically manage volume in real time
    /// while preserving audio dynamics.
    /// </summary>
    public class DynamicAmp : SampleProcessorBase
    {
        /// <summary>
        /// The target RMS volume level (between 0.0 - 1.0)
        /// </summary>
        private float targetRmsLevel;

        /// <summary>
        /// Attack time in seconds - how long it takes for the volume increase to respond
        /// </summary>
        private float attackTime;

        /// <summary>
        /// Release time in seconds - how long it takes to respond to a volume decrease
        /// </summary>
        private float releaseTime;

        /// <summary>
        /// Noise threshold - signal levels below this level are not amplified
        /// </summary>
        private float noiseGate;

        /// <summary>
        /// The current gain level
        /// </summary>
        private float currentGain = 1.0f;

        /// <summary>
        /// The previous RMS value
        /// </summary>
        private float lastRms = 0.0f;

        /// <summary>
        /// Creates a new DynamicAmp instance with the given parameters
        /// </summary>
        /// <param name="targetLevel">Target RMS level (between 0.0 - 1.0)</param>
        /// <param name="attackTimeSeconds">Attack time in seconds (minimum 0.001)</param>
        /// <param name="releaseTimeSeconds">Release time in seconds (minimum 0.001)</param>
        /// <param name="noiseThreshold">Noise threshold value (between 0.0 - 1.0)</param>
        /// <exception cref="ArgumentException">If any parameter value is invalid</exception>
        public DynamicAmp(float targetLevel = 0.2f, float attackTimeSeconds = 0.1f,
                         float releaseTimeSeconds = 0.3f, float noiseThreshold = 0.001f)
        {
            ValidateAndSetTargetLevel(targetLevel);
            ValidateAndSetAttackTime(attackTimeSeconds);
            ValidateAndSetReleaseTime(releaseTimeSeconds);
            ValidateAndSetNoiseGate(noiseThreshold);
        }

        private void ValidateAndSetTargetLevel(float level)
        {
            if (level < 0.0f || level > 1.0f)
            {
                throw new ArgumentException($"The target level value must be between 0 and 1. Resulting value: {level}");
            }
            targetRmsLevel = level;
        }

        private void ValidateAndSetAttackTime(float timeInSeconds)
        {
            if (timeInSeconds < 0.001f)
            {
                throw new ArgumentException($"The attack time must be at least 0.001 seconds. Result: {timeInSeconds}");
            }
            attackTime = timeInSeconds;
        }

        private void ValidateAndSetReleaseTime(float timeInSeconds)
        {
            if (timeInSeconds < 0.001f)
            {
                throw new ArgumentException($"The release time must be at least 0.001 seconds. Result: {timeInSeconds}");
            }
            releaseTime = timeInSeconds;
        }

        private void ValidateAndSetNoiseGate(float threshold)
        {
            if (threshold < 0.0f || threshold > 1.0f)
            {
                throw new ArgumentException($"The noise threshold value must be between 0 and 1. The resulting value is: {threshold}");
            }
            noiseGate = threshold;
        }

        /// <summary>
        /// Sets the target volume level
        /// </summary>
        /// <param name="level">Target RMS level (between 0.0 - 1.0)</param>
        /// <exception cref="ArgumentException">If the value is invalid</exception>
        public void SetTargetLevel(float level)
        {
            ValidateAndSetTargetLevel(level);
        }

        /// <summary>
        /// Processes incoming audio samples and adjusts the volume to the appropriate level
        /// </summary>
        /// <param name="samples">Array of stereo audio samples</param>
        public override void Process(Span<float> samples)
        {
            // RMS calculation
            float sumSquares = 0.0f;
            for (int i = 0; i < samples.Length; i++)
            {
                sumSquares += samples[i] * samples[i];
            }
            float rms = MathF.Sqrt(sumSquares / samples.Length);

            // Noise threshold management
            if (rms < noiseGate)
            {
                return;
            }

            // Calculate target gain
            float targetGain = targetRmsLevel / Math.Max(rms, noiseGate);

            // Using time constants for smoother transitions
            float timeConstant = (targetGain > currentGain) ? attackTime : releaseTime;
            float alpha = MathF.Exp(-1.0f / (timeConstant * 44100.0f / samples.Length));

            // Update reinforcement
            currentGain = alpha * currentGain + (1.0f - alpha) * targetGain;

            // Modify samples
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= currentGain;
            }

            lastRms = rms;
        }

        /// <summary>
        /// Sets the attack time
        /// </summary>
        /// <param name="timeInSeconds">Attack time in seconds</param>
        /// <exception cref="ArgumentException">If the value is invalid</exception>
        public void SetAttackTime(float timeInSeconds)
        {
            ValidateAndSetAttackTime(timeInSeconds);
        }

        /// <summary>
        /// Sets the release time
        /// </summary>
        /// <param name="timeInSeconds">Release time in seconds</param>
        /// <exception cref="ArgumentException">If the value is invalid</exception>
        public void SetReleaseTime(float timeInSeconds)
        {
            ValidateAndSetReleaseTime(timeInSeconds);
        }

        /// <summary>
        /// Sets the noise threshold value
        /// </summary>
        /// <param name="threshold">Noise threshold value (between 0.0 - 1.0)</param>
        /// <exception cref="ArgumentException">If the value is invalid</exception>
        public void SetNoiseGate(float threshold)
        {
            ValidateAndSetNoiseGate(threshold);
        }
    }
}
