using System;
using Ownaudio.Audio.Effects;
using Ownaudio.Safe.Effects;
using OwnaudioNET.Effects;

namespace OwnaudioNET.Features.Matchering;

/// <summary>
/// The mastering render's own way into the engine: native effects addressed by param id,
/// with no managed IEffectProcessor in between. The managed effects are a parameter model
/// for a mixer chain — going through them here meant every field they don't mirror was
/// dropped on the floor, and their unit conversions sat between the match and the DSP for
/// no reason.
/// </summary>
internal static class NativeMastering
{
    /// <summary>
    /// Blocks the render pushes through the chain. 512 frames is what the analysis and the
    /// old chain used, and the effects are block-size invariant anyway.
    /// </summary>
    internal const int BlockFrames = 512;

    private const uint Eq30BandGain0 = 2;
    private const uint Eq30BandQ0 = 32;
    private const uint Eq30BandFreq0 = 62;

    private const uint CompThreshold = 2;
    private const uint CompRatio = 3;
    private const uint CompKnee = 4;
    private const uint CompAttack = 5;
    private const uint CompRelease = 6;
    private const uint CompAutoRelease = 7;
    private const uint CompLookahead = 8;
    private const uint CompDetector = 9;
    private const uint CompTopology = 10;
    private const uint CompStereoLink = 11;
    private const uint CompChannelMode = 12;
    private const uint CompSidechainHighPass = 13;
    private const uint CompMakeup = 14;
    private const uint CompAutoMakeup = 15;
    private const uint CompRange = 16;

    private const uint AmpTargetLoudness = 2;
    private const uint AmpWindow = 3;
    private const uint AmpMaxBoost = 4;
    private const uint AmpMaxCut = 5;
    private const uint AmpRiseRate = 6;
    private const uint AmpFallRate = 7;
    private const uint AmpTolerance = 8;
    private const uint AmpSmoothing = 9;
    private const uint AmpRelativeGate = 10;
    private const uint AmpFreezeThreshold = 11;
    private const uint AmpCeiling = 12;
    private const uint AmpLimiter = 13;
    private const uint AmpLookahead = 14;
    private const uint AmpLimiterRelease = 15;
    private const uint AmpInitialGain = 16;

    /// <summary>
    /// 30-band EQ with a bell per band. The Q matters as much as the gain: the band gains
    /// come out of a deconvolution that assumed exactly these widths.
    /// </summary>
    internal static StandaloneEffect Equalizer30(int sampleRate, int channels,
        float[] frequencies, float[] qFactors, float[] gainsDb)
    {
        var _eq = new StandaloneEffect(EffectType.Equalizer30, sampleRate, channels);

        for (int i = 0; i < gainsDb.Length; i++)
        {
            _eq.SetParam(Eq30BandFreq0 + (uint)i, frequencies[i]);
            _eq.SetParam(Eq30BandQ0 + (uint)i, qFactors[i]);
            _eq.SetParam(Eq30BandGain0 + (uint)i, gainsDb[i]);
        }

        return _eq;
    }

    /// <summary>
    /// The log-domain compressor with every field of the settings - knee, look-ahead, auto
    /// release, mid/side, key high-pass and range all reach the DSP.
    /// </summary>
    internal static StandaloneEffect OwnCompressor(int sampleRate, int channels, OwnCompressorSettings settings)
    {
        var _comp = new StandaloneEffect(EffectType.OwnCompressor, sampleRate, channels);

        _comp.SetParam(CompThreshold, settings.ThresholdDb);
        _comp.SetParam(CompRatio, settings.Ratio);
        _comp.SetParam(CompKnee, settings.KneeDb);
        _comp.SetParam(CompAttack, settings.AttackMs);
        _comp.SetParam(CompRelease, settings.ReleaseMs);
        _comp.SetParam(CompAutoRelease, settings.AutoRelease ? 1f : 0f);
        _comp.SetParam(CompLookahead, settings.LookaheadMs);
        _comp.SetParam(CompDetector, (float)settings.Detector);
        _comp.SetParam(CompTopology, (float)OwnCompressorTopology.FeedForward);
        _comp.SetParam(CompStereoLink, settings.StereoLink);
        _comp.SetParam(CompChannelMode, (float)settings.ChannelMode);
        _comp.SetParam(CompSidechainHighPass, settings.SidechainHighPassHz);
        _comp.SetParam(CompMakeup, settings.MakeupDb);
        _comp.SetParam(CompAutoMakeup, 0f);
        _comp.SetParam(CompRange, settings.RangeDb);

        return _comp;
    }

