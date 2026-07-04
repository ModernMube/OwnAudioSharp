using System;
using System.IO;
using FluentAssertions;
using Ownaudio.Core;
using OwnaudioNET.Effects;
using OwnaudioNET.Engine;
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
