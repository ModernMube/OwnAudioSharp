using System;
using System.Numerics;
using OwnaudioNET.Dsp;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// Folds audio into the 30 ISO third-octave bands the graphic EQ runs on. Fixed FFT size,
    /// all scratch pre-allocated - the mic monitor pushes into it every few dozen ms.
    /// </summary>
    internal sealed class SmartMasterSpectrumAnalyzer
    {
        private const int FftSize = 4096;
        private const float FloorDb = -100f;

        /// <summary>
        /// Fewest bins a band may ride on, keeps the bottom of the range steady.
        /// </summary>
        private const int MinBandBins = 3;

        /// <summary>
        /// Same centres, same order as SmartMasterConfig.GraphicEQGains.
        /// </summary>
        private static readonly float[] _centres =
        {
            20f, 25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f,
            200f, 250f, 315f, 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f,
            2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f, 16000f
        };

        private readonly float[] _window = new float[FftSize];
        private readonly float[] _frame = new float[FftSize];
        private readonly Complex[] _fft = new Complex[FftSize];
        private readonly int[] _bandFrom = new int[SmartMasterConfig.EqBands];
        private readonly int[] _bandTo = new int[SmartMasterConfig.EqBands];
        private readonly float[] _levels = new float[SmartMasterConfig.EqBands];
        private readonly double[] _sum = new double[SmartMasterConfig.EqBands];

        private int _filled;
        private int _windows;

        public SmartMasterSpectrumAnalyzer(int sampleRate)
        {
            for (int i = 0; i < FftSize; i++)
                _window[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (FftSize - 1));

            double _binWidth = (double)sampleRate / FftSize;
            for (int b = 0; b < _centres.Length; b++)
            {
                int _from = Math.Clamp((int)(_centres[b] * Math.Pow(2.0, -1.0 / 6.0) / _binWidth), 1, FftSize / 2 - 1);
                int _to = Math.Clamp((int)(_centres[b] * Math.Pow(2.0, 1.0 / 6.0) / _binWidth) + 1, _from + 1, FftSize / 2);

                //Down at 20Hz a third-octave is narrower than a bin and would rattle by several dB
                //window to window. The reference gets the same widening, so it cancels.
                if (_to - _from < MinBandBins)
                {
                    _from = Math.Max(1, (_from + _to) / 2 - MinBandBins / 2);
                    _to = Math.Min(FftSize / 2, _from + MinBandBins);
                }

                _bandFrom[b] = _from;
                _bandTo[b] = _to;
            }
        }

        /// <summary>
        /// Feeds an interleaved block, summed to mono. Every window it completes lands in the
        /// running average that Average reads back.
        /// </summary>
        public void Push(ReadOnlySpan<float> interleaved, int channels)
        {
            float _scale = 1f / Math.Max(1, channels);

            for (int i = 0; i + channels <= interleaved.Length; i += channels)
            {
                float _mono = 0f;
                for (int c = 0; c < channels; c++) _mono += interleaved[i + c];

                _frame[_filled++] = _mono * _scale;
                if (_filled < FftSize) continue;

                _bandLevels(_frame, _levels);
                for (int b = 0; b < _sum.Length; b++) _sum[b] += _levels[b];

                _windows++;
                _filled = 0;
            }
        }

        /// <summary>
        /// Throws the running average away, the next window starts a fresh one.
        /// </summary>
        public void ResetAverage()
        {
            Array.Clear(_sum);
            _windows = 0;
            _filled = 0;
        }

        /// <summary>
        /// Mean band levels in dBFS since the last ResetAverage.
        /// </summary>
        /// <returns>How many windows went into it, 0 leaves into untouched.</returns>
        public int Average(float[] into)
        {
            if (_windows == 0) return 0;

            for (int b = 0; b < _sum.Length; b++) into[b] = (float)(_sum[b] / _windows);
            return _windows;
        }

        /// <summary>
        /// Mean band levels of a mono buffer, 75% overlapping windows. That's the reference run:
        /// the same noise through the same analyzer, so the window's smearing and the band edge
        /// rounding drop out of the room curve.
        /// </summary>
        public float[] AnalyzeBuffer(ReadOnlySpan<float> mono)
        {
            var _total = new double[SmartMasterConfig.EqBands];
            var _result = new float[SmartMasterConfig.EqBands];
            int _count = 0;

            for (int offset = 0; offset + FftSize <= mono.Length; offset += FftSize / 4)
            {
                _bandLevels(mono.Slice(offset, FftSize), _levels);
                for (int b = 0; b < _total.Length; b++) _total[b] += _levels[b];
                _count++;
            }

            for (int b = 0; b < _result.Length; b++)
                _result[b] = _count > 0 ? (float)(_total[b] / _count) : FloorDb;

            return _result;
        }

        /// <summary>
        /// One Hann windowed FFT folded into the bands. Power is summed over a band's bins,
        /// never averaged - averaging tilts the readout about 1.5dB per octave.
        /// </summary>
        private void _bandLevels(ReadOnlySpan<float> frame, float[] into)
        {
            for (int i = 0; i < FftSize; i++)
                _fft[i] = new Complex(frame[i] * _window[i], 0.0);

            OwnAudioFft.Forward(_fft);

            double _norm = 2.0 / FftSize;
            for (int b = 0; b < into.Length; b++)
            {
                double _power = 0;
                for (int bin = _bandFrom[b]; bin < _bandTo[b]; bin++)
                {
                    double _magnitude = _fft[bin].Magnitude * _norm;
                    _power += _magnitude * _magnitude;
                }

                into[b] = _power > 1e-14 ? MathF.Max((float)(10.0 * Math.Log10(_power)), FloorDb) : FloorDb;
            }
        }
    }
}
