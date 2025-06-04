using Ownaudio.Processors;
using System;
using System.Runtime.CompilerServices;

namespace Ownaudio.Fx
{
    /// <summary>
    /// EQ fx 
    /// </summary>         
    public class Equalizer : SampleProcessorBase
    {
        private readonly BiquadFilter[][] _filters;
        private readonly float[] _gains;
        private readonly float[] _frequencies;
        private readonly float[] _qFactors;
        private const int BANDS = 10;

        /// <summary>
        /// EQ constructor
        /// </summary>
        /// <param name="sampleRate"></param>
        public Equalizer(float sampleRate = 44100)
        {
            _gains = new float[BANDS];
            _frequencies = new float[BANDS];
            _qFactors = new float[BANDS];
            _filters = new BiquadFilter[BANDS][];


            for (int band = 0; band < BANDS; band++)        // Set default values
            {
                _filters[band] = new BiquadFilter[2];
                for (int i = 0; i < 2; i++)
                {
                    _filters[band][i] = new BiquadFilter();
                }

                _frequencies[band] = 1000.0f;  // 1kHz
                _qFactors[band] = 1.4f;        // Default Q
                _gains[band] = 0.0f;           // 0dB

                UpdateFilters(band, sampleRate);
            }
        }

        /// <summary>
        /// Update band filter
        /// </summary>
        /// <param name="band"></param>
        /// <param name="sampleRate"></param>
        private void UpdateFilters(int band, float sampleRate)
        {
            for (int i = 0; i < 2; i++)
            {
                _filters[band][i].SetPeakingEq(
                    sampleRate,
                    _frequencies[band],
                    _qFactors[band],
                    _gains[band]
                );
            }
        }

        /// <summary>
        /// Set EQ band parameters
        /// </summary>
        /// <param name="band">index of the band to be modified (0-9)</param>
        /// <param name="frequency">middle frequency (between 20Hz - 20kHz)</param>
        /// <param name="q">Q factor (0.1 - 10 között)</param>
        /// <param name="gainDB">gain (between -12dB and +12dB)</param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public void SetBandGain(int band, float frequency, float q, float gainDB)
        {
            if (band < 0 || band >= BANDS)
                throw new ArgumentOutOfRangeException(nameof(band));

            // Checking and limiting values
            frequency = Math.Max(20.0f, Math.Min(20000.0f, frequency));  // 20Hz - 20kHz
            q = Math.Max(0.1f, Math.Min(10.0f, q));                      // Q: 0.1 - 10
            gainDB = FastClamp(gainDB);                  // -12dB - +12dB

            // Saving values
            _frequencies[band] = frequency;
            _qFactors[band] = q;
            _gains[band] = gainDB;

            // Update filters with new parameters
            UpdateFilters(band, _filters[band][0].SampleRate);
        }

        /// <summary>
        /// EQ process
        /// </summary>
        /// <param name="samples"></param>
        public override void Process(Span<float> samples)
        {
            for (int i = 0; i < samples.Length; i++)        // We process all samples
            {
                float sample = samples[i];

                for (int band = 0; band < BANDS; band++)    // Pass the pattern through each lane
                {
                    if (Math.Abs(_gains[band] - 0.0f) > float.Epsilon)
                    {
                        // Cascaded filters
                        sample = _filters[band][0].Process(sample);
                        sample = _filters[band][1].Process(sample);
                    }
                }

                samples[i] = sample;
            }
        }

        /// <summary>
        /// Resets the equalizer by clearing all internal filter states.
        /// Does not modify any band settings or parameters.
        /// </summary>
        public override void Reset()
        {
            for (int band = 0; band < BANDS; band++)
            {
                for (int i = 0; i < 2; i++)
                {
                    _filters[band][i].Reset();
                }
            }
        }

        /// <summary>
        /// Fast audio clamping function that constrains values to the valid audio range [-1.0, 1.0].
        /// </summary>
        /// <param name="value">The audio sample value to clamp.</param>
        /// <returns>The clamped value within the range [-1.0, 1.0].</returns>
        /// <remarks>
        /// This method is aggressively inlined for maximum performance in audio processing loops.
        /// Audio clamping is essential to prevent:
        /// - Digital audio clipping and distortion
        /// - Hardware damage from excessive signal levels
        /// - Unwanted artifacts in the audio output
        /// 
        /// Values below -1.0 are clamped to -1.0, values above 1.0 are clamped to 1.0,
        /// and values within the valid range are passed through unchanged.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float FastClamp(float value)
        {
            return value < -12.0f ? -12.0f : (value > 12.0f ? 12.0f : value);
        }
    }

    /// <summary>
    /// EQ band class
    /// </summary>
    public class BiquadFilter
    {
        private float _a0, _a1, _a2, _b0, _b1, _b2;
        private float _x1, _x2, _y1, _y2;

        /// <summary>
        /// Sample rate
        /// </summary>
        public float SampleRate { get; private set; }

        /// <summary>
        /// Setting peaking EQ
        /// </summary>
        /// <param name="sampleRate"></param>
        /// <param name="centerFreq"></param>
        /// <param name="q"></param>
        /// <param name="gainDB"></param>
        public void SetPeakingEq(float sampleRate, float centerFreq, float q, float gainDB)
        {
            SampleRate = sampleRate;

            float omega = 2.0f * MathF.PI * centerFreq / sampleRate;
            float alpha = MathF.Sin(omega) / (2.0f * q);
            float a = MathF.Pow(10.0f, gainDB / 40.0f);

            _b0 = 1.0f + alpha * a;
            _b1 = -2.0f * MathF.Cos(omega);
            _b2 = 1.0f - alpha * a;
            _a0 = 1.0f + alpha / a;
            _a1 = -2.0f * MathF.Cos(omega);
            _a2 = 1.0f - alpha / a;

            // Normalize
            float factor = 1.0f / _a0;
            _b0 *= factor;
            _b1 *= factor;
            _b2 *= factor;
            _a1 *= factor;
            _a2 *= factor;
        }

        /// <summary>
        /// Biquad filter process
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public float Process(float input)
        {
            float output = _b0 * input + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;

            _x2 = _x1;
            _x1 = input;
            _y2 = _y1;
            _y1 = output;

            return output;
        }

        /// <summary>
        /// Resets the biquad filter's internal state by clearing previous input and output values.
        /// Does not modify any filter coefficients or parameters.
        /// </summary>
        public void Reset()
        {
            _x1 = 0.0f;
            _x2 = 0.0f;
            _y1 = 0.0f;
            _y2 = 0.0f;
        }
    }
}


