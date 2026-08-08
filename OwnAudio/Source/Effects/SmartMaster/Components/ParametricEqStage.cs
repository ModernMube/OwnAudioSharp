using System;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// Input parametric EQ, eight sweepable bands. The stage you tune a room with
    /// once the graphic EQ has done the coarse work. A band at 0 dB is skipped.
    /// </summary>
    public class ParametricEqStage
    {
        private readonly float _sampleRate;
        private readonly BiquadCoeffs[] _coeffs;
        private readonly int[] _active;
        private int _activeCount;

        private BiquadState[] _state;
        private int _channels;
        private readonly int _bands;

        public ParametricEqStage(float sampleRate, ParametricBand[] bands, int channels)
        {
            _sampleRate = sampleRate;
            _bands = SmartMasterConfig.ParametricBands;
            _coeffs = new BiquadCoeffs[_bands];
            _active = new int[_bands];
            _channels = Math.Max(channels, 1);
            _state = new BiquadState[_channels * _bands];

            SetBands(bands);
        }

        /// <summary>
        /// Rebuilds every band's coefficients and the active list.
        /// </summary>
        public void SetBands(ParametricBand[] bands)
        {
            var fitted = ParametricBand.Fit(bands);
            _activeCount = 0;

            for (int i = 0; i < _bands; i++)
            {
                ParametricBand b = fitted[i];
                float q = Math.Clamp(b.Q, 0.1f, 16.0f);
                float gain = Math.Clamp(b.GainDb, -20.0f, 20.0f);

                _coeffs[i] = b.Shape switch
                {
                    ParametricShape.LowShelf => BiquadCoeffs.LowShelf(_sampleRate, b.Frequency, q, gain),
                    ParametricShape.HighShelf => BiquadCoeffs.HighShelf(_sampleRate, b.Frequency, q, gain),
                    _ => BiquadCoeffs.Peaking(_sampleRate, b.Frequency, q, gain)
                };

                if (Math.Abs(gain) > 0.01f) _active[_activeCount++] = i;
            }
        }

        /// <summary>
        /// Runs the active bands over an interleaved block, in place.
        /// </summary>
        public void Process(Span<float> buffer, int frameCount, int channels)
        {
            if (_activeCount == 0 || channels <= 0) return;

            if (channels > _channels)
            {
                _channels = channels;
                _state = new BiquadState[_channels * _bands];
            }

            for (int f = 0; f < frameCount; f++)
            {
                int b = f * channels;
                for (int c = 0; c < channels; c++)
                {
                    float x = buffer[b + c];
                    for (int a = 0; a < _activeCount; a++)
                    {
                        int band = _active[a];
                        x = _state[c * _bands + band].Tick(_coeffs[band], x);
                    }
                    buffer[b + c] = x;
                }
            }
        }

        public void Reset()
        {
            for (int i = 0; i < _state.Length; i++) _state[i].Clear();
        }
    }
}
