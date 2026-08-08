using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// RBJ biquad coefficients, a0 already divided out. Shared by the subsonic
    /// filter, the parametric EQ and the subharmonic synth.
    /// </summary>
    internal struct BiquadCoeffs
    {
        public float B0, B1, B2, A1, A2;

        /// <summary>
        /// Pass-through.
        /// </summary>
        public static BiquadCoeffs Identity => new BiquadCoeffs { B0 = 1.0f };

        /// <summary>
        /// 2nd order high-pass.
        /// </summary>
        public static BiquadCoeffs HighPass(float sampleRate, float freq, float q)
        {
            _omega(sampleRate, freq, out float sinW, out float cosW);
            float alpha = sinW / (2.0f * q);

            return _norm((1 + cosW) / 2, -(1 + cosW), (1 + cosW) / 2, 1 + alpha, -2 * cosW, 1 - alpha);
        }

        /// <summary>
        /// 2nd order low-pass.
        /// </summary>
        public static BiquadCoeffs LowPass(float sampleRate, float freq, float q)
        {
            _omega(sampleRate, freq, out float sinW, out float cosW);
            float alpha = sinW / (2.0f * q);

            return _norm((1 - cosW) / 2, 1 - cosW, (1 - cosW) / 2, 1 + alpha, -2 * cosW, 1 - alpha);
        }

        /// <summary>
        /// Band-pass, unity gain at the centre.
        /// </summary>
        public static BiquadCoeffs BandPass(float sampleRate, float freq, float q)
        {
            _omega(sampleRate, freq, out float sinW, out float cosW);
            float alpha = sinW / (2.0f * q);

            return _norm(alpha, 0.0f, -alpha, 1 + alpha, -2 * cosW, 1 - alpha);
        }

        /// <summary>
        /// Peaking bell with the given gain in dB at its centre.
        /// </summary>
        public static BiquadCoeffs Peaking(float sampleRate, float freq, float q, float gainDb)
        {
            _omega(sampleRate, freq, out float sinW, out float cosW);
            float alpha = sinW / (2.0f * q);
            float a = MathF.Pow(10.0f, gainDb / 40.0f);

            return _norm(1 + alpha * a, -2 * cosW, 1 - alpha * a, 1 + alpha / a, -2 * cosW, 1 - alpha / a);
        }

        /// <summary>
        /// Low shelf, gain applies below the corner.
        /// </summary>
        public static BiquadCoeffs LowShelf(float sampleRate, float freq, float q, float gainDb)
        {
            _omega(sampleRate, freq, out float sinW, out float cosW);
            float a = MathF.Pow(10.0f, gainDb / 40.0f);
            float beta = 2.0f * MathF.Sqrt(a) * (sinW / (2.0f * q));
            float ap1 = a + 1.0f, am1 = a - 1.0f;

            return _norm(
                a * (ap1 - am1 * cosW + beta), 2 * a * (am1 - ap1 * cosW), a * (ap1 - am1 * cosW - beta),
                ap1 + am1 * cosW + beta, -2 * (am1 + ap1 * cosW), ap1 + am1 * cosW - beta);
        }

        /// <summary>
        /// High shelf, gain applies above the corner.
        /// </summary>
        public static BiquadCoeffs HighShelf(float sampleRate, float freq, float q, float gainDb)
        {
            _omega(sampleRate, freq, out float sinW, out float cosW);
            float a = MathF.Pow(10.0f, gainDb / 40.0f);
            float beta = 2.0f * MathF.Sqrt(a) * (sinW / (2.0f * q));
            float ap1 = a + 1.0f, am1 = a - 1.0f;

            return _norm(
                a * (ap1 + am1 * cosW + beta), -2 * a * (am1 + ap1 * cosW), a * (ap1 + am1 * cosW - beta),
                ap1 - am1 * cosW + beta, 2 * (am1 - ap1 * cosW), ap1 - am1 * cosW - beta);
        }

        private static BiquadCoeffs _norm(float b0, float b1, float b2, float a0, float a1, float a2)
        {
            float inv = 1.0f / a0;
            return new BiquadCoeffs { B0 = b0 * inv, B1 = b1 * inv, B2 = b2 * inv, A1 = a1 * inv, A2 = a2 * inv };
        }

        private static void _omega(float sampleRate, float freq, out float sinW, out float cosW)
        {
            float sr = sampleRate > 0 ? sampleRate : 44100.0f;
            float w = 2.0f * MathF.PI * Math.Clamp(freq, 1.0f, sr * 0.49f) / sr;

            sinW = MathF.Sin(w);
            cosW = MathF.Cos(w);
        }
    }

    /// <summary>
    /// Transposed DF-II state for one section on one channel.
    /// </summary>
    internal struct BiquadState
    {
        private float _z1, _z2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Tick(in BiquadCoeffs c, float x)
        {
            float y = c.B0 * x + _z1;
            _z1 = _flush(c.B1 * x - c.A1 * y + _z2);
            _z2 = _flush(c.B2 * x - c.A2 * y);

            return y;
        }

        public void Clear() { _z1 = 0.0f; _z2 = 0.0f; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float _flush(float v) => MathF.Abs(v) < 1e-25f ? 0.0f : v;
    }
}
