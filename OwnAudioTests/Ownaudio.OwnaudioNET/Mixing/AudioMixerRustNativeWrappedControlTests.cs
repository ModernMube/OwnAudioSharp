using System;
using System.IO;
using FluentAssertions;
using Ownaudio.Audio.Tracks;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using OwnaudioNET.Synchronization;
using Xunit;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace Ownaudio.OwnaudioNET.Tests.Mixing;

/// <summary>
/// Control-plane coverage for effect-wrapped tracks on the Rust-native chain. Volume, pan, loop
/// and start offset used to be mirrored only for bare sources, so a <see cref="SourceWithEffects"/>
/// fell through every branch of the sync tick and its gain never reached the native track.
/// </summary>
[Collection("RustNativeChain")]
public sealed class AudioMixerRustNativeWrappedControlTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int MixerBufferFrames = 512;

    private readonly bool? _priorOverride;
    private readonly IAudioEngine _engine;
    private readonly AudioConfig _config;
    private readonly string _wavPath;

    public AudioMixerRustNativeWrappedControlTests()
    {
        _priorOverride = RustNativeChain.Override;
        RustNativeChain.Override = true;

        _config = new AudioConfig { SampleRate = SampleRate, Channels = Channels, BufferSize = MixerBufferFrames };
        _engine = AudioEngineFactory.CreateMockEngine(_config);
        _wavPath = _writeTempWav(SampleRate);
    }

    public void Dispose()
    {
        RustNativeChain.Override = _priorOverride;
        _engine.Dispose();
        try { if (File.Exists(_wavPath)) File.Delete(_wavPath); } catch { }
    }

    [Fact]
    public void WrappedFileSource_MirrorsVolumeAndPanOntoNativeTrack()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);

        var fileSource = new FileSource(_wavPath);
        using var wrapped = new SourceWithEffects(fileSource);
        mixer.AddSource(wrapped).Should().BeTrue();

        wrapped.Volume = 0.25f;
        wrapped.Pan = -0.5f;
        mixer.SyncRustControlStateOnce();

        AudioTrack track = fileSource.RustTrack!;
        track.Gain.Should().BeApproximately(0.25f, 1e-4f, "the wrapper's volume has to reach the native track");
        track.Pan.Should().BeApproximately(-0.5f, 1e-4f);
    }

    [Fact]
    public void WrappedFileSource_MirrorsLoopOntoNativeFileTrack()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);

        var fileSource = new FileSource(_wavPath);
        using var wrapped = new SourceWithEffects(fileSource);
        mixer.AddSource(wrapped);

        wrapped.Loop = true;
        mixer.SyncRustControlStateOnce();

        fileSource.RustFileTrack!.Loop.Should().BeTrue();
    }

    [Fact]
    public void WrappedSampleSource_MirrorsVolumeOntoNativeTrack()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);

        var samples = new float[SampleRate * Channels];
        var sampleSource = new SampleSource(samples, _config);
        using var wrapped = new SourceWithEffects(sampleSource);
        mixer.AddSource(wrapped);

        wrapped.Volume = 0.7f;
        mixer.SyncRustControlStateOnce();

        sampleSource.RustTrack!.Gain.Should().BeApproximately(0.7f, 1e-4f);
    }

    [Fact]
    public void WrappedFileSource_RoutesChannelMapSetOnTheWrapper()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);

        var fileSource = new FileSource(_wavPath);
        using var wrapped = new SourceWithEffects(fileSource);
        wrapped.RouteToChannels(2, 3);

        mixer.AddSource(wrapped);

        wrapped.OutputChannelMapping.Should().Equal(2, 3);
        fileSource.OutputChannelMapping.Should().Equal(new[] { 2, 3 }, "the wrapper writes the map onto the inner source");
    }

    [Fact]
    public void WrappedFileSource_LiveChannelMapEdit_IsPickedUpByTheTick()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);

        var fileSource = new FileSource(_wavPath);
        using var wrapped = new SourceWithEffects(fileSource);
        mixer.AddSource(wrapped);

        wrapped.OutputChannelMapping = new[] { 1, 0 };
        mixer.Invoking(m => m.SyncRustChannelMapsOnce()).Should().NotThrow();

        wrapped.OutputChannelMapping.Should().Equal(1, 0);
    }

    [Fact]
    public void WrappedFileSource_TempoAndPitchReachTheNativeTrack()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);

        var fileSource = new FileSource(_wavPath);
        using var wrapped = new SourceWithEffects(fileSource);
        mixer.AddSource(wrapped);

        wrapped.Tempo = 1.1f;
        wrapped.PitchShift = 3.0f;

        fileSource.RustTrack!.Tempo.Should().BeApproximately(1.1f, 1e-4f);
        fileSource.RustTrack!.PitchSemitones.Should().BeApproximately(3.0f, 1e-4f);
    }

    [Fact]
    public void MixerSeek_RepositionsAWrappedTrackByItsStartOffset()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);

        var fileSource = new FileSource(_wavPath);
        using var wrapped = new SourceWithEffects(fileSource);
        mixer.AddSource(wrapped);

        wrapped.StartOffset = 0.2;
        mixer.Seek(0.5);

        fileSource.Position.Should().BeApproximately(0.3, 0.02, "content time is project time minus the start offset");
    }

    [Fact]
    public void WrappedFileSource_LiveStartOffsetEdit_LandsOnTheTrack()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);

        var fileSource = new FileSource(_wavPath);
        using var wrapped = new SourceWithEffects(fileSource);
        mixer.AddSource(wrapped);

        mixer.Seek(0.6);
        wrapped.StartOffset = 0.4;
        mixer.SyncRustStartOffsetsOnce();

        fileSource.Position.Should().BeApproximately(0.2, 0.02, "the changed offset is re-applied without an explicit seek");
    }

    [Fact]
    public void AddSource_AttachesWrappedSourceToMasterClock()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);

        var fileSource = new FileSource(_wavPath);
        using var wrapped = new SourceWithEffects(fileSource);

        wrapped.IsAttachedToClock.Should().BeFalse();
        mixer.AddSource(wrapped);

        wrapped.SupportsMasterClock.Should().BeTrue();
        wrapped.IsAttachedToClock.Should().BeTrue("an effect-wrapped track must ride the master clock too");
        fileSource.IsSynchronized.Should().BeTrue();

        mixer.RemoveSource(wrapped);
        wrapped.IsAttachedToClock.Should().BeFalse("removing detaches the inner source as well");
    }

    [Fact]
    public void StartOffset_RoundTripsThroughTheWrapper()
    {
        var fileSource = new FileSource(_wavPath);
        using var wrapped = new SourceWithEffects(fileSource);

        wrapped.StartOffset = 1.25;

        fileSource.StartOffset.Should().Be(1.25);
        wrapped.StartOffset.Should().Be(1.25);
    }

    [Fact]
    public void ClockCalls_OnANonClockInnerSource_AreIgnored()
    {
        using var inner = new PlainSource(_config);
        using var wrapped = new SourceWithEffects(inner);

        wrapped.SupportsMasterClock.Should().BeFalse();
        wrapped.IsAttachedToClock.Should().BeFalse();
        wrapped.StartOffset.Should().Be(0.0);

        var clock = new MasterClock(SampleRate, Channels);
        wrapped.Invoking(w => w.AttachToClock(clock)).Should().NotThrow();
        wrapped.Invoking(w => w.StartOffset = 2.0).Should().NotThrow();
        wrapped.StartOffset.Should().Be(0.0);
    }

    [Fact]
    public void ReadSamplesAtTime_RunsTheEffectChain()
    {
        using var inner = new PlainSource(_config);
        using var wrapped = new SourceWithEffects(inner);
        wrapped.AddEffect(new HalfGainEffect());

        var buffer = new float[128 * Channels];
        wrapped.ReadSamplesAtTime(0.0, buffer, 128, out ReadResult result).Should().BeTrue();

        result.FramesRead.Should().Be(128);
        buffer[0].Should().BeApproximately(0.5f, 1e-5f, "the fx chain must run on the clock-aligned path too");
    }

    private static string _writeTempWav(int frames)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ownaudio_wrapctl_{Guid.NewGuid():N}.wav");

        int dataLen = frames * Channels * 2;
        short blockAlign = (short)(Channels * 2);

        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + dataLen);
            w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((ushort)1);
            w.Write((ushort)Channels);
            w.Write(SampleRate);
            w.Write(SampleRate * Channels * 2);
            w.Write((ushort)blockAlign);
            w.Write((ushort)16);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(dataLen);

            for (int i = 0; i < frames; i++)
            {
                short value = (short)((i % 1000) * 30);
                for (int c = 0; c < Channels; c++) w.Write(value);
            }
        }

        return path;
    }
}

