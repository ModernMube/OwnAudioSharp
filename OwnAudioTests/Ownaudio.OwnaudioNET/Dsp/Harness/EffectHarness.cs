using System;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Harness;

/// <summary>
/// Runs an effect over a test signal the way the mixer would — in blocks, never one
/// giant call — and hands back the processed buffer.
/// </summary>
public static class EffectHarness
{
    /// <summary>
    /// Rate the whole DSP suite runs at.
    /// </summary>
    public const int SampleRate = 48000;

    /// <summary>
    /// Stereo everywhere; a couple of tests override this.
    /// </summary>
    public const int Channels = 2;

    /// <summary>
    /// Block the mixer feeds effects with.
    /// </summary>
    public const int BlockFrames = 512;

    /// <summary>
    /// Frames we throw away before measuring, so lookahead, gain ramps and filter
    /// state have settled. Half a second is plenty for everything in the box.
    /// </summary>
    public const int SettleFrames = SampleRate / 2;

    /// <summary>
    /// Standard config for the suite.
    /// </summary>
    public static AudioConfig Config(int channels = Channels, int sampleRate = SampleRate) => new AudioConfig
    {
        SampleRate = sampleRate,
        Channels = channels,
        BufferSize = BlockFrames,
        EnableOutput = false,
        EnableInput = false
    };

    /// <summary>
    /// Copies the input, pushes it through the effect in blocks and returns the result.
    /// The effect is initialized here, so tests only have to set parameters.
    /// </summary>
    public static float[] Render(IEffectProcessor fx, float[] input, int channels = Channels, int blockFrames = BlockFrames, int sampleRate = SampleRate)
    {
        fx.Initialize(Config(channels, sampleRate));
        return RenderInto(fx, input, channels, blockFrames);
    }

    /// <summary>
    /// Same, but assumes the effect is already initialized — for the invariance tests
    /// that render twice with different block sizes.
    /// </summary>
    public static float[] RenderInto(IEffectProcessor fx, float[] input, int channels = Channels, int blockFrames = BlockFrames)
    {
        float[] _out = (float[])input.Clone();
        Span<float> _span = _out;
        int _frames = input.Length / channels;

        for (int f = 0; f < _frames; f += blockFrames)
        {
            int _n = Math.Min(blockFrames, _frames - f);
            fx.Process(_span.Slice(f * channels, _n * channels), _n);
        }

        return _out;
    }

    /// <summary>
    /// The part of a rendered buffer worth measuring — everything after the settle
    /// window, pulled out as a single channel.
    /// </summary>
    public static float[] Steady(float[] rendered, int channels = Channels, int channel = 0, int settleFrames = SettleFrames)
    {
        int _frames = rendered.Length / channels;
        if (settleFrames >= _frames) settleFrames = _frames / 2;

        float[] _mono = new float[_frames - settleFrames];
        for (int f = 0; f < _mono.Length; f++)
            _mono[f] = rendered[(settleFrames + f) * channels + channel];

        return _mono;
    }

    /// <summary>
    /// Feeds a steady tone through the effect and reports how much the level at that
    /// frequency moved, in dB. This is the workhorse behind every EQ and filter test.
    /// </summary>
    public static double MeasureGainDb(IEffectProcessor fx, double freq, double levelDb = -20.0, int channels = Channels, int seconds = 1)
    {
        int _frames = SettleFrames + SampleRate * seconds;
        float[] _in = SignalGenerator.Sine(freq, levelDb, _frames, channels, SampleRate);
        float[] _out = Render(fx, _in, channels);

        float[] _dry = Steady(_in, channels);
        float[] _wet = Steady(_out, channels);

        return SignalMeasure.MagnitudeDbAt(_wet, freq, SampleRate) - SignalMeasure.MagnitudeDbAt(_dry, freq, SampleRate);
    }

    /// <summary>
    /// Gain in dB at a whole list of frequencies, each measured on its own fresh
    /// effect instance so one probe can't leave state behind for the next.
    /// </summary>
    public static double[] MeasureResponse(Func<IEffectProcessor> factory, double[] freqs, double levelDb = -20.0)
    {
        double[] _gains = new double[freqs.Length];

        for (int i = 0; i < freqs.Length; i++)
        {
            using (IEffectProcessor _fx = factory())
            {
                _gains[i] = MeasureGainDb(_fx, freqs[i], levelDb);
            }
        }

        return _gains;
    }
}
