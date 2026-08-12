using Ownaudio.Core;
using OwnaudioNET.Effects.SmartMaster;
using Xunit;
using System;

namespace Ownaudio.Test.OwnaudioNET.Effects
{
    /// <summary>
    /// Unit tests for SmartMasterEffect
    /// Tests gain staging, volume preservation, and preset configurations
    /// </summary>
    public class SmartMasterEffectTests
    {
        private const float SampleRate = 48000f;
        private const int Channels = 2;
        private const int FrameCount = 1024;

        /// <summary>
        /// Helper to create a test audio buffer with a sine wave
        /// </summary>
        private float[] CreateTestBuffer(float frequency, float amplitude, int frameCount, int channels)
        {
            float[] buffer = new float[frameCount * channels];
            for (int i = 0; i < frameCount; i++)
            {
                float sample = amplitude * MathF.Sin(2.0f * MathF.PI * frequency * i / SampleRate);
                for (int ch = 0; ch < channels; ch++)
                {
                    buffer[i * channels + ch] = sample;
                }
            }
            return buffer;
        }

        /// <summary>
        /// Calculate RMS (Root Mean Square) level of a buffer
        /// </summary>
        private float CalculateRMS(Span<float> buffer)
        {
            float sum = 0.0f;
            for (int i = 0; i < buffer.Length; i++)
            {
                sum += buffer[i] * buffer[i];
            }
            return MathF.Sqrt(sum / buffer.Length);
        }

        /// <summary>
        /// RMS past the chain's look-ahead ramp-in. The output limiter really does
        /// delay now, so the first LatencySamples frames of the very first block are
        /// still the empty delay line and would drag a whole-buffer RMS down.
        /// </summary>
        private float CalculateSteadyRMS(float[] buffer, SmartMasterEffect effect)
        {
            int skip = Math.Min(effect.LatencySamples * Channels, buffer.Length - Channels);
            return CalculateRMS(buffer.AsSpan(skip));
        }

        /// <summary>
        /// Calculate peak level of a buffer
        /// </summary>
        private float CalculatePeak(Span<float> buffer)
        {
            float peak = 0.0f;
            for (int i = 0; i < buffer.Length; i++)
            {
                float abs = MathF.Abs(buffer[i]);
                if (abs > peak)
                    peak = abs;
            }
            return peak;
        }

        /// <summary>
        /// Convert linear amplitude to dB
        /// </summary>
        private float LinearToDb(float linear)
        {
            return 20.0f * MathF.Log10(Math.Max(linear, 1e-6f));
        }

        [Fact]
        public void SmartMasterEffect_Initialization_ShouldSucceed()
        {
            using var effect = new SmartMasterEffect();
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };
            effect.Initialize(config);

            Assert.NotNull(effect);
            Assert.True(effect.Enabled);
            Assert.Equal("SmartMaster", effect.Name);
        }

        [Fact]
        public void SmartMasterEffect_DefaultPreset_ShouldNotReduceVolumeDrastically()
        {
            using var effect = new SmartMasterEffect();
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };
            effect.Initialize(config);

            effect.LoadSpeakerPreset(SpeakerType.Default);

            float amplitude = 0.25f;
            float[] buffer = CreateTestBuffer(1000f, amplitude, FrameCount, Channels);
            float inputRMS = CalculateRMS(buffer);
            float inputDb = LinearToDb(inputRMS);

            effect.Process(buffer, FrameCount);

            float outputRMS = CalculateSteadyRMS(buffer, effect);
            float outputDb = LinearToDb(outputRMS);
            float volumeLoss = inputDb - outputDb;