/// <summary>
/// Bare IAudioSource that can't ride a master clock — the wrapper's fallback path needs one.
/// Hands back a constant 1.0 so an effect's work on the buffer is trivially visible.
/// </summary>
internal sealed class PlainSource : IAudioSource
{
    public PlainSource(AudioConfig config)
    {
        Config = config;
        StreamInfo = new AudioStreamInfo(config.Channels, config.SampleRate, TimeSpan.FromSeconds(1));
    }

    public Guid Id { get; } = Guid.NewGuid();
    public AudioState State => AudioState.Playing;
    public AudioConfig Config { get; }
    public AudioStreamInfo StreamInfo { get; }
    public float Volume { get; set; } = 1.0f;
    public float Pan { get; set; }
    public bool Loop { get; set; }
    public double Position => 0.0;
    public double Duration => 1.0;
    public bool IsEndOfStream => false;
    public float Tempo { get; set; } = 1.0f;
    public float PitchShift { get; set; }

    public int ReadSamples(Span<float> buffer, int frameCount)
    {
        buffer.Slice(0, frameCount * Config.Channels).Fill(1.0f);
        return frameCount;
    }

    public bool Seek(double positionInSeconds) => true;
    public void Play() { }
    public void Pause() { }
    public void Stop() { }
    public void Dispose() { }

#pragma warning disable CS0067
    public event EventHandler<AudioStateChangedEventArgs>? StateChanged;
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;
    public event EventHandler<AudioErrorEventArgs>? Error;
#pragma warning restore CS0067
}

/// <summary>
/// Halves everything it's given, so a single read tells us whether the chain ran.
/// </summary>
internal sealed class HalfGainEffect : IEffectProcessor
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Name => "HalfGain";
    public bool Enabled { get; set; } = true;
    public float Mix { get; set; } = 1.0f;

    public void Initialize(AudioConfig config) { }

    public void Process(Span<float> buffer, int frameCount)
    {
        for (int i = 0; i < buffer.Length; i++) buffer[i] *= 0.5f;
    }

    public void Reset() { }
    public void Dispose() { }
}
