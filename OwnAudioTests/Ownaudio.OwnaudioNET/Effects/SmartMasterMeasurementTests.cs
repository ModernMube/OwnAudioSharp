using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Ownaudio.Core;
using OwnaudioNET.Effects.SmartMaster;
using OwnaudioNET.Effects.SmartMaster.Components;
using OwnaudioNET.Engine;
using OwnaudioNET.Mixing;
using Xunit;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace Ownaudio.Test.OwnaudioNET.Effects
{
    /// <summary>
    /// The room measurement without a room: the analyzer against its own reference, and the
    /// mixer wiring the measurement plays and records through.
    /// </summary>
    [Collection("RustNativeChain")]
    public sealed class SmartMasterMeasurementTests : IDisposable
    {
        private const int SampleRate = 48000;
        private const int Channels = 2;
        private const int BufferFrames = 512;

        private readonly bool? _priorOverride;
        private readonly IAudioEngine _engine;

        public SmartMasterMeasurementTests()
        {
            _priorOverride = RustNativeChain.Override;
            RustNativeChain.Override = true;

            _engine = AudioEngineFactory.CreateMockEngine(
                new AudioConfig { SampleRate = SampleRate, Channels = Channels, BufferSize = BufferFrames });
        }

        public void Dispose()
        {
            RustNativeChain.Override = _priorOverride;
            _engine.Dispose();
        }

        [Fact]
        public void Analyzer_StreamedPinkNoise_MatchesTheReferenceRun()
        {
            var reference = new float[SampleRate * 8];
            new PinkNoise().Render(reference, 1, -1);
            float[] _expected = new SmartMasterSpectrumAnalyzer(SampleRate).AnalyzeBuffer(reference);

            var analyzer = new SmartMasterSpectrumAnalyzer(SampleRate);
            var noise = new PinkNoise();
            var block = new float[1920 * Channels];

            for (int i = 0; i < SampleRate * 8 / 1920; i++)
            {
                noise.Render(block, Channels, -1);
                analyzer.Push(block, Channels);
            }

            var measured = new float[SmartMasterConfig.EqBands];
            analyzer.Average(measured).Should().BeGreaterThan(50);

            for (int b = 0; b < measured.Length; b++)
                measured[b].Should().BeApproximately(_expected[b], 2f, $"band {b} is the same noise through the same analyzer");
        }

        [Fact]
        public void Analyzer_Sine_LandsInItsOwnBand()
        {
            var analyzer = new SmartMasterSpectrumAnalyzer(SampleRate);
            var sine = new float[SampleRate];
            for (int i = 0; i < sine.Length; i++) sine[i] = 0.5f * MathF.Sin(2f * MathF.PI * 1000f * i / SampleRate);

            analyzer.Push(sine, 1);

            var bands = new float[SmartMasterConfig.EqBands];
            analyzer.Average(bands);

            Array.IndexOf(bands, bands.Max()).Should().Be(17, "band 17 is centred on 1kHz");
        }

        [Fact]
        public void Analyzer_NoFullWindowYet_ReportsNothing()
        {
            var analyzer = new SmartMasterSpectrumAnalyzer(SampleRate);
            analyzer.Push(new float[1000], 1);

            analyzer.Average(new float[SmartMasterConfig.EqBands]).Should().Be(0);
        }

        [Fact]
        public void PinkNoise_ChannelMask_FeedsOnlyThatChannel()
        {
            var block = new float[256 * Channels];
            new PinkNoise().Render(block, Channels, 1);

            for (int i = 0; i < block.Length; i += Channels)
                block[i].Should().Be(0f);

            block.Should().Contain(v => v != 0f);
        }

        [Fact]
        public async Task Measurement_OffTheBus_FailsWithAReason()
        {
            using var effect = new SmartMasterEffect();
            effect.Initialize(new AudioConfig { SampleRate = SampleRate, Channels = Channels, BufferSize = BufferFrames });

            await effect.StartMeasurementAsync();

            effect.GetMeasurementStatus().ErrorMessage.Should().Contain("AddMasterEffect");
            effect.Enabled.Should().BeTrue("the bypass during the measurement is undone");
        }

        [Fact]
        public async Task Measurement_StoppedMixer_FailsWithoutTouchingTheConfig()
        {
            using var mixer = new AudioMixer(_engine, BufferFrames);
            var effect = new SmartMasterEffect();
            mixer.AddMasterEffect(effect);

            effect.GetConfiguration().GraphicEQGains[5] = 3.5f;

            await effect.StartMeasurementAsync();

            effect.GetMeasurementStatus().ErrorMessage.Should().Contain("not running");
            effect.GetConfiguration().GraphicEQGains[5].Should().Be(3.5f, "a failed measurement leaves the live settings alone");
        }

        [Fact]
        public async Task Measurement_AfterRemoveMasterEffect_ForgetsTheMixer()
        {
            using var mixer = new AudioMixer(_engine, BufferFrames);
            using var effect = new SmartMasterEffect();
            mixer.AddMasterEffect(effect);
            mixer.RemoveMasterEffect(effect);

            await effect.StartMeasurementAsync();

            effect.GetMeasurementStatus().ErrorMessage.Should().Contain("AddMasterEffect");
        }
    }
}
