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
            // Arrange & Act
            using var effect = new SmartMasterEffect();
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };
            effect.Initialize(config);

            // Assert
            Assert.NotNull(effect);
            Assert.True(effect.Enabled);
            Assert.Equal("SmartMaster", effect.Name);
        }

        [Fact]
        public void SmartMasterEffect_DefaultPreset_ShouldNotReduceVolumeDrastically()
        {
            // Arrange
            using var effect = new SmartMasterEffect();
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };
            effect.Initialize(config);

            // Load default preset (transparent)
            effect.LoadSpeakerPreset(SpeakerType.Default);

            // Create test signal: 1kHz sine at -12 dB RMS
            float amplitude = 0.25f; // ~-12 dB RMS
            float[] buffer = CreateTestBuffer(1000f, amplitude, FrameCount, Channels);
            float inputRMS = CalculateRMS(buffer);
            float inputDb = LinearToDb(inputRMS);

            // Act
            effect.Process(buffer, FrameCount);

            // Assert
            float outputRMS = CalculateRMS(buffer);
            float outputDb = LinearToDb(outputRMS);
            float volumeLoss = inputDb - outputDb;

            // Default preset should be nearly transparent (max 1 dB loss)
            Assert.True(volumeLoss < 1.0f, 
                $"Default preset caused {volumeLoss:F2} dB volume loss (Input: {inputDb:F2} dB, Output: {outputDb:F2} dB)");
        }

        [Fact]
        public void SmartMasterEffect_HiFiPreset_ShouldPreserveVolume()
        {
            // Arrange
            using var effect = new SmartMasterEffect();
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };
            effect.Initialize(config);

            // Load HiFi preset
            effect.LoadSpeakerPreset(SpeakerType.HiFi);

            // Create test signal: 1kHz sine at -12 dB RMS
            float amplitude = 0.25f; // ~-12 dB RMS
            float[] buffer = CreateTestBuffer(1000f, amplitude, FrameCount, Channels);
            float inputRMS = CalculateRMS(buffer);
            float inputDb = LinearToDb(inputRMS);

            // Act
            effect.Process(buffer, FrameCount);

            // Assert
            float outputRMS = CalculateRMS(buffer);
            float outputDb = LinearToDb(outputRMS);
            float volumeLoss = inputDb - outputDb;

            // HiFi preset should preserve volume within 2 dB
            Assert.True(volumeLoss < 2.0f, 
                $"HiFi preset caused {volumeLoss:F2} dB volume loss (Input: {inputDb:F2} dB, Output: {outputDb:F2} dB)");
            
            // Should not boost excessively either
            Assert.True(volumeLoss > -3.0f, 
                $"HiFi preset caused {-volumeLoss:F2} dB volume boost (Input: {inputDb:F2} dB, Output: {outputDb:F2} dB)");
        }

        [Fact]
        public void SmartMasterEffect_AllPresets_ShouldNotCauseExcessiveVolumeLoss()
        {
            // Test all presets to ensure none cause the reported 75% (-12 dB) volume loss
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
                // Arrange
                using var effect = new SmartMasterEffect();
                var config = new AudioConfig
                {
                    SampleRate = (int)SampleRate,
                    Channels = Channels,
                    BufferSize = 512
                };
                effect.Initialize(config);
                effect.LoadSpeakerPreset(preset);

                // Create test signal: 1kHz sine at -12 dB RMS
                float amplitude = 0.25f;
                float[] buffer = CreateTestBuffer(1000f, amplitude, FrameCount, Channels);
                float inputRMS = CalculateRMS(buffer);
                float inputDb = LinearToDb(inputRMS);

                // Act
                effect.Process(buffer, FrameCount);

                // Assert
                float outputRMS = CalculateRMS(buffer);
                float outputDb = LinearToDb(outputRMS);
                float volumeLoss = inputDb - outputDb;

                // No preset should cause more than 3 dB volume loss
                Assert.True(volumeLoss < 3.0f, 
                    $"{preset} preset caused {volumeLoss:F2} dB volume loss (Input: {inputDb:F2} dB, Output: {outputDb:F2} dB)");
            }
        }

        [Fact]
        public void SmartMasterEffect_ClubPreset_ShouldHandleHighLevelSignals()
        {
            // Arrange
            using var effect = new SmartMasterEffect();
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };
            effect.Initialize(config);
            effect.LoadSpeakerPreset(SpeakerType.Club);

            // Create hot signal: 1kHz sine at -3 dB RMS (high level)
            float amplitude = 0.7f; // ~-3 dB RMS
            float[] buffer = CreateTestBuffer(1000f, amplitude, FrameCount, Channels);
            float inputPeak = CalculatePeak(buffer);

            // Act
            effect.Process(buffer, FrameCount);

            // Assert
            float outputPeak = CalculatePeak(buffer);
            
            // Should not clip (limiter should prevent this)
            Assert.True(outputPeak <= 1.0f, 
                $"Club preset caused clipping: peak = {outputPeak:F3}");
            
            // Should limit but not reduce too much
            Assert.True(outputPeak > 0.5f, 
                $"Club preset reduced signal too much: peak = {outputPeak:F3}");
        }

        [Fact]
        public void SmartMasterEffect_WithSubharmonics_ShouldNotDistort()
        {
            // Arrange
            using var effect = new SmartMasterEffect();
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };
            effect.Initialize(config);
            
            // Load preset with subharmonics enabled
            effect.LoadSpeakerPreset(SpeakerType.Club); // Has SubharmonicMix = 0.40

            // Create low frequency signal: 80Hz sine at -12 dB RMS
            float amplitude = 0.25f;
            float[] buffer = CreateTestBuffer(80f, amplitude, FrameCount, Channels);

            // Act
            effect.Process(buffer, FrameCount);

            // Assert
            float outputPeak = CalculatePeak(buffer);
            
            // Should not clip even with subharmonics
            Assert.True(outputPeak <= 1.0f, 
                $"Subharmonic synthesis caused clipping: peak = {outputPeak:F3}");
        }

        [Fact]
        public void SmartMasterEffect_Disabled_ShouldPassthrough()
        {
            // Arrange
            using var effect = new SmartMasterEffect();
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };
            effect.Initialize(config);
            effect.LoadSpeakerPreset(SpeakerType.HiFi);
            
            // Disable effect
            effect.Enabled = false;

            // Create test signal
            float amplitude = 0.25f;
            float[] buffer = CreateTestBuffer(1000f, amplitude, FrameCount, Channels);
            float[] originalBuffer = new float[buffer.Length];
            Array.Copy(buffer, originalBuffer, buffer.Length);

            // Act
            effect.Process(buffer, FrameCount);

            // Assert
            // Buffer should be unchanged
            for (int i = 0; i < buffer.Length; i++)
            {
                Assert.Equal(originalBuffer[i], buffer[i], 5); // 5 decimal places precision
            }
        }

        [Fact]
        public void SmartMasterEffect_Reset_ShouldClearState()
        {
            // Arrange
            using var effect = new SmartMasterEffect();
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };
            effect.Initialize(config);
            effect.LoadSpeakerPreset(SpeakerType.HiFi);

            // Process some audio to build up state
            float[] buffer1 = CreateTestBuffer(1000f, 0.5f, FrameCount, Channels);
            effect.Process(buffer1, FrameCount);

            // Act
            effect.Reset();

            // Process again with same input
            float[] buffer2 = CreateTestBuffer(1000f, 0.5f, FrameCount, Channels);
            effect.Process(buffer2, FrameCount);

            // Assert
            // After reset, processing should be consistent (no leftover state)
            // This is a basic sanity check - detailed state verification would require internal access
            float rms1 = CalculateRMS(buffer1);
            float rms2 = CalculateRMS(buffer2);
            
            // RMS should be similar (within 1 dB) after reset
            float diff = MathF.Abs(LinearToDb(rms1) - LinearToDb(rms2));
            Assert.True(diff < 1.0f, 
                $"Reset did not clear state properly: RMS difference = {diff:F2} dB");
        }

        [Fact]
        public void SmartMasterEffect_LongDuration_ShouldMaintainStability()
        {
            // Arrange
            using var effect = new SmartMasterEffect();
            var config = new AudioConfig
            {
                SampleRate = (int)SampleRate,
                Channels = Channels,
                BufferSize = 512
            };
            effect.Initialize(config);
            effect.LoadSpeakerPreset(SpeakerType.HiFi);

            // Process multiple blocks to test stability
            float[] buffer = CreateTestBuffer(1000f, 0.25f, FrameCount, Channels);
            float firstBlockRMS = 0.0f;
            float lastBlockRMS = 0.0f;

            // Act - process 10 blocks
            for (int block = 0; block < 10; block++)
            {
                float[] blockBuffer = CreateTestBuffer(1000f, 0.25f, FrameCount, Channels);
                effect.Process(blockBuffer, FrameCount);
                
                if (block == 0)
                    firstBlockRMS = CalculateRMS(blockBuffer);
                if (block == 9)
                    lastBlockRMS = CalculateRMS(blockBuffer);
            }

            // Assert
            // RMS should stabilize and not drift significantly
            float drift = MathF.Abs(LinearToDb(firstBlockRMS) - LinearToDb(lastBlockRMS));
            Assert.True(drift < 0.5f, 
                $"Effect showed instability over time: drift = {drift:F2} dB");
        }
    }
}
