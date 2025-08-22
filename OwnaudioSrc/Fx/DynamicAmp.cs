using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Preset options for DynamicAmp configuration
    /// </summary>
    public enum DynamicAmpPreset
    {
        /// <summary>
        /// Gentle compression for speech processing
        /// </summary>
        Speech,
        /// <summary>
        /// Moderate compression for music with preserved dynamics
        /// </summary>
        Music,
        /// <summary>
        /// Strong compression for broadcast/podcast content
        /// </summary>
        Broadcast,
        /// <summary>
        /// Aggressive limiting for maximum loudness
        /// </summary>
        Mastering,
        /// <summary>
        /// Subtle enhancement for live performance
        /// </summary>
        Live,
        /// <summary>
        /// Transparent processing for studio monitoring
        /// </summary>
        Transparent
    }

    /// <summary>
    /// An adaptive volume control class that can dynamically manage volume in real time
    /// while preserving audio dynamics.
    /// </summary>
    public class DynamicAmp : SampleProcessorBase
    {
        /// <summary>
        /// The target RMS volume level in dB (between -40.0 - 0.0)
        /// </summary>
        private float targetRmsLevelDb;

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
        /// Maximum allowed gain to prevent over-amplification
        /// </summary>
        private float maxGain;

        /// <summary>
        /// The current gain level
        /// </summary>
        private float currentGain = 1.0f;

        /// <summary>
        /// The previous RMS value
        /// </summary>
        private float lastRms = 0.0f;

        /// <summary>
        /// Sample rate of the audio being processed
        /// </summary>
        private float sampleRate;

        /// <summary>
        /// Circular buffer for RMS calculation over a longer window
        /// </summary>
        private float[]? rmsBuffer;

        /// <summary>
        /// Current index in the circular buffer
        /// </summary>
        private int rmsBufferIndex = 0;

        /// <summary>
        /// Length of the RMS window in samples
        /// </summary>
        private int rmsWindowLength;

        /// <summary>
        /// Current sum of squares in the RMS window
        /// </summary>
        private float windowSumSquares = 0.0f;

        /// <summary>
        /// Flag to track if buffer is filled
        /// </summary>
        private bool bufferFilled = false;

        /// <summary>
        /// Creates a new DynamicAmp instance with the given parameters
        /// </summary>
        /// <param name="targetLevel">Target RMS level in dB (between -40.0 - 0.0)</param>
        /// <param name="attackTimeSeconds">Attack time in seconds (minimum 0.001)</param>
        /// <param name="releaseTimeSeconds">Release time in seconds (minimum 0.001)</param>
        /// <param name="noiseThreshold">Noise threshold value (between 0.0 - 1.0)</param>
        /// <param name="maxGainValue">Maximum allowed gain (default: 10.0)</param>
        /// <param name="sampleRateHz">Sample rate in Hz (default: 44100)</param>
        /// <param name="rmsWindowSeconds">RMS calculation window in seconds (default: 0.3)</param>
        /// <exception cref="ArgumentException">If any parameter value is invalid</exception>
        public DynamicAmp(float targetLevel = -9.0f, float attackTimeSeconds = 0.2f,
                         float releaseTimeSeconds = 0.8f, float noiseThreshold = 0.005f,
                         float maxGainValue = 10.0f, float sampleRateHz = 44100.0f,
                         float rmsWindowSeconds = 0.3f)
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
                throw new ArgumentException($"The target level value must be between -40.0 and 0.0 dB. Resulting value: {levelDb}");
            }
            targetRmsLevelDb = levelDb;
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

        private void ValidateAndSetMaxGain(float gain)
        {
            if (gain <= 0.0f)
            {
                throw new ArgumentException($"Maximum gain must be greater than 0. Value: {gain}");
            }
            maxGain = gain;
        }

        private void ValidateAndSetSampleRate(float rate)
        {
            if (rate <= 0.0f)
            {
                throw new ArgumentException($"Sample rate must be greater than 0. Value: {rate}");
            }
            sampleRate = rate;
        }

        /// <summary>
        /// Sets the target volume level in dB
        /// </summary>
        /// <param name="levelDb">Target RMS level in dB (between -40.0 - 0.0)</param>
        /// <exception cref="ArgumentException">If the value is invalid</exception>
        public void SetTargetLevel(float levelDb)
        {
            ValidateAndSetTargetLevel(levelDb);
        }

        /// <summary>
        /// Processes incoming audio samples and adjusts the volume to the appropriate level
        /// </summary>
        /// <param name="samples">Array of stereo audio samples</param>
        public override void Process(Span<float> samples)
        {
            if (samples.Length == 0) return;

            float bufferRms = UpdateRmsWindow(samples);

            // Convert dB to linear
            float targetRmsLinear = DbToLinear(targetRmsLevelDb);

            // Apply noise gate
            if (bufferRms < noiseGate)
            {
                return;
            }

            // Calculate target gain with safety check
            float targetGain = Math.Min(targetRmsLinear / Math.Max(bufferRms, 1e-10f), maxGain);

            // Smooth gain changes
            float timeConstant = (targetGain > currentGain) ? attackTime : releaseTime;
            float alpha = CalculateAlpha(timeConstant, samples.Length);

            // Fast attack for sudden level increases
            if (targetGain < currentGain && bufferRms > lastRms * 1.5f)
            {
                alpha = CalculateAlpha(0.5f * attackTime, samples.Length);
            }

            currentGain = alpha * currentGain + (1.0f - alpha) * targetGain;

            // Apply gain and soft limiting
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
                float sample = samples[i];
                bufferSumSquares += sample * sample;
            }
            float bufferMeanSquare = bufferSumSquares / samples.Length;

#nullable disable
            // Update circular buffer
            if (bufferFilled)
            {
                windowSumSquares -= rmsBuffer[rmsBufferIndex];
            }

            rmsBuffer[rmsBufferIndex] = bufferMeanSquare;
            windowSumSquares += bufferMeanSquare;
#nullable restore

            rmsBufferIndex = (rmsBufferIndex + 1) % rmsWindowLength;
            if (rmsBufferIndex == 0)
            {
                bufferFilled = true;
            }

            // Calculate RMS
            int validSamples = bufferFilled ? rmsWindowLength : rmsBufferIndex;
            float meanSquare = windowSumSquares / validSamples;

            // Safety check for NaN/Infinity
            if (float.IsNaN(meanSquare) || float.IsInfinity(meanSquare) || meanSquare < 0)
            {
                return 0.0f;
            }

            return MathF.Sqrt(meanSquare);
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

        /// <summary>
        /// Sets the maximum allowed gain
        /// </summary>
        /// <param name="gain">Maximum gain value (must be greater than 0)</param>
        /// <exception cref="ArgumentException">If the value is invalid</exception>
        public void SetMaxGain(float gain)
        {
            ValidateAndSetMaxGain(gain);
        }

        /// <summary>
        /// Sets the sample rate of the audio being processed
        /// </summary>
        /// <param name="rate">Sample rate in Hz</param>
        /// <exception cref="ArgumentException">If the value is invalid</exception>
        public void SetSampleRate(float rate)
        {
            float oldWindowSeconds = rmsWindowLength / sampleRate;
            ValidateAndSetSampleRate(rate);
            InitializeRmsBuffer(oldWindowSeconds);
        }

        /// <summary>
        /// Gets the current gain value
        /// </summary>
        public float CurrentGain => currentGain;

        /// <summary>
        /// Gets the last calculated RMS value
        /// </summary>
        public float LastRms => lastRms;
    }
}