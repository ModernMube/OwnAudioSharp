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
        /// Default settings with balanced parameters
        /// </summary>
        Default,
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
            ValidateAndSetMaxGain(maxGainValue);
            ValidateAndSetSampleRate(sampleRateHz);

            InitializeRmsBuffer(rmsWindowSeconds);
        }

        /// <summary>
        /// Creates a new DynamicAmp instance with a predefined preset
        /// </summary>
        /// <param name="preset">The preset configuration to apply</param>
        /// <param name="sampleRateHz">Sample rate in Hz (default: 44100)</param>
        /// <param name="rmsWindowSeconds">RMS calculation window in seconds (default: 0.3)</param>
        public DynamicAmp(DynamicAmpPreset preset, float sampleRateHz = 44100.0f, float rmsWindowSeconds = 0.3f)
        {
            ValidateAndSetSampleRate(sampleRateHz);
            InitializeRmsBuffer(rmsWindowSeconds);
            SetPreset(preset);
            currentGain = 3.0f;
        }

        /// <summary>
        /// Initializes the RMS calculation buffer
        /// </summary>
        /// <param name="windowSeconds">RMS window length in seconds</param>
        private void InitializeRmsBuffer(float windowSeconds)
        {
            rmsWindowLength = Math.Max(1, (int)(sampleRate * windowSeconds));
            rmsBuffer = new float[rmsWindowLength];

            float estimatedInputRms = DbToLinear(-16.0f); 
            float initialRmsSquared = estimatedInputRms * estimatedInputRms;

            // Fill buffer with estimated value
            for (int i = 0; i < rmsWindowLength; i++)
            {
                rmsBuffer[i] = initialRmsSquared;
            }

            rmsBufferIndex = 0;
            windowSumSquares = initialRmsSquared * rmsWindowLength;
            bufferFilled = true;
        }

        /// <summary>
        /// Validates and sets the target level parameter
        /// </summary>
        /// <param name="levelDb">Target level in dB</param>
        /// <exception cref="ArgumentException">If level is outside valid range</exception>
        private void ValidateAndSetTargetLevel(float levelDb)
        {
            if (levelDb < -40.0f || levelDb > 0.0f)
            {
                throw new ArgumentException($"The target level value must be between -40.0 and 0.0 dB. Resulting value: {levelDb}");
            }
            targetRmsLevelDb = levelDb;
        }

        /// <summary>
        /// Validates and sets the attack time parameter
        /// </summary>
        /// <param name="timeInSeconds">Attack time in seconds</param>
        /// <exception cref="ArgumentException">If time is too small</exception>
        private void ValidateAndSetAttackTime(float timeInSeconds)
        {
            if (timeInSeconds < 0.001f)
            {
                throw new ArgumentException($"The attack time must be at least 0.001 seconds. Result: {timeInSeconds}");
            }
            attackTime = timeInSeconds;
        }

        /// <summary>
        /// Validates and sets the release time parameter
        /// </summary>
        /// <param name="timeInSeconds">Release time in seconds</param>
        /// <exception cref="ArgumentException">If time is too small</exception>
        private void ValidateAndSetReleaseTime(float timeInSeconds)
        {
            if (timeInSeconds < 0.001f)
            {
                throw new ArgumentException($"The release time must be at least 0.001 seconds. Result: {timeInSeconds}");
            }
            releaseTime = timeInSeconds;
        }

        /// <summary>
        /// Validates and sets the noise gate parameter
        /// </summary>
        /// <param name="threshold">Noise threshold value</param>
        /// <exception cref="ArgumentException">If threshold is outside valid range</exception>
        private void ValidateAndSetNoiseGate(float threshold)
        {
            if (threshold < 0.0f || threshold > 1.0f)
            {
                throw new ArgumentException($"The noise threshold value must be between 0 and 1. The resulting value is: {threshold}");
            }
            noiseGate = threshold;
        }

        /// <summary>
        /// Validates and sets the maximum gain parameter
        /// </summary>
        /// <param name="gain">Maximum gain value</param>
        /// <exception cref="ArgumentException">If gain is not positive</exception>
        private void ValidateAndSetMaxGain(float gain)
        {
            if (gain <= 0.0f)
            {
                throw new ArgumentException($"Maximum gain must be greater than 0. Value: {gain}");
            }
            maxGain = gain;
        }

        /// <summary>
        /// Validates and sets the sample rate parameter
        /// </summary>
        /// <param name="rate">Sample rate in Hz</param>
        /// <exception cref="ArgumentException">If sample rate is not positive</exception>
        private void ValidateAndSetSampleRate(float rate)
        {
            if (rate <= 0.0f)
            {
                throw new ArgumentException($"Sample rate must be greater than 0. Value: {rate}");
            }
            sampleRate = rate;
        }

        /// <summary>
        /// Gets or sets the target volume level in dB (between -40.0 - 0.0)
        /// </summary>
        public float TargetLevel
        {
            get => targetRmsLevelDb;
            set => ValidateAndSetTargetLevel(value);
        }

        /// <summary>
        /// Gets or sets the attack time in seconds (minimum 0.001)
        /// </summary>
        public float AttackTime
        {
            get => attackTime;
            set => ValidateAndSetAttackTime(value);
        }

        /// <summary>
        /// Gets or sets the release time in seconds (minimum 0.001)
        /// </summary>
        public float ReleaseTime
        {
            get => releaseTime;
            set => ValidateAndSetReleaseTime(value);
        }

        /// <summary>
        /// Gets or sets the noise threshold value (between 0.0 - 1.0)
        /// </summary>
        public float NoiseGate
        {
            get => noiseGate;
            set => ValidateAndSetNoiseGate(value);
        }

        /// <summary>
        /// Gets or sets the maximum allowed gain (must be greater than 0)
        /// </summary>
        public float MaxGain
        {
            get => maxGain;
            set => ValidateAndSetMaxGain(value);
        }

        /// <summary>
        /// Gets or sets the sample rate in Hz (must be greater than 0)
        /// </summary>
        public float SampleRate
        {
            get => sampleRate;
            set
            {
                float oldWindowSeconds = rmsWindowLength / sampleRate;
                ValidateAndSetSampleRate(value);
                InitializeRmsBuffer(oldWindowSeconds);
            }
        }

        /// <summary>
        /// Processes incoming audio samples and adjusts the volume to the appropriate level
        /// </summary>
        /// <param name="samples">Array of audio samples to process in-place</param>
        public override void Process(Span<float> samples)
        {
            if (samples.Length == 0) return;

            float bufferRms = UpdateRmsWindow(samples);

            if (bufferRms < noiseGate)
            {
                return;
            }

            float targetRmsLinear = DbToLinear(targetRmsLevelDb);
            float targetGain = Math.Min(targetRmsLinear / Math.Max(bufferRms, 1e-10f), maxGain);

            ApplyGain(samples, targetGain, bufferRms);
            lastRms = bufferRms;
        }

        /// <summary>
        /// Applies gain with signal integrity protection
        /// </summary>
        /// <param name="samples">Audio samples to process</param>
        /// <param name="targetGain">Target gain value</param>
        /// <param name="bufferRms">Current RMS level</param>
        private void ApplyGain(Span<float> samples, float targetGain, float bufferRms)
        {
            float maxPeak = 0.0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float absValue = Math.Abs(samples[i]);
                if (absValue > maxPeak)
                    maxPeak = absValue;
            }

            float predictedPeak = maxPeak * targetGain;
            float safeGain = targetGain;

            if (predictedPeak > 0.95f) // Leave 5% headroom
            {
                safeGain = 0.95f / Math.Max(maxPeak, 1e-10f);
                targetGain = Math.Min(targetGain, safeGain);
            }

            // Gain smoothing
            float timeConstant = (targetGain > currentGain) ? attackTime : releaseTime;
            float alpha = CalculateAlpha(timeConstant, samples.Length);

            if (targetGain < currentGain && bufferRms > lastRms * 1.5f)
            {
                alpha = CalculateAlpha(0.5f * attackTime, samples.Length);
            }

            currentGain = alpha * currentGain + (1.0f - alpha) * targetGain;

            for (int i = 0; i < samples.Length; i++)
            {
                float processedSample = samples[i] * currentGain;

                if (processedSample > 1.0f)
                    samples[i] = 1.0f;
                else if (processedSample < -1.0f)
                    samples[i] = -1.0f;
                else
                    samples[i] = processedSample;
            }
        }

        /// <summary>
        /// Sets predefined preset configurations for different use cases
        /// </summary>
        /// <param name="preset">The preset configuration to apply</param>
        public void SetPreset(DynamicAmpPreset preset)
        {
            switch (preset)
            {
                case DynamicAmpPreset.Default:
                    targetRmsLevelDb = -9.0f;
                    attackTime = 0.2f;
                    releaseTime = 0.8f;
                    noiseGate = 0.005f;
                    maxGain = 10.0f;
                    break;

                case DynamicAmpPreset.Speech:
                    targetRmsLevelDb = -12.0f;
                    attackTime = 0.003f;
                    releaseTime = 0.1f;
                    noiseGate = 0.01f;
                    maxGain = 6.0f;
                    break;

                case DynamicAmpPreset.Music:
                    targetRmsLevelDb = -14.0f;
                    attackTime = 0.01f;
                    releaseTime = 0.3f;
                    noiseGate = 0.002f;
                    maxGain = 4.0f;
                    break;

                case DynamicAmpPreset.Broadcast:
                    targetRmsLevelDb = -16.0f;
                    attackTime = 0.001f;
                    releaseTime = 0.05f;
                    noiseGate = 0.008f;
                    maxGain = 8.0f;
                    break;

                case DynamicAmpPreset.Mastering:
                    targetRmsLevelDb = -8.0f;
                    attackTime = 0.0001f;
                    releaseTime = 0.02f;
                    noiseGate = 0.001f;
                    maxGain = 12.0f;
                    break;

                case DynamicAmpPreset.Live:
                    targetRmsLevelDb = -18.0f;
                    attackTime = 0.005f;
                    releaseTime = 0.2f;
                    noiseGate = 0.015f;
                    maxGain = 3.0f;
                    break;

                case DynamicAmpPreset.Transparent:
                    targetRmsLevelDb = -20.0f;
                    attackTime = 0.02f;
                    releaseTime = 0.5f;
                    noiseGate = 0.001f;
                    maxGain = 2.0f;
                    break;
            }
        }

        /// <summary>
        /// Converts dB to linear value
        /// </summary>
        /// <param name="db">Value in decibels</param>
        /// <returns>Linear amplitude value</returns>
        private static float DbToLinear(float db)
        {
            return MathF.Pow(10.0f, db / 20.0f);
        }

        /// <summary>
        /// Calculates the alpha value for exponential smoothing
        /// </summary>
        /// <param name="timeConstant">Time constant in seconds</param>
        /// <param name="bufferLength">Buffer length in samples</param>
        /// <returns>Alpha coefficient for smoothing</returns>
        private float CalculateAlpha(float timeConstant, int bufferLength)
        {
            return MathF.Exp(-bufferLength / (timeConstant * sampleRate));
        }

        /// <summary>
        /// Resets the dynamic amplifier's internal state by clearing the RMS window, 
        /// resetting gain and envelope values. Does not modify any settings or parameters.
        /// </summary>
        public override void Reset()
        {
            currentGain = 3.0f;
            lastRms = 0.0f;
            windowSumSquares = 0.0f;
            rmsBufferIndex = 0;
            bufferFilled = false;

            if (rmsBuffer != null)
            {
                Array.Clear(rmsBuffer, 0, rmsBuffer.Length);
            }
        }

        /// <summary>
        /// Updates the RMS window and calculates the current RMS value using circular buffer
        /// </summary>
        /// <param name="samples">New samples to add to the window</param>
        /// <returns>Current RMS value over the window</returns>
        private float UpdateRmsWindow(Span<float> samples)
        {
            // Calculate mean square for this buffer
            float bufferSumSquares = 0.0f;
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
        /// Gets the current gain value
        /// </summary>
        public float CurrentGain => currentGain;

        /// <summary>
        /// Gets the last calculated RMS value
        /// </summary>
        public float LastRms => lastRms;
    }
}
