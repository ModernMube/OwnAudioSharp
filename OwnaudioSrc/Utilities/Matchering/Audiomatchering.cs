using MathNet.Numerics.IntegralTransforms;
using Ownaudio.Sources;
using System;
using System.Linq;
using System.Numerics;

namespace Ownaudio.Utilities.Matchering
{
    /// <summary>
    /// Provides audio spectrum analysis and EQ matching functionality for audio processing applications.
    /// Implements advanced FFT-based frequency analysis and intelligent EQ adjustment algorithms.
    /// </summary>
    public partial class AudioAnalyzer
    {
        #region Constants and Fields

        /// <summary>
        /// Standard ISO frequency bands used for 10-band equalizer analysis (Hz).
        /// </summary>
        private readonly float[] FrequencyBands = {
            20f, 25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f,
            200f, 250f, 315f, 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f,
            2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f, 16000f
        };

        #endregion

        #region Public API Methods

        /// <summary>
        /// Analyzes the frequency spectrum and dynamics of an audio file.
        /// </summary>
        /// <param name="filePath">Full path to the audio file to analyze</param>
        /// <returns>Complete spectrum analysis including frequency bands and dynamic range information</returns>
        /// <exception cref="InvalidOperationException">Thrown when the audio file cannot be loaded</exception>
        public AudioSpectrum AnalyzeAudioFile(string filePath)
        {
            using var source = new Source();
            source.LoadAsync(filePath).Wait();

            if (!source.IsLoaded)
                throw new InvalidOperationException($"Cannot load audio file: {filePath}");

            var audioData = source.GetFloatAudioData(TimeSpan.Zero);
            var channels = source.CurrentDecoder?.StreamInfo.Channels ?? 2;
            var sampleRate = source.CurrentDecoder?.StreamInfo.SampleRate ?? 44100;

            for (int i = 0; i < audioData.Length; i++)
                audioData[i] *= 0.85f;

            var monoData = ConvertToMono(audioData, channels);
            var frequencySpectrum = AnalyzeFrequencySpectrumAdvanced(monoData, sampleRate);
            var dynamics = AnalyzeDynamics(monoData);

            return new AudioSpectrum
            {
                FrequencyBands = frequencySpectrum,
                RMSLevel = dynamics.RMS,
                PeakLevel = dynamics.Peak,
                DynamicRange = dynamics.DynamicRange,
                Loudness = dynamics.Loudness
            };
        }

        /// <summary>
        /// Performs complete EQ matching between source and target audio files with intelligent processing.
        /// </summary>
        /// <param name="sourceFile">Path to the source audio file to be processed</param>
        /// <param name="targetFile">Path to the target audio file to match</param>
        /// <param name="outputFile">Path where the processed audio will be saved</param>
        public void ProcessEQMatching(string sourceFile, string targetFile, string outputFile)
        {
            Console.WriteLine("Analyzing source audio...");
            var sourceSpectrum = AnalyzeAudioFile(sourceFile);

            Console.WriteLine("Analyzing target audio...");
            var targetSpectrum = AnalyzeAudioFile(targetFile);

            var eqAdjustments = CalculateDirectEQAdjustments(sourceSpectrum, targetSpectrum);
            var ampSettings = CalculateDynamicAmpSettings(sourceSpectrum, targetSpectrum);

            Console.WriteLine("Processing audio with direct EQ approach...");
            ApplyDirectEQProcessing(sourceFile, outputFile, eqAdjustments, ampSettings, sourceSpectrum, targetSpectrum);

            Console.WriteLine($"EQ matching completed. Output saved to: {outputFile}");
            PrintAnalysisResults(sourceSpectrum, targetSpectrum, eqAdjustments);
        }

        #endregion

        #region Audio Format Conversion

        /// <summary>
        /// Converts multi-channel audio data to mono by averaging channels.
        /// </summary>
        /// <param name="audioData">Input audio data array</param>
        /// <param name="channels">Number of audio channels</param>
        /// <returns>Mono audio data array</returns>
        private float[] ConvertToMono(float[] audioData, int channels)
        {
            if (channels == 1) return audioData;

            var monoData = new float[audioData.Length / channels];
            for (int i = 0; i < monoData.Length; i++)
            {
                monoData[i] = (audioData[i * 2] + audioData[i * 2 + 1]) / 2.0f;
            }
            return monoData;
        }

        #endregion

        #region Frequency Spectrum Analysis

