using Ownaudio.Processors;
using System;
using System.Collections.Generic;

namespace Ownaudio.Fx
{
    /// <summary>
    /// An adaptive volume control class that can dynamically manage volume in real time
    /// while preserving audio dynamics.
    /// </summary>
    public class DynamicAmp : SampleProcessorBase
    {
        /// <summary>
        /// The target RMS volume level (between -20.0 - 0.0)
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
        private Queue<float> rmsWindow;

        /// <summary>
        /// Length of the RMS window in samples
        /// </summary>
        private int rmsWindowLength;

        /// <summary>
        /// Current sum of squares in the RMS window
        /// </summary>
        private float windowSumSquares = 0.0f;

        /// <summary>
        /// Creates a new DynamicAmp instance with the given parameters
        /// </summary>
        /// <param name="targetLevel">Target RMS level (between -20.0 - 0.0)</param>
        /// <param name="attackTimeSeconds">Attack time in seconds (minimum 0.001)</param>
        /// <param name="releaseTimeSeconds">Release time in seconds (minimum 0.001)</param>
        /// <param name="noiseThreshold">Noise threshold value (between 0.0 - 1.0)</param>
        /// <param name="maxGainValue">Maximum allowed gain (default: 10.0)</param>
        /// <param name="sampleRateHz">Sample rate in Hz (default: 44100)</param>
        /// <param name="rmsWindowSeconds">RMS calculation window in seconds (default: 0.3)</param>
        /// <exception cref="ArgumentException">If any parameter value is invalid</exception>
        public DynamicAmp(float targetLevel = -9.0f, float attackTimeSeconds = 0.2f,
                         float releaseTimeSeconds = 0.8f, float noiseThreshold = 0.005f,
                         float maxGainValue = 1.2f, float sampleRateHz = 44100.0f,
                         float rmsWindowSeconds = 0.5f)
        {
            ValidateAndSetTargetLevel(targetLevel);
            ValidateAndSetAttackTime(attackTimeSeconds);
            ValidateAndSetReleaseTime(releaseTimeSeconds);                        
            ValidateAndSetNoiseGate(noiseThreshold);
            ValidateAndSetMaxGain(maxGainValue);
            ValidateAndSetSampleRate(sampleRateHz);

            rmsWindowLength = (int)(sampleRateHz * rmsWindowSeconds);
            rmsWindow = new Queue<float>(rmsWindowLength);

            for (int i = 0; i < rmsWindowLength; i++)
            {
                rmsWindow.Enqueue(0.0f);
            }
        }

        private void ValidateAndSetTargetLevel(float level)
        {
            if (level < -20.0f || level > 0.0f)
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
            float bufferRms = UpdateRmsWindow(samples);
            float _targetRMSLevel = (float)Math.Pow(10, targetRmsLevel / 20);

            if (bufferRms < noiseGate)
            {
                return;
            }

            float targetGain = Math.Min(_targetRMSLevel / Math.Max(bufferRms, noiseGate), maxGain);

            float timeConstant = (targetGain > currentGain) ? attackTime : releaseTime;
            float alpha = MathF.Exp(-1.0f / (timeConstant * sampleRate / samples.Length));

            if (targetGain < currentGain && bufferRms > lastRms * 1.5f)
            {
                alpha = MathF.Exp(-1.0f / (0.5f * attackTime * sampleRate / samples.Length));
            }

            currentGain = alpha * currentGain + (1.0f - alpha) * targetGain;
            
            for (int i = 0; i < samples.Length; i++)
            {
                float amplifiedSample = samples[i] * currentGain;

                if (Math.Abs(amplifiedSample) > 0.9f)
                {
                    amplifiedSample = Math.Sign(amplifiedSample) * (0.9f + 0.1f * MathF.Tanh((Math.Abs(amplifiedSample) - 0.9f) / 0.1f));
                }

                samples[i] = amplifiedSample;
            }

            lastRms = bufferRms;
        }

        /// <summary>
        /// Updates the RMS window and calculates the current RMS value
        /// </summary>
        /// <param name="samples">New samples to add to the window</param>
        /// <returns>Current RMS value over the window</returns>
        private float UpdateRmsWindow(Span<float> samples)
        {
            float bufferSumSquares = 0.0f;
            for (int i = 0; i < samples.Length; i++)
            {
                bufferSumSquares += samples[i] * samples[i];
            }
            float bufferMeanSquare = bufferSumSquares / samples.Length;

            rmsWindow.Enqueue(bufferMeanSquare);
            windowSumSquares += bufferMeanSquare;

            if (rmsWindow.Count > rmsWindowLength)
            {
                windowSumSquares -= rmsWindow.Dequeue();
            }

            return MathF.Sqrt(windowSumSquares / rmsWindow.Count);
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
            ValidateAndSetSampleRate(rate);
            rmsWindowLength = (int)(sampleRate * (rmsWindowLength / (float)rmsWindow.Count));
        }
    }
}
