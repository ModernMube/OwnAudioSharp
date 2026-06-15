namespace SoundTouch;

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using OwnaudioNET.Dsp;

/// <summary>
/// BPM detector using spectral flux onset detection and autocorrelation-based tempo estimation.
/// Replaces the legacy SoundTouch envelope-based approach with a more accurate algorithm.
/// </summary>
public sealed class BpmDetect : IDisposable
{
    #region Fields

    /// <summary>
    /// FFT window size in samples.
    /// </summary>
    private const int FftSize = 512;

    /// <summary>
    /// Hop size in decimated samples between successive spectral frames.
    /// </summary>
    private const int HopSize = 128;

    /// <summary>
    /// Target sample rate to which audio is decimated before analysis.
    /// </summary>
    private const int TargetSampleRate = 11025;

    /// <summary>
    /// Minimum BPM value considered during peak search.
    /// </summary>
    private const float MinBpm = 45f;

    /// <summary>
    /// Maximum BPM value considered during peak search.
    /// </summary>
    private const float MaxBpm = 190f;

    /// <summary>
    /// Centre of the perceptual tempo prior in BPM. The normalised autocorrelation
    /// is weighted by a log-Gaussian peaking at this tempo to resolve octave
    /// (half/double tempo) ambiguities towards musically typical values.
    /// </summary>
    private const float PreferredBpm = 120f;

    /// <summary>
    /// Standard deviation of the perceptual tempo prior, expressed in octaves.
    /// Larger values flatten the prior; smaller values bias more strongly towards
    /// <see cref="PreferredBpm"/>.
    /// </summary>
    private const float TempoPriorSigma = 0.9f;

    /// <summary>
    /// Length of onset history in seconds.
    /// </summary>
    private const float HistorySeconds = 8.0f;

    /// <summary>
    /// FFT complex buffer, re-used every hop.
    /// </summary>
    private readonly System.Numerics.Complex[] _fftBuffer;

    /// <summary>
    /// Magnitude spectrum from the previous hop for spectral flux computation.
    /// </summary>
    private readonly float[] _prevMagnitudes;

    /// <summary>
    /// Pre-computed Hamming window coefficients, length FftSize.
    /// </summary>
    private readonly float[] _window;

    /// <summary>
    /// Circular buffer holding the most recent FftSize decimated mono samples.
    /// </summary>
    private readonly float[] _slideBuffer;

    /// <summary>
    /// Circular onset history buffer storing spectral flux values.
    /// </summary>
    private readonly float[] _onsetHistory;

    /// <summary>
    /// Autocorrelation result array; index equals lag in hops.
    /// </summary>
    private readonly float[] _xcorrResult;

    /// <summary>
    /// Number of input channels.
    /// </summary>
    private readonly int _channels;

    /// <summary>
    /// Number of onset history slots.
    /// </summary>
    private readonly int _historySize;

    /// <summary>
    /// Decimation factor: input samples per decimated sample.
    /// </summary>
    private readonly int _decimateBy;

    /// <summary>
    /// Effective sample rate after decimation, in Hz. Equals
    /// <c>sampleRate / _decimateBy</c> and may differ from
    /// <see cref="TargetSampleRate"/> when the input rate is not an integer
    /// multiple of it (e.g. 48000 Hz decimates to 12000 Hz, not 11025 Hz).
    /// </summary>
    private readonly float _effectiveSampleRate;

    /// <summary>
    /// Number of spectral hops per second at the effective sample rate.
    /// Used to convert autocorrelation lag (in hops) to BPM.
    /// </summary>
    private readonly float _hopRate;

    /// <summary>
    /// Write cursor in the circular slide buffer.
    /// </summary>
    private int _slidePos;

    /// <summary>
    /// Number of decimated samples accumulated since the last hop trigger.
    /// </summary>
    private int _hopAccum;

    /// <summary>
    /// Write cursor in the circular onset history buffer.
    /// </summary>
    private int _historyWritePos;

    /// <summary>
    /// Number of valid onset history entries written so far, capped at _historySize.
    /// </summary>
    private int _historyCount;

    /// <summary>
    /// Number of input samples accumulated for the current decimation window.
    /// </summary>
    private int _decimateCount;

    /// <summary>
    /// Running sum of input samples for the current decimation window.
    /// </summary>
    private double _decimateSum;

    #endregion

    #region Constructors

