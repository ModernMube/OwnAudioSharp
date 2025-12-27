using System;
using System.Linq;
using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// Spectrum analyzer for SmartMaster calibration.
    /// FFT-based frequency analysis.
    /// </summary>
    internal class SmartMasterSpectrumAnalyzer
    {
        /// <summary>
        /// Sample rate in Hz.
        /// </summary>
        private readonly int _sampleRate;
        
        /// <summary>
        /// ISO standard 31 band center frequencies (20Hz - 20kHz).
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
        /// Analyzes the spectrum of recorded audio and returns band energies.
        /// </summary>
        /// <param name="audioData">Recorded audio samples</param>
        /// <returns>31-band frequency spectrum (linear amplitude values)</returns>
        public float[] AnalyzeSpectrum(float[] audioData)
        {
            int fftSize = GetOptimalFFTSize();
            float[] window = GenerateHannWindow(fftSize);
            
            // Overlapping FFT analysis
            const float overlapRatio = 0.75f;
            int hopSize = (int)(fftSize * (1 - overlapRatio));
            
            float[] bandEnergies = new float[_frequencyBands.Length];
            int windowCount = Math.Max(1, (audioData.Length - fftSize) / hopSize + 1);
            
            for (int w = 0; w < windowCount; w++)
            {
                int startIdx = w * hopSize;
                if (startIdx + fftSize > audioData.Length) break;
                
                // Prepare FFT input
                Complex[] fftInput = new Complex[fftSize];
                for (int i = 0; i < fftSize; i++)
                {
                    fftInput[i] = audioData[startIdx + i] * window[i];
                }
                
                // Perform FFT
                Fourier.Forward(fftInput, FourierOptions.Matlab);
                
                // Calculate band energy
                for (int band = 0; band < _frequencyBands.Length; band++)
                {
                    float energy = CalculateBandEnergy(fftInput, _frequencyBands[band], fftSize);
                    bandEnergies[band] += energy;
                }
            }
            
            // Averaging
            for (int i = 0; i < bandEnergies.Length; i++)
            {
                bandEnergies[i] /= windowCount;
            }
            
            return bandEnergies;
        }
        
        /// <summary>
        /// Calculate RMS level from audio data.
        /// </summary>
        public float CalculateRMS(ReadOnlySpan<float> audioData)
        {
            if (audioData.Length == 0) return 0f;
            
            double sum = 0;
            for (int i = 0; i < audioData.Length; i++)
            {
                sum += audioData[i] * audioData[i];
            }
            
            return (float)Math.Sqrt(sum / audioData.Length);
        }
        
        /// <summary>
        /// Calculate RMS level in dB.
        /// </summary>
        public float CalculateRMSdB(ReadOnlySpan<float> audioData)
        {
            float rms = CalculateRMS(audioData);
            return 20f * (float)Math.Log10(Math.Max(rms, 1e-10f));
        }
        
        private int GetOptimalFFTSize()
        {
            if (_sampleRate >= 96000) return 32768;
            if (_sampleRate >= 48000) return 16384;
            return 8192;
        }
        
        private float[] GenerateHannWindow(int size)
        {
            float[] window = new float[size];
            for (int i = 0; i < size; i++)
            {
                window[i] = 0.5f * (1.0f - (float)Math.Cos(2.0 * Math.PI * i / (size - 1)));
            }
            return window;
        }
        
        
        private float CalculateBandEnergy(Complex[] fftOutput, float centerFreq, int fftSize)
        {
            // Calculate bandwidth (23% of center frequency)
            float bandwidth = centerFreq * 0.23f;
            float startFreq = Math.Max(0, centerFreq - bandwidth / 2);
            float endFreq = Math.Min(_sampleRate / 2.0f, centerFreq + bandwidth / 2);
            
            // FFT bin indices
            int startBin = (int)Math.Floor(startFreq * fftSize / _sampleRate);
            int endBin = (int)Math.Ceiling(endFreq * fftSize / _sampleRate);
            
            startBin = Math.Max(0, startBin);
            endBin = Math.Min(fftSize / 2, endBin);
            
            if (startBin >= endBin) return 0;
            
            // Sum energy
            double energySum = 0;
            int binCount = 0;
            
            for (int bin = startBin; bin <= endBin; bin++)
            {
                double magnitude = fftOutput[bin].Magnitude;
                energySum += magnitude * magnitude;
                binCount++;
            }
            
            if (binCount == 0) return 0;
            
            // RMS value
            double rms = Math.Sqrt(energySum / binCount);
            
            // Normalization
            rms /= (fftSize / 2.0);
            
            return (float)rms;
        }
    }
}
