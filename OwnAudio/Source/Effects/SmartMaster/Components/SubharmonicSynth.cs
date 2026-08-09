using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// Two band octave divider, dbx style: 48-72Hz gives 24-36Hz, 72-112Hz gives
    /// 36-56Hz, added in parallel over the dry signal.
    /// Earlier builds got this wrong twice. One band-passed 40-120Hz into a
    /// waveshaper and crossfaded it in, which makes harmonics rather than
    /// subharmonics and drops the whole mix. The other was this divider with no
    /// retrigger lockout and a narrow resonant output filter: the flip-flop could
    /// toggle twice inside a cycle and slide into period-3 or period-4, dropping
    /// inharmonic tones under the bass, and the sub level swung about 37dB
    /// depending on where the note fell in the band.
    /// </summary>
    public class SubharmonicSynth
    {
        private float _mix;

        private readonly DividerBand _low;
        private readonly DividerBand _high;

        /// <summary>
        /// Crossfade between the bands, 0 = low, 1 = high. A note near 72Hz drives
        /// both source bands and their squares are phase independent, so summing
        /// them partially cancels. Letting the louder band win keeps one clean
        /// generator running; the glide gives the switch hysteresis.
        /// </summary>
        private float _bandSel;
        private readonly float _selGlide;

        /// <summary>
        /// Headroom scale at mix 1. A square's fundamental is 4/pi of its
        /// amplitude and the output filters are flat in the passband, so this
        /// lands the sub around half the source band's level.
        /// </summary>
        private const float SubTrim = 0.4f;

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
            float sr = sampleRate > 0.0f ? sampleRate : 44100.0f;

            _low = new DividerBand(sr, 48.0f, 72.0f, 24.0f, 36.0f);
            _high = new DividerBand(sr, 72.0f, 112.0f, 36.0f, 56.0f);
            _selGlide = MathF.Exp(-1.0f / (0.050f * sr));
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

                float lo = _low.Tick(mono);
                float hi = _high.Tick(mono);

                float want = _high.Env > _low.Env ? 1.0f : 0.0f;
                _bandSel = _selGlide * _bandSel + (1.0f - _selGlide) * want;

                float sub = (lo * (1.0f - _bandSel) + hi * _bandSel) * amount;

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
            _bandSel = 0.0f;
        }

        /// <summary>
        /// Isolates a source octave, halves it with a Schmitt trigger driven
        /// flip-flop, then filters the square down into the target band.
        /// </summary>
        private sealed class DividerBand
        {
            private readonly BiquadCoeffs _src;
            private BiquadState _src1, _src2;

            /// <summary>
            /// Flat high-pass / low-pass pair bounding the output to the target
            /// band. A resonant band-pass here is what made the level depend on
            /// which note was playing.
            /// </summary>
            private readonly BiquadCoeffs _outHp;
            private readonly BiquadCoeffs _outLp;
            private BiquadState _outHpState, _outLpState;

            private readonly float _envAttack;
            private readonly float _envRelease;
            private float _env;

            private float _flip = 1.0f;
            private bool _armed;

            /// <summary>
            /// Samples since the last toggle, and the minimum that must pass
            /// before the next one. Without this the divider slips to period-3
            /// or period-4 on a ripply waveform.
            /// </summary>
            private int _sinceFlip;
            private readonly int _lockout;

            private const float Gate = 1e-4f;

            public float Level = 1.0f;

            public float Env => _env;

            public DividerBand(float sampleRate, float srcLo, float srcHi, float outLo, float outHi)
            {
                float srcCentre = MathF.Sqrt(srcLo * srcHi);

                _src = BiquadCoeffs.BandPass(sampleRate, srcCentre, srcCentre / (srcHi - srcLo));
                _outHp = BiquadCoeffs.HighPass(sampleRate, outLo, 0.707f);
                _outLp = BiquadCoeffs.LowPass(sampleRate, outHi, 0.707f);

                _envAttack = _timeCoeff(8.0f, sampleRate);
                _envRelease = _timeCoeff(120.0f, sampleRate);

                // Three quarters of the shortest cycle the band can carry.
                _lockout = (int)(0.75f * sampleRate / srcHi);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public float Tick(float x)
            {
                float s = _src2.Tick(_src, _src1.Tick(_src, x));

                float a = MathF.Abs(s);
                float c = a > _env ? _envAttack : _envRelease;
                _env = c * _env + (1.0f - c) * a;

                if (_sinceFlip < int.MaxValue) _sinceFlip++;

                if (_env < Gate)
                    return _shape(0.0f);

                float hyst = 0.25f * _env;
                if (_armed)
                {
                    if (s < -hyst && _sinceFlip >= _lockout)
                    {
                        _armed = false;
                        _flip = -_flip;
                        _sinceFlip = 0;
                    }
                }
                else if (s > hyst) { _armed = true; }

                return _shape(_flip * _env) * Level;
            }

            public void Reset()
            {
                _src1.Clear(); _src2.Clear();
                _outHpState.Clear(); _outLpState.Clear();
                _env = 0.0f;
                _flip = 1.0f;
                _armed = false;
                _sinceFlip = 0;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private float _shape(float x)
            {
                return _outLpState.Tick(_outLp, _outHpState.Tick(_outHp, x));
            }

            private static float _timeCoeff(float ms, float sampleRate)
            {
                return MathF.Exp(-1.0f / (ms * 0.001f * sampleRate));
            }
        }
    }
}
