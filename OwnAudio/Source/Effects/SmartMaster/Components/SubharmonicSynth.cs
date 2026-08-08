using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// Two band octave divider, dbx style: 48-72Hz gives 24-36Hz, 72-112Hz gives
    /// 36-56Hz, both added in parallel over the dry signal.
    /// The old build band-passed 40-120Hz into a waveshaper and crossfaded it in,
    /// which made harmonics (not subharmonics) and dropped the whole mix.
    /// </summary>
    public class SubharmonicSynth
    {
        private float _mix;

        private readonly DividerBand _low;
        private readonly DividerBand _high;

        /// <summary>
        /// The divided square's fundamental is 4/pi of its amplitude, this trims
        /// it back to roughly the source level.
        /// </summary>
        private const float SubTrim = 0.55f;

        public bool Enabled { get; set; }

        /// <summary>
        /// Master level of the synthesized sub, 0 = off, 1 = full.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Level of the 24-36Hz band.
        /// </summary>
        public float LowLevel
        {
            get => _low.Level;
            set => _low.Level = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Level of the 36-56Hz band.
        /// </summary>
        public float HighLevel
        {
            get => _high.Level;
            set => _high.Level = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Sample rate in Hz.
        /// </summary>
        public SubharmonicSynth(float sampleRate)
        {
            _low = new DividerBand(sampleRate, 48.0f, 72.0f, 24.0f, 36.0f);
            _high = new DividerBand(sampleRate, 72.0f, 112.0f, 36.0f, 56.0f);
        }

        /// <summary>
        /// Adds the synthesized sub to an interleaved block, in place. Generation
        /// runs off the mono sum - low bass is mono anyway, and one shared signal
        /// on every channel stays phase coherent.
        /// </summary>
        public void Process(Span<float> buffer, int frameCount, int channels)
        {
            if (!Enabled || _mix <= 0.0f || channels <= 0) return;

            float amount = _mix * SubTrim;
            float invCh = 1.0f / channels;

            for (int f = 0; f < frameCount; f++)
            {
                int b = f * channels;

                float mono = 0.0f;
                for (int c = 0; c < channels; c++) mono += buffer[b + c];
                mono *= invCh;

                float sub = (_low.Tick(mono) + _high.Tick(mono)) * amount;

                for (int c = 0; c < channels; c++)
                    buffer[b + c] = Math.Clamp(buffer[b + c] + sub, -1.5f, 1.5f);
            }
        }

        /// <summary>
        /// Drops the filter tails, e.g. when playback restarts.
        /// </summary>
        public void Reset()
        {
            _low.Reset();
            _high.Reset();
        }

        /// <summary>
        /// Isolates a source octave, halves it with a Schmitt trigger driven
        /// flip-flop, then band-passes the square down to a near sine.
        /// </summary>
        private sealed class DividerBand
        {
            private readonly BiquadCoeffs _src;
            private readonly BiquadCoeffs _out;
            private BiquadState _src1, _src2, _out1, _out2;

            private readonly float _envAttack;
            private readonly float _envRelease;
            private float _env;

            private float _flip = 1.0f;
            private bool _armed;

            private const float Gate = 1e-4f;

            public float Level = 1.0f;

            public DividerBand(float sampleRate, float srcLo, float srcHi, float outLo, float outHi)
            {
                float srcCentre = MathF.Sqrt(srcLo * srcHi);
                float outCentre = MathF.Sqrt(outLo * outHi);

                _src = BiquadCoeffs.BandPass(sampleRate, srcCentre, srcCentre / (srcHi - srcLo));
                _out = BiquadCoeffs.BandPass(sampleRate, outCentre, outCentre / (outHi - outLo));

                _envAttack = _timeCoeff(8.0f, sampleRate);
                _envRelease = _timeCoeff(120.0f, sampleRate);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public float Tick(float x)
            {
                float s = _src2.Tick(_src, _src1.Tick(_src, x));

                float a = MathF.Abs(s);
                float c = a > _env ? _envAttack : _envRelease;
                _env = c * _env + (1.0f - c) * a;

                if (_env < Gate)
                    return _out2.Tick(_out, _out1.Tick(_out, 0.0f));

                float hyst = 0.25f * _env;
                if (_armed)
                {
                    if (s < -hyst) { _armed = false; _flip = -_flip; }
                }
                else if (s > hyst) { _armed = true; }

                return _out2.Tick(_out, _out1.Tick(_out, _flip * _env)) * Level;
            }

            public void Reset()
            {
                _src1.Clear(); _src2.Clear();
                _out1.Clear(); _out2.Clear();
                _env = 0.0f;
                _flip = 1.0f;
                _armed = false;
            }

            private static float _timeCoeff(float ms, float sampleRate)
            {
                return MathF.Exp(-1.0f / (ms * 0.001f * sampleRate));
            }
        }
    }
}