        /// <summary>
        /// Performs advanced frequency spectrum analysis using overlapped FFT with Blackman-Harris windowing.
        /// </summary>
        /// <param name="audioData">Audio samples to analyze</param>
        /// <param name="sampleRate">Sample rate of the audio in Hz</param>
        /// <returns>Normalized frequency spectrum energy values for each band</returns>
        private float[] AnalyzeFrequencySpectrumAdvanced(float[] audioData, int sampleRate)
        {
            int fftSize = GetOptimalFFTSize(sampleRate);
            const float overlapRatio = 0.875f;

            var hopSize = (int)(fftSize * (1 - overlapRatio));
            var windowFunction = GenerateBlackmanHarrisWindow(fftSize);

            float windowSum = windowFunction.Sum();
            float windowNormFactor = windowSum / fftSize;

            var bandEnergies = new float[FrequencyBands.Length];
            int windowCount = Math.Max(1, (audioData.Length - fftSize) / hopSize + 1);

            for (int w = 0; w < windowCount; w++)
            {
                int startIdx = w * hopSize;
                if (startIdx + fftSize > audioData.Length) break;

                var audioSegment = audioData.Skip(startIdx).Take(fftSize).ToArray();
                var fftInput = PrepareFFTInput(audioSegment, windowFunction, fftSize);

                Fourier.Forward(fftInput, FourierOptions.Matlab);

                for (int band = 0; band < FrequencyBands.Length; band++)
                {
                    var energy = CalculateBandEnergyAdvanced(fftInput, FrequencyBands[band],
                                                           sampleRate, fftSize, windowNormFactor);
                    bandEnergies[band] += energy;
                }
            }

            for (int i = 0; i < bandEnergies.Length; i++)
            {
                bandEnergies[i] /= windowCount;
            }

            return NormalizeSpectrum(bandEnergies);
        }

        /// <summary>
        /// Determines the optimal FFT size based on the audio sample rate for best frequency resolution.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate in Hz</param>
        /// <returns>Optimal FFT size as power of 2</returns>
        private int GetOptimalFFTSize(int sampleRate)
        {
            if (sampleRate >= 96000) return 16384;
            if (sampleRate >= 48000) return 8192;
            return 4096;
        }

        /// <summary>
        /// Calculates weighted energy for a specific frequency band using advanced interpolation.
        /// </summary>
        /// <param name="fftOutput">Complex FFT output data</param>
        /// <param name="centerFreq">Center frequency of the band in Hz</param>
        /// <param name="sampleRate">Audio sample rate in Hz</param>
        /// <param name="fftSize">Size of the FFT used</param>
        /// <param name="windowNormFactor">Window function normalization factor</param>
        /// <returns>Weighted RMS energy value for the frequency band</returns>
        private float CalculateBandEnergyAdvanced(Complex[] fftOutput, float centerFreq,
                                          int sampleRate, int fftSize, float windowNormFactor)
        {
            var bandwidth = GetBandwidth(centerFreq);
            var startFreq = Math.Max(0, centerFreq - bandwidth / 2);
            var endFreq = Math.Min(sampleRate / 2.0f, centerFreq + bandwidth / 2);

            double startBinExact = startFreq * fftSize / (double)sampleRate;
            double endBinExact = endFreq * fftSize / (double)sampleRate;

            int startBin = Math.Max(0, (int)Math.Floor(startBinExact));
            int endBin = Math.Min(fftSize / 2, (int)Math.Ceiling(endBinExact));

            if (startBin >= endBin) return 0;

            double energySum = 0;
            double weightSum = 0;

            for (int bin = startBin; bin <= endBin; bin++)
            {
                double binFreq = bin * (double)sampleRate / fftSize;

                if (binFreq < startFreq || binFreq > endFreq) continue;

                double weight = CalculateFrequencyWeight(binFreq, centerFreq, bandwidth);
                double magnitude = fftOutput[bin].Magnitude;
                energySum += magnitude * magnitude * weight;
                weightSum += weight;
            }

            if (weightSum == 0) return 0;

            double weightedRMS = Math.Sqrt(energySum / weightSum);
            weightedRMS /= (windowNormFactor * fftSize / 2.0);

            return (float)weightedRMS;
        }

        /// <summary>
        /// Calculates linear weighting for frequency bins within a band based on distance from center.
        /// </summary>
        /// <param name="binFreq">Frequency of the FFT bin in Hz</param>
        /// <param name="centerFreq">Center frequency of the band in Hz</param>
        /// <param name="bandwidth">Total bandwidth of the frequency band in Hz</param>
        /// <returns>Linear weight value between 0 and 1</returns>
        private double CalculateFrequencyWeight(double binFreq, float centerFreq, float bandwidth)
        {
            double distance = Math.Abs(binFreq - centerFreq);
            double maxDistance = bandwidth / 2.0;

            if (distance >= maxDistance) return 0;

            return 1.0 - (distance / maxDistance);
        }

        /// <summary>
        /// Calculates the bandwidth for a frequency band using proportional scaling.
        /// </summary>
        /// <param name="centerFreq">Center frequency of the band in Hz</param>
        /// <returns>Bandwidth in Hz proportional to center frequency</returns>
        private float GetBandwidth(float centerFreq)
        {
            return centerFreq * 0.23f;
        }

        #endregion

        #region Window Functions

        /// <summary>
        /// Generates a Blackman-Harris window function for FFT windowing with excellent sidelobe suppression.
        /// </summary>
        /// <param name="size">Size of the window in samples</param>
        /// <returns>Array of window coefficients</returns>
        private float[] GenerateBlackmanHarrisWindow(int size)
        {
            var window = new float[size];
            const double a0 = 0.35875;
            const double a1 = 0.48829;
            const double a2 = 0.14128;
            const double a3 = 0.01168;

            for (int i = 0; i < size; i++)
            {
                double n = (double)i / (size - 1);
                window[i] = (float)(a0 - a1 * Math.Cos(2 * Math.PI * n) +
                                     a2 * Math.Cos(4 * Math.PI * n) -
                                     a3 * Math.Cos(6 * Math.PI * n));
            }
            return window;
        }

