using System;
using FluentAssertions;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Sources;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.Sources;

/// <summary>
/// The wrapper used to swallow the sync group and the meters: wrapping a source in effects
/// dropped ISynchronizable and OutputLevels off the surface. These pin the delegation down,
/// including the case where the inner source is a hand-rolled IAudioSource that has neither.
/// </summary>
[Collection("RustNativeChain")]
public sealed class SourceWithEffectsDelegationTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly bool? _priorOverride;
    private readonly AudioConfig _config;

    public SourceWithEffectsDelegationTests()
    {
        _priorOverride = RustNativeChain.Override;
        RustNativeChain.Override = true;

        _config = new AudioConfig { SampleRate = SampleRate, Channels = Channels, BufferSize = 512 };
    }

    public void Dispose() => RustNativeChain.Override = _priorOverride;

    private SampleSource _createSampleSource()
    {
        var _samples = new float[SampleRate * Channels];
        return new SampleSource(_samples, _config);
    }

    [Fact]
    public void Wrapper_IsSynchronizable()
    {
        using var wrapped = new SourceWithEffects(_createSampleSource());

        wrapped.Should().BeAssignableTo<ISynchronizable>();
        wrapped.SupportsSyncGroup.Should().BeTrue();
    }

    [Fact]
    public void SyncGroupId_GoesThroughToTheInnerSource()
    {
        var inner = _createSampleSource();
        using var wrapped = new SourceWithEffects(inner);

        wrapped.SyncGroupId = "stems";
        wrapped.IsSynchronized = true;

        inner.SyncGroupId.Should().Be("stems");
        inner.IsSynchronized.Should().BeTrue();
        wrapped.SyncGroupId.Should().Be("stems");
    }

    [Fact]
    public void OutputLevels_ComeFromTheInnerSource()
    {
        var inner = _createSampleSource();
        using var wrapped = new SourceWithEffects(inner);

        inner.SetOutputLevels((0.4f, 0.7f));

        wrapped.OutputLevels.left.Should().BeApproximately(0.4f, 1e-4f);
        wrapped.OutputLevels.right.Should().BeApproximately(0.7f, 1e-4f);
    }

    [Fact]
    public void PositionChanged_SubscribesOnTheInnerSource()
    {
        var inner = _createSampleSource();
        using var wrapped = new SourceWithEffects(inner);

        int _fired = 0;
        EventHandler _handler = (_, _) => _fired++;

        wrapped.PositionChanged += _handler;
        inner.RaisePositionChangedForTests();
        _fired.Should().Be(1);

        wrapped.PositionChanged -= _handler;
        inner.RaisePositionChangedForTests();
        _fired.Should().Be(1, "the unsubscribe has to reach the inner source too");
    }

    [Fact]
    public void PlainInnerSource_KeepsTheSurfaceQuiet()
    {
        using var wrapped = new SourceWithEffects(new BareSource(_config));

        wrapped.SupportsSyncGroup.Should().BeFalse();
        wrapped.SamplePosition.Should().Be(0L);
        wrapped.SyncGroupId.Should().BeNull();
        wrapped.IsSynchronized.Should().BeFalse();
        wrapped.OutputLevels.Should().Be((0f, 0f));

        //None of these can reach anything, but they must not blow up either
        wrapped.SyncGroupId = "nowhere";
        wrapped.IsSynchronized = true;
        wrapped.ResyncTo(1024);

        wrapped.IsSynchronized.Should().BeFalse();
    }

    /// <summary>
    /// Hand-rolled source: implements IAudioSource and nothing else.
    /// </summary>
    private sealed class BareSource : IAudioSource
    {
        private readonly AudioConfig _cfg;

        public BareSource(AudioConfig config) { _cfg = config; }

        public Guid Id { get; } = Guid.NewGuid();
        public AudioState State => AudioState.Stopped;
        public AudioConfig Config => _cfg;
        public AudioStreamInfo StreamInfo => new AudioStreamInfo(_cfg.Channels, _cfg.SampleRate, TimeSpan.Zero);
        public float Volume { get; set; } = 1.0f;
        public float Pan { get; set; }
        public bool Loop { get; set; }
        public double Position => 0.0;
        public double Duration => 0.0;
        public bool IsEndOfStream => false;
        public float Tempo { get; set; } = 1.0f;
        public float PitchShift { get; set; }

        public int ReadSamples(Span<float> buffer, int frameCount) => 0;
        public bool Seek(double positionInSeconds) => false;
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
}
