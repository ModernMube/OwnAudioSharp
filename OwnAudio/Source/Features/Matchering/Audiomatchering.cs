using OwnaudioNET.Dsp;
using OwnaudioNET.Sources;
using Logger;
using System.Numerics;

namespace OwnaudioNET.Features.Matchering
{
    /// <summary>
    /// FFT spectrum analysis and EQ matching between two tracks.
    /// </summary>
    public partial class AudioAnalyzer
    {
        #region Constants and Fields

        /// <summary>
        /// ISO 1/3 octave centers, 20Hz..16k. The whole chain is built around these 30 bands.
        /// </summary>
        private static readonly float[] _freqBands = {
            20f, 25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f,
            200f, 250f, 315f, 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f,
            2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f, 16000f
        };

        /// <summary>
        /// Pretty labels for the bands above, log only.
        /// </summary>
        private static readonly string[] _bandNames = {
            "20Hz", "25Hz", "31Hz", "40Hz", "50Hz", "63Hz", "80Hz", "100Hz", "125Hz", "160Hz",
            "200Hz", "250Hz", "315Hz", "400Hz", "500Hz", "630Hz", "800Hz", "1kHz", "1.25kHz", "1.6kHz",
            "2kHz", "2.5kHz", "3.15kHz", "4kHz", "5kHz", "6.3kHz", "8kHz", "10kHz", "12.5kHz", "16kHz"
        };

        private static readonly object _analyzerLock = new object();

        /// <summary>
        /// Segment length / overlap / outlier knobs.
        /// </summary>
        private readonly SegmentedAnalysisConfig _segmentConfig = new SegmentedAnalysisConfig();

        #endregion

        #region Public API Methods

        /// <summary>
        /// Chops the file into overlapping segments, analyzes each one, then folds them
        /// into a single weighted spectrum. Locked because FileSource init doesn't like
        /// being spun up concurrently.
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns>Weighted average spectrum of the whole file.</returns>
        public AudioSpectrum AnalyzeAudioFile(string filePath)
        {
            lock (_analyzerLock)
            {
                System.Threading.Thread.Sleep(50);

                using FileSource source = new FileSource(filePath);

                if (source.Duration == 0)
                    throw new InvalidOperationException($"Cannot load audio file: {filePath}");

                float[] audioData = source.GetFloatAudioData(TimeSpan.Zero);
                int channels = source.StreamInfo.Channels;
                int sampleRate = source.StreamInfo.SampleRate;

                Log.Info($"Analyzing {filePath} ({channels} ch, {sampleRate}Hz)...");

                if (channels == 1) return _analyzeMono(audioData, sampleRate);

                int perChannel = audioData.Length / channels;
                var channelSpectra = new List<AudioSpectrum>(channels);
                float[] scratch = new float[perChannel];

                for (int c = 0; c < channels; c++)
                {
                    for (int i = 0; i < perChannel; i++)
                        scratch[i] = audioData[i * channels + c];

                    Log.Info($"Analyzing Channel {c + 1}...");
                    channelSpectra.Add(_analyzeMono(scratch, sampleRate));
                }

                return _averageSpectra(channelSpectra);
            }
        }

        /// <summary>
        /// Runs the segment pipeline on one channel worth of samples.
        /// </summary>
        private AudioSpectrum _analyzeMono(float[] monoData, int sampleRate)
        {
            Log.Info($"Starting segmented analysis (Length: {monoData.Length / (float)sampleRate:F1}s)");

            var segments = _createSegments(monoData, sampleRate);
            var analyses = _filterOutliers(_analyzeSegments(segments, sampleRate));
            var spectrum = _weightedAverage(analyses);

            Log.Info($"\n=== ANALYZED SPECTRUM INFO ===");
            Log.Info($"RMS: {spectrum.RMSLevel:F6}, Peak: {spectrum.PeakLevel:F6}");
            Log.Info($"Loudness: {spectrum.Loudness:F1} dBFS, Dynamic Range: {spectrum.DynamicRange:F1} dB");

            return spectrum;
        }