        /// <summary>
        /// Prepares audio data for FFT analysis by applying windowing and zero-padding.
        /// </summary>
        /// <param name="audioSegment">Audio data segment to process</param>
        /// <param name="window">Window function coefficients</param>
        /// <param name="fftSize">Target FFT size for zero-padding</param>
        /// <returns>Complex array ready for FFT processing</returns>
        private Complex[] PrepareFFTInput(float[] audioSegment, float[] window, int fftSize)
        {
            var fftInput = new Complex[fftSize];
            int dataLength = Math.Min(audioSegment.Length, window.Length);

            for (int i = 0; i < fftSize; i++)
            {
                if (i < dataLength)
                {
                    fftInput[i] = audioSegment[i] * window[i];
                }
                else
                {
                    fftInput[i] = Complex.Zero;
                }
            }

            return fftInput;
        }

        #endregion

        #region Signal Processing Utilities

        /// <summary>
        /// Normalizes spectrum values to a 0-1 range based on the maximum energy value.
        /// </summary>
        /// <param name="spectrum">Input frequency spectrum array</param>
        /// <returns>Normalized spectrum with values between 0 and 1</returns>
        private float[] NormalizeSpectrum(float[] spectrum)
        {
            float max = spectrum.Max();
            if (max > 0)
            {
                for (int i = 0; i < spectrum.Length; i++)
                {
                    spectrum[i] /= max;
                }
            }
            return spectrum;
        }

        #endregion

        #region Results and Diagnostics

        /// <summary>
        /// Prints comprehensive analysis results and safety information to the console.
        /// </summary>
        /// <param name="source">Source audio spectrum analysis</param>
        /// <param name="target">Target audio spectrum analysis</param>
        /// <param name="eqAdjustments">Applied EQ adjustments in dB</param>
        private void PrintAnalysisResults(AudioSpectrum source, AudioSpectrum target, float[] eqAdjustments)
        {
            Console.WriteLine("\n=== ANALYSIS RESULTS ===");
            Console.WriteLine($"Source - RMS: {source.RMSLevel:F3}, Peak: {source.PeakLevel:F3}, Loudness: {source.Loudness:F1} LUFS");
            Console.WriteLine($"Target - RMS: {target.RMSLevel:F3}, Peak: {target.PeakLevel:F3}, Loudness: {target.Loudness:F1} LUFS");

            float sourceCrest = 20 * (float)Math.Log10(source.PeakLevel / Math.Max(source.RMSLevel, 1e-10f));
            float targetCrest = 20 * (float)Math.Log10(target.PeakLevel / Math.Max(target.RMSLevel, 1e-10f));
            Console.WriteLine($"Crest Factor - Source: {sourceCrest:F1}dB, Target: {targetCrest:F1}dB");

            float totalBoost = eqAdjustments.Where(x => x > 0).Sum();
            string riskLevel = totalBoost > 30 ? "HIGH" : totalBoost > 15 ? "MEDIUM" : "LOW";
            Console.WriteLine($"Distortion Risk: {riskLevel} (Total boost: {totalBoost:F1}dB)");

            Console.WriteLine("\nEQ Adjustments (with distortion protection):");
            //var bandNames = new[] { "31Hz", "63Hz", "125Hz", "250Hz", "500Hz", "1kHz", "2kHz", "4kHz", "8kHz", "16kHz" };
            var bandNames = new[] { 
                "20Hz", "25Hz", "31Hz", "40Hz", "50Hz", "63Hz", "80Hz", "100Hz", "125Hz", "160Hz",
                "200Hz", "250Hz", "315Hz", "400Hz", "500Hz", "630Hz", "800Hz", "1kHz", "1.25kHz", "1.6kHz",
                "2kHz", "2.5kHz", "3.15kHz", "4kHz", "5kHz", "6.3kHz", "8kHz", "10kHz", "12.5kHz", "16kHz"
            };
            for (int i = 0; i < eqAdjustments.Length; i++)
            {
                string warning = Math.Abs(eqAdjustments[i]) > 8.0f ? " ⚠️" : "";
                Console.WriteLine($"{bandNames[i]}: {eqAdjustments[i]:+0.0;-0.0} dB{warning}");
            }

            Console.WriteLine("\n=== SAFETY FEATURES ACTIVE ===");
            Console.WriteLine("✓ Frequency-specific boost limits");
            Console.WriteLine("✓ Dynamic headroom calculation");
            Console.WriteLine("✓ Psychoacoustic weighting");
            Console.WriteLine("✓ EQ curve smoothing");
            Console.WriteLine("✓ Safety limiter (-0.1dB ceiling)");
            Console.WriteLine("✓ Real-time clipping detection");
        }

        #endregion
    }
}
