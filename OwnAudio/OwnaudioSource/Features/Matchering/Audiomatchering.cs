using MathNet.Numerics.IntegralTransforms;
using OwnaudioNET.Sources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace OwnaudioNET.Features.Matchering
{
    /// <summary>
    /// Provides audio spectrum analysis and EQ matching functionality for audio processing applications.
    /// Implements advanced FFT-based frequency analysis and intelligent EQ adjustment algorithms.
    /// </summary>
    public partial class AudioAnalyzer
    {
        #region Constants and Fields

        /// <summary>
        /// Standard ISO frequency bands used for 30-band equalizer analysis in Hz.
        /// Contains frequencies from 20Hz to 16kHz following ISO standards for audio analysis.
        /// </summary>
        private readonly float[] FrequencyBands = {
            20f, 25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f,
            200f, 250f, 315f, 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f,
            2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f, 16000f
        };

        #endregion

        #region Segmented Analysis Configuration

        /// <summary>
        /// Configuration instance for segmented analysis parameters.
        /// </summary>
        private readonly SegmentedAnalysisConfig _segmentConfig = new();

        #endregion

        #region Public API Methods

        // Thread synchronization lock to prevent race condition during MiniAudio initialization
        private static readonly object _analyzerLock = new object();

        /// <summary>
        /// Performs enhanced audio analysis using segmented approach with weighted averaging.
        /// Divides the audio into overlapping segments and analyzes each segment separately
        /// to provide more accurate and robust spectrum analysis.
        /// Thread-safe: Uses lock to prevent concurrent FileSource creation during initialization.
        /// </summary>
        /// <param name="filePath">Path to the audio file to analyze</param>
        /// <returns>Weighted average spectrum analysis result</returns>
        /// <exception cref="InvalidOperationException">Thrown when the audio file cannot be loaded</exception>
        public AudioSpectrum AnalyzeAudioFile(string filePath)
        {
            // CRITICAL: Lock prevents race condition when multiple FileSource instances
            // try to initialize MiniAudio bindings simultaneously on first run
            lock (_analyzerLock)
            {
                // Give MiniAudio DLL time to fully initialize on first call
                System.Threading.Thread.Sleep(300);

                using FileSource source = new FileSource(filePath);

                if (source.Duration == 0)
                    throw new InvalidOperationException($"Cannot load audio file: {filePath}");

                float[] audioData = source.GetFloatAudioData(TimeSpan.Zero);
                int channels = source.StreamInfo.Channels;
                int sampleRate = source.StreamInfo.SampleRate;

                float[] monoData = ConvertToMono(audioData, channels);

                Console.WriteLine($"Starting segmented analysis: {filePath}");
                Console.WriteLine($"Audio length: {monoData.Length / (float)sampleRate:F1}s, Sample rate: {sampleRate}Hz");

                List<AudioSegment> segments = CreateAudioSegments(monoData, sampleRate);
                List<SegmentAnalysis> segmentAnalyses = AnalyzeSegments(segments, sampleRate);
                List<SegmentAnalysis> filteredAnalyses = FilterOutlierSegments(segmentAnalyses);

                return CalculateWeightedAverageSpectrum(filteredAnalyses);
            }
        }

        /// <summary>
        /// Performs enhanced EQ matching between source and target audio files using segmented analysis.
        /// Analyzes both files in segments, calculates optimal EQ adjustments, and applies them to the source.
        /// </summary>
        /// <param name="sourceFile">Path to the source audio file to be processed</param>
        /// <param name="targetFile">Path to the target audio file to match</param>
        /// <param name="outputFile">Path where the processed audio will be saved</param>
        public void ProcessEQMatching(string sourceFile, string targetFile, string outputFile)
        {
            Console.WriteLine("=== SEGMENTED EQ MATCHING ===");

            Console.WriteLine("Analyzing source audio (segmented)...");
            AudioSpectrum sourceSpectrum = AnalyzeAudioFile(sourceFile);

            Console.WriteLine("Analyzing target audio (segmented)...");
            AudioSpectrum targetSpectrum = AnalyzeAudioFile(targetFile);

            float[] eqAdjustments = CalculateDirectEQAdjustments(sourceSpectrum, targetSpectrum);
            DynamicAmpSettings ampSettings = CalculateDynamicAmpSettings(sourceSpectrum, targetSpectrum);

            Console.WriteLine("Processing audio with segmented-based EQ...");
            ApplyDirectEQProcessing(sourceFile, outputFile, eqAdjustments, ampSettings, sourceSpectrum, targetSpectrum);

            //PrintSegmentedAnalysisResults(sourceSpectrum, targetSpectrum, eqAdjustments);
        }

        #endregion

        #region Segment Creation and Management

        /// <summary>
        /// Creates overlapping audio segments for individual analysis.
        /// Divides the input audio into smaller segments with configurable overlap
        /// to ensure smooth analysis across the entire audio duration.
        /// </summary>
        /// <param name="audioData">Input audio data as float array</param>
        /// <param name="sampleRate">Sample rate of the audio in Hz</param>
        /// <returns>List of audio segments with metadata</returns>
        private List<AudioSegment> CreateAudioSegments(float[] audioData, int sampleRate)
        {
            List<AudioSegment> segments = new List<AudioSegment>();

            int segmentSamples = (int)(_segmentConfig.SegmentLengthSeconds * sampleRate);
            int hopSize = (int)(segmentSamples * (1 - _segmentConfig.OverlapRatio));

            if (audioData.Length <= segmentSamples)
                throw new InvalidOperationException($"The audio is too short. Less than 10 seconds!: {audioData.Length / sampleRate} second");


            Console.WriteLine($"Segment size: {segmentSamples} samples ({_segmentConfig.SegmentLengthSeconds}s)");
            Console.WriteLine($"Hop size: {hopSize} samples (overlap: {_segmentConfig.OverlapRatio * 100:F1}%)");

            for (int start = 0; start < audioData.Length - segmentSamples; start += hopSize)
            {
                int actualLength = Math.Min(segmentSamples, audioData.Length - start);
                float[]? segmentData = new float[actualLength];
                Array.Copy(audioData, start, segmentData, 0, actualLength);

                // Calculate segment energy to filter out silent parts
                float segmentRMS = CalculateRMS(segmentData);
                float segmentEnergyDB = 20 * (float)Math.Log10(Math.Max(segmentRMS, 1e-10f));

                segments.Add(new AudioSegment
                {
                    Data = segmentData,
                    StartTime = (float)start / sampleRate,
                    Duration = (float)actualLength / sampleRate,
                    EnergyLevel = segmentEnergyDB,
                    SampleRate = sampleRate
                });
            }

            Console.WriteLine($"Created {segments.Count} segments");
            return segments;
        }

        /// <summary>
        /// Analyzes frequency spectrum and dynamics for all audio segments.
        /// Processes each segment individually and filters out segments that are too quiet
        /// based on the configured energy threshold.
        /// </summary>
        /// <param name="segments">List of audio segments to analyze</param>
        /// <param name="sampleRate">Sample rate of the audio in Hz</param>
        /// <returns>List of segment analysis results</returns>
        private List<SegmentAnalysis> AnalyzeSegments(List<AudioSegment> segments, int sampleRate)
        {
            List<SegmentAnalysis> analyses = new List<SegmentAnalysis>();
            int validSegments = 0;

            for (int i = 0; i < segments.Count; i++)
            {
                AudioSegment? segment = segments[i];

                // Skip segments that are too quiet
                if (segment.EnergyLevel < _segmentConfig.MinSegmentEnergyThreshold)
                {
                    //Console.WriteLine($"Segment {i + 1} skipped (too quiet: {segment.EnergyLevel:F1}dBFS)");
                    continue;
                }

                float[] spectrum = AnalyzeSegmentSpectrum(segment.Data, sampleRate);
                DynamicsInfo dynamics = AnalyzeAbsoluteDynamics(segment.Data);

                analyses.Add(new SegmentAnalysis
                {
                    SegmentIndex = i,
                    StartTime = segment.StartTime,
                    Duration = segment.Duration,
                    EnergyLevel = segment.EnergyLevel,
                    FrequencySpectrum = spectrum,
                    Dynamics = dynamics,
                    Weight = CalculateSegmentWeight(segment, dynamics)
                });

                validSegments++;
            }

            Console.WriteLine($"Completed analysis: {validSegments} valid segments from {segments.Count} total");
            return analyses;
        }

        #endregion

        #region Segment Analysis Methods

        /// <summary>
        /// Analyzes frequency spectrum for a single audio segment.
        /// Uses the existing FFT analysis method optimized for segment-specific analysis.
        /// </summary>
        /// <param name="segmentData">Audio data for the segment</param>
        /// <param name="sampleRate">Sample rate of the audio in Hz</param>
        /// <returns>Frequency spectrum analysis result for the segment</returns>
        private float[] AnalyzeSegmentSpectrum(float[] segmentData, int sampleRate)
        {
            return AnalyzeFrequencySpectrumAbsolute(segmentData, sampleRate);
        }

        /// <summary>
        /// Calculates a weight factor for a segment based on its audio characteristics.
        /// Considers energy level, dynamic range, and position within the audio file
        /// to determine how much influence this segment should have in the final result.
        /// </summary>
        /// <param name="segment">Audio segment information</param>
        /// <param name="dynamics">Dynamic analysis information for the segment</param>
        /// <returns>Weight factor for the segment (higher values indicate more importance)</returns>
        private float CalculateSegmentWeight(AudioSegment segment, DynamicsInfo dynamics)
        {
            float energyWeight = 1.0f;
            float dynamicWeight = 1.0f;
            float positionWeight = 1.0f;

            // Energy-based weighting - prefer segments with good signal level
            if (segment.EnergyLevel > -20.0f)
                energyWeight = 1.2f; // Boost loud segments
            else if (segment.EnergyLevel < -40.0f)
                energyWeight = 0.7f; // Reduce quiet segments

            // Dynamic range weighting - prefer segments with balanced dynamics
            float idealDynamicRange = 15.0f; // dB
            float dynamicDifference = Math.Abs(dynamics.DynamicRange - idealDynamicRange);
            dynamicWeight = Math.Max(0.5f, 1.0f - (dynamicDifference / 20.0f));

            // Position weighting - slightly prefer middle sections
            float normalizedPosition = segment.StartTime / (segment.StartTime + segment.Duration);
            if (normalizedPosition > 0.2f && normalizedPosition < 0.8f)
                positionWeight = 1.1f; // Boost middle sections slightly

            return energyWeight * dynamicWeight * positionWeight;
        }

        #endregion

        #region Outlier Detection and Filtering

        /// <summary>
        /// Filters out outlier segments using statistical analysis across frequency bands.
        /// Identifies segments that deviate significantly from the norm and excludes them
        /// from the final averaging to improve analysis accuracy.
        /// </summary>
        /// <param name="analyses">List of segment analyses to filter</param>
        /// <returns>Filtered list of segment analyses with outliers removed</returns>
        private List<SegmentAnalysis> FilterOutlierSegments(List<SegmentAnalysis> analyses)
        {
            if (analyses.Count < 3) return analyses; // Need minimum segments for statistical analysis

            Console.WriteLine("\n=== OUTLIER DETECTION ===");

            List<SegmentAnalysis> filtered = new List<SegmentAnalysis>();
            int outlierCount = 0;

            // Calculate statistics for each frequency band
            for (int band = 0; band < FrequencyBands.Length; band++)
            {
                float[] bandValues = analyses.Select(a => a.FrequencySpectrum[band]).ToArray();
                var stats = CalculateBandStatistics(bandValues);

                foreach (var analysis in analyses)
                {
                    float deviation = Math.Abs(analysis.FrequencySpectrum[band] - stats.Mean) / stats.StdDev;
                    if (deviation > _segmentConfig.OutlierThreshold)
                    {
                        analysis.OutlierScore += 1.0f;
                    }
                }
            }

            // Filter based on outlier scores
            float maxOutlierScore = FrequencyBands.Length * 0.3f; // Allow outliers in up to 30% of bands

            foreach (var analysis in analyses)
            {
                if (analysis.OutlierScore <= maxOutlierScore)
                {
                    filtered.Add(analysis);
                }
                else
                {
                    outlierCount++;
                    //Console.WriteLine($"Segment {analysis.SegmentIndex + 1} filtered as outlier (score: {analysis.OutlierScore:F1})");
                }
            }

            Console.WriteLine($"Filtered {outlierCount} outlier segments, kept {filtered.Count} segments");
            return filtered;
        }

        /// <summary>
        /// Calculates statistical measures (mean, standard deviation, median) for a frequency band.
        /// Used for outlier detection by providing statistical reference points.
        /// </summary>
        /// <param name="values">Array of values for statistical calculation</param>
        /// <returns>Tuple containing mean, standard deviation, and median values</returns>
        private (float Mean, float StdDev, float Median) CalculateBandStatistics(float[] values)
        {
            var sortedValues = values.OrderBy(x => x).ToArray();
            float mean = values.Average();
            float median = sortedValues[sortedValues.Length / 2];

            float variance = values.Select(x => (x - mean) * (x - mean)).Average();
            float stdDev = (float)Math.Sqrt(variance);

            return (mean, Math.Max(stdDev, 1e-10f), median);
        }

        #endregion

        #region Weighted Average Calculation

        /// <summary>
        /// Calculates weighted average spectrum from filtered segment analyses.
        /// Combines individual segment results using their calculated weights
        /// to produce a final representative spectrum for the entire audio.
        /// </summary>
        /// <param name="analyses">List of filtered segment analyses</param>
        /// <returns>Weighted average audio spectrum</returns>
        /// <exception cref="InvalidOperationException">Thrown when no valid segments are available for analysis</exception>
        private AudioSpectrum CalculateWeightedAverageSpectrum(List<SegmentAnalysis> analyses)
        {
            if (analyses.Count == 0)
                throw new InvalidOperationException("No valid segments for analysis");

            float[]? weightedSpectrum = new float[FrequencyBands.Length];
            float totalWeight = 0f;
            float weightedRMS = 0f;
            float weightedPeak = 0f;
            float weightedLoudness = 0f;
            float weightedDynamicRange = 0f;

            Console.WriteLine("\n=== WEIGHTED AVERAGING ===");

            foreach (var analysis in analyses)
            {
                float weight = analysis.Weight;
                totalWeight += weight;

                // Weight spectrum values
                for (int i = 0; i < FrequencyBands.Length; i++)
                {
                    weightedSpectrum[i] += analysis.FrequencySpectrum[i] * weight;
                }

                // Weight dynamics
                weightedRMS += analysis.Dynamics.RMS * weight;
                weightedPeak = Math.Max(weightedPeak, analysis.Dynamics.Peak); // Peak is maximum, not averaged
                weightedLoudness += analysis.Dynamics.Loudness * weight;
                weightedDynamicRange += analysis.Dynamics.DynamicRange * weight;
            }

            // Normalize by total weight
            for (int i = 0; i < FrequencyBands.Length; i++)
            {
                weightedSpectrum[i] /= totalWeight;
            }

            weightedRMS /= totalWeight;
            weightedLoudness /= totalWeight;
            weightedDynamicRange /= totalWeight;

            Console.WriteLine($"Averaged {analyses.Count} segments with total weight: {totalWeight:F2}");

            return new AudioSpectrum
            {
                FrequencyBands = weightedSpectrum,
                RMSLevel = weightedRMS,
                PeakLevel = weightedPeak,
                DynamicRange = weightedDynamicRange,
                Loudness = weightedLoudness
            };
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Calculates the Root Mean Square (RMS) level for audio data.
        /// RMS provides a measure of the average signal level over time.
        /// </summary>
        /// <param name="audioData">Audio data array to analyze</param>
        /// <returns>RMS level as a float value</returns>
        private float CalculateRMS(float[] audioData)
        {
            if (audioData.Length == 0) return 0f;

            double sum = audioData.Sum(sample => sample * sample);
            return (float)Math.Sqrt(sum / audioData.Length);
        }

        /// <summary>
        /// Prints detailed results from segmented analysis including configuration parameters.
        /// Displays analysis method details and calls the standard results printing method.
        /// </summary>
        /// <param name="source">Source audio spectrum analysis</param>
        /// <param name="target">Target audio spectrum analysis</param>
        /// <param name="eqAdjustments">Applied EQ adjustments in dB</param>
        private void PrintSegmentedAnalysisResults(AudioSpectrum source, AudioSpectrum target, float[] eqAdjustments)
        {
            Console.WriteLine("\n=== SEGMENTED ANALYSIS RESULTS ===");
            Console.WriteLine($"Analysis method: {_segmentConfig.SegmentLengthSeconds}s segments with {_segmentConfig.OverlapRatio * 100:F0}% overlap");
            Console.WriteLine($"Outlier threshold: {_segmentConfig.OutlierThreshold} standard deviations");
            Console.WriteLine($"Energy threshold: {_segmentConfig.MinSegmentEnergyThreshold:F1}dBFS");

            PrintAnalysisResults(source, target, eqAdjustments);
        }

        #endregion

        #region Audio Format Conversion

        /// <summary>
        /// Converts multi-channel audio data to mono by averaging channels.
        /// For stereo input, averages left and right channels to create mono output.
        /// </summary>
        /// <param name="audioData">Input audio data array</param>
        /// <param name="channels">Number of audio channels in the input</param>
        /// <returns>Mono audio data array</returns>
        private float[] ConvertToMono(float[] audioData, int channels)
        {
            if (channels == 1) return audioData;

            float[]? monoData = new float[audioData.Length / channels];
            for (int i = 0; i < monoData.Length; i++)
            {
                monoData[i] = (audioData[i * 2] + audioData[i * 2 + 1]) / 2.0f;
            }
            return monoData;
        }

        #endregion

        #region Frequency Spectrum Analysis

        /// <summary>
        /// Performs advanced frequency spectrum analysis using overlapped FFT with Flat-Top windowing.
        /// Provides accurate amplitude measurements across the configured frequency bands
        /// using optimized FFT parameters and windowing functions.
        /// </summary>
        /// <param name="audioData">Audio samples to analyze</param>
        /// <param name="sampleRate">Sample rate of the audio in Hz</param>
        /// <returns>Normalized frequency spectrum energy values for each band</returns>
        private float[] AnalyzeFrequencySpectrumAbsolute(float[] audioData, int sampleRate)
        {
            int fftSize = GetOptimalFFTSize(sampleRate);
            const float overlapRatio = 0.75f; // Smaller overlap for more accurate measurement

            int hopSize = (int)(fftSize * (1 - overlapRatio));
            float[] windowFunction = GenerateFlatTopWindow(fftSize); // More accurate amplitude measurement

            // Calculate windowing correction for normalization
            float windowSum = windowFunction.Sum();
            float windowNormFactor = windowSum / fftSize;

            float[] bandEnergies = new float[FrequencyBands.Length];
            int windowCount = Math.Max(1, (audioData.Length - fftSize) / hopSize + 1);

            for (int w = 0; w < windowCount; w++)
            {
                int startIdx = w * hopSize;
                if (startIdx + fftSize > audioData.Length) break;

                float[]? audioSegment = audioData.Skip(startIdx).Take(fftSize).ToArray();
                Complex[] fftInput = PrepareFFTInput(audioSegment, windowFunction, fftSize);

                Fourier.Forward(fftInput, FourierOptions.Matlab);

                for (int band = 0; band < FrequencyBands.Length; band++)
                {
                    float energy = CalculateBandEnergyAbsolute(fftInput, FrequencyBands[band],
                                                           sampleRate, fftSize, windowNormFactor);
                    bandEnergies[band] += energy;
                }
            }

            // Averaging - but NO normalization!
            for (int i = 0; i < bandEnergies.Length; i++)
            {
                bandEnergies[i] /= windowCount;
            }

            // RETURN ABSOLUTE VALUES - no normalization!
            return bandEnergies;
        }

        /// <summary>
        /// Determines the optimal FFT size based on the audio sample rate for best frequency resolution.
        /// Higher sample rates use larger FFT sizes to maintain good frequency resolution.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate in Hz</param>
        /// <returns>Optimal FFT size as power of 2</returns>
        private int GetOptimalFFTSize(int sampleRate)
        {
            if (sampleRate >= 96000) return 32768;
            if (sampleRate >= 48000) return 16384;
            return 8192;
        }

        /// <summary>
        /// Calculates weighted energy for a specific frequency band using advanced interpolation.
        /// Uses frequency weighting to provide accurate energy measurements for each frequency band
        /// with proper windowing correction.
        /// </summary>
        /// <param name="fftOutput">Complex FFT output data</param>
        /// <param name="centerFreq">Center frequency of the band in Hz</param>
        /// <param name="sampleRate">Audio sample rate in Hz</param>
        /// <param name="fftSize">Size of the FFT used</param>
        /// <param name="windowNormFactor">Window function normalization factor</param>
        /// <returns>Weighted RMS energy value for the frequency band</returns>
        private float CalculateBandEnergyAbsolute(Complex[] fftOutput, float centerFreq,
                                        int sampleRate, int fftSize, float windowNormFactor)
        {
            float bandwidth = GetBandwidth(centerFreq);
            float startFreq = Math.Max(0, centerFreq - bandwidth / 2);
            float endFreq = Math.Min(sampleRate / 2.0f, centerFreq + bandwidth / 2);

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

            // RMS calculation with windowing correction
            double weightedRMS = Math.Sqrt(energySum / weightSum);

            // Apply windowing correction - but maintain absolute level!
            weightedRMS /= (windowNormFactor * fftSize / 2.0);

            // Return ABSOLUTE RMS value (no further normalization)
            return (float)weightedRMS;
        }

        /// <summary>
        /// Calculates linear weighting for frequency bins within a band based on distance from center.
        /// Provides smooth frequency weighting that reduces edge effects in frequency band analysis.
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
        /// Uses a proportional relationship to center frequency for psychoacoustically appropriate bandwidths.
        /// </summary>
        /// <param name="centerFreq">Center frequency of the band in Hz</param>
        /// <returns>Bandwidth in Hz proportional to center frequency</returns>
        private float GetBandwidth(float centerFreq)
        {
            return centerFreq * 0.23f;
        }

        /// <summary>
        /// Analyzes absolute dynamic characteristics of audio data including RMS, peak, and loudness.
        /// Calculates absolute values without normalization to preserve true dynamic information.
        /// </summary>
        /// <param name="audioData">Audio data array to analyze</param>
        /// <returns>Dynamic analysis information with absolute values</returns>
        private DynamicsInfo AnalyzeAbsoluteDynamics(float[] audioData)
        {
            if (audioData.Length == 0)
                return new DynamicsInfo();

            // ABSOLUTE RMS calculation
            double sumSquares = audioData.Sum(sample => sample * sample);
            float absoluteRMS = (float)Math.Sqrt(sumSquares / audioData.Length);

            // ABSOLUTE peak level
            float absolutePeak = audioData.Max(sample => Math.Abs(sample));

            // ABSOLUTE loudness (with dBFS reference)
            float absoluteLoudness = 20 * (float)Math.Log10(Math.Max(absoluteRMS, 1e-10f));

            // Dynamic range (peak-to-RMS ratio)
            float dynamicRange = 20 * (float)Math.Log10(absolutePeak / Math.Max(absoluteRMS, 1e-10f));

            //Console.WriteLine($"Absolute dynamics - RMS: {absoluteRMS:F6}, Peak: {absolutePeak:F6}");
            //Console.WriteLine($"Absolute levels - RMS: {absoluteLoudness:F1}dBFS, Peak: {20 * Math.Log10(absolutePeak):F1}dBFS");

            return new DynamicsInfo
            {
                RMS = absoluteRMS,           // Absolute RMS value
                Peak = absolutePeak,         // Absolute peak value
                Loudness = absoluteLoudness, // dBFS loudness
                DynamicRange = dynamicRange  // Peak-to-RMS ratio
            };
        }

        #endregion

        #region Window Functions

        /// <summary>
        /// Generates a Flat-Top window function for FFT windowing with excellent amplitude accuracy.
        /// Flat-Top windows provide superior amplitude measurement accuracy compared to other window types
        /// at the cost of slightly reduced frequency resolution.
        /// </summary>
        /// <param name="size">Size of the window in samples</param>
        /// <returns>Array of window coefficients</returns>
        private float[] GenerateFlatTopWindow(int size)
        {
            float[] window = new float[size];
            const double a0 = 0.21557895;
            const double a1 = 0.41663158;
            const double a2 = 0.277263158;
            const double a3 = 0.083578947;
            const double a4 = 0.006947368;

            for (int i = 0; i < size; i++)
            {
                double n = (double)i / (size - 1);
                window[i] = (float)(a0 - a1 * Math.Cos(2 * Math.PI * n) +
                                   a2 * Math.Cos(4 * Math.PI * n) -
                                   a3 * Math.Cos(6 * Math.PI * n) +
                                   a4 * Math.Cos(8 * Math.PI * n));
            }
            return window;
        }

        /// <summary>
        /// Prepares audio data for FFT analysis by applying windowing and zero-padding.
        /// Applies the window function to reduce spectral leakage and pads with zeros
        /// if the audio segment is shorter than the FFT size.
        /// </summary>
        /// <param name="audioSegment">Audio data segment to process</param>
        /// <param name="window">Window function coefficients</param>
        /// <param name="fftSize">Target FFT size for zero-padding</param>
        /// <returns>Complex array ready for FFT processing</returns>
        private Complex[] PrepareFFTInput(float[] audioSegment, float[] window, int fftSize)
        {
            Complex[]? fftInput = new Complex[fftSize];
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

        #region Results and Diagnostics

        /// <summary>
        /// Prints comprehensive analysis results and safety information to the console.
        /// Displays detailed spectrum analysis results, EQ adjustments, and safety warnings
        /// to help users understand the processing that was applied.
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
            string[] bandNames = new[] {
                "20Hz", "25Hz", "31Hz", "40Hz", "50Hz", "63Hz", "80Hz", "100Hz", "125Hz", "160Hz",
                "200Hz", "250Hz", "315Hz", "400Hz", "500Hz", "630Hz", "800Hz", "1kHz", "1.25kHz", "1.6kHz",
                "2kHz", "2.5kHz", "3.15kHz", "4kHz", "5kHz", "6.3kHz", "8kHz", "10kHz", "12.5kHz", "16kHz"
            };
            for (int i = 0; i < eqAdjustments.Length; i++)
            {
                string warning = Math.Abs(eqAdjustments[i]) > 8.0f ? " ⚠️" : "";
                // Console.WriteLine($"{bandNames[i]}: {eqAdjustments[i]:+0.0;-0.0} dB{warning}");
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
