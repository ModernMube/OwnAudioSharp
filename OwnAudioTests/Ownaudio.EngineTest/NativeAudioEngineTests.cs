using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Core;
using System;
using System.Threading;

namespace Ownaudio.EngineTest
{
    /// <summary>
    /// Tests for the NativeAudioEngine (PortAudio/MiniAudio hybrid).
    /// </summary>
    [TestClass]
    public class NativeAudioEngineTests
    {
        [TestMethod]
        public void NativeEngine_CreateViaFactory_Success()
        {
            // Arrange
            var config = AudioConfig.Default;

            // Act
            IAudioEngine? engine = null;
            Exception? caughtException = null;

            try
            {
                engine = AudioEngineFactory.Create(config);
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }

            // Assert
            Assert.IsNull(caughtException, $"Factory should create engine without exception. Error: {caughtException?.Message}");
            Assert.IsNotNull(engine, "Engine should not be null");

            // Verify engine is working by checking stopped state
            int stoppedState = engine.OwnAudioEngineStopped();
            Assert.IsTrue(stoppedState >= 0, "Engine should be in valid state");

            // Cleanup
            engine?.Dispose();
        }

        [TestMethod]
        public void NativeEngine_GetBackendInfo_Success()
        {
            // Arrange
            var config = AudioConfig.Default;

            // Act
            using (var engine = AudioEngineFactory.Create(config))
            {
                // The NativeAudioEngine logs which backend it's using (PortAudio or MiniAudio)
                // Just verify the engine is working
                Assert.IsNotNull(engine);

                int stoppedState = engine.OwnAudioEngineStopped();
                Assert.IsTrue(stoppedState >= 0, "Engine should be in valid state");
            }

            // Assert - no exceptions means success
            Assert.IsTrue(true);
        }

        [TestMethod]
        public void NativeEngine_EnumerateDevices_Success()
        {
            // Arrange
            var config = AudioConfig.Default;

            // Act
            using (var engine = AudioEngineFactory.Create(config))
            {
                var outputDevices = engine.GetOutputDevices();
                var inputDevices = engine.GetInputDevices();

                // Assert
                Assert.IsNotNull(outputDevices, "Output devices should not be null");
                Assert.IsNotNull(inputDevices, "Input devices should not be null");

                Console.WriteLine($"Found {outputDevices.Count} output devices:");
                foreach (var device in outputDevices)
                {
                    Console.WriteLine($"  - {device}");
                }

                Console.WriteLine($"Found {inputDevices.Count} input devices:");
                foreach (var device in inputDevices)
                {
                    Console.WriteLine($"  - {device}");
                }

                // At least one output device should exist
                Assert.IsTrue(outputDevices.Count > 0, "Should have at least one output device");
            }
        }

        [TestMethod]
        public void NativeEngine_StartStop_Success()
        {
            // Arrange
            var config = AudioConfig.Default;

            // Act
            using (var engine = AudioEngineFactory.Create(config))
            {
                int startResult = engine.Start();
                Assert.AreEqual(0, startResult, "Start should succeed");

                // Let it run for a short time
                Thread.Sleep(100);

                int stopResult = engine.Stop();
                Assert.AreEqual(0, stopResult, "Stop should succeed");
            }

            // Assert - no exceptions means success
            Assert.IsTrue(true);
        }

        [TestMethod]
        public void NativeEngine_SendAudioData_Success()
        {
            // Arrange
            var config = AudioConfig.Default;
            config.BufferSize = 512;

            // Act
            using (var engine = AudioEngineFactory.Create(config))
            {
                engine.Start();

                // Generate a simple sine wave
                float[] audioData = new float[config.BufferSize * config.Channels];
                double frequency = 440.0; // A4 note
                double phase = 0.0;

                for (int i = 0; i < config.BufferSize; i++)
                {
                    float sample = (float)Math.Sin(2.0 * Math.PI * phase);
                    audioData[i * config.Channels] = sample;
                    if (config.Channels == 2)
                        audioData[i * config.Channels + 1] = sample;

                    phase += frequency / config.SampleRate;
                    if (phase >= 1.0) phase -= 1.0;
                }

                // Send audio data
                engine.Send(audioData.AsSpan());

                // Assert - no exception means success

                // Let it play
                Thread.Sleep(500);

                engine.Stop();
            }
        }

        [TestMethod]
        public void NativeEngine_GetPlatformInfo_ContainsNativeEngine()
        {
            // Act
            string platformInfo = AudioEngineFactory.GetPlatformInfo();

            // Assert
            Assert.IsNotNull(platformInfo);
            Assert.IsTrue(platformInfo.Contains("NativeAudioEngine"),
                "Platform info should mention NativeAudioEngine as primary implementation");
            Assert.IsTrue(platformInfo.Contains("PortAudio") || platformInfo.Contains("MiniAudio"),
                "Platform info should mention the underlying backends");

            Console.WriteLine("Platform Info:");
            Console.WriteLine(platformInfo);
        }

        [TestMethod]
        public void NativeEngine_MultipleStartStop_Success()
        {
            // Arrange
            var config = AudioConfig.Default;

            // Act & Assert
            using (var engine = AudioEngineFactory.Create(config))
            {
                for (int i = 0; i < 3; i++)
                {
                    int startResult = engine.Start();
                    Assert.AreEqual(0, startResult, $"Start iteration {i} should succeed");

                    Thread.Sleep(50);

                    int stopResult = engine.Stop();
                    Assert.AreEqual(0, stopResult, $"Stop iteration {i} should succeed");

                    Thread.Sleep(50);
                }
            }
        }

        [TestMethod]
        public void NativeEngine_LowLatencyConfig_Success()
        {
            // Arrange
            var config = AudioConfig.LowLatency;

            // Act & Assert
            using (var engine = AudioEngineFactory.Create(config))
            {
                Assert.IsNotNull(engine);

                engine.Start();
                Thread.Sleep(100);
                engine.Stop();
            }
        }

        [TestMethod]
        public void NativeEngine_HighLatencyConfig_Success()
        {
            // Arrange
            var config = AudioConfig.HighLatency;

            // Act & Assert
            using (var engine = AudioEngineFactory.Create(config))
            {
                Assert.IsNotNull(engine);

                engine.Start();
                Thread.Sleep(100);
                engine.Stop();
            }
        }
    }
}
