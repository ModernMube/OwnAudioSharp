using System;

namespace Ownaudio.EngineTest
{
    /// <summary>
    /// Helper utilities for audio engine tests.
    /// Provides common functions for generating test audio data and validating results.
    /// </summary>
    public static class TestHelpers
    {
        /// <summary>
        /// Generates a sine wave audio signal.
        /// </summary>
        /// <param name="frequency">Frequency in Hz</param>
        /// <param name="sampleRate">Sample rate in Hz</param>
        /// <param name="channels">Number of channels (interleaved)</param>
        /// <param name="durationSeconds">Duration in seconds</param>
        /// <param name="amplitude">Amplitude (0.0 to 1.0)</param>
        /// <returns>Array of interleaved float samples</returns>
        public static float[] GenerateSineWave(
            float frequency,
            int sampleRate,
            int channels,
            double durationSeconds,
            float amplitude = 0.5f)
        {
            int frameCount = (int)(durationSeconds * sampleRate);
            int sampleCount = frameCount * channels;
            float[] samples = new float[sampleCount];

            for (int frame = 0; frame < frameCount; frame++)
            {
                float value = (float)Math.Sin(2.0 * Math.PI * frequency * frame / sampleRate) * amplitude;

                for (int channel = 0; channel < channels; channel++)
                {
                    samples[frame * channels + channel] = value;
                }
            }

            return samples;
        }

        /// <summary>
        /// Generates silence (zeros).
        /// </summary>
        /// <param name="sampleCount">Number of samples</param>
        /// <returns>Array of zeros</returns>
        public static float[] GenerateSilence(int sampleCount)
        {
            return new float[sampleCount];
        }

        /// <summary>
        /// Generates white noise.
        /// </summary>
        /// <param name="sampleCount">Number of samples</param>
        /// <param name="amplitude">Amplitude (0.0 to 1.0)</param>
        /// <param name="seed">Random seed for reproducibility</param>
        /// <returns>Array of random float samples</returns>
        public static float[] GenerateWhiteNoise(int sampleCount, float amplitude = 0.1f, int seed = 42)
        {
            var random = new Random(seed);
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = ((float)random.NextDouble() * 2.0f - 1.0f) * amplitude;
            }

            return samples;
        }

        /// <summary>
        /// Generates a square wave audio signal.
        /// </summary>
        /// <param name="frequency">Frequency in Hz</param>
        /// <param name="sampleRate">Sample rate in Hz</param>
        /// <param name="channels">Number of channels (interleaved)</param>
        /// <param name="durationSeconds">Duration in seconds</param>
        /// <param name="amplitude">Amplitude (0.0 to 1.0)</param>
        /// <returns>Array of interleaved float samples</returns>
        public static float[] GenerateSquareWave(
            float frequency,
            int sampleRate,
            int channels,
            double durationSeconds,
            float amplitude = 0.5f)
        {
            int frameCount = (int)(durationSeconds * sampleRate);
            int sampleCount = frameCount * channels;
            float[] samples = new float[sampleCount];

            for (int frame = 0; frame < frameCount; frame++)
            {
                double phase = (2.0 * Math.PI * frequency * frame / sampleRate) % (2.0 * Math.PI);
                float value = phase < Math.PI ? amplitude : -amplitude;

                for (int channel = 0; channel < channels; channel++)
                {
                    samples[frame * channels + channel] = value;
                }
            }

            return samples;
        }

        /// <summary>
        /// Generates a sawtooth wave audio signal.
        /// </summary>
        /// <param name="frequency">Frequency in Hz</param>
        /// <param name="sampleRate">Sample rate in Hz</param>
        /// <param name="channels">Number of channels (interleaved)</param>
        /// <param name="durationSeconds">Duration in seconds</param>
        /// <param name="amplitude">Amplitude (0.0 to 1.0)</param>
        /// <returns>Array of interleaved float samples</returns>
        public static float[] GenerateSawtoothWave(
            float frequency,
            int sampleRate,
            int channels,
            double durationSeconds,
            float amplitude = 0.5f)
        {
            int frameCount = (int)(durationSeconds * sampleRate);
            int sampleCount = frameCount * channels;
            float[] samples = new float[sampleCount];

            for (int frame = 0; frame < frameCount; frame++)
            {
                double phase = (frequency * frame / sampleRate) % 1.0;
                float value = (float)(2.0 * phase - 1.0) * amplitude;

                for (int channel = 0; channel < channels; channel++)
                {
                    samples[frame * channels + channel] = value;
                }
            }

            return samples;
        }

        /// <summary>
        /// Validates that audio samples are within a valid range.
        /// </summary>
        /// <param name="samples">Audio samples to validate</param>
        /// <param name="minValue">Minimum valid value</param>
        /// <param name="maxValue">Maximum valid value</param>
        /// <returns>True if all samples are valid, false otherwise</returns>
        public static bool ValidateSampleRange(float[] samples, float minValue = -2.0f, float maxValue = 2.0f)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                if (float.IsNaN(samples[i]) || float.IsInfinity(samples[i]))
                    return false;