        /// <summary>
        /// Folds per-channel spectra into one. RMS is combined in the power domain,
        /// peak is just the loudest of the bunch.
        /// </summary>
        private AudioSpectrum _averageSpectra(List<AudioSpectrum> spectra)
        {
            if (spectra == null || spectra.Count == 0) return new AudioSpectrum();
            if (spectra.Count == 1) return spectra[0];

            int bands = spectra[0].FrequencyBands.Length;
            float[] avg = new float[bands];

            double sumRmsSquares = 0;
            double maxPeak = 0;

            foreach (var s in spectra)
            {
                for (int i = 0; i < bands; i++) avg[i] += s.FrequencyBands[i];

                sumRmsSquares += s.RMSLevel * s.RMSLevel;
                maxPeak = Math.Max(maxPeak, s.PeakLevel);
            }

            for (int i = 0; i < bands; i++) avg[i] /= spectra.Count;

            float rms = (float)Math.Sqrt(sumRmsSquares / spectra.Count);
            float peak = (float)maxPeak;

            return new AudioSpectrum
            {
                FrequencyBands = avg,
                RMSLevel = rms,
                PeakLevel = peak,
                Loudness = 20 * (float)Math.Log10(Math.Max(rms, 1e-10f)),
                DynamicRange = 20 * (float)Math.Log10(peak / Math.Max(rms, 1e-10f))
            };
        }

        /// <summary>
        /// Analyze both files, work out EQ / dynamics deltas, render the source through the chain.
        /// </summary>
        /// <param name="sourceFile">Track we want to change.</param>
        /// <param name="targetFile">Reference we're matching to.</param>
        /// <param name="outputFile"></param>
        public void ProcessEQMatching(string sourceFile, string targetFile, string outputFile)
        {
            Log.Info("=== SEGMENTED EQ MATCHING ===");

            Log.Info("Analyzing source audio (segmented)...");
            AudioSpectrum sourceSpectrum = AnalyzeAudioFile(sourceFile);

            Log.Info("Analyzing target audio (segmented)...");
            AudioSpectrum targetSpectrum = AnalyzeAudioFile(targetFile);

            float[] eqAdjustments = _calcEqAdjustments(sourceSpectrum, targetSpectrum);
            DynamicAmpSettings ampSettings = _ampSettings(sourceSpectrum, targetSpectrum);
            var compSettings = _compSettings(sourceSpectrum, targetSpectrum);

            Log.Info("Processing audio with segmented-based EQ...");
            _applyEqProcessing(sourceFile, outputFile, eqAdjustments, ampSettings, compSettings, sourceSpectrum, targetSpectrum);
        }

        #endregion

        #region Segment Creation and Management

        /// <summary>
        /// Slices the buffer into overlapping chunks and tags each with its energy.
        /// </summary>
        private List<AudioSegment> _createSegments(float[] audioData, int sampleRate)
        {
            var segments = new List<AudioSegment>();

            int segmentSamples = (int)(_segmentConfig.SegmentLengthSeconds * sampleRate);
            int hopSize = (int)(segmentSamples * (1 - _segmentConfig.OverlapRatio));

            if (audioData.Length <= segmentSamples)
                throw new InvalidOperationException($"The audio is too short. Less than 10 seconds!: {audioData.Length / sampleRate} second");

            Log.Info($"Segment size: {segmentSamples} samples ({_segmentConfig.SegmentLengthSeconds}s)");
            Log.Info($"Hop size: {hopSize} samples (overlap: {_segmentConfig.OverlapRatio * 100:F1}%)");

            for (int start = 0; start < audioData.Length - segmentSamples; start += hopSize)
            {
                int len = Math.Min(segmentSamples, audioData.Length - start);
                float[] data = new float[len];
                Array.Copy(audioData, start, data, 0, len);

                float rms = _calcRms(data);

                segments.Add(new AudioSegment
                {
                    Data = data,
                    StartTime = (float)start / sampleRate,
                    Duration = (float)len / sampleRate,
                    EnergyLevel = 20 * (float)Math.Log10(Math.Max(rms, 1e-10f)),
                    SampleRate = sampleRate
                });
            }

            Log.Info($"Created {segments.Count} segments");
            return segments;
        }

        /// <summary>
        /// Spectrum + dynamics for every segment that clears the energy gate.
        /// </summary>
        private List<SegmentAnalysis> _analyzeSegments(List<AudioSegment> segments, int sampleRate)
        {
            var analyses = new List<SegmentAnalysis>(segments.Count);

            for (int i = 0; i < segments.Count; i++)
            {
                AudioSegment segment = segments[i];
                if (segment.EnergyLevel < _segmentConfig.MinSegmentEnergyThreshold) continue;

                DynamicsInfo dynamics = _analyzeDynamics(segment.Data);

                analyses.Add(new SegmentAnalysis
                {
                    SegmentIndex = i,
                    StartTime = segment.StartTime,
                    Duration = segment.Duration,
                    EnergyLevel = segment.EnergyLevel,
                    FrequencySpectrum = _analyzeSpectrum(segment.Data, sampleRate),
                    Dynamics = dynamics,
                    Weight = _segmentWeight(segment, dynamics)
                });
            }

            Log.Info($"Completed analysis: {analyses.Count} valid segments from {segments.Count} total");
            return analyses;
        }

