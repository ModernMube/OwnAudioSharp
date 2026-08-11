using System;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Harness;

/// <summary>
/// Test signals for the DSP suite. Everything comes back interleaved, and levels
/// are given in dBFS so the tests read like the spec they check.
/// </summary>
public static class SignalGenerator
{
    /// <summary>
    /// dBFS to linear amplitude.
    /// </summary>
    public static float FromDb(double db) => (float)Math.Pow(10.0, db / 20.0);

    /// <summary>
    /// Steady sine at the given level, same phase on every channel.
    /// </summary>
    public static float[] Sine(double freq, double levelDb, int frames, int channels, int sampleRate)
    {
        float[] _buf = new float[frames * channels];
        float _amp = FromDb(levelDb);
        double _step = 2.0 * Math.PI * freq / sampleRate;

        for (int f = 0; f < frames; f++)
        {
            float _s = _amp * (float)Math.Sin(_step * f);
            for (int c = 0; c < channels; c++)
                _buf[f * channels + c] = _s;
        }

        return _buf;
    }

    /// <summary>
    /// Sine on one channel only, the rest stays silent. Handy for stereo routing
    /// and ping-pong checks.
    /// </summary>
    public static float[] SineOnChannel(double freq, double levelDb, int frames, int channels, int sampleRate, int channel)
    {
        float[] _buf = new float[frames * channels];
        float _amp = FromDb(levelDb);
        double _step = 2.0 * Math.PI * freq / sampleRate;

        for (int f = 0; f < frames; f++)
            _buf[f * channels + channel] = _amp * (float)Math.Sin(_step * f);

        return _buf;
    }

    /// <summary>
    /// Sine that runs for burstFrames and then goes quiet — for attack/release and
    /// decay measurements.
    /// </summary>
    public static float[] SineBurst(double freq, double levelDb, int burstFrames, int totalFrames, int channels, int sampleRate)
    {
        float[] _buf = Sine(freq, levelDb, totalFrames, channels, sampleRate);
        Array.Clear(_buf, burstFrames * channels, (totalFrames - burstFrames) * channels);
        return _buf;
    }

    /// <summary>
    /// Two-level sine: quiet first, then jumps to loud at the halfway point. This is
    /// the classic compressor/limiter step.
    /// </summary>
    public static float[] SineStep(double freq, double quietDb, double loudDb, int stepFrame, int frames, int channels, int sampleRate)
    {
        float[] _buf = new float[frames * channels];
        double _step = 2.0 * Math.PI * freq / sampleRate;

        for (int f = 0; f < frames; f++)
        {
            float _amp = FromDb(f < stepFrame ? quietDb : loudDb);
            float _s = _amp * (float)Math.Sin(_step * f);
            for (int c = 0; c < channels; c++)
                _buf[f * channels + c] = _s;
        }

        return _buf;
    }

    /// <summary>
    /// Single full-scale sample followed by silence.
    /// </summary>
    public static float[] Impulse(int frames, int channels, double levelDb = 0.0)
    {
        float[] _buf = new float[frames * channels];
        float _amp = FromDb(levelDb);
        for (int c = 0; c < channels; c++)
            _buf[c] = _amp;

        return _buf;
    }

    /// <summary>
    /// Digital silence.
    /// </summary>
    public static float[] Silence(int frames, int channels) => new float[frames * channels];

    /// <summary>
    /// Seeded white noise so every run gets the exact same buffer.
    /// </summary>
    public static float[] Noise(double levelDb, int frames, int channels, int seed = 1234)
    {
        float[] _buf = new float[frames * channels];
        float _amp = FromDb(levelDb);
        Random _rng = new Random(seed);

        for (int i = 0; i < _buf.Length; i++)
            _buf[i] = _amp * (float)(_rng.NextDouble() * 2.0 - 1.0);

        return _buf;
    }

    /// <summary>
    /// Log sweep from startFreq to endFreq. Only used where a broadband picture is
    /// needed — for a known tone the Goertzel probe is more precise.
    /// </summary>
    public static float[] LogSweep(double startFreq, double endFreq, double levelDb, int frames, int channels, int sampleRate)
    {
        float[] _buf = new float[frames * channels];
        float _amp = FromDb(levelDb);
        double _duration = (double)frames / sampleRate;
        double _k = Math.Log(endFreq / startFreq);
        double _phase = 0.0;

        for (int f = 0; f < frames; f++)
        {
            double _t = (double)f / sampleRate;
            double _inst = startFreq * Math.Exp(_k * _t / _duration);
            _phase += 2.0 * Math.PI * _inst / sampleRate;

            float _s = _amp * (float)Math.Sin(_phase);
            for (int c = 0; c < channels; c++)
                _buf[f * channels + c] = _s;
        }

        return _buf;
    }
}
