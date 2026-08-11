using System;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Harness;

/// <summary>
/// Measures what came out of an effect. Everything is reported in dB, Hz or samples,
/// never in raw linear amplitude — a failing assert should read like a bug report.
/// </summary>
public static class SignalMeasure
{
    private const double Floor = 1e-12;

    /// <summary>
    /// Linear amplitude to dBFS, clamped at -240 so silence doesn't produce -Inf.
    /// </summary>
    public static double ToDb(double linear) => 20.0 * Math.Log10(Math.Max(Math.Abs(linear), Floor));

    /// <summary>
    /// Pulls one channel out of an interleaved buffer.
    /// </summary>
    public static float[] Channel(ReadOnlySpan<float> interleaved, int channels, int channel)
    {
        int _frames = interleaved.Length / channels;
        float[] _mono = new float[_frames];
        for (int f = 0; f < _frames; f++)
            _mono[f] = interleaved[f * channels + channel];

        return _mono;
    }

    /// <summary>
    /// Highest absolute sample, in dBFS.
    /// </summary>
    public static double PeakDb(ReadOnlySpan<float> samples)
    {
        double _peak = 0.0;
        foreach (float s in samples)
        {
            double _a = Math.Abs(s);
            if (_a > _peak) _peak = _a;
        }

        return ToDb(_peak);
    }

    /// <summary>
    /// RMS of the whole span, in dBFS.
    /// </summary>
    public static double RmsDb(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return ToDb(0.0);

        double _sum = 0.0;
        foreach (float s in samples)
            _sum += (double)s * s;

        return ToDb(Math.Sqrt(_sum / samples.Length));
    }

    /// <summary>
    /// Amplitude of one frequency component, via Hann-windowed single-bin DFT. Works
    /// for off-bin frequencies too, and the window keeps neighbouring tones from
    /// leaking in — which a bare Goertzel would not.
    /// </summary>
    public static double AmplitudeAt(ReadOnlySpan<float> mono, double freq, int sampleRate)
    {
        int _n = mono.Length;
        if (_n == 0) return 0.0;

        double _w = 2.0 * Math.PI * freq / sampleRate;
        double _re = 0.0, _im = 0.0, _winSum = 0.0;

        for (int i = 0; i < _n; i++)
        {
            double _hann = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (_n - 1));
            double _x = mono[i] * _hann;
            _re += _x * Math.Cos(_w * i);
            _im -= _x * Math.Sin(_w * i);
            _winSum += _hann;
        }

