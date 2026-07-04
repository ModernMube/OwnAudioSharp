using System;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Ownaudio.Core;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using Xunit;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace Ownaudio.OwnaudioNET.Tests.Characterization;

/// <summary>
/// WS0 (plan 14 / D.0b) golden-master characterization of the legacy <see cref="AudioMixer"/>
/// multitrack behavior — specifically the two properties the Rust-native <c>MultiTrackMixer</c>
/// must reproduce for behavioral parity: sample-locked (drift-free) consumption of all sources,
/// and additive summation of source signals. Uses the mock engine and deterministic constant
/// <see cref="SampleSource"/> inputs so the assertions are hardware-free and stable.
/// </summary>
[Collection("RustNativeChain")]
public sealed class AudioMixerCharacterizationTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int MixerBufferFrames = 512;

    private readonly AudioConfig _config = new()
    {
        SampleRate = SampleRate,
        Channels = Channels,
        BufferSize = MixerBufferFrames
    };

    private readonly bool? _priorRustNativeOverride;
    private IAudioEngine? _engine;
    private AudioMixer? _mixer;
    private SampleSource? _source1;
    private SampleSource? _source2;

    public AudioMixerCharacterizationTests()
    {
        // This is a golden master of the legacy managed mix path (MixThread peak/drift metering),
        // which the Rust-native chain (default as of 4.0) bypasses — pin legacy for these tests.
        _priorRustNativeOverride = global::OwnaudioNET.Engine.RustNativeChain.Override;
        global::OwnaudioNET.Engine.RustNativeChain.Override = false;
    }

    public void Dispose()
    {
        _mixer?.Dispose();
        _engine?.Dispose();
        _source1?.Dispose();
        _source2?.Dispose();
        global::OwnaudioNET.Engine.RustNativeChain.Override = _priorRustNativeOverride;
    }

    /// <summary>
    /// Builds a constant-amplitude source of the given duration and steady value.
    /// </summary>
    private SampleSource CreateConstantSource(double durationSeconds, float value)
    {
        int totalSamples = (int)(durationSeconds * SampleRate) * Channels;
        var samples = Enumerable.Repeat(value, totalSamples).ToArray();
        var source = new SampleSource(samples, _config) { Volume = 1.0f };
        return source;
    }

    /// <summary>
    /// All sources are consumed in lockstep by the mixer: every mix block reads the same frame
    /// count from each source, so their reported positions advance together and never drift apart
    /// by more than a single mix block, at any observation point during a run.
    /// </summary>
    [Fact]
    public void MultipleSources_StaySampleLocked_WithoutDrift()
    {
        _engine = AudioEngineFactory.CreateMockEngine(_config);
        _mixer = new AudioMixer(_engine, MixerBufferFrames);

        _source1 = CreateConstantSource(durationSeconds: 3.0, value: 0.3f);
        _source2 = CreateConstantSource(durationSeconds: 3.0, value: 0.3f);
        _source1.Play();
        _source2.Play();

        _mixer.AddSource(_source1);
        _mixer.AddSource(_source2);
        _mixer.Start();

        // One mix block is MixerBufferFrames/SampleRate seconds; allow up to ~1.5 blocks of
        // read-interleave skew when sampling the two positions from outside the mix thread.
        double blockSeconds = (double)MixerBufferFrames / SampleRate;
        double driftTolerance = blockSeconds * 1.5;

        double previousMin = 0.0;
        for (int i = 0; i < 3; i++)
        {
            Thread.Sleep(100);

            double pos1 = _source1.Position;
            double pos2 = _source2.Position;

            Math.Abs(pos1 - pos2).Should().BeLessThan(driftTolerance,
                because: "the mixer consumes every source by the same frame count per block (sample-locked)");

            double currentMin = Math.Min(pos1, pos2);
            currentMin.Should().BeGreaterThan(previousMin,
                because: "both sources must keep advancing while the mixer runs");
            previousMin = currentMin;
        }
    }

    /// <summary>
    /// The mixer combines sources additively: two constant sources of 0.4 and 0.3 produce a mix
    /// whose measured peak equals their sum (0.7), staying below the [-1, 1] limiter ceiling.
    /// </summary>
    [Fact]
    public void TwoConstantSources_SumAdditively_InThePeakLevel()
    {
        _engine = AudioEngineFactory.CreateMockEngine(_config);
        _mixer = new AudioMixer(_engine, MixerBufferFrames);

        _source1 = CreateConstantSource(durationSeconds: 2.0, value: 0.4f);
        _source2 = CreateConstantSource(durationSeconds: 2.0, value: 0.3f);
        _source1.Play();
        _source2.Play();

        _mixer.AddSource(_source1);
        _mixer.AddSource(_source2);
        _mixer.Start();

        Thread.Sleep(120);

        _mixer.LeftPeak.Should().BeApproximately(0.7f, 0.02f,
            because: "additive mixing sums 0.4 + 0.3 = 0.7 below the limiter ceiling");
        _mixer.RightPeak.Should().BeApproximately(0.7f, 0.02f,
            because: "additive mixing is symmetric across channels");
    }

    /// <summary>
    /// A single source passes through the mixer at its own amplitude, establishing the additive
    /// baseline: adding a second source strictly increases the mixed peak.
    /// </summary>
    [Fact]
    public void AddingASecondSource_IncreasesTheMixedPeak()
    {
        _engine = AudioEngineFactory.CreateMockEngine(_config);
        _mixer = new AudioMixer(_engine, MixerBufferFrames);

        _source1 = CreateConstantSource(durationSeconds: 3.0, value: 0.3f);
        _source1.Play();
        _mixer.AddSource(_source1);
        _mixer.Start();

        Thread.Sleep(120);
        float singleSourcePeak = _mixer.LeftPeak;
        singleSourcePeak.Should().BeApproximately(0.3f, 0.02f,
            because: "one 0.3 source passes through at its own amplitude");

        _source2 = CreateConstantSource(durationSeconds: 3.0, value: 0.3f);
        _source2.Play();
        _mixer.AddSource(_source2);

        Thread.Sleep(120);
        float twoSourcePeak = _mixer.LeftPeak;

        twoSourcePeak.Should().BeGreaterThan(singleSourcePeak + 0.1f,
            because: "a second source is added into the mix, raising the peak toward 0.6");
    }
}
