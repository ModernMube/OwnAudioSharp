using System;

namespace Ownaudio.Sources.Extensions
{
    /// <summary>
    /// 
    /// </summary>
    public static class Sources
    {
        /// <summary>
        /// Calculates the average signal levels for a stereo audio signal using Span.
        /// </summary>
        /// <param name="stereoAudioData">The stereo audio data span where even indices are left channel and odd indices are right channel.</param>
        /// <returns>A tuple containing the average levels for (left channel, right channel).</returns>
        /// <remarks>
        /// This method processes stereo audio data by:
        /// - Separating left channel (even indices: 0, 2, 4, ...) and right channel (odd indices: 1, 3, 5, ...)
        /// - Using absolute values to measure signal amplitude regardless of polarity
        /// - Calculating separate averages for each channel
        /// - Returning (0, 0) if no data is available for processing
        /// 
        /// The returned values represent the average amplitude levels which can be used
        /// for audio level monitoring, VU meters, or automatic gain control.
        /// </remarks>
        public static (float, float) CalculateAverageStereoLevelsSpan(ReadOnlySpan<float> stereoAudioData)
        {
            if (stereoAudioData.Length == 0)
            {
                return (0f, 0f);
            }

            float leftChannelSum = 0;
            float rightChannelSum = 0;
            int leftSampleCount = 0;
            int rightSampleCount = 0;

            // Left channel: 0, 2, 4, ...
            // Right channel: 1, 3, 5, ...
            for (int i = 0; i < stereoAudioData.Length; i++)
            {
                if (i % 2 == 0)
                {
                    leftChannelSum += Math.Abs(stereoAudioData[i]);
                    leftSampleCount++;
                }
                else
                {
                    rightChannelSum += Math.Abs(stereoAudioData[i]);
                    rightSampleCount++;
                }
            }

            // Calculating averages
            float leftAverage = leftSampleCount > 0 ? leftChannelSum / leftSampleCount : 0;
            float rightAverage = rightSampleCount > 0 ? rightChannelSum / rightSampleCount : 0;

            return (leftAverage, rightAverage);
        }

        /// <summary>
        /// Calculates the average signal levels for a stereo audio signal in dB using Span.
        /// </summary>
        /// <param name="stereoAudioData">The stereo audio data span where even indices are left channel and odd indices are right channel.</param>
        /// <returns>A tuple containing the average levels in dB for (left channel, right channel).</returns>
        /// <remarks>
        /// This method processes stereo audio data and converts to dB using 20*log10(amplitude).
        /// Returns negative infinity for zero amplitude values.
        /// </remarks>
        public static (float, float) CalculateAverageStereoLevelsDbSpan(ReadOnlySpan<float> stereoAudioData)
        {
            var (leftLevel, rightLevel) = CalculateAverageStereoLevelsSpan(stereoAudioData);
            return (ConvertToDb(leftLevel), ConvertToDb(rightLevel));
        }

        /// <summary>
        /// Calculates the average signal level for a mono audio signal using Span.
        /// </summary>
        /// <param name="monoAudioData">The mono audio data span.</param>
        /// <returns>A tuple where the first value is the mono level and the second value is always 0 (for consistency with stereo format).</returns>
        /// <remarks>
        /// This method processes mono audio data by:
        /// - Using absolute values to measure signal amplitude regardless of polarity
        /// - Calculating the average amplitude across all samples
        /// - Returning the result in stereo-compatible format (mono level, 0)
        /// - Handling empty data gracefully
        /// 
        /// The returned format maintains consistency with stereo level calculations
        /// while providing meaningful mono audio level information.
        /// </remarks>
        public static (float, float) CalculateAverageMonoLevelSpan(ReadOnlySpan<float> monoAudioData)
        {
            if (monoAudioData.Length == 0)
            {
                return (0f, 0f);
            }

            float leftChannelSum = 0;

            for (int i = 0; i < monoAudioData.Length; i++)
            {
                leftChannelSum += Math.Abs(monoAudioData[i]);
            }

            float leftAverage = monoAudioData.Length > 0 ? leftChannelSum / monoAudioData.Length : 0;
            return (leftAverage, 0f);
        }

        /// <summary>
        /// Calculates the average signal level for a mono audio signal in dB using Span.
        /// </summary>
        /// <param name="monoAudioData">The mono audio data span.</param>
        /// <returns>A tuple where the first value is the mono level in dB and the second value is negative infinity.</returns>
        /// <remarks>
        /// This method processes mono audio data and converts to dB using 20*log10(amplitude).
        /// Returns negative infinity for zero amplitude values.
        /// </remarks>
        public static (float, float) CalculateAverageMonoLevelDbSpan(ReadOnlySpan<float> monoAudioData)
        {
            var (leftLevel, _) = CalculateAverageMonoLevelSpan(monoAudioData);
            return (ConvertToDb(leftLevel), float.NegativeInfinity);
        }