            Assert.True(volumeLoss < 1.0f, 
                $"Default preset caused {volumeLoss:F2} dB volume loss (Input: {inputDb:F2} dB, Output: {outputDb:F2} dB)");
        }

        [Fact]
        public void SmartMasterEffect_HiFiPreset_ShouldPreserveVolume()
        {
            using var effect = new SmartMasterEffect();
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };
            effect.Initialize(config);

            effect.LoadSpeakerPreset(SpeakerType.HiFi);

            float amplitude = 0.25f;
            float[] buffer = CreateTestBuffer(1000f, amplitude, FrameCount, Channels);
            float inputRMS = CalculateRMS(buffer);
            float inputDb = LinearToDb(inputRMS);

            effect.Process(buffer, FrameCount);

            float outputRMS = CalculateRMS(buffer);
            float outputDb = LinearToDb(outputRMS);
            float volumeLoss = inputDb - outputDb;

            Assert.True(volumeLoss < 2.0f, 
                $"HiFi preset caused {volumeLoss:F2} dB volume loss (Input: {inputDb:F2} dB, Output: {outputDb:F2} dB)");
            
            Assert.True(volumeLoss > -3.0f, 
                $"HiFi preset caused {-volumeLoss:F2} dB volume boost (Input: {inputDb:F2} dB, Output: {outputDb:F2} dB)");
        }

        [Fact]
        public void SmartMasterEffect_AllPresets_ShouldNotCauseExcessiveVolumeLoss()
        {
            var presets = new[] 
            { 
                SpeakerType.Default, 
                SpeakerType.HiFi, 
                SpeakerType.Headphone, 
                SpeakerType.Studio, 
                SpeakerType.Club, 
                SpeakerType.Concert 
            };

            foreach (var preset in presets)
            {
                using var effect = new SmartMasterEffect();
                var config = new AudioConfig
                {
                    SampleRate = (int)SampleRate,
                    Channels = Channels,
                    BufferSize = 512
                };
                effect.Initialize(config);
                effect.LoadSpeakerPreset(preset);

                float amplitude = 0.25f;
                float[] buffer = CreateTestBuffer(1000f, amplitude, FrameCount, Channels);
                float inputRMS = CalculateRMS(buffer);
                float inputDb = LinearToDb(inputRMS);

                effect.Process(buffer, FrameCount);

                float outputRMS = CalculateRMS(buffer);
                float outputDb = LinearToDb(outputRMS);
                float volumeLoss = inputDb - outputDb;

                Assert.True(volumeLoss < 3.0f, 
                    $"{preset} preset caused {volumeLoss:F2} dB volume loss (Input: {inputDb:F2} dB, Output: {outputDb:F2} dB)");
            }
        }

        [Fact]
        public void SmartMasterEffect_ClubPreset_ShouldHandleHighLevelSignals()
        {
            using var effect = new SmartMasterEffect();
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };
            effect.Initialize(config);
            effect.LoadSpeakerPreset(SpeakerType.Club);

            float amplitude = 0.7f;
            float[] buffer = CreateTestBuffer(1000f, amplitude, FrameCount, Channels);
            float inputPeak = CalculatePeak(buffer);

            effect.Process(buffer, FrameCount);

            float outputPeak = CalculatePeak(buffer);
            
            Assert.True(outputPeak <= 1.0f, 
                $"Club preset caused clipping: peak = {outputPeak:F3}");
            
            Assert.True(outputPeak > 0.5f, 
                $"Club preset reduced signal too much: peak = {outputPeak:F3}");
        }

        [Fact]
        public void SmartMasterEffect_WithSubharmonics_ShouldNotDistort()
        {
            using var effect = new SmartMasterEffect();
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };
            effect.Initialize(config);
            
            effect.LoadSpeakerPreset(SpeakerType.Club);

            float amplitude = 0.25f;
            float[] buffer = CreateTestBuffer(80f, amplitude, FrameCount, Channels);

            effect.Process(buffer, FrameCount);

            float outputPeak = CalculatePeak(buffer);
            
            Assert.True(outputPeak <= 1.0f, 
                $"Subharmonic synthesis caused clipping: peak = {outputPeak:F3}");
        }

        [Fact]
        public void SmartMasterEffect_Disabled_ShouldPassthrough()
        {
            using var effect = new SmartMasterEffect();
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };
            effect.Initialize(config);
            effect.LoadSpeakerPreset(SpeakerType.HiFi);
            
            effect.Enabled = false;

            float amplitude = 0.25f;
            float[] buffer = CreateTestBuffer(1000f, amplitude, FrameCount, Channels);
            float[] originalBuffer = new float[buffer.Length];
            Array.Copy(buffer, originalBuffer, buffer.Length);

            effect.Process(buffer, FrameCount);

            for (int i = 0; i < buffer.Length; i++)
            {
                Assert.Equal(originalBuffer[i], buffer[i], 5);
            }
        }

        [Fact]
        public void SmartMasterEffect_Reset_ShouldClearState()
        {
            using var effect = new SmartMasterEffect();
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };
            effect.Initialize(config);
            effect.LoadSpeakerPreset(SpeakerType.HiFi);

            float[] buffer1 = CreateTestBuffer(1000f, 0.5f, FrameCount, Channels);
            effect.Process(buffer1, FrameCount);

            effect.Reset();

            float[] buffer2 = CreateTestBuffer(1000f, 0.5f, FrameCount, Channels);
            effect.Process(buffer2, FrameCount);

            float rms1 = CalculateRMS(buffer1);
            float rms2 = CalculateRMS(buffer2);
            
            float diff = MathF.Abs(LinearToDb(rms1) - LinearToDb(rms2));
            Assert.True(diff < 1.0f, 
                $"Reset did not clear state properly: RMS difference = {diff:F2} dB");
        }

        [Fact]
        public void SmartMasterEffect_LongDuration_ShouldMaintainStability()
        {
            using var effect = new SmartMasterEffect();
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };
            effect.Initialize(config);
            effect.LoadSpeakerPreset(SpeakerType.HiFi);

            //The opening blocks are the chain settling in, so we read after it arrived
            const int SettleBlocks = 10;
            float settledRMS = 0.0f;
            float lastBlockRMS = 0.0f;

            for (int block = 0; block < SettleBlocks + 20; block++)
            {
                float[] blockBuffer = CreateTestBuffer(1000f, 0.25f, FrameCount, Channels);
                effect.Process(blockBuffer, FrameCount);

                if (block == SettleBlocks)
                    settledRMS = CalculateSteadyRMS(blockBuffer, effect);
                if (block == SettleBlocks + 19)
                    lastBlockRMS = CalculateSteadyRMS(blockBuffer, effect);
            }

            float drift = MathF.Abs(LinearToDb(settledRMS) - LinearToDb(lastBlockRMS));
            Assert.True(drift < 0.5f,
                $"Effect showed instability over time: drift = {drift:F2} dB");
        }

        [Fact]
        public void SmartMasterEffect_ApplyConfiguration_ShouldRebuildTheChain()
        {
            using var effect = new SmartMasterEffect();
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };
            effect.Initialize(config);
            effect.LoadSpeakerPreset(SpeakerType.Default);

            float[] before = CreateTestBuffer(1000f, 0.25f, FrameCount, Channels);
            effect.Process(before, FrameCount);

            var masterConfig = effect.GetConfiguration();
            for (int i = 0; i < SmartMasterConfig.EqBands; i++)
                masterConfig.GraphicEQGains[i] = -12.0f;

            effect.ApplyConfiguration();

            float[] after = CreateTestBuffer(1000f, 0.25f, FrameCount, Channels);
            effect.Process(after, FrameCount);

            float drop = LinearToDb(CalculateRMS(before)) - LinearToDb(CalculateRMS(after));
            Assert.True(drop > 6.0f, $"ApplyConfiguration did not reach the chain, level dropped only {drop:F2} dB");
        }

        [Fact]
        public void SmartMasterConfig_ArrayLengths_ShouldMatchTheChain()
        {
            var config = new SmartMasterConfig();

            Assert.Equal(SmartMasterConfig.EqBands, config.GraphicEQGains.Length);
            Assert.Equal(SmartMasterConfig.AlignChannels, config.TimeDelays.Length);
            Assert.Equal(SmartMasterConfig.AlignChannels, config.PhaseInvert.Length);
            Assert.Equal(SmartMasterConfig.EqBands, new MeasurementResults().FrequencyResponse.Length);
        }

        [Fact]
        public void SmartMasterConfig_OddSizedArrays_ShouldGetFitted()
        {
            var config = new SmartMasterConfig();
            float[] legacyGains = new float[31];
            legacyGains[0] = 4.0f;
            legacyGains[30] = 9.0f;

            config.GraphicEQGains = legacyGains;
            config.TimeDelays = new float[] { 1.0f };

            Assert.Equal(SmartMasterConfig.EqBands, config.GraphicEQGains.Length);
            Assert.Equal(4.0f, config.GraphicEQGains[0]);
            Assert.Equal(SmartMasterConfig.AlignChannels, config.TimeDelays.Length);
            Assert.Equal(1.0f, config.TimeDelays[0]);
        }

        [Fact]
        public void SmartMasterChain_LimiterRelease_ShouldReachTheLimiter()
        {
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };

            float[] fast = _limitedBlock(config, 1.0f);
            float[] slow = _limitedBlock(config, 1000.0f);

            bool differs = false;
            for (int i = 0; i < fast.Length; i++)
            {
                if (MathF.Abs(fast[i] - slow[i]) > 1e-6f) { differs = true; break; }
            }

            Assert.True(differs, "LimiterRelease had no effect on the chain output");
        }

        /// <summary>
        /// Pushes a loud transient plus a quiet tail through a chain built with the given
        /// limiter release, and hands back what came out.
        /// </summary>
        private float[] _limitedBlock(AudioConfig config, float releaseMs)
        {
            var masterConfig = new SmartMasterConfig
            {
                LimiterThreshold = -20.0f,
                LimiterCeiling = -2.0f,
                LimiterRelease = releaseMs
            };

            using var chain = new SmartMasterAudioChain(config.SampleRate, config.Channels);
            chain.Configure(config, masterConfig);

            float[] loud = CreateTestBuffer(1000f, 1.0f, FrameCount, Channels);
            chain.Process(loud, FrameCount);

            float[] quiet = CreateTestBuffer(1000f, 0.05f, FrameCount, Channels);
            chain.Process(quiet, FrameCount);

            return quiet;
        }
    }
}
