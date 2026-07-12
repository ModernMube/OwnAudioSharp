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

    public AudioMixerCharacterizationTests()
    {
        // This is a golden master of the legacy managed mix path (MixThread peak/drift metering),
        // which the Rust-native chain (default as of 4.0) bypasses — pin legacy for these tests.
        _priorRustNativeOverride = global::OwnaudioNET.Engine.RustNativeChain.Override;
        global::OwnaudioNET.Engine.RustNativeChain.Override = false;
    }

    public void Dispose()
    {
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

}
