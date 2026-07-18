using System;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// Sub generator: linear phase FIR bandpass into a soft clipper, blended
    /// back over the dry signal.
    /// </summary>
    public class SubharmonicSynth
    {
        private float _mix = 0.0f;

        /// <summary>
        /// 40-120Hz bandpass, the bit we feed the waveshaper.
        /// </summary>
        private readonly FIRFilter _bandpassFilter;

        /// <summary>
        /// Scratch space for the filtered copy, grows if a block is bigger.
        /// </summary>
        private float[] _filteredBuffer;

        public bool Enabled { get; set; }

        /// <summary>
        /// 0.0 = dry only, 1.0 = all sub.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Sample rate in Hz. Kernel is 127 taps with a Kaiser window.
        /// </summary>
        /// <param name="sampleRate"></param>
        public SubharmonicSynth(float sampleRate)
        {
            _bandpassFilter = new FIRFilter(FIRFilter.CreateBandpassKernel(sampleRate, 40.0f, 120.0f, 127, 5.0f), maxChannels: 2);
            _filteredBuffer = new float[2048 * 2];
        }

        /// <summary>
        /// Runs an interleaved block in place.
        /// </summary>
        public void Process(Span<float> buffer, int frameCount, int channels)
        {
            if (!Enabled || _mix <= 0.0f) return;

            int count = frameCount * channels;
            if (_filteredBuffer.Length < count) _filteredBuffer = new float[count];

            Span<float> filtered = _filteredBuffer.AsSpan(0, count);
            buffer.Slice(0, count).CopyTo(filtered);
            _bandpassFilter.Process(filtered, frameCount, channels);

            for (int i = 0; i < count; i++)
            {
                float sub = _waveshape(filtered[i] * 2.0f);
                float mixed = buffer[i] * (1.0f - _mix) + sub * _mix;
                buffer[i] = Math.Clamp(mixed, -1.0f, 1.0f);
            }
        }

        private static float _waveshape(float x) => x / (1.0f + MathF.Abs(x));

        /// <summary>
        /// Drops the filter tail, e.g. when playback restarts.
        /// </summary>
        public void Reset()
        {
            _bandpassFilter.Reset();
            Array.Clear(_filteredBuffer);
        }
    }
}
