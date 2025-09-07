using MathNet.Numerics.IntegralTransforms;
using Ownaudio.Sources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Ownaudio.Utilities.Matchering
{
    /// <summary>
    /// Audio spectrum analysis and EQ matching implementation
    /// </summary>
    public partial class AudioAnalyzer
    {
        #region Constants and Fields

        /// <summary>
        /// Center frequencies for frequency bands (Hz)
        /// </summary>
        private readonly float[] FrequencyBands = {
            31.25f,
            62.5f,
            125f,
            250f,
            500f,
            1000f,
            2000f,
            4000f,
            8000f,
            16000f
        };

        #endregion

        #region Public Methods

        /// <summary>
        /// Analyzes spectrum from an audio file
        /// </summary>
        /// <param name="filePath">Path to the audio file</param>
        /// <returns>Audio spectrum analysis results</returns>
        public AudioSpectrum AnalyzeAudioFile(string filePath)
        {
            using var source = new Source();
            source.LoadAsync(filePath).Wait();

            if (!source.IsLoaded)
                throw new InvalidOperationException($"Cannot load audio file: {filePath}");

            var audioData = source.GetFloatAudioData(TimeSpan.Zero);
            var channels = source.CurrentDecoder?.StreamInfo.Channels ?? 2;
            var sampleRate = source.CurrentDecoder?.StreamInfo.SampleRate ?? 44100;

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
        /// Performs EQ matching and processing
        /// </summary>
        /// <param name="sourceFile">Source audio file path</param>
        /// <param name="targetFile">Target audio file path</param>
        /// <param name="outputFile">Output file path</param>
        public void ProcessEQMatching(string sourceFile, string targetFile, string outputFile)
        {
            Console.WriteLine("Analyzing source audio...");
            var sourceSpectrum = AnalyzeAudioFile(sourceFile);

            Console.WriteLine("Analyzing target audio...");
            var targetSpectrum = AnalyzeAudioFile(targetFile);

            var eqAdjustments = CalculateEQAdjustments(sourceSpectrum, targetSpectrum);
            var compressionSettings = CalculateMultibandCompressionSettings(sourceSpectrum, targetSpectrum);
            var ampSettings = CalculateDynamicAmpSettings(sourceSpectrum, targetSpectrum);

            Console.WriteLine("Processing audio with calculated settings...");
            ApplyProcessingOffline(sourceFile, outputFile, eqAdjustments, compressionSettings, ampSettings);

            Console.WriteLine($"EQ matching completed. Output saved to: {outputFile}");
            PrintAnalysisResults(sourceSpectrum, targetSpectrum, eqAdjustments);
        }

        #endregion

        #region Audio Conversion

        /// <summary>
        /// Converts stereo audio to mono
        /// </summary>
        /// <param name="audioData">Input audio data</param>
        /// <param name="channels">Number of channels</param>
        /// <returns>Mono audio data</returns>
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

        #region Frequency Analysis

        /// <summary>
        /// Advanced frequency spectrum analysis using FFT
        /// </summary>
        /// <param name="audioData">Audio data to analyze</param>
        /// <param name="sampleRate">Sample rate in Hz</param>
        /// <returns>Frequency spectrum data</returns>
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

            var rawSpectrum = NormalizeSpectrum(bandEnergies);

            var sourceSpectrum = new AudioSpectrum { FrequencyBands = rawSpectrum };
            var weightedSpectrum = ApplyPsychoacousticWeighting(rawSpectrum, sourceSpectrum);

            return weightedSpectrum;
        }

        /// <summary>
        /// Determines optimal FFT size based on sample rate
        /// </summary>
        /// <param name="sampleRate">Audio sample rate</param>
        /// <returns>Optimal FFT size</returns>
        private int GetOptimalFFTSize(int sampleRate)
        {
            if (sampleRate >= 96000) return 16384;
            if (sampleRate >= 48000) return 8192;
            return 4096;
        }

        /// <summary>
        /// Calculates energy for a specific frequency band using advanced methods
        /// </summary>
        /// <param name="fftOutput">FFT output data</param>
        /// <param name="centerFreq">Center frequency of the band</param>
        /// <param name="sampleRate">Audio sample rate</param>
        /// <param name="fftSize">FFT size used</param>
        /// <param name="windowNormFactor">Window normalization factor</param>
        /// <returns>Band energy value</returns>
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
        /// Calculates frequency weighting for band analysis
        /// </summary>
        /// <param name="binFreq">Frequency of the FFT bin</param>
        /// <param name="centerFreq">Center frequency of the band</param>
        /// <param name="bandwidth">Bandwidth of the frequency band</param>
        /// <returns>Weight value</returns>
        private double CalculateFrequencyWeight(double binFreq, float centerFreq, float bandwidth)
        {
            double distance = Math.Abs(binFreq - centerFreq);
            double maxDistance = bandwidth / 2.0;

            if (distance >= maxDistance) return 0;

            return 1.0 - (distance / maxDistance);
        }

        /// <summary>
        /// Calculates bandwidth for a given center frequency
        /// </summary>
        /// <param name="centerFreq">Center frequency</param>
        /// <returns>Bandwidth in Hz</returns>
        private float GetBandwidth(float centerFreq)
        {
            return centerFreq * 0.23f;
        }

        #endregion

        #region Window Functions

        /// <summary>
        /// Generates Blackman-Harris window function
        /// </summary>
        /// <param name="size">Window size</param>
        /// <returns>Window coefficients</returns>
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
        /// Prepares FFT input data with windowing and zero-padding
        /// </summary>
        /// <param name="audioSegment">Audio data segment</param>
        /// <param name="window">Window function coefficients</param>
        /// <param name="fftSize">Target FFT size</param>
        /// <returns>Prepared complex array for FFT</returns>
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

        #region Psychoacoustic Processing

        /// <summary>
        /// Applies psychoacoustic weighting to frequency spectrum
        /// </summary>
        /// <param name="spectrum">Input frequency spectrum</param>
        /// <param name="sourceSpectrum">Source audio spectrum for context</param>
        /// <returns>Weighted spectrum</returns>
        private float[] ApplyPsychoacousticWeighting(float[] spectrum, AudioSpectrum sourceSpectrum)
        {
            var weightedSpectrum = new float[spectrum.Length];

            for (int i = 0; i < FrequencyBands.Length; i++)
            {
                float freq = FrequencyBands[i];

                float aWeight = CalculateAWeighting(freq);
                float loudnessWeight = CalculateLoudnessWeighting(freq);
                float masking = CalculateSpectralMasking(spectrum, i);

                // Csökkentett súlyozás - kevésbé befolyásolja az eredményt
                float totalWeight = (aWeight + loudnessWeight) / 4.0f; // 2.0f helyett 4.0f
                totalWeight *= (1.0f - masking * 0.5f); // masking hatás csökkentése

                // Magasak esetén még kisebb súlyozás
                if (freq > 4000f)
                {
                    totalWeight *= 0.5f;
                }

                weightedSpectrum[i] = spectrum[i] * (float)Math.Pow(10, totalWeight / 20.0);
            }

            return weightedSpectrum;
        }

        /// <summary>
        /// Calculates A-weighting curve value for a frequency
        /// </summary>
        /// <param name="frequency">Frequency in Hz</param>
        /// <returns>A-weighting value in dB</returns>
        private float CalculateAWeighting(float frequency)
        {
            double f = frequency;
            double f2 = f * f;
            double f4 = f2 * f2;

            const double c1 = 12194.0 * 12194.0;
            const double c2 = 20.6 * 20.6;
            const double c3 = 107.7 * 107.7;
            const double c4 = 737.9 * 737.9;

            double numerator = c1 * f4;
            double denominator = (f2 + c2) * Math.Sqrt((f2 + c3) * (f2 + c4)) * (f2 + c1);

            double aWeight = 20.0 * Math.Log10(numerator / denominator) + 2.0;

            return (float)Math.Max(-50.0, Math.Min(10.0, aWeight));
        }

        /// <summary>
        /// Calculates equal loudness contour weighting (ISO 226)
        /// </summary>
        /// <param name="frequency">Frequency in Hz</param>
        /// <returns>Loudness weighting in dB</returns>
        private float CalculateLoudnessWeighting(float frequency)
        {
            var loudnessTable = new Dictionary<float, float>
            {
                { 31.25f, -39.0f },   { 62.5f, -26.0f },    { 125f, -16.0f },
                { 250f, -8.6f },      { 500f, -3.2f },      { 1000f, 0.0f },
                { 2000f, 1.0f },      { 4000f, 1.0f },      { 8000f, -1.1f },
                { 16000f, -6.6f }
            };

            var sortedKeys = loudnessTable.Keys.OrderBy(x => x).ToArray();

            if (frequency <= sortedKeys[0]) return loudnessTable[sortedKeys[0]];
            if (frequency >= sortedKeys.Last()) return loudnessTable[sortedKeys.Last()];

            for (int i = 0; i < sortedKeys.Length - 1; i++)
            {
                if (frequency >= sortedKeys[i] && frequency <= sortedKeys[i + 1])
                {
                    float ratio = (frequency - sortedKeys[i]) / (sortedKeys[i + 1] - sortedKeys[i]);
                    return loudnessTable[sortedKeys[i]] +
                           ratio * (loudnessTable[sortedKeys[i + 1]] - loudnessTable[sortedKeys[i]]);
                }
            }

            return 0.0f;
        }

        /// <summary>
        /// Calculates spectral masking effects
        /// </summary>
        /// <param name="spectrum">Frequency spectrum</param>
        /// <param name="bandIndex">Index of the band to analyze</param>
        /// <returns>Masking factor (0-1)</returns>
        private float CalculateSpectralMasking(float[] spectrum, int bandIndex)
        {
            float masking = 0.0f;

            for (int i = 0; i < spectrum.Length; i++)
            {
                if (i == bandIndex) continue;

                float freqDiff = Math.Abs(FrequencyBands[i] - FrequencyBands[bandIndex]);
                float levelDiff = spectrum[i] - spectrum[bandIndex];

                if (levelDiff > 0 && freqDiff < FrequencyBands[bandIndex] * 0.5f)
                {
                    float maskingStrength = levelDiff * (1.0f - freqDiff / (FrequencyBands[bandIndex] * 0.5f));
                    masking = Math.Max(masking, Math.Min(0.8f, maskingStrength * 0.1f));
                }
            }

            return masking;
        }

        #endregion
         
        #region Audio Processing

        /// <summary>
        /// Applies processing to audio file with calculated settings
        /// </summary>
        /// <param name="inputFile">Input file path</param>
        /// <param name="outputFile">Output file path</param>
        /// <param name="eqAdjustments">EQ adjustment values</param>
        /// <param name="compression">Compression settings</param>
        /// <param name="dynamicAmp">Dynamic amplification settings</param>
        private void ApplyProcessingOffline(string inputFile, string outputFile,
            float[] eqAdjustments, CompressionSettings[] compression, DynamicAmpSettings dynamicAmp)
        {
            try
            {
                Console.WriteLine($"Starting offline processing: {inputFile} -> {outputFile}");

                using var source = new Source();
                source.LoadAsync(inputFile).Wait();

                if (!source.IsLoaded)
                    throw new InvalidOperationException($"Cannot load audio file: {inputFile}");

                var audioData = source.GetFloatAudioData(TimeSpan.Zero);
                var channels = source.CurrentDecoder?.StreamInfo.Channels ?? 2;
                var sampleRate = source.CurrentDecoder?.StreamInfo.SampleRate ?? 44100;
                var totalDuration = source.Duration;

                Console.WriteLine($"Audio loaded: {totalDuration}, {channels} channels, {sampleRate} Hz");

                var processor = new MultibandProcessor(eqAdjustments, compression, dynamicAmp);

                int chunkSize = 512 * channels;
                var processedData = new List<float>();

                int totalSamples = audioData.Length;
                int processedSamples = 0;

                for (int offset = 0; offset < totalSamples; offset += chunkSize)
                {
                    int samplesToProcess = Math.Min(chunkSize, totalSamples - offset);

                    var chunk = new float[samplesToProcess];
                    Array.Copy(audioData, offset, chunk, 0, samplesToProcess);

                    processor.Process(chunk.AsSpan());

                    processedData.AddRange(chunk);

                    processedSamples += samplesToProcess;
                    float progress = (float)processedSamples / totalSamples * 100f;

                    Console.Write($"\rProcessing: {progress:F1}%");
                }

                Console.WriteLine("\nProcessing completed. Writing to file...");

                Ownaudio.Utilities.WaveFile.WriteFile(outputFile, processedData.ToArray(), sampleRate, channels, 24);

                Console.WriteLine($"Output file created: {outputFile}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during offline processing: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Normalizes frequency spectrum values
        /// </summary>
        /// <param name="spectrum">Input spectrum</param>
        /// <returns>Normalized spectrum</returns>
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

        /// <summary>
        /// Prints detailed analysis results to console
        /// </summary>
        /// <param name="source">Source spectrum analysis</param>
        /// <param name="target">Target spectrum analysis</param>
        /// <param name="eqAdjustments">Applied EQ adjustments</param>
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
            var bandNames = new[] { "31Hz", "63Hz", "125Hz", "250Hz", "500Hz", "1kHz", "2kHz", "4kHz", "8kHz", "16kHz" };
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
