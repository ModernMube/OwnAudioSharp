using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Ownaudio.Utilities.Extensions
{
    /// <summary>
    /// Provides audio onset detection functionality using various algorithms including spectral flux, 
    /// high frequency content, complex domain, and energy-based methods.
    /// </summary>
    public class OnsetDetector
    {
        /// <summary>
        /// Represents a complex number with real and imaginary components for FFT calculations.
        /// </summary>
        public struct ComplexNumber
        {
            /// <summary>
            /// Gets or sets the real component of the complex number.
            /// </summary>
            public float Real;

            /// <summary>
            /// Gets or sets the imaginary component of the complex number.
            /// </summary>
            public float Imaginary;

            /// <summary>
            /// Gets the magnitude (absolute value) of the complex number.
            /// </summary>
            public float Magnitude => (float)Math.Sqrt(Real * Real + Imaginary * Imaginary);

            /// <summary>
            /// Initializes a new instance of the ComplexNumber struct.
            /// </summary>
            /// <param name="real">The real component.</param>
            /// <param name="imaginary">The imaginary component.</param>
            public ComplexNumber(float real, float imaginary)
            {
                Real = real;
                Imaginary = imaginary;
            }
        }

        /// <summary>
        /// Computes the Fast Fourier Transform of the input signal using the Cooley-Tukey algorithm.
        /// </summary>
        /// <param name="input">The input audio samples as a float array.</param>
        /// <returns>An array of complex numbers representing the frequency domain representation.</returns>
        public static ComplexNumber[] FFT(float[] input)
        {
            int n = input.Length;

            int fftSize = 1;
            while (fftSize < n) fftSize *= 2;

            var paddedInput = new ComplexNumber[fftSize];
            for (int i = 0; i < n; i++)
            {
                paddedInput[i] = new ComplexNumber(input[i], 0);
            }
            for (int i = n; i < fftSize; i++)
            {
                paddedInput[i] = new ComplexNumber(0, 0);
            }

            return FFTRecursive(paddedInput);
        }

        /// <summary>
        /// Performs recursive FFT computation using the divide-and-conquer approach.
        /// </summary>
        /// <param name="input">The complex input array to transform.</param>
        /// <returns>The FFT result as an array of complex numbers.</returns>
        private static ComplexNumber[] FFTRecursive(ComplexNumber[] input)
        {
            int n = input.Length;

            if (n <= 1) return input;

            var even = new ComplexNumber[n / 2];
            var odd = new ComplexNumber[n / 2];

            for (int i = 0; i < n / 2; i++)
            {
                even[i] = input[2 * i];
                odd[i] = input[2 * i + 1];
            }

            var evenFFT = FFTRecursive(even);
            var oddFFT = FFTRecursive(odd);

            var result = new ComplexNumber[n];

            for (int k = 0; k < n / 2; k++)
            {
                double angle = -2.0 * Math.PI * k / n;
                var twiddle = new ComplexNumber(
                    (float)Math.Cos(angle),
                    (float)Math.Sin(angle)
                );

                var oddTerm = ComplexMultiply(twiddle, oddFFT[k]);

                result[k] = ComplexAdd(evenFFT[k], oddTerm);
                result[k + n / 2] = ComplexSubtract(evenFFT[k], oddTerm);
            }

            return result;
        }

        /// <summary>
        /// Multiplies two complex numbers.
        /// </summary>
        /// <param name="a">The first complex number.</param>
        /// <param name="b">The second complex number.</param>
        /// <returns>The product of the two complex numbers.</returns>
        private static ComplexNumber ComplexMultiply(ComplexNumber a, ComplexNumber b)
        {
            return new ComplexNumber(
                a.Real * b.Real - a.Imaginary * b.Imaginary,
                a.Real * b.Imaginary + a.Imaginary * b.Real
            );
        }

        /// <summary>
        /// Adds two complex numbers.
        /// </summary>
        /// <param name="a">The first complex number.</param>
        /// <param name="b">The second complex number.</param>
        /// <returns>The sum of the two complex numbers.</returns>
        private static ComplexNumber ComplexAdd(ComplexNumber a, ComplexNumber b)
        {
            return new ComplexNumber(a.Real + b.Real, a.Imaginary + b.Imaginary);
        }

        /// <summary>
        /// Subtracts one complex number from another.
        /// </summary>
        /// <param name="a">The complex number to subtract from.</param>
        /// <param name="b">The complex number to subtract.</param>
        /// <returns>The difference of the two complex numbers.</returns>
        private static ComplexNumber ComplexSubtract(ComplexNumber a, ComplexNumber b)
        {
            return new ComplexNumber(a.Real - b.Real, a.Imaginary - b.Imaginary);
        }

        /// <summary>
        /// Calculates an adaptive threshold for onset detection based on spectral flux values.
        /// </summary>
        /// <param name="spectralFluxValues">A list of spectral flux values to analyze.</param>
        /// <param name="multiplier">The sensitivity multiplier for threshold calculation. Default is 1.5.</param>
        /// <returns>The calculated adaptive threshold value.</returns>
        public static float CalculateAdaptiveThreshold(List<float> spectralFluxValues, float multiplier = 1.5f)
        {
            if (spectralFluxValues.Count == 0) return 0.1f;

            var sorted = spectralFluxValues.OrderBy(x => x).ToList();
            float median = sorted[sorted.Count / 2];
            float mean = spectralFluxValues.Average();

            return Math.Max(median, mean) * multiplier;
        }

        /// <summary>
        /// Detects audio onsets using the specified detection method.
        /// </summary>
        /// <param name="audio">The audio samples as a float array.</param>
        /// <param name="sampleRate">The sample rate of the audio in Hz.</param>
        /// <param name="method">The onset detection method to use. Default is SpectralFlux.</param>
        /// <returns>A list of sample positions where onsets were detected.</returns>
        public static List<int> DetectOnsets(float[] audio, int sampleRate, OnsetMethod method = OnsetMethod.SpectralFlux)
        {
            int frameSize = 1024;
            int hopSize = 512;
            var onsets = new List<int>();
            var detectionValues = new List<float>();

            for (int i = 0; i < audio.Length - frameSize; i += hopSize)
            {
                var frame1 = audio.Skip(i).Take(frameSize).ToArray();
                var frame2 = audio.Skip(i + hopSize).Take(frameSize).ToArray();

                float detectionValue = 0;

                switch (method)
                {
                    case OnsetMethod.SpectralFlux:
                        detectionValue = CalculateSpectralFlux(frame1, frame2);
                        break;
                    case OnsetMethod.HighFrequencyContent:
                        detectionValue = CalculateHighFrequencyContent(frame1);
                        break;
                    case OnsetMethod.ComplexDomain:
                        detectionValue = CalculateComplexDomain(frame1, frame2);
                        break;
                    case OnsetMethod.EnergyBased:
                        detectionValue = CalculateEnergyOnset(frame1, frame2);
                        break;
                }

                detectionValues.Add(detectionValue);
            }

            float threshold = CalculateAdaptiveThreshold(detectionValues);

            for (int i = 1; i < detectionValues.Count - 1; i++)
            {
                if (detectionValues[i] > threshold &&
                    detectionValues[i] > detectionValues[i - 1] &&
                    detectionValues[i] > detectionValues[i + 1])
                {
                    int samplePosition = i * hopSize;

                    if (onsets.Count == 0 || samplePosition - onsets.Last() > sampleRate * 0.05)
                    {
                        onsets.Add(samplePosition);
                    }
                }
            }

            return onsets;
        }

        /// <summary>
        /// Calculates spectral flux between two audio frames for onset detection.
        /// </summary>
        /// <param name="frame1">The first audio frame.</param>
        /// <param name="frame2">The second audio frame.</param>
        /// <returns>The spectral flux value representing the change in spectral content.</returns>
        public static float CalculateSpectralFlux(float[] frame1, float[] frame2)
        {
            var fft1 = FFT(frame1);
            var fft2 = FFT(frame2);

            float spectralFlux = 0;
            int bins = Math.Min(fft1.Length / 2, fft2.Length / 2);

            for (int j = 1; j < bins; j++)
            {
                float mag1 = fft1[j].Magnitude;
                float mag2 = fft2[j].Magnitude;

                float diff = Math.Max(0, mag2 - mag1);
                spectralFlux += diff;
            }

            return spectralFlux;
        }

        /// <summary>
        /// Calculates high frequency content for onset detection, emphasizing higher frequencies.
        /// </summary>
        /// <param name="frame">The audio frame to analyze.</param>
        /// <returns>The high frequency content value.</returns>
        public static float CalculateHighFrequencyContent(float[] frame)
        {
            var fft = FFT(frame);
            float hfc = 0;
            int bins = fft.Length / 2;

            for (int j = 1; j < bins; j++)
            {
                hfc += j * fft[j].Magnitude;
            }

            return hfc;
        }

        /// <summary>
        /// Calculates complex domain onset detection value based on phase and magnitude changes.
        /// </summary>
        /// <param name="frame1">The first audio frame.</param>
        /// <param name="frame2">The second audio frame.</param>
        /// <returns>The complex domain detection value.</returns>
        public static float CalculateComplexDomain(float[] frame1, float[] frame2)
        {
            var fft1 = FFT(frame1);
            var fft2 = FFT(frame2);

            float complexDiff = 0;
            int bins = Math.Min(fft1.Length / 2, fft2.Length / 2);

            for (int j = 1; j < bins; j++)
            {
                float phase1 = (float)Math.Atan2(fft1[j].Imaginary, fft1[j].Real);
                float phase2 = (float)Math.Atan2(fft2[j].Imaginary, fft2[j].Real);

                float phaseDiff = Math.Abs(phase2 - phase1);
                if (phaseDiff > Math.PI) phaseDiff = 2 * (float)Math.PI - phaseDiff;

                complexDiff += phaseDiff * fft2[j].Magnitude;
            }

            return complexDiff;
        }

        /// <summary>
        /// Calculates energy-based onset detection by comparing energy levels between frames.
        /// </summary>
        /// <param name="frame1">The first audio frame.</param>
        /// <param name="frame2">The second audio frame.</param>
        /// <returns>The energy difference value for onset detection.</returns>
        public static float CalculateEnergyOnset(float[] frame1, float[] frame2)
        {
            float energy1 = frame1.Sum(x => x * x);
            float energy2 = frame2.Sum(x => x * x);

            return Math.Max(0, energy2 - energy1);
        }

        /// <summary>
        /// Performs advanced onset detection by combining multiple detection methods and post-processing results.
        /// </summary>
        /// <param name="audio">The audio samples as a float array.</param>
        /// <param name="sampleRate">The sample rate of the audio in Hz.</param>
        /// <param name="sensitivity">The sensitivity multiplier for threshold calculation. Default is 1.5.</param>
        /// <param name="minimumInterval">The minimum time interval between onsets in seconds. Default is 0.05 seconds.</param>
        /// <returns>A refined list of sample positions where onsets were detected.</returns>
        public static List<int> DetectOnsetsAdvanced(float[] audio, int sampleRate,
            float sensitivity = 1.5f, float minimumInterval = 0.05f)
        {
            var fluxOnsets = DetectOnsets(audio, sampleRate, OnsetMethod.SpectralFlux);
            var hfcOnsets = DetectOnsets(audio, sampleRate, OnsetMethod.HighFrequencyContent);
            var energyOnsets = DetectOnsets(audio, sampleRate, OnsetMethod.EnergyBased);

            var allOnsets = new List<int>();
            allOnsets.AddRange(fluxOnsets);
            allOnsets.AddRange(hfcOnsets);
            allOnsets.AddRange(energyOnsets);

            allOnsets.Sort();
            var finalOnsets = new List<int>();
            int minSamples = (int)(minimumInterval * sampleRate);

            foreach (int onset in allOnsets)
            {
                if (finalOnsets.Count == 0 || onset - finalOnsets.Last() >= minSamples)
                {
                    finalOnsets.Add(onset);
                }
            }

            return finalOnsets;
        }

        /// <summary>
        /// Specifies the available onset detection methods.
        /// </summary>
        public enum OnsetMethod
        {
            /// <summary>
            /// Spectral flux method - detects changes in spectral magnitude.
            /// </summary>
            SpectralFlux,

            /// <summary>
            /// High frequency content method - emphasizes higher frequency components.
            /// </summary>
            HighFrequencyContent,

            /// <summary>
            /// Complex domain method - analyzes both magnitude and phase changes.
            /// </summary>
            ComplexDomain,

            /// <summary>
            /// Energy-based method - detects changes in signal energy.
            /// </summary>
            EnergyBased
        }
    }
}