                if (samples[i] < minValue || samples[i] > maxValue)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Calculates the RMS (Root Mean Square) level of audio samples.
        /// </summary>
        /// <param name="samples">Audio samples</param>
        /// <returns>RMS level</returns>
        public static float CalculateRMS(float[] samples)
        {
            if (samples == null || samples.Length == 0)
                return 0.0f;

            double sum = 0.0;
            for (int i = 0; i < samples.Length; i++)
            {
                sum += samples[i] * samples[i];
            }

            return (float)Math.Sqrt(sum / samples.Length);
        }

        /// <summary>
        /// Finds the peak (maximum absolute value) in audio samples.
        /// </summary>
        /// <param name="samples">Audio samples</param>
        /// <returns>Peak value</returns>
        public static float FindPeak(float[] samples)
        {
            if (samples == null || samples.Length == 0)
                return 0.0f;

            float peak = 0.0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float abs = Math.Abs(samples[i]);
                if (abs > peak)
                    peak = abs;
            }

            return peak;
        }

        /// <summary>
        /// Checks if audio samples contain only silence (all zeros or near-zero).
        /// </summary>
        /// <param name="samples">Audio samples</param>
        /// <param name="threshold">Threshold for considering a sample as silence</param>
        /// <returns>True if samples are silent, false otherwise</returns>
        public static bool IsSilent(float[] samples, float threshold = 0.0001f)
        {
            if (samples == null || samples.Length == 0)
                return true;

            for (int i = 0; i < samples.Length; i++)
            {
                if (Math.Abs(samples[i]) > threshold)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Compares two audio buffers for equality within a tolerance.
        /// </summary>
        /// <param name="buffer1">First buffer</param>
        /// <param name="buffer2">Second buffer</param>
        /// <param name="tolerance">Tolerance for floating-point comparison</param>
        /// <returns>True if buffers are equal within tolerance, false otherwise</returns>
        public static bool AreBuffersEqual(float[] buffer1, float[] buffer2, float tolerance = 0.0001f)
        {
            if (buffer1 == null || buffer2 == null)
                return buffer1 == buffer2;

            if (buffer1.Length != buffer2.Length)
                return false;

            for (int i = 0; i < buffer1.Length; i++)
            {
                if (Math.Abs(buffer1[i] - buffer2[i]) > tolerance)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Applies a fade-in to audio samples.
        /// </summary>
        /// <param name="samples">Audio samples to modify</param>
        /// <param name="fadeDurationSeconds">Fade duration in seconds</param>
        /// <param name="sampleRate">Sample rate in Hz</param>
        /// <param name="channels">Number of channels</param>
        public static void ApplyFadeIn(float[] samples, double fadeDurationSeconds, int sampleRate, int channels)
        {
            int fadeFrames = (int)(fadeDurationSeconds * sampleRate);
            int fadeSamples = Math.Min(fadeFrames * channels, samples.Length);

            for (int i = 0; i < fadeSamples; i++)
            {
                float gain = (float)i / fadeSamples;
                samples[i] *= gain;
            }
        }

        /// <summary>
        /// Applies a fade-out to audio samples.
        /// </summary>
        /// <param name="samples">Audio samples to modify</param>
        /// <param name="fadeDurationSeconds">Fade duration in seconds</param>
        /// <param name="sampleRate">Sample rate in Hz</param>
        /// <param name="channels">Number of channels</param>
        public static void ApplyFadeOut(float[] samples, double fadeDurationSeconds, int sampleRate, int channels)
        {
            int fadeFrames = (int)(fadeDurationSeconds * sampleRate);
            int fadeSamples = Math.Min(fadeFrames * channels, samples.Length);
            int startIndex = samples.Length - fadeSamples;

            for (int i = 0; i < fadeSamples; i++)
            {
                float gain = 1.0f - ((float)i / fadeSamples);
                samples[startIndex + i] *= gain;
            }
        }

        /// <summary>
        /// Converts decibels to linear amplitude.
        /// </summary>
        /// <param name="db">Decibel value</param>
        /// <returns>Linear amplitude</returns>
        public static float DbToLinear(float db)
        {
            return (float)Math.Pow(10.0, db / 20.0);
        }

        /// <summary>
        /// Converts linear amplitude to decibels.
        /// </summary>
        /// <param name="linear">Linear amplitude</param>
        /// <returns>Decibel value</returns>
        public static float LinearToDb(float linear)
        {
            if (linear <= 0.0f)
                return -100.0f; // Effectively negative infinity

            return (float)(20.0 * Math.Log10(linear));
        }

        /// <summary>
        /// Rounds up to the next power of 2.
        /// </summary>
        /// <param name="value">Input value</param>
        /// <returns>Next power of 2</returns>
        public static int NextPowerOf2(int value)
        {
            if (value <= 0)
                return 1;

            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            value++;

            return value;
        }
    }
}
