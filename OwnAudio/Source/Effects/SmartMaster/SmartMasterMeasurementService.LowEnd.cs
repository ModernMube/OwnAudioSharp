using Ownaudio.Core;
using OwnaudioNET.Effects.SmartMaster.Components;
using OwnaudioNET.Sources;
using Logger;

namespace OwnaudioNET.Effects.SmartMaster
{
    /// <summary>
    /// Low-end verdict off the measured spectrum - is the sub there, is the bottom weak.
    /// </summary>
    internal sealed partial class SmartMasterMeasurementService
    {
        /// <summary>
        /// 200Hz-2kHz, everything else is judged against this.
        /// </summary>
        private const int RefBandFirst = 10;
        private const int RefBandLast = 20;

        private const float MinCaptureDb = -60.0f;
        private const float WeakLowDb = 12.0f;
        private const float SubDropDb = 12.0f;

        /// <summary>
        /// Low end verdict, both deficits already relative to what the analyzer makes
        /// of the same noise.
        /// </summary>
        private readonly struct LowEndReading
        {
            public readonly float CaptureDb;
            public readonly float LowDeficit;
            public readonly float SubDeficit;

            public LowEndReading(float captureDb, float lowDeficit, float subDeficit)
            {
                CaptureDb = captureDb;
                LowDeficit = lowDeficit;
                SubDeficit = subDeficit;
            }

            public bool Valid => CaptureDb > MinCaptureDb;

            public bool WeakLow => Valid && LowDeficit > WeakLowDb;

            /// <summary>
            /// Only if the box does 40-80Hz but runs out under it - a divider on a
            /// speaker already down at 60Hz just eats headroom.
            /// </summary>
            public bool WantsSubharmonic => Valid && !WeakLow && SubDeficit > SubDropDb;

            public float SubharmonicMix => Math.Clamp(0.08f + (SubDeficit - SubDropDb) * 0.01f, 0.08f, 0.18f);
        }

        /// <summary>
        /// 20-31.5Hz and 40-80Hz against the midrange, corrected by the reference.
        /// </summary>
        private static LowEndReading _evaluateLowEnd(float[] measured, float[] reference, float captureDb)
        {
            if (measured.Length < SmartMasterConfig.EqBands || reference.Length < SmartMasterConfig.EqBands)
            {
                Log.Warning("[SmartMaster] No spectrum to judge the low end from");
                return default;
            }

            float lowDeficit = _groupDeficit(measured, reference, 3, 6);
            float subDeficit = _groupDeficit(measured, reference, 0, 2) - lowDeficit;

            Log.Info($"[SmartMaster] Low end: capture {captureDb:F1} dBFS, 40-80Hz {lowDeficit:F1} dB under the midrange, 20-31.5Hz {subDeficit:F1} dB under that");

            return new LowEndReading(captureDb, lowDeficit, subDeficit);
        }

        /// <summary>
        /// Drop against the midrange, minus the same drop in the reference. Mic gain
        /// falls out of the first difference, the analyzer's tilt out of the second.
        /// </summary>
        private static float _groupDeficit(float[] measured, float[] reference, int first, int last)
        {
            float measuredDrop = _bandGroupDb(measured, RefBandFirst, RefBandLast) - _bandGroupDb(measured, first, last);
            float referenceDrop = _bandGroupDb(reference, RefBandFirst, RefBandLast) - _bandGroupDb(reference, first, last);

            return measuredDrop - referenceDrop;
        }

        /// <summary>
        /// Mean band level over an index range, dB in and dB out - averaged as power.
        /// </summary>
        private static float _bandGroupDb(float[] bandsDb, int first, int last)
        {
            double power = 0;
            for (int i = first; i <= last; i++)
                power += Math.Pow(10.0, bandsDb[i] / 10.0);

            power /= last - first + 1;
            return power > 1e-20 ? (float)(10.0 * Math.Log10(power)) : -100f;
        }
    }
}
