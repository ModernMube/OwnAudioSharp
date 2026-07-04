using System;
using System.IO;
using FluentAssertions;
using Ownaudio.Audio.Tracks;
using Ownaudio.Core;
using OwnaudioNET.Effects;
using OwnaudioNET.Engine;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using Xunit;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace Ownaudio.OwnaudioNET.Tests.Mixing;

/// <summary>
/// Tests for routing per-track (per-source) effects onto the native track effect chain in the
/// Rust-native chain (plan E.2). A <see cref="SourceWithEffects"/> wrapping a <see cref="FileSource"/>
/// has its effect list reconciled onto the underlying native track and its parameters mirrored.
/// </summary>
[Collection("RustNativeChain")]
public sealed class AudioMixerRustNativeTrackEffectTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int MixerBufferFrames = 512;

    private readonly bool? _priorOverride;
    private readonly IAudioEngine _engine;
    private readonly string _wavPath;

    public AudioMixerRustNativeTrackEffectTests()
    {
        _priorOverride = RustNativeChain.Override;
        RustNativeChain.Override = true;

        var config = new AudioConfig { SampleRate = SampleRate, Channels = Channels, BufferSize = MixerBufferFrames };
        _engine = AudioEngineFactory.CreateMockEngine(config);
        _wavPath = WriteTempWav(Channels, SampleRate, frames: SampleRate);
    }

    public void Dispose()
    {
        RustNativeChain.Override = _priorOverride;
        _engine.Dispose();
        try { if (File.Exists(_wavPath)) File.Delete(_wavPath); } catch { /* best effort */ }
    }

    [Fact]
    public void SourceWithEffects_RoutesTrackEffectsToNativeChain_AndMirrorsParams()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);

        var fileSource = new FileSource(_wavPath);
        var wrapped = new SourceWithEffects(fileSource);
        var compressor = new CompressorEffect(threshold: 0.5f, ratio: 4.0f); // 0.5 linear → -6.02 dB
        wrapped.AddEffect(compressor);

        mixer.AddSource(wrapped);
        mixer.ReconcileRustTrackEffectsOnce();

        AudioTrack track = fileSource.RustTrack!;
        track.Should().NotBeNull("the wrapped file source must get a native track");
        track.Effects.Effects.Count.Should().Be(1, "the wrapper's effect is routed onto the native track chain");

        object native = track.Effects.Effects[0];
        track.Effects.GetParam(native, 2).Should().BeApproximately(20f * MathF.Log10(0.5f), 0.1f); // threshold dB
        track.Effects.GetParam(native, 3).Should().BeApproximately(4.0f, 0.01f);                    // ratio
    }

    [Fact]
    public void AddingAndRemovingTrackEffect_ReconcilesNativeChain()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);

        var fileSource = new FileSource(_wavPath);
        var wrapped = new SourceWithEffects(fileSource);
        mixer.AddSource(wrapped);

        mixer.ReconcileRustTrackEffectsOnce();
        fileSource.RustTrack!.Effects.Effects.Count.Should().Be(0, "no effects yet");

        var reverb = new ReverbEffect();
        wrapped.AddEffect(reverb);
        mixer.ReconcileRustTrackEffectsOnce();
        fileSource.RustTrack!.Effects.Effects.Count.Should().Be(1, "adding a managed effect rebuilds the native chain");

        wrapped.RemoveEffect(reverb);
        mixer.ReconcileRustTrackEffectsOnce();
        fileSource.RustTrack!.Effects.Effects.Count.Should().Be(0, "removing it reconciles the native chain back to empty");
    }

    private static string WriteTempWav(int channels, int sampleRate, int frames)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ownaudio_trackfx_{Guid.NewGuid():N}.wav");

        int dataLen = frames * channels * 2;
        int byteRate = sampleRate * channels * 2;
        short blockAlign = (short)(channels * 2);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + dataLen);
        w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);
        w.Write((ushort)1);
        w.Write((ushort)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((ushort)blockAlign);
        w.Write((ushort)16);
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(dataLen);
        for (int i = 0; i < frames; i++)
        {
            short value = (short)((i % 1000) * 30);
            for (int c = 0; c < channels; c++) w.Write(value);
        }
        return path;
    }
}