        /// <summary>
        /// How much a segment should count in the final average. Loud, sanely dynamic
        /// and mid-of-the-track segments get a small bump.
        /// </summary>
        private float _segmentWeight(AudioSegment segment, DynamicsInfo dynamics)
        {
            float energyWeight = 1.0f;
            if (segment.EnergyLevel > -20.0f) energyWeight = 1.2f;
            else if (segment.EnergyLevel < -40.0f) energyWeight = 0.7f;

            float dynamicWeight = Math.Max(0.5f, 1.0f - (Math.Abs(dynamics.DynamicRange - 15.0f) / 20.0f));

            float pos = segment.StartTime / (segment.StartTime + segment.Duration);
            float positionWeight = (pos > 0.2f && pos < 0.8f) ? 1.1f : 1.0f;

            return energyWeight * dynamicWeight * positionWeight;
        }

        #endregion

        #region Outlier Detection and Filtering

        /// <summary>
        /// Drops segments that sit way off the mean in too many bands. Anything that
        /// deviates in more than ~30% of the bands is thrown out.
        /// </summary>
        private List<SegmentAnalysis> _filterOutliers(List<SegmentAnalysis> analyses)
        {
            if (analyses.Count < 3) return analyses;

            for (int band = 0; band < _freqBands.Length; band++)
            {
                float sum = 0f;
                foreach (var a in analyses) sum += a.FrequencySpectrum[band];
                float mean = sum / analyses.Count;

                float sq = 0f;
                foreach (var a in analyses)
                {
                    float d = a.FrequencySpectrum[band] - mean;
                    sq += d * d;
                }

                float sd = Math.Max((float)Math.Sqrt(sq / analyses.Count), 1e-10f);

                foreach (var a in analyses)
                {
                    if (Math.Abs(a.FrequencySpectrum[band] - mean) / sd > _segmentConfig.OutlierThreshold)
                        a.OutlierScore += 1.0f;
                }
            }

            float maxScore = _freqBands.Length * 0.3f;
            var filtered = new List<SegmentAnalysis>(analyses.Count);

            foreach (var a in analyses)
                if (a.OutlierScore <= maxScore) filtered.Add(a);

            Log.Info($"Filtered {analyses.Count - filtered.Count} outlier segments, kept {filtered.Count} segments");
            return filtered;
        }

        #endregion

        #region Weighted Average Calculation

        /// <summary>
        /// Weighted fold of the surviving segments into one spectrum.
        /// </summary>
        private AudioSpectrum _weightedAverage(List<SegmentAnalysis> analyses)
        {
            if (analyses.Count == 0)
                throw new InvalidOperationException("No valid segments for analysis");

            float[] spectrum = new float[_freqBands.Length];
            float totalWeight = 0f, rms = 0f, peak = 0f, loudness = 0f, dr = 0f;

            foreach (var a in analyses)
            {
                float w = a.Weight;
                totalWeight += w;

                for (int i = 0; i < _freqBands.Length; i++)
                    spectrum[i] += a.FrequencySpectrum[i] * w;

                rms += a.Dynamics.RMS * w;
                peak = Math.Max(peak, a.Dynamics.Peak);
                loudness += a.Dynamics.Loudness * w;
                dr += a.Dynamics.DynamicRange * w;
            }

            for (int i = 0; i < _freqBands.Length; i++) spectrum[i] /= totalWeight;

            Log.Info($"Averaged {analyses.Count} segments with total weight: {totalWeight:F2}");

            return new AudioSpectrum
            {
                FrequencyBands = spectrum,
                RMSLevel = rms / totalWeight,
                PeakLevel = peak,
                DynamicRange = dr / totalWeight,
                Loudness = loudness / totalWeight
            };
        }

        #endregion

        #region Frequency Spectrum Analisys

