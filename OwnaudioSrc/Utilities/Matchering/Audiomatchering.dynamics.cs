using System;
using System.Linq;

namespace Ownaudio.Utilities.Matchering
{
    partial class AudioAnalyzer
    {
        #region Dynamics Analysis

        /// <summary>
        /// Analyzes audio dynamics (RMS, peak, loudness, dynamic range)
        /// </summary>
        /// <param name="audioData">Audio data to analyze</param>
        /// <returns>Dynamics analysis results</returns>
        private DynamicsInfo AnalyzeDynamics(float[] audioData)
        {
            if (audioData.Length == 0)
                return new DynamicsInfo();

            double sumSquares = audioData.Sum(sample => sample * sample);
            float rms = (float)Math.Sqrt(sumSquares / audioData.Length);

            float peak = audioData.Max(sample => Math.Abs(sample));

            float loudness = 20 * (float)Math.Log10(Math.Max(rms, 1e-10f)) - 23.0f;

            var sortedLevels = audioData.Select(Math.Abs).OrderByDescending(x => x).ToArray();
            int top10Percent = Math.Max(1, sortedLevels.Length / 10);
            int top90Percent = Math.Max(1, sortedLevels.Length * 9 / 10);

            float dynamicRange = 20 * (float)Math.Log10(
                Math.Max(sortedLevels.Take(top10Percent).Average(), 1e-10f) /
                Math.Max(sortedLevels.Take(top90Percent).Average(), 1e-10f));

            return new DynamicsInfo
            {
                RMS = rms,
                Peak = peak,
                Loudness = loudness,
                DynamicRange = Math.Max(0, dynamicRange)
            };
        }

        #endregion

        #region Amplification Settings         

        /// <summary>
        /// Calculates DynamicAmp settings based on loudness analysis
        /// </summary>
        /// <param name="source">Source audio spectrum</param>
        /// <param name="target">Target audio spectrum</param>
        /// <returns>Dynamic amplification settings</returns>
        private DynamicAmpSettings CalculateDynamicAmpSettings(AudioSpectrum source, AudioSpectrum target)
        {
            float loudnessDifference = target.Loudness - source.Loudness;

            return new DynamicAmpSettings
            {
                TargetLevel = -9.0f + Math.Max(-6f, Math.Min(6f, loudnessDifference)),
                AttackTime = 0.1f,
                ReleaseTime = 0.5f,
                MaxGain = 6.0f
            };
        }

        #endregion

    }
}