    /// <summary>
    /// The loudness rider with its true-peak limiter, the last stage of the chain. The initial
    /// gain is where it starts: the render pulls the file down to make room for the EQ boosts
    /// and this opens it back up, instead of leaving the rider to slew there.
    /// </summary>
    internal static StandaloneEffect OwnDynamicAmp(int sampleRate, int channels, OwnDynamicAmpSettings settings)
    {
        var _amp = new StandaloneEffect(EffectType.OwnDynamicAmp, sampleRate, channels);

        _amp.SetParam(AmpLookahead, settings.LookaheadMs);
        _amp.SetParam(AmpTargetLoudness, settings.TargetLoudness);
        _amp.SetParam(AmpWindow, settings.WindowSeconds);
        _amp.SetParam(AmpMaxBoost, settings.MaxBoostDb);
        _amp.SetParam(AmpMaxCut, settings.MaxCutDb);
        _amp.SetParam(AmpRiseRate, settings.RiseRateDbPerSec);
        _amp.SetParam(AmpFallRate, settings.FallRateDbPerSec);
        _amp.SetParam(AmpTolerance, settings.ToleranceDb);
        _amp.SetParam(AmpSmoothing, settings.SmoothingMs);
        _amp.SetParam(AmpRelativeGate, settings.RelativeGateLu);
        _amp.SetParam(AmpFreezeThreshold, settings.FreezeThresholdLufs);
        _amp.SetParam(AmpCeiling, settings.CeilingDbtp);
        _amp.SetParam(AmpLimiter, 1f);
        _amp.SetParam(AmpLimiterRelease, settings.LimiterReleaseMs);
        _amp.SetParam(AmpInitialGain, settings.InitialGainDb);

        return _amp;
    }

    /// <summary>
    /// The rider held at unity: nothing left of it but the true-peak limiter.
    /// </summary>
    internal static StandaloneEffect TruePeakLimiter(int sampleRate, int channels, float ceilingDbtp)
    {
        return OwnDynamicAmp(sampleRate, channels, new OwnDynamicAmpSettings
        {
            MaxBoostDb = 0f,
            MaxCutDb = 0f,
            InitialGainDb = 0f,
            CeilingDbtp = ceilingDbtp,
            LimiterReleaseMs = 60f
        });
    }

    /// <summary>
    /// Runs the whole buffer through the chain in order, block by block. Reports progress
    /// as a 0-1 fraction so the caller decides what to log.
    /// </summary>
    internal static void Render(float[] audioData, int channels, StandaloneEffect[] chain,
        Action<float>? progress = null)
    {
        int _samplesPerBlock = BlockFrames * channels;
        int _totalSamples = (audioData.Length / channels) * channels;

        for (int offset = 0; offset < _totalSamples; offset += _samplesPerBlock)
        {
            int _count = Math.Min(_samplesPerBlock, _totalSamples - offset);
            int _frames = _count / channels;
            Span<float> _block = audioData.AsSpan(offset, _count);

            foreach (StandaloneEffect effect in chain)
                effect.Process(_block, _frames);

            progress?.Invoke((float)(offset + _count) / _totalSamples);
        }
    }

    /// <summary>
    /// The look-aheads of the chain delay the whole render, so the head is silence and the
    /// last few ms are still sitting in the delay lines. Pushes silence through the chain to
    /// get the tail back, then slides everything into place.
    /// </summary>
    internal static void CompensateLatency(float[] audioData, int totalSamples, int channels,
        StandaloneEffect[] chain)
    {
        int _latency = 0;
        foreach (StandaloneEffect effect in chain) _latency += effect.LatencySamples;

        int _shift = _latency * channels;
        if (_shift <= 0 || _shift >= totalSamples) return;

        float[] _tail = new float[_shift];
        Render(_tail, channels, chain);

        Array.Copy(audioData, _shift, audioData, 0, totalSamples - _shift);
        Array.Copy(_tail, 0, audioData, totalSamples - _shift, _shift);
    }
}