        /// <summary>
        /// Calculates the average signal levels for a stereo audio signal.
        /// </summary>
        /// <param name="stereoAudioData">The stereo audio data array where even indices are left channel and odd indices are right channel.</param>
        /// <returns>A tuple containing the average levels for (left channel, right channel).</returns>
        /// <remarks>
        /// This method processes stereo audio data by:
        /// - Separating left channel (even indices: 0, 2, 4, ...) and right channel (odd indices: 1, 3, 5, ...)
        /// - Using absolute values to measure signal amplitude regardless of polarity
        /// - Calculating separate averages for each channel
        /// - Returning (0, 0) if no data is available for processing
        /// 
        /// The returned values represent the average amplitude levels which can be used
        /// for audio level monitoring, VU meters, or automatic gain control.
        /// </remarks>
        public static (float, float) CalculateAverageStereoLevels(float[] stereoAudioData)
        {
            if (stereoAudioData == null || stereoAudioData.Length == 0)
            {
                Console.WriteLine("No data available for processing.");
                return (0f, 0f);
            }

            return CalculateAverageStereoLevelsSpan(stereoAudioData.AsSpan());
        }

        /// <summary>
        /// Calculates the average signal levels for a stereo audio signal in dB.
        /// </summary>
        /// <param name="stereoAudioData">The stereo audio data array where even indices are left channel and odd indices are right channel.</param>
        /// <returns>A tuple containing the average levels in dB for (left channel, right channel).</returns>
        /// <remarks>
        /// This method processes stereo audio data and converts to dB using 20*log10(amplitude).
        /// Returns negative infinity for zero amplitude values.
        /// </remarks>
        public static (float, float) CalculateAverageStereoLevelsDb(float[] stereoAudioData)
        {
            if (stereoAudioData == null || stereoAudioData.Length == 0)
            {
                Console.WriteLine("No data available for processing.");
                return (float.NegativeInfinity, float.NegativeInfinity);
            }

            return CalculateAverageStereoLevelsDbSpan(stereoAudioData.AsSpan());
        }

        /// <summary>
        /// Calculates the average signal level for a mono audio signal.
        /// </summary>
        /// <param name="monoAudioData">The mono audio data array.</param>
        /// <returns>A tuple where the first value is the mono level and the second value is always 0 (for consistency with stereo format).</returns>
        /// <remarks>
        /// This method processes mono audio data by:
        /// - Using absolute values to measure signal amplitude regardless of polarity
        /// - Calculating the average amplitude across all samples
        /// - Returning the result in stereo-compatible format (mono level, 0)
        /// - Handling empty or null data gracefully
        /// 
        /// The returned format maintains consistency with stereo level calculations
        /// while providing meaningful mono audio level information.
        /// </remarks>
        public static (float, float) CalculateAverageMonoLevel(float[] monoAudioData)
        {
            if (monoAudioData == null || monoAudioData.Length == 0)
            {
                return (0f, 0f);
            }

            return CalculateAverageMonoLevelSpan(monoAudioData.AsSpan());
        }

        /// <summary>
        /// Calculates the average signal level for a mono audio signal in dB.
        /// </summary>
        /// <param name="monoAudioData">The mono audio data array.</param>
        /// <returns>A tuple where the first value is the mono level in dB and the second value is negative infinity.</returns>
        /// <remarks>
        /// This method processes mono audio data and converts to dB using 20*log10(amplitude).
        /// Returns negative infinity for zero amplitude values.
        /// </remarks>
        public static (float, float) CalculateAverageMonoLevelDb(float[] monoAudioData)
        {
            if (monoAudioData == null || monoAudioData.Length == 0)
            {
                return (float.NegativeInfinity, float.NegativeInfinity);
            }

            return CalculateAverageMonoLevelDbSpan(monoAudioData.AsSpan());
        }

        /// <summary>
        /// Converts linear amplitude to decibels.
        /// </summary>
        /// <param name="linearValue">Linear amplitude value (0.0 to 1.0)</param>
        /// <returns>Value in decibels, or negative infinity for zero amplitude</returns>
        private static float ConvertToDb(float linearValue)
        {
            if (linearValue <= 0)
                return float.NegativeInfinity;

            return 20.0f * (float)Math.Log10(linearValue);
        }
    }
}
