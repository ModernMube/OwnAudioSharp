using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Core;
using System;
using System.Threading;

namespace Ownaudio.EngineTest
{
    /// <summary>
    /// Test suite for audio capture functionality.
    /// Tests audio input, buffer management, and recording scenarios.
    /// Platform-independent tests using AudioEngineFactory.
    /// </summary>
    [TestClass]
    public class AudioCaptureTests
    {
        [TestMethod]
        public void Receives_WithInputEnabled_ShouldReturnSamples()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 512,
                EnableInput = true,
                EnableOutput = true // Enable output as well for duplex mode
            };
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();

            // Wait a bit for some audio to be captured
            Thread.Sleep(200);

            // Act
            int result = engine.Receives(out float[] samples);

            // Assert
            Assert.IsTrue(result >= 0, "Receives should return non-negative value");
            if (result > 0)
            {
                Assert.IsNotNull(samples, "Receives should return a non-null array when data available");
                Assert.IsTrue(samples.Length > 0, "Receives should return a non-empty array when data available");
            }

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void Receives_WithoutStarting_ShouldReturnError()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 512,
                EnableInput = true,
                EnableOutput = true
            };
            using var engine = AudioEngineFactory.Create(config);

            // Act
            int result = engine.Receives(out float[] samples);

            // Assert
            Assert.AreEqual(-1, result, "Receives should return -1 when engine is not started");
            Assert.IsNull(samples, "Samples should be null when engine is not started");
        }

        [TestMethod]
        public void Receives_AfterStop_ShouldReturnError()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 512,
                EnableInput = true,
                EnableOutput = true
            };
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();
            Thread.Sleep(50);
            engine.Stop();

            // Act
            int result = engine.Receives(out float[] samples);

            // Assert
            Assert.AreEqual(-1, result, "Receives should return -1 after engine is stopped");
            Assert.IsNull(samples, "Samples should be null after engine is stopped");
        }

        [TestMethod]
        public void Receives_MultipleTimes_ShouldSucceed()
        {
            // Arrange             
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 512,
                EnableInput = true,
                EnableOutput = true // Duplex mode
            };
            using var engine = AudioEngineFactory.Create(config);
            engine.Initialize(config);
            engine.Start();

            Thread.Sleep(200);

            // Act
            for (int i = 0; i < 5; i++)
            {
                int result = engine.Receives(out float[] samples);

                // Assert
                Assert.IsTrue(result >= 0, $"Receives #{i + 1} should succeed");
                if (result > 0)
                {
                    Assert.IsNotNull(samples, $"Samples #{i + 1} should not be null when data available");
                }

                Thread.Sleep(20);
            }

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void Receives_ShouldReturnExpectedBufferSize()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 512,
                EnableInput = true,
                EnableOutput = true
            };
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();

            Thread.Sleep(200);

            // Act
            int result = engine.Receives(out float[] samples);

            // Assert
            Assert.IsTrue(result >= 0, "Receives should return non-negative value");
            if (result > 0 && samples != null)
            {
                int expectedMaxSize = engine.FramesPerBuffer * config.Channels;
                Assert.IsTrue(samples.Length <= expectedMaxSize * 2,
                    $"Buffer size should be reasonable (got {samples.Length}, max expected ~{expectedMaxSize * 2})");
            }

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        [Ignore("Mono configuration may not be supported on all hardware")]
        public void Receives_MonoInput_ShouldSucceed()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 1,
                BufferSize = 512,
                EnableInput = true,
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

                Thread.Sleep(200);

                // Act
                int result = engine.Receives(out float[] samples);

                // Assert
                Assert.IsTrue(result >= 0, "Receives should succeed with mono input");

                // Cleanup
                engine.Stop();
            }
        }

        [TestMethod]
        public void Receives_DuplexMode_ShouldSucceed()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 512,
                EnableInput = true,
                EnableOutput = true
            };
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();

            Thread.Sleep(100);

            // Act
            int result = engine.Receives(out float[] samples);

            // Assert
            Assert.IsTrue(result >= 0, "Receives should succeed in duplex mode");
            Assert.IsNotNull(samples, "Samples should not be null in duplex mode");

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void Receives_ContinuousCapture_ShouldSucceed()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 512,
                EnableInput = true,
                EnableOutput = true
            };
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();

            Thread.Sleep(100);

            int totalSamplesCaptured = 0;
            int iterations = 20;

            // Act - simulate continuous capture
            for (int i = 0; i < iterations; i++)
            {
                int result = engine.Receives(out float[] samples);
                Assert.IsTrue(result >= 0, $"Iteration {i + 1} should succeed");

                if (samples != null && result > 0)
                {
                    totalSamplesCaptured += result;
                }

                Thread.Sleep(10);
            }

            // Assert
            Assert.IsTrue(totalSamplesCaptured > 0, "Should have captured some samples during the test");

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void Receives_AfterRestart_ShouldSucceed()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 512,
                EnableInput = true,
                EnableOutput = true
            };
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();
            Thread.Sleep(100);

            // First capture
            int result1 = engine.Receives(out float[] samples1);
            Assert.IsTrue(result1 >= 0, "First capture should succeed");

            // Restart
            engine.Stop();
            engine.Start();
            Thread.Sleep(100);

            // Act - Second capture after restart
            int result2 = engine.Receives(out float[] samples2);

            // Assert
            Assert.IsTrue(result2 >= 0, "Capture after restart should succeed");
            Assert.IsNotNull(samples2, "Samples after restart should not be null");

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void Receives_LowLatencyConfig_ShouldSucceed()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 128,
                EnableInput = true,
                EnableOutput = true
            };
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();

            Thread.Sleep(100);

            // Act
            int result = engine.Receives(out float[] samples);

            // Assert
            Assert.IsTrue(result >= 0, "Low latency capture should succeed");
            Assert.IsNotNull(samples, "Samples should not be null with low latency config");

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void Receives_SamplesAreValid_ShouldNotContainNaN()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 512,
                EnableInput = true,
                EnableOutput = true
            };
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();

            Thread.Sleep(100);

            // Act
            int result = engine.Receives(out float[] samples);

            // Assert
            Assert.IsTrue(result >= 0, "Receives should succeed");
            if (samples != null && result > 0)
            {
                for (int i = 0; i < result && i < samples.Length; i++)
                {
                    Assert.IsFalse(float.IsNaN(samples[i]), $"Sample at index {i} should not be NaN");
                    Assert.IsFalse(float.IsInfinity(samples[i]), $"Sample at index {i} should not be infinity");
                }
            }

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void Receives_SamplesInRange_ShouldBeWithinValidRange()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 512,
                EnableInput = true,
                EnableOutput = true
            };
            using var engine = AudioEngineFactory.Create(config);
            engine.Start();

            Thread.Sleep(100);

            // Act
            int result = engine.Receives(out float[] samples);

            // Assert
            Assert.IsTrue(result >= 0, "Receives should succeed");
            if (samples != null && result > 0)
            {
                for (int i = 0; i < result && i < samples.Length; i++)
                {
                    Assert.IsTrue(samples[i] >= -1.5f && samples[i] <= 1.5f,
                        $"Sample at index {i} should be in valid range [-1.5, 1.5], got {samples[i]}");
                }
            }

            // Cleanup
            engine.Stop();
        }
    }
}
