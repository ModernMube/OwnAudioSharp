using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Ownaudio.Core;
using OwnaudioNET.Effects;
using OwnaudioNET.Engine;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Mixing;
using Xunit;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace Ownaudio.OwnaudioNET.Tests.Mixing;

/// <summary>
/// Tests for routing managed master effects onto the native master bus in the Rust-native chain
/// (plan E.3). The managed effect stays the parameter model; a paired native effect is created and
/// its parameters are mirrored (with unit conversion) by the control-rate mirror pass.
/// </summary>
[Collection("RustNativeChain")]
public sealed class AudioMixerRustNativeMasterEffectTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int MixerBufferFrames = 512;

    private readonly bool? _priorOverride;
    private readonly IAudioEngine _engine;

    public AudioMixerRustNativeMasterEffectTests()
    {
        _priorOverride = RustNativeChain.Override;
        RustNativeChain.Override = true;

        var config = new AudioConfig { SampleRate = SampleRate, Channels = Channels, BufferSize = MixerBufferFrames };
        _engine = AudioEngineFactory.CreateMockEngine(config);
    }

    public void Dispose()
    {
        RustNativeChain.Override = _priorOverride;
        _engine.Dispose();
    }

    [Fact]
    public void AddMasterEffect_CreatesPairedNativeEffect()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        var compressor = new CompressorEffect();

        mixer.AddMasterEffect(compressor);

        mixer.RustSession.Should().NotBeNull("adding a master effect lazily creates the shared session");
        mixer.RustSession!.MasterEffects.Effects.Count.Should().Be(1);
    }

    [Fact]
    public void MasterEffect_MirrorsManagedParamsOntoNative_WithUnitConversion()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);

        // Managed threshold is linear 0–1; native wants dB. 0.5 → 20·log10(0.5) ≈ -6.02 dB.
        var compressor = new CompressorEffect(threshold: 0.5f, ratio: 4.0f);
        mixer.AddMasterEffect(compressor);

        mixer.MirrorRustMasterEffectsOnce();

        MasterEffectChainProxy chain = new(mixer);
        chain.GetParam(2).Should().BeApproximately(20f * MathF.Log10(0.5f), 0.1f); // threshold dB
        chain.GetParam(3).Should().BeApproximately(4.0f, 0.01f);                   // ratio
        chain.GetParam(0).Should().BeApproximately(1.0f, 0.01f);                   // enabled
    }

    [Fact]
    public void AllBuiltInEffects_RouteToNativeMasterBus()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);

        IEffectProcessor[] effects =
        {
            new ReverbEffect(),
            new EqualizerEffect(sampleRate: SampleRate),
            new Equalizer30BandEffect(sampleRate: SampleRate),
            new CompressorEffect(),
            new LimiterEffect(SampleRate),
            new DelayEffect(sampleRate: SampleRate),
            new ChorusEffect(sampleRate: SampleRate),
            new DistortionEffect(),
            new OverdriveEffect(),
            new FlangerEffect(sampleRate: SampleRate),
            new PhaserEffect(sampleRate: SampleRate),
            new RotaryEffect(),
            new AutoGainEffect(),
            new EnhancerEffect(sampleRate: SampleRate),
            new DynamicAmpEffect(),
        };

        foreach (IEffectProcessor effect in effects)
        {
            mixer.AddMasterEffect(effect);
        }

        // Every built-in effect has an adapter, so all of them are paired onto the native bus.
        mixer.RustSession.Should().NotBeNull();
        mixer.RustSession!.MasterEffects.Effects.Count.Should().Be(effects.Length,
            "every built-in effect type has a native adapter and must route to the master bus");
    }

    [Theory]
    [MemberData(nameof(ParamMirrorCases))]
    public void BuiltInEffect_MirrorsRepresentativeParam(
        Func<IEffectProcessor> factory, Action<IEffectProcessor> setParam, uint nativeParamId, float expected)
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        IEffectProcessor effect = factory();
        setParam(effect);

        mixer.AddMasterEffect(effect);
        mixer.MirrorRustMasterEffectsOnce();

        var chain = mixer.RustSession!.MasterEffects;
        object native = chain.Effects[0];
        chain.GetParam(native, nativeParamId).Should().BeApproximately(expected, 0.01f);
    }

    public static IEnumerable<object[]> ParamMirrorCases()
    {
        // Reverb room size (native param 2, 0–1).
        yield return new object[]
        {
            (Func<IEffectProcessor>)(() => new ReverbEffect()),
            (Action<IEffectProcessor>)(e => ((ReverbEffect)e).RoomSize = 0.7f),
            (uint)2, 0.7f,
        };
        // Equalizer band 0 gain (native param 2, dB).
        yield return new object[]
        {
            (Func<IEffectProcessor>)(() => new EqualizerEffect(sampleRate: SampleRate)),
            (Action<IEffectProcessor>)(e => ((EqualizerEffect)e).Band0Gain = 6.0f),
            (uint)2, 6.0f,
        };
        // Delay time (native param 2, ms).
        yield return new object[]
        {
            (Func<IEffectProcessor>)(() => new DelayEffect(sampleRate: SampleRate)),
            (Action<IEffectProcessor>)(e => ((DelayEffect)e).Time = 250),
            (uint)2, 250f,
        };
    }

    [Fact]
    public void RepeatedMirror_DoesNotFloodCommandQueue()
    {
        // The mock engine never drains the mixer command queue, so if the mirror enqueued a
        // set_param for every parameter on every tick, thousands of ticks would overflow the queue
        // and the next add/remove would fail (the reported crash). Dirty tracking must keep the
        // steady-state mirror at zero commands.
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        mixer.AddMasterEffect(new CompressorEffect());

        for (int i = 0; i < 5000; i++)
        {
            mixer.MirrorRustMasterEffectsOnce();
        }

        // Adding another effect enqueues a command; without dirty tracking the queue would be full
        // and this would throw (OwnAudioException: command queue is full).
        Action act = () =>
        {
            mixer.AddMasterEffect(new ReverbEffect());
            mixer.ClearMasterEffects();
        };
        act.Should().NotThrow();
    }

    [Fact]
    public void RemoveMasterEffect_RemovesPairedNativeEffect()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        var compressor = new CompressorEffect();
        mixer.AddMasterEffect(compressor);
        mixer.RustSession!.MasterEffects.Effects.Count.Should().Be(1);

        mixer.RemoveMasterEffect(compressor).Should().BeTrue();
        mixer.RustSession!.MasterEffects.Effects.Count.Should().Be(0);
    }

    /// <summary>Reads native master effect params off the first (only) master effect under test.</summary>
    private readonly struct MasterEffectChainProxy
    {
        private readonly AudioMixer _mixer;
        public MasterEffectChainProxy(AudioMixer mixer) => _mixer = mixer;

        public float GetParam(uint paramId)
        {
            var chain = _mixer.RustSession!.MasterEffects;
            object native = chain.Effects[0];
            return chain.GetParam(native, paramId) ?? float.NaN;
        }
    }
}
