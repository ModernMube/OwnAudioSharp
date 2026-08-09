using System;
using System.Numerics;
using OwnaudioNET.Dsp;

namespace OwnaudioNET.Monitoring;

/// <summary>
/// Turns an <see cref="EffectTap"/> into two spectra: the signal going into the effect chain and
/// the one coming out. Poll Update from a UI timer, then read the magnitude spans - they only
/// change when Update returns true.
/// </summary>
public sealed class EffectSpectrumAnalyzer : IDisposable
{
    private readonly EffectTap _tap;
    private readonly int _fftSize;
    private readonly int _bins;

    private readonly float[] _window;
    private readonly float[] _preWindow;
    private readonly float[] _postWindow;
    private readonly float[] _preDb;
    private readonly float[] _postDb;
    private readonly float[] _frequencies;

    private readonly float[] _preChunk;
    private readonly float[] _postChunk;
    private readonly Complex[] _scratch;

    private int _filled;
    private bool _disposed;

    /// <summary>
    /// Wraps a tap. fftSize is rounded up to a power of two, 2048 is a decent default - about
    /// 23 Hz per bin at 48k, fine enough to see what an EQ or a comp is doing.
    /// </summary>
    public EffectSpectrumAnalyzer(EffectTap tap, int fftSize = 2048)
    {
        _tap = tap ?? throw new ArgumentNullException(nameof(tap));
        _fftSize = _roundUpPow2(Math.Max(64, fftSize));
        _bins = _fftSize / 2;

        _window = new float[_fftSize];
        _preWindow = new float[_fftSize];
        _postWindow = new float[_fftSize];
        _preDb = new float[_bins];
        _postDb = new float[_bins];
        _frequencies = new float[_bins];
        _scratch = new Complex[_fftSize];

        //One tap read pulls at most a full window's worth of frames, interleaved
        int chunk = _fftSize * Math.Max(1, tap.Channels);
        _preChunk = new float[chunk];
        _postChunk = new float[chunk];

        for (int i = 0; i < _fftSize; i++)
            _window[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (_fftSize - 1));

        float binWidth = tap.SampleRate / (float)_fftSize;
        for (int i = 0; i < _bins; i++)
            _frequencies[i] = i * binWidth;

        for (int i = 0; i < _bins; i++)
        {
            _preDb[i] = FloorDb;
            _postDb[i] = FloorDb;
        }
    }

    /// <summary>
    /// Anything quieter than this reads as silence.
    /// </summary>
    public const float FloorDb = -120f;

    /// <summary>
    /// Center frequency of each bin, in Hz.
    /// </summary>
    public ReadOnlySpan<float> Frequencies => _frequencies;

    /// <summary>
    /// Magnitudes of the signal going into the chain, in dBFS.
    /// </summary>
    public ReadOnlySpan<float> PreMagnitudesDb => _preDb;

    /// <summary>
    /// Magnitudes of the signal the chain produced, in dBFS.
    /// </summary>
    public ReadOnlySpan<float> PostMagnitudesDb => _postDb;

    /// <summary>
    /// How many bins the spectra have.
    /// </summary>
    public int BinCount => _bins;

    /// <summary>
    /// Drains the tap and recomputes both spectra once a full window has come in. Returns false
    /// when there wasn't enough new audio yet, in which case the magnitudes are left alone.
    /// </summary>
    public bool Update()
    {
        if (_disposed) { return false; }

        int channels = Math.Max(1, _tap.Channels);
        bool refreshed = false;

        while (_filled < _fftSize)
        {
            int wanted = (_fftSize - _filled) * channels;
            int read = _tap.Read(_preChunk.AsSpan(0, wanted), _postChunk.AsSpan(0, wanted));
            if (read == 0) { break; }

            int frames = read / channels;
            _appendMono(_preChunk, _preWindow, frames, channels);
            _appendMono(_postChunk, _postWindow, frames, channels);
            _filled += frames;

            if (_filled >= _fftSize)
            {
                _transform(_preWindow, _preDb);
                _transform(_postWindow, _postDb);
                _filled = 0;
                refreshed = true;
            }
        }

        return refreshed;
    }

    /// <summary>
    /// Downmixes the interleaved chunk and parks it at the end of the window.
    /// </summary>
    private void _appendMono(float[] source, float[] window, int frames, int channels)
    {
        for (int f = 0; f < frames; f++)
        {
            float sum = 0f;
            int at = f * channels;
            for (int c = 0; c < channels; c++)
                sum += source[at + c];

            window[_filled + f] = sum / channels;
        }
    }

    /// <summary>
    /// Windows the samples, transforms, and writes the half spectrum out as dB. Calibrated against
    /// a sine, so a full-scale tone reads 0 dBFS - the DC bin sits 6 dB high, as usual.
    /// </summary>
    private void _transform(float[] samples, float[] outDb)
    {
        for (int i = 0; i < _fftSize; i++)
            _scratch[i] = new Complex(samples[i] * _window[i], 0.0);

        OwnAudioFft.Forward(_scratch);

        //Hann loses half the energy, and a real signal splits into two mirrored halves
        float scale = 4f / _fftSize;
        for (int i = 0; i < _bins; i++)
        {
            float magnitude = (float)_scratch[i].Magnitude * scale;
            outDb[i] = magnitude > 1e-7f ? MathF.Max(FloorDb, 20f * MathF.Log10(magnitude)) : FloorDb;
        }
    }

    private static int _roundUpPow2(int value)
    {
        int size = 64;
        while (size < value) size <<= 1;
        return size;
    }

    /// <summary>
    /// Closes the underlying tap too.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tap.Dispose();
    }
}
