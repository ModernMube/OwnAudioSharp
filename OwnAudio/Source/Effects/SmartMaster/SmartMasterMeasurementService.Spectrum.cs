using OwnaudioNET.Effects.SmartMaster.Components;
using Logger;

namespace OwnaudioNET.Effects.SmartMaster
{
    /// <summary>
    /// The spectrum half of the measurement: the reference run, the room curve against it and
    /// turning that curve into correction values.
    /// </summary>
    internal sealed partial class SmartMasterMeasurementService
    {
        /// <summary>
        /// Band levels of the reference run and the rate they were taken at. Nobody writes into
        /// the array afterwards, so one copy serves every measurement.
        /// </summary>
        private static float[]? _reference;
        private static int _referenceRate;

        /// <summary>
        /// The same noise the speakers get, through the same analyzer. Subtracting it takes the
        /// window's low end smearing and the band edge rounding out, what's left is the room.
        /// </summary>
        private float[] _referenceSpectrum()
        {
            if (_reference != null && _referenceRate == _config.SampleRate) return _reference;

            var _buffer = new float[_config.SampleRate * 8];
            new PinkNoise().Render(_buffer, 1, -1);

            _reference = new SmartMasterSpectrumAnalyzer(_config.SampleRate).AnalyzeBuffer(_buffer);
            _referenceRate = _config.SampleRate;
            return _reference;
        }

        /// <summary>
        /// Deviation per band, positive where the room is short. Both curves are lined up on
        /// the midrange first, so the mic's distance and gain don't matter.
        /// </summary>
        private static void _fillFrequencyResponse(MeasurementResults results, float[] measured, float[] reference)
        {
            float offset = _bandGroupDb(measured, RefBandFirst, RefBandLast) - _bandGroupDb(reference, RefBandFirst, RefBandLast);

            for (int i = 0; i < SmartMasterConfig.EqBands; i++)
                results.FrequencyResponse[i] = reference[i] + offset - measured[i];

            Log.Info("[SmartMaster] Spectrum analysis completed:");
            for (int i = 0; i < SmartMasterConfig.EqBands; i++)
            {
                Log.Info($"  Band {i}: {results.FrequencyResponse[i]:+0.0;-0.0} dB");
            }
        }

        /// <summary>
        /// How much of the measured deviation we actually dial in. A room is not a
        /// minimum phase system, so correcting it 1:1 mostly makes it sound worse.
        /// </summary>
        private const float CorrectionFactor = 0.65f;

        /// <summary>
        /// Target house curve in dB against a flat reference, per EQ band. Slightly
        /// warm at the bottom and rolled off on top - a genuinely flat room is
        /// fatiguing, which is why nobody tunes to one.
        /// </summary>
        private static readonly float[] TargetCurve =
        {
             3.0f,  2.8f,  2.6f,  2.3f,  2.0f,  1.7f,  1.4f,  1.0f,  0.7f,  0.4f,
             0.2f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f, -0.2f,
            -0.4f, -0.7f, -1.0f, -1.3f, -1.6f, -1.9f, -2.2f, -2.6f, -3.0f, -3.5f
        };

        /// <summary>
        /// Turns a measurement into a config: smoothed, partially applied EQ plus
        /// the channel alignment the sweep found. Boosts stay short because filling
        /// a null costs headroom and rarely fills it.
        /// </summary>
        private void CalculateCorrectionsToConfig(MeasurementResults results, LowEndReading lowEnd, SmartMasterConfig targetConfig)
        {
            var wanted = new float[SmartMasterConfig.EqBands];
            for (int i = 0; i < wanted.Length; i++)
                wanted[i] = results.FrequencyResponse[i] + TargetCurve[i];

            float[] smoothed = _smoothed(wanted);

            for (int i = 0; i < SmartMasterConfig.EqBands; i++)
            {
                float maxBoost = i < 5 ? 2.0f : 6.0f;
                targetConfig.GraphicEQGains[i] = Math.Clamp(smoothed[i] * CorrectionFactor, -12.0f, maxBoost);
            }

            targetConfig.TimeDelays = results.ChannelDelays;
            targetConfig.PhaseInvert = results.ChannelPolarity;

            if (lowEnd.WantsSubharmonic)
            {
                targetConfig.SubharmonicEnabled = true;
                targetConfig.SubharmonicMix = lowEnd.SubharmonicMix;
                Log.Info($"[SmartMaster] Subharmonic Synth on at mix {lowEnd.SubharmonicMix:F2}, sub band is {lowEnd.SubDeficit:F1} dB under the 40-80Hz range");
            }
            else if (lowEnd.WeakLow)
            {
                Log.Info("[SmartMaster] Low end is weak, leaving it to the EQ");
            }
        }

        /// <summary>
        /// 1-2-1 weighted over neighbouring bands. A single mic position is full of
        /// narrow interference dips that say nothing about the system.
        /// </summary>
        private static float[] _smoothed(float[] raw)
        {
            var smoothed = new float[raw.Length];

            for (int i = 0; i < raw.Length; i++)
            {
                float previous = raw[Math.Max(0, i - 1)];
                float next = raw[Math.Min(raw.Length - 1, i + 1)];
                smoothed[i] = (previous + raw[i] * 2f + next) * 0.25f;
            }

            return smoothed;
        }
    }
}
