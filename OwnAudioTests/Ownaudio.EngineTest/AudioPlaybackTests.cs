using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using System;
using System.Threading;

namespace Ownaudio.EngineTest
{
    /// <summary>
    /// Test suite for audio playback functionality.
    /// Tests audio sending, buffer management, and playback scenarios.
    /// Platform-independent tests using AudioEngineFactory.
    /// </summary>
    [TestClass]
    public class AudioPlaybackTests
    {
        [TestMethod]
        public void Send_WithValidSamples_ShouldSucceed()
        {
            // Arrange
            var config = AudioConfig.Default;
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();

            float[] samples = GenerateSilence(config.Channels * 100);

            // Act & Assert (should not throw)
            engine.Send(samples.AsSpan());

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void Send_WithoutStarting_ShouldThrow()
        {
            // Arrange
            var config = AudioConfig.Default;
            using var engine = AudioEngineFactory.Create(config);

            float[] samples = GenerateSilence(config.Channels * 100);

            // Act & Assert
            try
            {
                engine.Send(samples.AsSpan());
                Assert.Fail("Expected AudioException was not thrown");
            }
            catch (AudioException)
            {
                // Expected
            }
        }

        [TestMethod]
        public void Send_WithSineWave_ShouldSucceed()
        {
            // Arrange
            var config = AudioConfig.Default;
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();

            float[] samples = GenerateSineWave(440.0f, config.SampleRate, config.Channels, 0.1);

            // Act & Assert (should not throw)
            engine.Send(samples.AsSpan());

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void Send_MultipleTimes_ShouldSucceed()
        {
            // Arrange
            var config = AudioConfig.Default;
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();

            // Act
            for (int i = 0; i < 10; i++)
            {
                float[] samples = GenerateSilence(config.Channels * 100);
                engine.Send(samples.AsSpan());
            }

            // Assert (no exceptions thrown)

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void Send_LargeBuffer_ShouldSucceed()
        {
            // Arrange
            var config = AudioConfig.Default;
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();

            // Send 1 second of audio
            int samplesCount = config.SampleRate * config.Channels;
            float[] samples = GenerateSilence(samplesCount);

            // Act & Assert (should not throw)
            engine.Send(samples.AsSpan());

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void Send_EmptySpan_ShouldSucceed()
        {
            // Arrange
            var config = AudioConfig.Default;
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();

            float[] samples = new float[0];

            // Act & Assert (should not throw)
            engine.Send(samples.AsSpan());

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void Send_AfterStop_ShouldThrow()
        {
            // Arrange
            var config = AudioConfig.Default;
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();
            engine.Stop();

            float[] samples = GenerateSilence(config.Channels * 100);

            // Act & Assert
            try
            {
                engine.Send(samples.AsSpan());
                Assert.Fail("Expected AudioException was not thrown");
            }
            catch (AudioException)
            {
                // Expected
            }
        }

        [TestMethod]
        public void Send_ContinuousPlayback_ShouldSucceed()
        {
            // Arrange
            var config = AudioConfig.Default;
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();

            int bufferSize = engine.FramesPerBuffer * config.Channels;
            int iterations = 20;

            // Act - simulate continuous playback
            for (int i = 0; i < iterations; i++)
            {
                float[] samples = GenerateSineWave(440.0f, config.SampleRate, config.Channels,
                    (float)engine.FramesPerBuffer / config.SampleRate);
                engine.Send(samples.AsSpan());
                Thread.Sleep(5); // Small delay between sends
            }

            // Assert (no exceptions thrown)

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void Send_DifferentFrequencies_ShouldSucceed()
        {
            // Arrange
            var config = AudioConfig.Default;
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();

            float[] frequencies = { 220.0f, 440.0f, 880.0f, 1760.0f };

            // Act
            foreach (var freq in frequencies)
            {
                float[] samples = GenerateSineWave(freq, config.SampleRate, config.Channels, 0.05);
                engine.Send(samples.AsSpan());
            }

            // Assert (no exceptions thrown)

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        [Ignore("Mono configuration may not be supported on all hardware")]
        public void Send_MonoConfig_ShouldSucceed()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 1,
                BufferSize = 512,
                EnableOutput = true
            };

            IAudioEngine? engine = null;
            try
            {
                engine = AudioEngineFactory.Create(config);
            }
            catch
            {
                Assert.Inconclusive("Mono configuration not supported on this hardware");
                return;
            }

            using (engine)
            {
                int startResult = engine.Start();
                if (startResult != 0)
                {
                    Assert.Inconclusive("Failed to start mono configuration");
                    return;
                }

                float[] samples = GenerateSilence(config.Channels * 100);

                // Act & Assert (should not throw)
                engine.Send(samples.AsSpan());

                // Cleanup
                engine.Stop();
            }
        }

        [TestMethod]
        public void Send_LowLatencyConfig_ShouldSucceed()
        {
            // Arrange
            var config = AudioConfig.LowLatency;
            using var engine = AudioEngineFactory.Create(config);
            engine.Initialize(config);
            engine.Start();

            float[] samples = GenerateSilence(config.Channels * 100);

            // Act & Assert (should not throw)
            engine.Send(samples.AsSpan());

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void Send_HighLatencyConfig_ShouldSucceed()
        {
            // Arrange
            var config = AudioConfig.HighLatency;
            using var engine = AudioEngineFactory.Create(config);
            engine.Initialize(config);
            engine.Start();

            float[] samples = GenerateSilence(config.Channels * 100);

            // Act & Assert (should not throw)
            engine.Send(samples.AsSpan());

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void Send_AfterRestart_ShouldSucceed()
        {
            // Arrange
            var config = AudioConfig.Default;
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();

            float[] samples = GenerateSilence(config.Channels * 100);
            engine.Send(samples.AsSpan());

            engine.Stop();
            engine.Start();

            // Act & Assert (should not throw)
            engine.Send(samples.AsSpan());

            // Cleanup
            engine.Stop();
        }

        #region Helper Methods

        /// <summary>
        /// Generates silence (zeros) for the specified number of samples.
        /// </summary>
        private float[] GenerateSilence(int sampleCount)
        {
            return new float[sampleCount];
        }

        /// <summary>
        /// Generates a sine wave with the specified parameters.
        /// </summary>
        /// <param name="frequency">Frequency in Hz</param>
        /// <param name="sampleRate">Sample rate in Hz</param>
        /// <param name="channels">Number of channels</param>
        /// <param name="duration">Duration in seconds</param>
        /// <returns>Array of interleaved samples</returns>
        private float[] GenerateSineWave(float frequency, int sampleRate, int channels, double duration)
        {
            int frameCount = (int)(duration * sampleRate);
            int sampleCount = frameCount * channels;
            float[] samples = new float[sampleCount];

            for (int frame = 0; frame < frameCount; frame++)
            {
                float value = (float)Math.Sin(2.0 * Math.PI * frequency * frame / sampleRate) * 0.3f;

                for (int channel = 0; channel < channels; channel++)
                {
                    samples[frame * channels + channel] = value;
                }
            }

            return samples;
        }

        #endregion
    }
}
