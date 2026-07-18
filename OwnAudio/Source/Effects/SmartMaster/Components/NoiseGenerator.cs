using System;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// Test signal source for calibration - white, pink and low-freq noise.
    /// Cold path only, we allocate the buffer we hand back.
    /// </summary>
    internal static class NoiseGenerator
    {
        private static readonly Random _random = new Random();

        /// <summary>
        /// Flat spectrum noise, amplitude in 0..1.
        /// </summary>
        public static float[] GenerateWhiteNoise(int sampleCount, float amplitude = 0.5f)
        {
            float[] noise = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
                noise[i] = _bipolar() * amplitude;

            return noise;
        }

        /// <summary>
        /// 1/f noise (-3dB/oct) the Voss-McCartney way, 7 octave generators.
        /// </summary>
        public static float[] GeneratePinkNoise(int sampleCount, float amplitude = 0.5f)
        {
            const int gens = 7;
            float[] noise = new float[sampleCount];
            float[] generators = new float[gens];

            for (int g = 0; g < gens; g++)
                generators[g] = _bipolar();

            for (int i = 0; i < sampleCount; i++)
            {
                float sum = 0;
                for (int g = 0; g < gens; g++)
                {
                    if (i % (1 << g) == 0) generators[g] = _bipolar();
                    sum += generators[g];
                }

                noise[i] = (sum / gens) * amplitude;
            }

            return noise;
        }

        /// <summary>
        /// White noise squeezed into roughly 20-100Hz with a pair of one-pole
        /// filters, then peak-normalised to the given amplitude.
        /// </summary>
        public static float[] GenerateLowFrequencyNoise(int sampleCount, int sampleRate, float amplitude = 0.5f)
        {
            float[] buf = GenerateWhiteNoise(sampleCount, amplitude);
            if (sampleCount < 2) return buf;

            float dt = 1.0f / sampleRate;
            float lpAlpha = dt / (1.0f / (2.0f * MathF.PI * 100.0f) + dt);
            float hpRc = 1.0f / (2.0f * MathF.PI * 20.0f);
            float hpAlpha = hpRc / (hpRc + dt);

            for (int i = 1; i < sampleCount; i++)
                buf[i] = buf[i - 1] + lpAlpha * (buf[i] - buf[i - 1]);

            float prevIn = buf[0], prevOut = 0f;
            for (int i = 1; i < sampleCount; i++)
            {
                float x = buf[i];
                prevOut = hpAlpha * (prevOut + x - prevIn);
                prevIn = x;
                buf[i] = prevOut;
            }

            float maxVal = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                float a = MathF.Abs(buf[i]);
                if (a > maxVal) maxVal = a;
            }

            if (maxVal > 0)
            {
                float scale = amplitude / maxVal;
                for (int i = 0; i < sampleCount; i++)
                    buf[i] *= scale;
            }

            return buf;
        }

        private static float _bipolar() => (float)_random.NextDouble() * 2.0f - 1.0f;
    }
}
