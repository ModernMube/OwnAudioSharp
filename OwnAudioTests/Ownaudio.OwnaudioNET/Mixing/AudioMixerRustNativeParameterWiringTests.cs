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
/// Covers the source parameters that used to stop at the managed object: tempo/pitch on the
/// non-file sources, pan on a standalone backend, and the PlaybackEnded latch the control tick
/// drives now that sources pick up the native EOS.
/// </summary>
/// <remarks>
/// Mock engine, no device — control plane only. Whether the native track actually stretches is
/// the Rust side's business and is covered there.
/// </remarks>
[Collection("RustNativeChain")]
public sealed class AudioMixerRustNativeParameterWiringTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int MixerBufferFrames = 512;

    private readonly bool? _priorOverride;
    private readonly IAudioEngine _engine;
    private readonly AudioConfig _config;

    public AudioMixerRustNativeParameterWiringTests()
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

    private SampleSource _createSampleSource()
    {
        var _samples = new float[SampleRate * Channels];
        for (int i = 0; i < _samples.Length; i++) _samples[i] = 0.25f;

        return new SampleSource(_samples, _config);
    }

    private StreamingSource _createStreamingSource()
    {
        return new StreamingSource((buffer, frames, startFrame) => buffer.Clear(), _config);
    }

    [Fact]
    public void SampleSource_TempoAndPitch_ReachTheNativeTrack()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        using var source = _createSampleSource();
        mixer.AddSource(source);

        source.Tempo = 1.15f;
        source.PitchShift = -4.0f;

        source.RustTrack!.Tempo.Should().BeApproximately(1.15f, 1e-4f);
        source.RustTrack!.PitchSemitones.Should().BeApproximately(-4.0f, 1e-4f);
    }

    [Fact]
    public void StreamingSource_TempoAndPitch_ReachTheNativeTrack()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        using var source = _createStreamingSource();
        mixer.AddSource(source);

        source.Tempo = 0.9f;
        source.PitchShift = 7.0f;

        source.RustTrack!.Tempo.Should().BeApproximately(0.9f, 1e-4f);
        source.RustTrack!.PitchSemitones.Should().BeApproximately(7.0f, 1e-4f);
    }

    [Fact]
    public void SampleSource_TempoSetBeforeAttach_LandsOnAttach()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        using var source = _createSampleSource();
        source.Tempo = 1.2f;

        mixer.AddSource(source);

        source.RustTrack!.Tempo.Should().BeApproximately(1.2f, 1e-4f);
    }

    [Fact]
    public void StandaloneSampleSource_AppliesPan()
    {
        using var source = _createSampleSource();
        source.Pan = -0.6f;

        source.EnsureStandaloneRustBackend();

        source.RustTrack!.Pan.Should().BeApproximately(-0.6f, 1e-4f);
    }

    [Fact]
    public void PlaybackEnded_FiresOnceWhenEveryStoppedSourceIsDone()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        using var source = _createSampleSource();
        mixer.AddSource(source);

        int _raised = 0;
        mixer.PlaybackEnded += (_, _) => _raised++;

        //Nothing has finished yet, so the tick must stay quiet
        mixer.PollRustPlaybackEndedOnce();
        _raised.Should().Be(0);

        source.MarkEndOfStreamForTests();

        mixer.PollRustPlaybackEndedOnce();
        mixer.PollRustPlaybackEndedOnce();

        _raised.Should().Be(1, "the latch has to hold across ticks");
    }

    [Fact]
    public void PlaybackEnded_RearmsAfterTheSourcePlaysAgain()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        using var source = _createSampleSource();
        mixer.AddSource(source);

        int _raised = 0;
        mixer.PlaybackEnded += (_, _) => _raised++;

        source.MarkEndOfStreamForTests();
        mixer.PollRustPlaybackEndedOnce();

        source.Play();
        mixer.PollRustPlaybackEndedOnce();

        source.MarkEndOfStreamForTests();
        mixer.PollRustPlaybackEndedOnce();

        _raised.Should().Be(2);
    }
}