        /// <summary>
        /// Plain RMS over a buffer.
        /// </summary>
        private float _calcRms(ReadOnlySpan<float> audioData)
        {
            if (audioData.Length == 0) return 0f;

            double sum = 0;
            for (int i = 0; i < audioData.Length; i++) sum += audioData[i] * audioData[i];

            return (float)Math.Sqrt(sum / audioData.Length);
        }

        /// <summary>
        /// Overlapped FFT with a Flat-Top window - we trade frequency resolution for
        /// amplitude accuracy, which is what matters when matching levels.
        /// </summary>
        private float[] _analyzeSpectrum(float[] audioData, int sampleRate)
        {
            int fftSize = _optimalFftSize(sampleRate);
            int hopSize = fftSize / 4;
            float[] window = _flatTopWindow(fftSize);

            float windowSum = 0f;
            for (int i = 0; i < fftSize; i++) windowSum += window[i];
            float windowNorm = windowSum / fftSize;

            float[] energies = new float[_freqBands.Length];
            Complex[] fft = new Complex[fftSize];
            int windowCount = Math.Max(1, (audioData.Length - fftSize) / hopSize + 1);

            for (int w = 0; w < windowCount; w++)
            {
                int start = w * hopSize;
                if (start + fftSize > audioData.Length) break;

                for (int i = 0; i < fftSize; i++)
                    fft[i] = audioData[start + i] * window[i];

                OwnAudioFft.Forward(fft);

                for (int band = 0; band < _freqBands.Length; band++)
                    energies[band] += _bandEnergy(fft, _freqBands[band], sampleRate, fftSize, windowNorm);
            }

            for (int i = 0; i < energies.Length; i++) energies[i] /= windowCount;

            return energies;
        }

        /// <summary>
        /// Bigger FFT at higher rates so the bin spacing stays usable.
        /// </summary>
        private int _optimalFftSize(int sampleRate)
        {
            if (sampleRate >= 96000) return 32768;
            if (sampleRate >= 48000) return 16384;
            return 8192;
        }

        /// <summary>
        /// Weighted RMS of the bins falling inside one band, corrected for the window gain.
        /// </summary>
        private float _bandEnergy(Complex[] fft, float centerFreq, int sampleRate, int fftSize, float windowNorm)
        {
            float bandwidth = centerFreq * 0.23f;
            float startFreq = Math.Max(0, centerFreq - bandwidth / 2);
            float endFreq = Math.Min(sampleRate / 2.0f, centerFreq + bandwidth / 2);

            int startBin = Math.Max(0, (int)Math.Floor(startFreq * fftSize / (double)sampleRate));
            int endBin = Math.Min(fftSize / 2, (int)Math.Ceiling(endFreq * fftSize / (double)sampleRate));

            if (startBin >= endBin) return 0;

            double energySum = 0, weightSum = 0;

            for (int bin = startBin; bin <= endBin; bin++)
            {
                double binFreq = bin * (double)sampleRate / fftSize;
                if (binFreq < startFreq || binFreq > endFreq) continue;

                double weight = 1.0 - (Math.Abs(binFreq - centerFreq) / (bandwidth / 2.0));
                if (weight <= 0) continue;

                double mag = fft[bin].Magnitude;
                energySum += mag * mag * weight;
                weightSum += weight;
            }

            if (weightSum == 0) return 0;

            return (float)(Math.Sqrt(energySum / weightSum) / (windowNorm * fftSize / 2.0));
        }

        /// <summary>
        /// Absolute RMS / peak / loudness / crest of a buffer, no normalization.
        /// </summary>
        private DynamicsInfo _analyzeDynamics(float[] audioData)
        {
            if (audioData.Length == 0) return new DynamicsInfo();

            double sumSquares = 0;
            float peak = 0f;

            for (int i = 0; i < audioData.Length; i++)
            {
                float s = audioData[i];
                sumSquares += s * s;
                float abs = Math.Abs(s);
                if (abs > peak) peak = abs;
            }

            float rms = (float)Math.Sqrt(sumSquares / audioData.Length);

            return new DynamicsInfo
            {
                RMS = rms,
                Peak = peak,
                Loudness = 20 * (float)Math.Log10(Math.Max(rms, 1e-10f)),
                DynamicRange = 20 * (float)Math.Log10(peak / Math.Max(rms, 1e-10f))
            };
        }

        /// <summary>
        /// Flat-Top window coefficients.
        /// </summary>
        private float[] _flatTopWindow(int size)
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

        #endregion
    }
}
