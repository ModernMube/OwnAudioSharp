using System;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// Subsonic high-pass, 4th order Butterworth. Everything under the cabinets'
    /// range is wasted excursion - it eats headroom and muddies the low mids.
    /// </summary>
    public class SubsonicFilter
    {
        /// <summary>
        /// Butterworth pole Qs for a 4th order cascade.
        /// </summary>
        private static readonly float[] StageQ = { 0.541196f, 1.306563f };

        private readonly float _sampleRate;
        private readonly BiquadCoeffs[] _coeffs = new BiquadCoeffs[2];
        private BiquadState[] _state;
        private int _channels;
        private float _frequency;

        public bool Enabled { get; set; }

        /// <summary>
        /// Corner in Hz, 10 to 300. State gets flushed if it really moved.
        /// </summary>
        public float Frequency
        {
            get => _frequency;
            set
            {
                float f = Math.Clamp(value, 10.0f, 300.0f);
                if (Math.Abs(_frequency - f) <= 0.01f) return;

                _frequency = f;
                _calcCoeffs();
                Reset();
            }
        }

        public SubsonicFilter(float sampleRate, float frequency, int channels)
        {
            _sampleRate = sampleRate;
            _frequency = Math.Clamp(frequency, 10.0f, 300.0f);
            _channels = Math.Max(channels, 1);
            _state = new BiquadState[_channels * 2];

            _calcCoeffs();
        }

        private void _calcCoeffs()
        {
            for (int s = 0; s < 2; s++)
                _coeffs[s] = BiquadCoeffs.HighPass(_sampleRate, _frequency, StageQ[s]);
        }

        /// <summary>
        /// Runs an interleaved block in place.
        /// </summary>
        public void Process(Span<float> buffer, int frameCount, int channels)
        {
            if (!Enabled || channels <= 0) return;

            if (channels > _channels)
            {
                _channels = channels;
                _state = new BiquadState[_channels * 2];
            }

            for (int f = 0; f < frameCount; f++)
            {
                int b = f * channels;
                for (int c = 0; c < channels; c++)
                {
                    int i = b + c;
                    buffer[i] = _state[c * 2 + 1].Tick(_coeffs[1], _state[c * 2].Tick(_coeffs[0], buffer[i]));
                }
            }
        }

        public void Reset()
        {
            for (int i = 0; i < _state.Length; i++) _state[i].Clear();
        }
    }
}
