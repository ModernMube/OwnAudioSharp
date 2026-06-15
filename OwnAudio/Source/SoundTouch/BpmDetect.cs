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
    /// Length of onset history in seconds.
    /// </summary>
    private const float HistorySeconds = 5.0f;

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
    /// Reusable peak finder instance.
    /// </summary>
    private readonly PeakFinder _peakFinder;

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

        _historySize = (int)(HistorySeconds * TargetSampleRate / HopSize);

        _fftBuffer = new System.Numerics.Complex[FftSize];
        _prevMagnitudes = new float[FftSize / 2 + 1];
        _window = new float[FftSize];
        _slideBuffer = new float[FftSize];
        _onsetHistory = new float[_historySize];
        _xcorrResult = new float[_historySize / 2 + 1];
        _peakFinder = new PeakFinder();

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

        float hopRate = TargetSampleRate / (float)HopSize;
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

        Array.Clear(_xcorrResult, 0, lagMax + 1);
        for (int lag = 0; lag <= lagMax; lag++)
        {
            _xcorrResult[lag] = VectorizedDotProduct(
                history[..(count - lag)],
                history[lag..count]);
        }

        Span<float> smoothed = stackalloc float[lagMax + 1];
        MAFilter(smoothed, _xcorrResult.AsSpan(0, lagMax + 1), 0, lagMax + 1, 9);

        double peak = _peakFinder.DetectPeak(smoothed, lagMin, lagMax + 1);

        if (peak < 1e-9)
        {
            return 0f;
        }

        float bpm = hopRate * 60f / (float)peak;
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
    /// Computes an N-point moving average of <paramref name="source"/> into <paramref name="dest"/>.
    /// Only the range [start, end) is processed; values outside that range are left untouched.
    /// Zero-allocation: operates entirely on pre-allocated spans.
    /// </summary>
    private static void MAFilter(
        Span<float> dest,
        ReadOnlySpan<float> source,
        int start, int end, int n)
    {
        int half = n / 2;
        for (int i = start; i < end; i++)
        {
            int i1 = Math.Max(start, i - half);
            int i2 = Math.Min(end, i + half + 1);
            float sum = 0f;
            for (int j = i1; j < i2; j++)
            {
                sum += source[j];
            }
            dest[i] = sum / (i2 - i1);
        }
    }

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
