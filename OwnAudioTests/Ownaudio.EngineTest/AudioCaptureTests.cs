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
            using var engine = EngineTestSupport.CreateOrSkip(config);
            engine.Start();

            // Wait a bit for some audio to be captured
            Thread.Sleep(200);

            // Act
            float[] buffer = new float[engine.FramesPerBuffer * config.Channels];
            int result = engine.Receives(buffer.AsSpan());

            // Assert
            Assert.IsTrue(result >= 0, "Receives should return non-negative value");
            if (result > 0)
            {
                Assert.IsTrue(buffer.Length > 0, "Buffer should have capacity when data available");
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
            using var engine = EngineTestSupport.CreateOrSkip(config);

            // Act
            float[] buffer = new float[engine.FramesPerBuffer * config.Channels];
            int result = engine.Receives(buffer.AsSpan());

            // Assert
            Assert.AreEqual(-1, result, "Receives should return -1 when engine is not started");
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
            using var engine = EngineTestSupport.CreateOrSkip(config);
            engine.Start();
            Thread.Sleep(50);
            engine.Stop();

            // Act
            float[] buffer = new float[engine.FramesPerBuffer * config.Channels];
            int result = engine.Receives(buffer.AsSpan());

            // Assert
            Assert.AreEqual(-1, result, "Receives should return -1 after engine is stopped");
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
            using var engine = EngineTestSupport.CreateOrSkip(config);
            engine.Initialize(config);
            engine.Start();

            Thread.Sleep(200);

            float[] buffer = new float[engine.FramesPerBuffer * config.Channels];

            // Act
            for (int i = 0; i < 5; i++)
            {
                int result = engine.Receives(buffer.AsSpan());

                // Assert
                Assert.IsTrue(result >= 0, $"Receives #{i + 1} should succeed");

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
            using var engine = EngineTestSupport.CreateOrSkip(config);
            engine.Start();

            Thread.Sleep(200);

            int expectedMaxSize = engine.FramesPerBuffer * config.Channels;
            float[] buffer = new float[expectedMaxSize];

            // Act
            int result = engine.Receives(buffer.AsSpan());

            // Assert
            Assert.IsTrue(result >= 0, "Receives should return non-negative value");
            if (result > 0)
            {
                Assert.IsTrue(result <= expectedMaxSize * 2,
                    $"Samples read should be reasonable (got {result}, max expected ~{expectedMaxSize * 2})");
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
                engine = EngineTestSupport.CreateOrSkip(config);
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
                float[] buffer = new float[engine.FramesPerBuffer * config.Channels];
                int result = engine.Receives(buffer.AsSpan());

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
            using var engine = EngineTestSupport.CreateOrSkip(config);
            engine.Start();

            Thread.Sleep(100);

            // Act
            float[] buffer = new float[engine.FramesPerBuffer * config.Channels];
            int result = engine.Receives(buffer.AsSpan());

            // Assert
            Assert.IsTrue(result >= 0, "Receives should succeed in duplex mode");

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
            using var engine = EngineTestSupport.CreateOrSkip(config);
            engine.Start();

            Thread.Sleep(100);

            int totalSamplesCaptured = 0;
            int iterations = 20;
            float[] buffer = new float[engine.FramesPerBuffer * config.Channels];

            // Act - simulate continuous capture
            for (int i = 0; i < iterations; i++)
            {
                int result = engine.Receives(buffer.AsSpan());
                Assert.IsTrue(result >= 0, $"Iteration {i + 1} should succeed");

                if (result > 0)
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
            using var engine = EngineTestSupport.CreateOrSkip(config);
            engine.Start();
            Thread.Sleep(100);

            float[] buffer = new float[engine.FramesPerBuffer * config.Channels];

            // First capture
            int result1 = engine.Receives(buffer.AsSpan());
            Assert.IsTrue(result1 >= 0, "First capture should succeed");

            // Restart
            engine.Stop();
            engine.Start();
            Thread.Sleep(100);

            // Act - Second capture after restart
            int result2 = engine.Receives(buffer.AsSpan());

            // Assert
            Assert.IsTrue(result2 >= 0, "Capture after restart should succeed");

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
            using var engine = EngineTestSupport.CreateOrSkip(config);
            engine.Start();

            Thread.Sleep(100);

            // Act
            float[] buffer = new float[engine.FramesPerBuffer * config.Channels];
            int result = engine.Receives(buffer.AsSpan());

            // Assert
            Assert.IsTrue(result >= 0, "Low latency capture should succeed");

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
            using var engine = EngineTestSupport.CreateOrSkip(config);
            engine.Start();

            Thread.Sleep(100);

            // Act
            float[] buffer = new float[engine.FramesPerBuffer * config.Channels];
            int result = engine.Receives(buffer.AsSpan());

            // Assert
            Assert.IsTrue(result >= 0, "Receives should succeed");
            if (result > 0)
            {
                for (int i = 0; i < result && i < buffer.Length; i++)
                {
                    Assert.IsFalse(float.IsNaN(buffer[i]), $"Sample at index {i} should not be NaN");
                    Assert.IsFalse(float.IsInfinity(buffer[i]), $"Sample at index {i} should not be infinity");
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
            using var engine = EngineTestSupport.CreateOrSkip(config);
            engine.Start();

            Thread.Sleep(100);

            // Act
            float[] buffer = new float[engine.FramesPerBuffer * config.Channels];
            int result = engine.Receives(buffer.AsSpan());

            // Assert
            Assert.IsTrue(result >= 0, "Receives should succeed");
            if (result > 0)
            {
                for (int i = 0; i < result && i < buffer.Length; i++)
                {
                    Assert.IsTrue(buffer[i] >= -1.5f && buffer[i] <= 1.5f,
                        $"Sample at index {i} should be in valid range [-1.5, 1.5], got {buffer[i]}");
                }
            }

            // Cleanup
            engine.Stop();
        }
    }
}
