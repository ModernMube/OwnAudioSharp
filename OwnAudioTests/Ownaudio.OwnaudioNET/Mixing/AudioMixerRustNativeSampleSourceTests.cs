using System;
using FluentAssertions;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using Xunit;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace Ownaudio.OwnaudioNET.Tests.Mixing;

/// <summary>
/// Tests for the fully-native Rust-native backing of <see cref="SampleSource"/>: the in-memory
/// buffer is served by a native <see cref="Ownaudio.Audio.Tracks.MemoryTrack"/> in the mixer's shared
/// session, so — like <see cref="FileSource"/> — the audio path is entirely native and the managed
/// side is only a controller.
/// </summary>
/// <remarks>
/// The tests use the mock engine and never open a device, so they are hardware-free; they verify the
/// control-plane wiring (track creation, control-state mirroring, teardown). The native serving of
/// the buffer itself is covered by the Rust <c>memory_source</c> unit tests and the FFI smoke tests.
/// </remarks>
[Collection("RustNativeChain")]
public sealed class AudioMixerRustNativeSampleSourceTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int MixerBufferFrames = 512;

    private readonly bool? _priorOverride;
    private readonly IAudioEngine _engine;
    private readonly AudioConfig _config;

    public AudioMixerRustNativeSampleSourceTests()
    {
        _priorOverride = RustNativeChain.Override;
        RustNativeChain.Override = true;

        _config = new AudioConfig { SampleRate = SampleRate, Channels = Channels, BufferSize = MixerBufferFrames };
        _engine = AudioEngineFactory.CreateMockEngine(_config);
    }

    public void Dispose()
    {
        RustNativeChain.Override = _priorOverride;
        _engine.Dispose();
    }

    private SampleSource CreateSampleSource(int frames)
    {
        var samples = new float[frames * Channels];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.5f;
        }

        return new SampleSource(samples, _config);
    }

    [Fact]
    public void AddSampleSource_AttachesNativeMemoryTrack()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        mixer.RustSession.Should().BeNull();

        using var source = CreateSampleSource(SampleRate);
        mixer.AddSource(source).Should().BeTrue();

        mixer.RustSession.Should().NotBeNull();
        mixer.RustSession!.Tracks.Count.Should().Be(1);
        source.RustTrack.Should().NotBeNull();
        source.RustTrack.Should().BeSameAs(mixer.RustSession!.Tracks[0]);
        source.RustMemoryTrack.Should().NotBeNull();
    }

    [Fact]
    public void AddEffectWrappedSampleSource_AttachesTrack()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        using var source = CreateSampleSource(SampleRate);
        using var wrapped = new SourceWithEffects(source);

        mixer.AddSource(wrapped).Should().BeTrue();

        mixer.RustSession!.Tracks.Count.Should().Be(1);
        source.RustTrack.Should().NotBeNull();
    }

    [Fact]
    public void SyncControlState_MirrorsVolumeAndLoop()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        using var source = CreateSampleSource(SampleRate);
        source.Volume = 0.5f;
        source.Loop = true;
        mixer.AddSource(source);

        mixer.SyncRustControlStateOnce();

        source.RustTrack!.Gain.Should().BeApproximately(0.5f, 0.0001f);
        source.RustMemoryTrack!.Loop.Should().BeTrue();
    }

    [Fact]
    public void RemoveSampleSource_RemovesTrack()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        using var a = CreateSampleSource(SampleRate);
        using var b = CreateSampleSource(SampleRate);
        mixer.AddSource(a);
        mixer.AddSource(b);
        mixer.RustSession!.Tracks.Count.Should().Be(2);

        mixer.RemoveSource(a).Should().BeTrue();

        mixer.RustSession!.Tracks.Count.Should().Be(1);
        a.RustTrack.Should().BeNull();
        b.RustTrack.Should().NotBeNull();
    }

    [Fact]
    public void SubmitSamples_ReloadsNativeSourceWithoutError()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        using var source = new SampleSource(SampleRate, _config); // dynamic buffer
        mixer.AddSource(source);

        var newData = new float[SampleRate * Channels];
        Action submit = () => source.SubmitSamples(newData);

        submit.Should().NotThrow();
        mixer.RustSession!.Tracks.Count.Should().Be(1);
    }

    [Fact]
    public void RoutedSampleSource_SyncTickAppliesWithoutError()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        using var source = CreateSampleSource(SampleRate);
        source.RouteToChannels(1, 0);
        mixer.AddSource(source);

        Action tick = () => mixer.SyncRustChannelMapsOnce();

        tick.Should().NotThrow();
        mixer.RustSession!.Tracks.Count.Should().Be(1);
    }

    [Fact]
    public void ClearSources_RemovesAllTracks()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        mixer.AddSource(CreateSampleSource(SampleRate));
        mixer.AddSource(CreateSampleSource(SampleRate));

        mixer.ClearSources();

        mixer.RustSession!.Tracks.Count.Should().Be(0);
    }

    [Fact]
    public void LegacyMixer_NoSessionForSampleSource()
    {
        RustNativeChain.Override = false;

        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        mixer.IsRustNative.Should().BeFalse();

        using var source = CreateSampleSource(SampleRate);
        mixer.AddSource(source).Should().BeTrue();

        mixer.RustSession.Should().BeNull();
    }
}
