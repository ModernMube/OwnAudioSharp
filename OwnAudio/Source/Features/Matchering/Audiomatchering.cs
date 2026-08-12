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

        /// <summary>
        /// Half a third of an octave, the geometric edge factor of an ISO band.
        /// </summary>
        private static readonly float _bandEdge = MathF.Pow(2.0f, 1.0f / 6.0f);

        /// <summary>
        /// Analysis window length in seconds. The FFT size follows from this so the
        /// bin spacing lands within a few percent of each other at 44.1k and 48k -
        /// picking the size straight from the sample rate used to give the two files
        /// a 2x resolution difference and bias every delta.
        /// </summary>
        private const float AnalysisWindowSeconds = 0.35f;

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

                return AnalyzeAudioBuffer(audioData, sampleRate, channels);
            }
        }

        /// <summary>
        /// Runs the segment pipeline on one channel worth of samples.
        /// </summary>
        private AudioSpectrum _analyzeMono(float[] monoData, int sampleRate)
        {
            Log.Info($"Starting segmented analysis (Length: {monoData.Length / (float)sampleRate:F1}s)");

            var context = _spectrumContext(sampleRate);
            var segments = _createSegments(monoData, sampleRate);
            var analyses = _filterOutliers(_analyzeSegments(segments, context));
            var spectrum = _weightedAverage(analyses);

            Log.Info($"\n=== ANALYZED SPECTRUM INFO ===");
            Log.Info($"RMS: {spectrum.RMSLevel:F6}, Peak: {spectrum.PeakLevel:F6}");
            Log.Info($"Loudness: {spectrum.Loudness:F1} dBFS, Dynamic Range: {spectrum.DynamicRange:F1} dB");

            return spectrum;
        }

        /// <summary>
        /// Folds per-channel spectra into one. Bands and RMS combine in the power
        /// domain, peak is just the loudest of the bunch.
        /// </summary>
        private AudioSpectrum _averageSpectra(List<AudioSpectrum> spectra)
        {
            if (spectra == null || spectra.Count == 0) return new AudioSpectrum();
            if (spectra.Count == 1) return spectra[0];

            int bands = spectra[0].FrequencyBands.Length;
            double[] power = new double[bands];

            double sumRmsSquares = 0;
            double maxPeak = 0;

            foreach (var s in spectra)
            {
                for (int i = 0; i < bands; i++) power[i] += s.FrequencyBands[i] * s.FrequencyBands[i];

                sumRmsSquares += s.RMSLevel * s.RMSLevel;
                maxPeak = Math.Max(maxPeak, s.PeakLevel);
            }

            float[] avg = new float[bands];
            for (int i = 0; i < bands; i++) avg[i] = (float)Math.Sqrt(power[i] / spectra.Count);

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
        /// Slices the buffer into overlapping views and tags each with its energy.
        /// The segments share the source array, nothing is copied.
        /// </summary>
        private List<AudioSegment> _createSegments(float[] audioData, int sampleRate)
        {
            var segments = new List<AudioSegment>();

            int segmentSamples = (int)(_segmentConfig.SegmentLengthSeconds * sampleRate);
            int hopSize = (int)(segmentSamples * (1 - _segmentConfig.OverlapRatio));

            if (audioData.Length <= segmentSamples)
                throw new InvalidOperationException($"The audio is too short. Less than {_segmentConfig.SegmentLengthSeconds:F0} seconds!: {audioData.Length / sampleRate} second");

            Log.Info($"Segment size: {segmentSamples} samples ({_segmentConfig.SegmentLengthSeconds}s)");
            Log.Info($"Hop size: {hopSize} samples (overlap: {_segmentConfig.OverlapRatio * 100:F1}%)");

            float totalDuration = audioData.Length / (float)sampleRate;

            for (int start = 0; start < audioData.Length - segmentSamples; start += hopSize)
            {
                int len = Math.Min(segmentSamples, audioData.Length - start);
                float rms = _calcRms(audioData.AsSpan(start, len));

                segments.Add(new AudioSegment
                {
                    Data = audioData,
                    Offset = start,
                    Length = len,
                    StartTime = (float)start / sampleRate,
                    Duration = (float)len / sampleRate,
                    TotalDuration = totalDuration,
                    EnergyLevel = 20 * (float)Math.Log10(Math.Max(rms, 1e-10f)),
                    SampleRate = sampleRate
                });
            }

            Log.Info($"Created {segments.Count} segments");
            return segments;
        }

        /// <summary>
        /// Spectrum + dynamics for every segment that clears the energy gate. Runs
        /// across cores - segments are independent and this is an offline path.
        /// </summary>
        private List<SegmentAnalysis> _analyzeSegments(List<AudioSegment> segments, SpectrumContext context)
        {
            var results = new SegmentAnalysis?[segments.Count];

            Parallel.For(0, segments.Count,
                () => new Complex[context.FftSize],
                (i, _, fftScratch) =>
                {
                    AudioSegment segment = segments[i];
                    if (segment.EnergyLevel < _segmentConfig.MinSegmentEnergyThreshold) return fftScratch;

                    DynamicsInfo dynamics = _analyzeDynamics(segment.Samples);

                    results[i] = new SegmentAnalysis
                    {
                        SegmentIndex = i,
                        StartTime = segment.StartTime,
                        Duration = segment.Duration,
                        EnergyLevel = segment.EnergyLevel,
                        FrequencySpectrum = _analyzeSpectrum(segment.Samples, context, fftScratch),
                        Dynamics = dynamics,
                        Weight = _segmentWeight(segment, dynamics)
                    };

                    return fftScratch;
                },
                _ => { });

            var analyses = new List<SegmentAnalysis>(segments.Count);
            foreach (var r in results)
                if (r is not null) analyses.Add(r);

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

            float pos = segment.TotalDuration > 0 ? segment.StartTime / segment.TotalDuration : 0.5f;
            float positionWeight = (pos > 0.2f && pos < 0.8f) ? 1.1f : 1.0f;

            return energyWeight * dynamicWeight * positionWeight;
        }

        #endregion

        #region Outlier Detection and Filtering

        /// <summary>
        /// Drops segments that sit way off the mean in too many bands. Statistics run
        /// in dB, otherwise the loud bass bands own the standard deviation and the top
        /// end can never register as an outlier at all.
        /// </summary>
        private List<SegmentAnalysis> _filterOutliers(List<SegmentAnalysis> analyses)
        {
            if (analyses.Count < 3) return analyses;

            for (int band = 0; band < _freqBands.Length; band++)
            {
                float sum = 0f;
                foreach (var a in analyses) sum += _toDb(a.FrequencySpectrum[band]);
                float mean = sum / analyses.Count;

                float sq = 0f;
                foreach (var a in analyses)
                {
                    float d = _toDb(a.FrequencySpectrum[band]) - mean;
                    sq += d * d;
                }

                float sd = Math.Max((float)Math.Sqrt(sq / analyses.Count), 1e-6f);

                foreach (var a in analyses)
                {
                    if (Math.Abs(_toDb(a.FrequencySpectrum[band]) - mean) / sd > _segmentConfig.OutlierThreshold)
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
        /// Weighted fold of the surviving segments into one spectrum. Bands average in
        /// the power domain so a loud segment doesn't get diluted by a quiet one.
        /// </summary>
        private AudioSpectrum _weightedAverage(List<SegmentAnalysis> analyses)
        {
            if (analyses.Count == 0)
                throw new InvalidOperationException("No valid segments for analysis");

            double[] power = new double[_freqBands.Length];
            float totalWeight = 0f, rms = 0f, peak = 0f, loudness = 0f, dr = 0f;

            foreach (var a in analyses)
            {
                float w = a.Weight;
                totalWeight += w;

                for (int i = 0; i < _freqBands.Length; i++)
                    power[i] += (double)a.FrequencySpectrum[i] * a.FrequencySpectrum[i] * w;

                rms += a.Dynamics.RMS * w;
                peak = Math.Max(peak, a.Dynamics.Peak);
                loudness += a.Dynamics.Loudness * w;
                dr += a.Dynamics.DynamicRange * w;
            }

            float[] spectrum = new float[_freqBands.Length];
            for (int i = 0; i < _freqBands.Length; i++)
                spectrum[i] = (float)Math.Sqrt(power[i] / totalWeight);

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
        /// Everything about an analysis that only depends on the sample rate: window,
        /// its power sum and the bin range of every band. Built once per rate.
        /// </summary>
        private sealed class SpectrumContext
        {
            public int SampleRate;
            public int FftSize;
            public int HopSize;
            public float[] Window = Array.Empty<float>();

            /// <summary>
            /// Sum of the squared window, the noise power the normalization needs.
            /// </summary>
            public double WindowPowerSum;

            public int[] BandStart = Array.Empty<int>();
            public int[] BandEnd = Array.Empty<int>();
        }

        private SpectrumContext? _cachedContext;

        /// <summary>
        /// Cached per rate, since both files usually share one.
        /// </summary>
        private SpectrumContext _spectrumContext(int sampleRate)
        {
            SpectrumContext? cached = _cachedContext;
            if (cached is not null && cached.SampleRate == sampleRate) return cached;

            int fftSize = _analysisFftSize(sampleRate);
            float[] window = _hannWindow(fftSize);

            double powerSum = 0;
            for (int i = 0; i < fftSize; i++) powerSum += (double)window[i] * window[i];

            var ctx = new SpectrumContext
            {
                SampleRate = sampleRate,
                FftSize = fftSize,
                HopSize = fftSize / 4,
                Window = window,
                WindowPowerSum = powerSum,
                BandStart = new int[_freqBands.Length],
                BandEnd = new int[_freqBands.Length]
            };

            _bandBins(ctx);
            _cachedContext = ctx;

            Log.Info($"Analysis FFT: {fftSize} bins at {sampleRate}Hz ({fftSize / (float)sampleRate * 1000f:F0}ms, {sampleRate / (float)fftSize:F2}Hz resolution)");
            return ctx;
        }

        /// <summary>
        /// Power of two closest to the fixed analysis window, so 44.1k and 48k end up
        /// with nearly the same resolution instead of a factor of two apart.
        /// </summary>
        private int _analysisFftSize(int sampleRate)
        {
            float wanted = sampleRate * AnalysisWindowSeconds;

            int size = 1024;
            while (size * 2 <= 65536 && Math.Abs(size * 2 - wanted) < Math.Abs(size - wanted))
                size *= 2;

            return size;
        }

        /// <summary>
        /// Geometric 1/3 octave edges per band, widened where the FFT can't resolve
        /// them - down at 20Hz the natural band is barely one bin wide, and a single
        /// bin reading is noise, not a measurement.
        /// </summary>
        private void _bandBins(SpectrumContext ctx)
        {
            float binWidth = ctx.SampleRate / (float)ctx.FftSize;
            float minHalf = 1.5f * binWidth;
            int maxBin = ctx.FftSize / 2 - 1;

            for (int i = 0; i < _freqBands.Length; i++)
            {
                float fc = _freqBands[i];
                float lo = Math.Min(fc / _bandEdge, fc - minHalf);
                float hi = Math.Max(fc * _bandEdge, fc + minHalf);

                int start = Math.Max(1, (int)Math.Ceiling(lo / binWidth));
                int end = Math.Min(maxBin, (int)Math.Floor(hi / binWidth));
                if (end < start) end = Math.Min(start, maxBin);

                ctx.BandStart[i] = start;
                ctx.BandEnd[i] = end;
            }
        }

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
        /// Overlapped FFT into per band RMS. Hann rather than Flat-Top: Flat-Top is for
        /// reading the amplitude of an isolated sine, and its ~9 bin main lobe smears
        /// broadband program material straight across neighbouring bands.
        /// </summary>
        private float[] _analyzeSpectrum(ReadOnlySpan<float> audioData, SpectrumContext ctx, Complex[] fft)
        {
            int fftSize = ctx.FftSize;
            int hopSize = ctx.HopSize;
            float[] window = ctx.Window;

            double[] power = new double[_freqBands.Length];
            int windows = 0;

            for (int start = 0; start + fftSize <= audioData.Length; start += hopSize)
            {
                for (int i = 0; i < fftSize; i++)
                    fft[i] = audioData[start + i] * window[i];

                OwnAudioFft.Forward(fft);

                for (int band = 0; band < _freqBands.Length; band++)
                    power[band] += _bandPower(fft, ctx, band);

                windows++;
            }

            float[] rms = new float[_freqBands.Length];
            if (windows == 0) return rms;

            for (int i = 0; i < rms.Length; i++)
                rms[i] = (float)Math.Sqrt(power[i] / windows);

            return rms;
        }

        /// <summary>
        /// Power inside one band. Single sided, normalized by the window's noise power
        /// so the reading is independent of the window and the FFT size.
        /// </summary>
        private double _bandPower(Complex[] fft, SpectrumContext ctx, int band)
        {
            int start = ctx.BandStart[band];
            int end = ctx.BandEnd[band];

            double sum = 0;
            for (int bin = start; bin <= end; bin++)
            {
                double re = fft[bin].Real, im = fft[bin].Imaginary;
                sum += re * re + im * im;
            }

            return 2.0 * sum / (ctx.FftSize * ctx.WindowPowerSum);
        }

        /// <summary>
        /// Absolute RMS / peak / loudness / crest of a buffer, no normalization.
        /// </summary>
        private DynamicsInfo _analyzeDynamics(ReadOnlySpan<float> audioData)
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
        /// Periodic Hann, the usual choice for spectral estimation.
        /// </summary>
        private float[] _hannWindow(int size)
        {
            float[] window = new float[size];
            for (int i = 0; i < size; i++)
                window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size));

            return window;
        }

        private static float _toDb(float linear) => 20f * MathF.Log10(Math.Max(linear, 1e-10f));

        #endregion
    }
}