        return 2.0 * Math.Sqrt(_re * _re + _im * _im) / _winSum;
    }

    /// <summary>
    /// Same component, expressed in dBFS.
    /// </summary>
    public static double MagnitudeDbAt(ReadOnlySpan<float> mono, double freq, int sampleRate)
        => ToDb(AmplitudeAt(mono, freq, sampleRate));

    /// <summary>
    /// Phase of one component in radians.
    /// </summary>
    public static double PhaseAt(ReadOnlySpan<float> mono, double freq, int sampleRate)
    {
        int _n = mono.Length;
        double _w = 2.0 * Math.PI * freq / sampleRate;
        double _re = 0.0, _im = 0.0;

        for (int i = 0; i < _n; i++)
        {
            double _hann = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (_n - 1));
            double _x = mono[i] * _hann;
            _re += _x * Math.Cos(_w * i);
            _im -= _x * Math.Sin(_w * i);
        }

        return Math.Atan2(_im, _re);
    }

    /// <summary>
    /// How much the effect changed the level at one frequency, in dB. Positive means boost.
    /// </summary>
    public static double GainDbAt(ReadOnlySpan<float> input, ReadOnlySpan<float> output, double freq, int sampleRate)
        => MagnitudeDbAt(output, freq, sampleRate) - MagnitudeDbAt(input, freq, sampleRate);

    /// <summary>
    /// Total harmonic distortion as a ratio (0.01 = 1%), summing harmonics 2..count
    /// against the fundamental.
    /// </summary>
    public static double Thd(ReadOnlySpan<float> mono, double fundamental, int sampleRate, int count = 8)
    {
        double _f0 = AmplitudeAt(mono, fundamental, sampleRate);
        if (_f0 < Floor) return 0.0;

        double _sum = 0.0;
        for (int h = 2; h <= count; h++)
        {
            double _freq = fundamental * h;
            if (_freq >= sampleRate / 2.0) break;

            double _a = AmplitudeAt(mono, _freq, sampleRate);
            _sum += _a * _a;
        }

        return Math.Sqrt(_sum) / _f0;
    }

    /// <summary>
    /// Lag in samples at which signal best matches reference. Used to find the echo
    /// a delay produced; lag 0 is skipped so the dry copy doesn't win.
    /// </summary>
    public static int DelaySamples(ReadOnlySpan<float> reference, ReadOnlySpan<float> signal, int minLag, int maxLag)
    {
        int _best = minLag;
        double _bestScore = double.NegativeInfinity;
        int _len = Math.Min(reference.Length, signal.Length);

        for (int lag = minLag; lag <= maxLag && lag < _len; lag++)
        {
            double _sum = 0.0;
            for (int i = 0; i + lag < _len; i++)
                _sum += reference[i] * signal[i + lag];

            if (_sum > _bestScore)
            {
                _bestScore = _sum;
                _best = lag;
            }
        }

        return _best;
    }

    /// <summary>
    /// Normalised correlation of the two channels: 1 = identical, 0 = unrelated.
    /// Modulation and reverb should push this well below 1.
    /// </summary>
    public static double StereoCorrelation(ReadOnlySpan<float> interleaved, int channels)
    {
        float[] _l = Channel(interleaved, channels, 0);
        float[] _r = Channel(interleaved, channels, 1);

        double _num = 0.0, _dl = 0.0, _dr = 0.0;
        for (int i = 0; i < _l.Length; i++)
        {
            _num += (double)_l[i] * _r[i];
            _dl += (double)_l[i] * _l[i];
            _dr += (double)_r[i] * _r[i];
        }

        double _den = Math.Sqrt(_dl * _dr);
        return _den < Floor ? 0.0 : _num / _den;
    }

    /// <summary>
    /// RMS in dB of one slice, given in frames rather than samples.
    /// </summary>
    public static double RmsDbOfFrames(ReadOnlySpan<float> interleaved, int channels, int startFrame, int frameCount)
        => RmsDb(interleaved.Slice(startFrame * channels, frameCount * channels));

    /// <summary>
    /// Walks forward in blocks and returns the frame where the level first drops
    /// dropDb below the starting level — an RT-style decay measurement.
    /// </summary>
    public static int DecayFrames(ReadOnlySpan<float> interleaved, int channels, int startFrame, double dropDb, int block = 256)
    {
        int _frames = interleaved.Length / channels;
        double _ref = RmsDbOfFrames(interleaved, channels, startFrame, Math.Min(block, _frames - startFrame));

        for (int f = startFrame + block; f + block <= _frames; f += block)
        {
            if (RmsDbOfFrames(interleaved, channels, f, block) <= _ref - dropDb)
                return f - startFrame;
        }

        return -1;
    }

    /// <summary>
    /// No NaN, no infinity anywhere.
    /// </summary>
    public static bool AllFinite(ReadOnlySpan<float> samples)
    {
        foreach (float s in samples)
        {
            if (!float.IsFinite(s)) return false;
        }

        return true;
    }

    /// <summary>
    /// Largest absolute difference between two buffers — the block-size invariance
    /// and determinism checks both boil down to this.
    /// </summary>
    public static double MaxDiff(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double _max = 0.0;
        int _len = Math.Min(a.Length, b.Length);

        for (int i = 0; i < _len; i++)
        {
            double _d = Math.Abs((double)a[i] - b[i]);
            if (_d > _max) _max = _d;
        }

        return _max;
    }

    /// <summary>
    /// Index of the first sample where the two buffers differ by more than tolerance,
    /// or -1. Makes a failed comparison point at a sample instead of just "differs".
    /// </summary>
    public static int FirstDiff(ReadOnlySpan<float> a, ReadOnlySpan<float> b, double tolerance)
    {
        int _len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < _len; i++)
        {
            if (Math.Abs((double)a[i] - b[i]) > tolerance) return i;
        }

        return -1;
    }
}
