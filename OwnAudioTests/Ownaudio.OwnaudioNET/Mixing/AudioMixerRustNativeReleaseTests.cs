using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Ownaudio.Core;
using Ownaudio.Safe.Exceptions;
using OwnaudioNET.Effects;
using OwnaudioNET.Engine;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Mixing;
using Xunit;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace Ownaudio.OwnaudioNET.Tests.Mixing;

/// <summary>
/// When a removed effect may be disposed. The mock engine never drains the command queue, which
/// is what lets these fill it; how many blocks a running output waits is the app soak's to see.
/// </summary>
[Collection("RustNativeChain")]
public sealed class AudioMixerRustNativeReleaseTests : IDisposable
{
    private readonly bool? _priorOverride;
    private readonly IAudioEngine _engine;

    public AudioMixerRustNativeReleaseTests()
    {
        _priorOverride = RustNativeChain.Override;
        RustNativeChain.Override = true;

        _engine = AudioEngineFactory.CreateMockEngine(new AudioConfig { SampleRate = 48000, Channels = 2, BufferSize = 512 });
    }

    public void Dispose()
    {
        RustNativeChain.Override = _priorOverride;
        _engine.Dispose();
    }

    [Fact]
    public async Task ARemovedEffectWithNoOutputIsReleasedAtOnce()
    {
        using var mixer = new AudioMixer(_engine, 512);
        var effect = new CompressorEffect();
        mixer.AddMasterEffect(effect);

        mixer.RemoveMasterEffect(effect);
        var wait = mixer.WhenEffectReleasedAsync(effect);

        (await Task.WhenAny(wait, Task.Delay(2000))).Should().BeSameAs(wait);
        await wait;
    }

    [Fact]
    public async Task AnEffectStillOnTheMasterIsWaitedFor()
    {
        using var mixer = new AudioMixer(_engine, 512);
        var effect = new CompressorEffect();
        mixer.AddMasterEffect(effect);

        using var cts = new CancellationTokenSource(150);

        await mixer.Awaiting(m => m.WhenEffectReleasedAsync(effect, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// The removal used to be dropped with a warning, the twin left on the chain for good — and a
    /// VST3 behind it unloaded under the audio thread.
    /// </summary>
    [Fact]
    public async Task ARemovalAFullQueueRefusedIsOwedAndWaitedFor()
    {
        using var mixer = new AudioMixer(_engine, 512);
        var effects = new List<IEffectProcessor>();

        try
        {
            while (true)
            {
                var effect = new CompressorEffect();
                mixer.AddMasterEffect(effect);
                effects.Add(effect);
            }
        }
        catch (CommandQueueFullException) { }

        effects.Should().NotBeEmpty();
        var first = effects[0];

        mixer.Invoking(m => m.RemoveMasterEffect(first)).Should().NotThrow();

        using var cts = new CancellationTokenSource(150);
        await mixer.Awaiting(m => m.WhenEffectReleasedAsync(first, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>("the twin is still on the chain until the queue takes the removal");
    }

    /// <summary>
    /// The count-in start parks the clock on the song's start point, as the plain one does; the
    /// silence itself is the native track's (start_silence in track.rs).
    /// </summary>
    [Fact]
    public void APreRolledStartParksTheClockOnTheStartPoint()
    {
        using var mixer = new AudioMixer(_engine, 512);

        mixer.StartPreparedSources(12.5, TimeSpan.FromSeconds(2));

        mixer.MasterClock.CurrentTimestamp.Should().Be(12.5);
    }

    [Fact]
    public void TheManagedMixRefusesAPreRoll()
    {
        RustNativeChain.Override = false;
        using var mixer = new AudioMixer(_engine, 512);

        mixer.Invoking(m => m.StartPreparedSources(0.0, TimeSpan.FromSeconds(2))).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public async Task TheManagedMixRefusesToSay()
    {
        RustNativeChain.Override = false;
        using var mixer = new AudioMixer(_engine, 512);

        await mixer.Awaiting(m => m.WhenEffectReleasedAsync(new CompressorEffect()))
            .Should().ThrowAsync<NotSupportedException>();
    }
}
