using System;
using Ownaudio.Audio.Effects;
using Ownaudio.Safe.Effects;

namespace OwnaudioNET.Features.Matchering;

/// <summary>
/// The mastering render's own way into the engine: native effects addressed by param id,
/// with no managed IEffectProcessor in between. The managed effects are a parameter model
/// for a mixer chain — going through them here meant every field they don't mirror was
/// dropped on the floor, and their unit conversions (linear thresholds, seconds vs ms) sat
/// between the preset and the DSP for no reason.
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

    private const uint CompThresholdDb = 2;
    private const uint CompRatio = 3;
    private const uint CompAttackMs = 4;
    private const uint CompReleaseMs = 5;
    private const uint CompMakeupDb = 6;

    private const uint AmpTargetRmsDb = 2;
    private const uint AmpAttackSeconds = 3;
    private const uint AmpReleaseSeconds = 4;
    private const uint AmpNoiseGateDb = 5;
    private const uint AmpMaxGain = 6;
    private const uint AmpMaxGainReductionDb = 7;
    private const uint AmpRmsWindowSeconds = 8;
    private const uint AmpMaxGainChangeDbPerSec = 9;
    private const uint AmpInitialGain = 10;

    private const uint LimThresholdDb = 2;
    private const uint LimCeilingDb = 3;
    private const uint LimReleaseMs = 4;
    private const uint LimLookaheadMs = 5;

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
    /// Threshold and makeup in dB, times in ms — the engine's units, not the managed
    /// effect's linear-threshold constructor.
    /// </summary>
    internal static StandaloneEffect Compressor(int sampleRate, int channels,
        float thresholdDb, float ratio, float attackMs, float releaseMs, float makeupDb)
    {
        var _comp = new StandaloneEffect(EffectType.Compressor, sampleRate, channels);

        _comp.SetParam(CompThresholdDb, thresholdDb);
        _comp.SetParam(CompRatio, ratio);
        _comp.SetParam(CompAttackMs, attackMs);
        _comp.SetParam(CompReleaseMs, releaseMs);
        _comp.SetParam(CompMakeupDb, makeupDb);

        return _comp;
    }

    /// <summary>
    /// The gain rider. initialGain is where it starts: the render pulls the file down to
    /// make room for the EQ boosts and this opens it back up, instead of leaving the rider
    /// to slew there over the first few seconds.
    /// </summary>
    internal static StandaloneEffect DynamicAmp(int sampleRate, int channels,
        float targetRmsDb, float attackSeconds, float releaseSeconds, float noiseGateDb,
        float maxGain, float maxGainReductionDb, float rmsWindowSeconds,
        float maxGainChangeDbPerSec, float initialGain)
    {
        var _amp = new StandaloneEffect(EffectType.DynamicAmp, sampleRate, channels);

        _amp.SetParam(AmpTargetRmsDb, targetRmsDb);
        _amp.SetParam(AmpAttackSeconds, attackSeconds);
        _amp.SetParam(AmpReleaseSeconds, releaseSeconds);
        _amp.SetParam(AmpNoiseGateDb, noiseGateDb);
        _amp.SetParam(AmpMaxGain, maxGain);
        _amp.SetParam(AmpMaxGainReductionDb, maxGainReductionDb);
        _amp.SetParam(AmpRmsWindowSeconds, rmsWindowSeconds);
        _amp.SetParam(AmpMaxGainChangeDbPerSec, maxGainChangeDbPerSec);
        _amp.SetParam(AmpInitialGain, initialGain);

        return _amp;
    }

    /// <summary>
    /// Peak limiter. Everything in dB / ms.
    /// </summary>
    internal static StandaloneEffect Limiter(int sampleRate, int channels,
        float thresholdDb, float ceilingDb, float releaseMs, float lookaheadMs)
    {
        var _lim = new StandaloneEffect(EffectType.Limiter, sampleRate, channels);

        _lim.SetParam(LimThresholdDb, thresholdDb);
        _lim.SetParam(LimCeilingDb, ceilingDb);
        _lim.SetParam(LimReleaseMs, releaseMs);
        _lim.SetParam(LimLookaheadMs, lookaheadMs);

        return _lim;
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
    /// The limiter's lookahead delays the whole render, so the head is silence and the last
    /// few ms are still sitting in its delay line. Pushes silence through to get the tail
    /// back, then slides everything into place.
    /// </summary>
    internal static void CompensateLatency(float[] audioData, int totalSamples, int channels,
        StandaloneEffect limiter)
    {
        int _shift = limiter.LatencySamples * channels;
        if (_shift <= 0 || _shift >= totalSamples) return;

        float[] _tail = new float[_shift];
        limiter.Process(_tail, limiter.LatencySamples);

        Array.Copy(audioData, _shift, audioData, 0, totalSamples - _shift);
        Array.Copy(_tail, 0, audioData, totalSamples - _shift, _shift);
    }
}
