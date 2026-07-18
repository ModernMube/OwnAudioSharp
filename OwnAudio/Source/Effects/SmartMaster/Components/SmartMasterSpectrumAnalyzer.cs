using System;
using System.Numerics;
using OwnaudioNET.Dsp;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// FFT based spectrum readout used by the SmartMaster calibration pass.
    /// </summary>
    internal class SmartMasterSpectrumAnalyzer
    {
        private readonly int _sampleRate;

        /// <summary>
        /// ISO 31 band centres, 20Hz - 20kHz.
        /// </summary>
        private static readonly float[] _frequencyBands = new float[]
        {
            20f, 25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f,
            200f, 250f, 315f, 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f,
            2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f, 16000f, 20000f
        };

        public SmartMasterSpectrumAnalyzer(int sampleRate)
        {
            _sampleRate = sampleRate;
        }

        /// <summary>
        /// Chews through the recording with 75% overlapping Hann windows.
        /// </summary>
        /// <returns>31 band spectrum, linear amplitudes.</returns>
        public float[] AnalyzeSpectrum(float[] audioData)
        {
            int fftSize = _sampleRate >= 96000 ? 32768 : (_sampleRate >= 48000 ? 16384 : 8192);
            int hopSize = fftSize / 4;

            float[] window = new float[fftSize];
            for (int i = 0; i < fftSize; i++)
                window[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / (fftSize - 1)));

            float[] bandEnergies = new float[_frequencyBands.Length];
            Complex[] fftInput = new Complex[fftSize];
            int windowCount = Math.Max(1, (audioData.Length - fftSize) / hopSize + 1);

            for (int w = 0; w < windowCount; w++)
            {
                int startIdx = w * hopSize;
                if (startIdx + fftSize > audioData.Length) break;

                for (int i = 0; i < fftSize; i++)
                    fftInput[i] = audioData[startIdx + i] * window[i];

                OwnAudioFft.Forward(fftInput);

                for (int band = 0; band < _frequencyBands.Length; band++)
                    bandEnergies[band] += _bandEnergy(fftInput, _frequencyBands[band], fftSize);
            }

            for (int i = 0; i < bandEnergies.Length; i++)
                bandEnergies[i] /= windowCount;

            return bandEnergies;
        }

        /// <summary>
        /// Plain RMS over the block.
        /// </summary>
        public float CalculateRMS(ReadOnlySpan<float> audioData)
        {
            if (audioData.Length == 0) return 0f;

            double sum = 0;
            for (int i = 0; i < audioData.Length; i++)
                sum += audioData[i] * audioData[i];

            return (float)Math.Sqrt(sum / audioData.Length);
        }

        /// <summary>
        /// Same as CalculateRMS but in dBFS.
        /// </summary>
        public float CalculateRMSdB(ReadOnlySpan<float> audioData)
        {
            float rms = CalculateRMS(audioData);
            return 20f * MathF.Log10(MathF.Max(rms, 1e-10f));
        }

        /// <summary>
        /// RMS magnitude of the bins falling into a 23% wide band around centerFreq.
        /// </summary>
        private float _bandEnergy(Complex[] fft, float centerFreq, int fftSize)
        {
            float bandwidth = centerFreq * 0.23f;
            float startFreq = Math.Max(0, centerFreq - bandwidth / 2);
            float endFreq = Math.Min(_sampleRate / 2.0f, centerFreq + bandwidth / 2);

            int startBin = Math.Max(0, (int)Math.Floor(startFreq * fftSize / _sampleRate));
            int endBin = Math.Min(fftSize / 2, (int)Math.Ceiling(endFreq * fftSize / _sampleRate));
            if (startBin >= endBin) return 0;

            double energySum = 0;
            for (int bin = startBin; bin <= endBin; bin++)
            {
                double magnitude = fft[bin].Magnitude;
                energySum += magnitude * magnitude;
            }

            double rms = Math.Sqrt(energySum / (endBin - startBin + 1));
            return (float)(rms / (fftSize / 2.0));
        }
    }
}