    /// <summary>
    /// Initialises a new BpmDetect instance, pre-allocating all processing buffers.
    /// </summary>
    /// <param name="numChannels">Number of audio channels (1 = mono, 2 = stereo, …).</param>
    /// <param name="sampleRate">Input audio sample rate in Hz.</param>
    public BpmDetect(int numChannels, int sampleRate)
    {
        _channels = Math.Max(1, numChannels);
        _decimateBy = Math.Max(1, sampleRate / TargetSampleRate);

        _effectiveSampleRate = sampleRate / (float)_decimateBy;
        _hopRate = _effectiveSampleRate / HopSize;

        _historySize = (int)(HistorySeconds * _effectiveSampleRate / HopSize);

        _fftBuffer = new System.Numerics.Complex[FftSize];
        _prevMagnitudes = new float[FftSize / 2 + 1];
        _window = new float[FftSize];
        _slideBuffer = new float[FftSize];
        _onsetHistory = new float[_historySize];
        _xcorrResult = new float[_historySize / 2 + 1];

        BuildHammingWindow();
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Feeds interleaved audio frames into the detector.
    /// </summary>
    /// <param name="samples">Interleaved samples; length must be numSamples * channels.</param>
    /// <param name="numSamples">Number of audio frames (not individual samples).</param>
    public void InputSamples(ReadOnlySpan<float> samples, int numSamples)
    {
        for (int frame = 0; frame < numSamples; frame++)
        {
            double monoSample = 0.0;
            int baseIndex = frame * _channels;

            for (int ch = 0; ch < _channels; ch++)
            {
                monoSample += samples[baseIndex + ch];
            }

            _decimateSum += monoSample;
            _decimateCount++;

            if (_decimateCount >= _decimateBy)
            {
                float decimated = (float)(_decimateSum / (_decimateBy * _channels));
                _decimateSum = 0.0;
                _decimateCount = 0;

                _slideBuffer[_slidePos & (FftSize - 1)] = decimated;
                _slidePos++;
                _hopAccum++;

                if (_hopAccum >= HopSize)
                {
                    ProcessHop();
                    _hopAccum = 0;
                }
            }
        }
    }

    /// <summary>
    /// Returns the estimated tempo in beats per minute, or 0 if insufficient data.
    /// </summary>
    /// <returns>BPM value in the range [MIN_BPM, MAX_BPM], or 0 when not yet determined.</returns>
    public float GetBpm()
    {
        int count = _historyCount;

        if (count < 150)
        {
            return 0f;
        }

        Span<float> history = stackalloc float[count];
        int startSlot = _historyWritePos - count;

        float sumOnset = 0f;
        for (int i = 0; i < count; i++)
        {
            int slot = startSlot + i;
            if (slot < 0) slot += _historySize;
            float val = _onsetHistory[slot];
            history[i] = val;
            sumOnset += val;
        }

        float mean = sumOnset / count;
        for (int i = 0; i < count; i++)
        {
            history[i] -= mean;
        }

        float hopRate = _hopRate;
        int lagMin = Math.Max(1, (int)(hopRate * 60f / MaxBpm));
        int lagMax = Math.Min(count / 2 - 1, (int)(hopRate * 60f / MinBpm) + 1);

        if (lagMax <= lagMin)
        {
            return 0f;
        }

        if (lagMax >= _xcorrResult.Length)
        {
            lagMax = _xcorrResult.Length - 1;
        }

        // Normalised autocorrelation (Pearson-style coefficient). Dividing each lag
        // by the geometric mean of the overlapping segment energies makes lags
        // comparable, so spurious long-lag/edge peaks no longer dominate the true
        // fundamental as they did with the raw dot product.
        Array.Clear(_xcorrResult, 0, lagMax + 1);
        for (int lag = 0; lag <= lagMax; lag++)
        {
            ReadOnlySpan<float> a = history[..(count - lag)];
            ReadOnlySpan<float> b = history[lag..count];

            float cross = VectorizedDotProduct(a, b);
            float energyA = VectorizedDotProduct(a, a);
            float energyB = VectorizedDotProduct(b, b);
            float denom = MathF.Sqrt(energyA * energyB);

            _xcorrResult[lag] = denom > 1e-9f ? cross / denom : 0f;
        }

        // Light 3-point smoothing. The autocorrelation peaks of a steady pulse are
        // only one bin wide, so the previous 9-point moving average smeared and
        // displaced them; a 3-point average removes single-bin noise without that.
        Span<float> smoothed = stackalloc float[lagMax + 1];
        smoothed[0] = _xcorrResult[0];
        smoothed[lagMax] = _xcorrResult[lagMax];
        for (int i = 1; i < lagMax; i++)
        {
            smoothed[i] = (_xcorrResult[i - 1] + _xcorrResult[i] + _xcorrResult[i + 1]) / 3f;
        }

        // Weight the smoothed autocorrelation by a perceptual tempo prior
        // (log-Gaussian centred on PreferredBpm) and pick the strongest lag. This
        // resolves octave errors, where the half-tempo lag often correlates as
        // strongly as the true beat, towards musically plausible tempi.
        int bestLag = -1;
        float bestScore = float.NegativeInfinity;
        for (int lag = lagMin; lag <= lagMax; lag++)
        {
            float bpmAtLag = hopRate * 60f / lag;
            float logRatio = MathF.Log2(bpmAtLag / PreferredBpm) / TempoPriorSigma;
            float weight = MathF.Exp(-0.5f * logRatio * logRatio);
            float score = smoothed[lag] * weight;

            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        if (bestLag < 0)
        {
            return 0f;
        }

        // Parabolic interpolation around the winning bin for sub-bin lag resolution,
        // replacing the fragile mass-centre estimate that failed near range edges.
        float peakLag = bestLag;
        if (bestLag > lagMin && bestLag < lagMax)
        {
            float y0 = smoothed[bestLag - 1];
            float y1 = smoothed[bestLag];
            float y2 = smoothed[bestLag + 1];
            float curvature = y0 - 2f * y1 + y2;

            if (MathF.Abs(curvature) > 1e-12f)
            {
                float delta = 0.5f * (y0 - y2) / curvature;
                if (delta > -1f && delta < 1f)
                {
                    peakLag = bestLag + delta;
                }
            }
        }

        if (peakLag < 1e-9f)
        {
            return 0f;
        }

        float bpm = hopRate * 60f / peakLag;
        return Math.Clamp(bpm, MinBpm, MaxBpm);
    }

    /// <summary>
    /// Returns detected beat positions and strengths into caller-supplied spans.
    /// Beat-level tracking is not implemented in this version; always returns 0.
    /// </summary>
    /// <param name="pos">Span to receive beat positions.</param>
    /// <param name="strength">Span to receive beat strengths.</param>
    /// <returns>Always 0.</returns>
    public int GetBeats(Span<float> pos, Span<float> strength)
    {
        return 0;
    }

    /// <summary>
    /// Disposes the instance. Currently a no-op; reserved for future native resource cleanup.
    /// </summary>
    public void Dispose()
    {
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Pre-computes Hamming window coefficients into _window.
    /// </summary>
    private void BuildHammingWindow()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _window[i] = 0.54f - 0.46f * MathF.Cos(2f * MathF.PI * i / (FftSize - 1));
        }
    }

    /// <summary>
    /// Processes one hop: runs FFT on the current slide window, computes spectral flux, and appends to history.
    /// </summary>
    private void ProcessHop()
    {
        int readStart = _slidePos - FftSize;

        for (int i = 0; i < FftSize; i++)
        {
            int slot = (readStart + i) & (FftSize - 1);
            float windowed = _slideBuffer[slot] * _window[i];
            _fftBuffer[i] = new System.Numerics.Complex(windowed, 0.0);
        }

        OwnAudioFft.Forward(_fftBuffer.AsSpan());

        float spectralFlux = 0f;
        int bins = FftSize / 2 + 1;

        for (int k = 0; k < bins; k++)
        {
            float magnitude = (float)(_fftBuffer[k].Real * _fftBuffer[k].Real + _fftBuffer[k].Imaginary * _fftBuffer[k].Imaginary);
            float diff = magnitude - _prevMagnitudes[k];

            if (diff > 0f)
            {
                spectralFlux += diff;
            }

            _prevMagnitudes[k] = magnitude;
        }

        _onsetHistory[_historyWritePos] = spectralFlux;
        _historyWritePos++;
        if (_historyWritePos >= _historySize)
        {
            _historyWritePos = 0;
        }
        _historyCount = Math.Min(_historyCount + 1, _historySize);
    }

    /// <summary>
    /// Computes the dot product of two equal-length spans using SIMD acceleration when available.
    /// </summary>
    /// <param name="a">First operand span.</param>
    /// <param name="b">Second operand span, same length as a.</param>
    /// <returns>Scalar dot product value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float VectorizedDotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float sum = 0f;
        int i = 0;
        int vSize = Vector<float>.Count;

        if (Vector.IsHardwareAccelerated && a.Length >= vSize)
        {
            var acc = Vector<float>.Zero;
            int limit = a.Length - vSize;

            for (; i <= limit; i += vSize)
            {
                acc += new Vector<float>(a.Slice(i)) * new Vector<float>(b.Slice(i));
            }

            sum = Vector.Dot(acc, Vector<float>.One);
        }

        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    #endregion
}
