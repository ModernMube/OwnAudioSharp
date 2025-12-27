using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using System;

namespace Ownaudio.EngineTest
{
    /// <summary>
    /// Test suite for AudioEngine initialization and lifecycle management.
    /// Tests engine creation, initialization, start/stop cycles, and disposal.
    /// Platform-independent tests using AudioEngineFactory.
    /// </summary>
    [TestClass]
    public class AudioEngineInitializationTests
    {
        [TestMethod]
        public void Factory_CreateDefault_ShouldCreateEngine()
        {
            // Arrange & Act
            using var engine = AudioEngineFactory.CreateDefault();

            // Assert
            Assert.IsNotNull(engine, "Engine should be created successfully");
            Assert.IsTrue(engine.FramesPerBuffer > 0, "FramesPerBuffer should be set after initialization");
        }

        [TestMethod]
        public void Factory_CreateWithValidConfig_ShouldReturnInitializedEngine()
        {
            // Arrange
            var config = AudioConfig.Default;

            // Act
            using var engine = AudioEngineFactory.Create(config);

            // Assert
            Assert.IsNotNull(engine, "Engine should be created successfully");
            Assert.IsTrue(engine.FramesPerBuffer > 0, "FramesPerBuffer should be set after initialization");
        }

        [TestMethod]
        public void Factory_CreateWithNullConfig_ShouldThrow()
        {
            // Act & Assert
            try
            {
                using var engine = AudioEngineFactory.Create(null!);
                Assert.Fail("Expected AudioException was not thrown");
            }
            catch (AudioException)
            {
                // Expected
            }
        }

        [TestMethod]
        public void Factory_CreateWithInvalidConfig_ShouldThrow()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = -1,
                Channels = 2,
                BufferSize = 512
            };

            // Act & Assert
            try
            {
                using var engine = AudioEngineFactory.Create(config);
                Assert.Fail("Expected AudioException was not thrown");
            }
            catch (AudioException)
            {
                // Expected
            }
        }

        [TestMethod]
        public void OwnAudioEngineActivate_AfterCreate_ShouldReturnIdle()
        {
            // Arrange
            using var engine = AudioEngineFactory.CreateDefault();

            // Act
            int state = engine.OwnAudioEngineActivate();

            // Assert
            Assert.AreEqual(0, state, "Engine should be in idle state after creation but before start");
        }

        [TestMethod]
        public void OwnAudioEngineStopped_BeforeStart_ShouldReturnStopped()
        {
            // Arrange
            using var engine = AudioEngineFactory.CreateDefault();

            // Act
            int stopped = engine.OwnAudioEngineStopped();

            // Assert
            Assert.AreEqual(1, stopped, "Engine should report as stopped before Start() is called");
        }

        [TestMethod]
        public void Start_AfterValidInitialization_ShouldReturnSuccess()
        {
            // Arrange
            using var engine = AudioEngineFactory.CreateDefault();

            // Act
            int result = engine.Start();

            // Assert
            Assert.AreEqual(0, result, "Start should return 0 (success)");

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void OwnAudioEngineActivate_AfterStart_ShouldReturnActive()
        {
            // Arrange
            using var engine = AudioEngineFactory.CreateDefault();
            engine.Start();

            // Act
            int state = engine.OwnAudioEngineActivate();

            // Assert
            Assert.AreEqual(1, state, "Engine should be in active state after Start()");

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void OwnAudioEngineStopped_AfterStart_ShouldReturnRunning()
        {
            // Arrange
            using var engine = AudioEngineFactory.CreateDefault();
            engine.Start();

            // Act
            int stopped = engine.OwnAudioEngineStopped();

            // Assert
            Assert.AreEqual(0, stopped, "Engine should report as running (0) after Start()");

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void Stop_AfterStart_ShouldReturnSuccess()
        {
            // Arrange
            using var engine = AudioEngineFactory.CreateDefault();
            engine.Start();

            // Act
            int result = engine.Stop();

            // Assert
            Assert.AreEqual(0, result, "Stop should return 0 (success)");
        }

        [TestMethod]
        public void Stop_WithoutStart_ShouldReturnSuccess()
        {
            // Arrange
            using var engine = AudioEngineFactory.CreateDefault();

            // Act
            int result = engine.Stop();

            // Assert
            Assert.AreEqual(0, result, "Stop should be idempotent and return 0 even if not started");
        }

        [TestMethod]
        public void OwnAudioEngineStopped_AfterStop_ShouldReturnStopped()
        {
            // Arrange
            using var engine = AudioEngineFactory.CreateDefault();
            engine.Start();
            engine.Stop();

            // Act
            int stopped = engine.OwnAudioEngineStopped();

            // Assert
            Assert.AreEqual(1, stopped, "Engine should report as stopped (1) after Stop()");
        }

        [TestMethod]
        public void StartStopCycle_MultipleTimes_ShouldSucceed()
        {
            // Arrange
            using var engine = AudioEngineFactory.CreateDefault();

            // Act & Assert
            for (int i = 0; i < 3; i++)
            {
                int startResult = engine.Start();
                Assert.AreEqual(0, startResult, $"Start #{i + 1} should succeed");
                Assert.AreEqual(0, engine.OwnAudioEngineStopped(), $"Engine should be running after Start #{i + 1}");

                System.Threading.Thread.Sleep(50); // Brief operation

                int stopResult = engine.Stop();
                Assert.AreEqual(0, stopResult, $"Stop #{i + 1} should succeed");
                Assert.AreEqual(1, engine.OwnAudioEngineStopped(), $"Engine should be stopped after Stop #{i + 1}");
            }
        }

        [TestMethod]
        public void Start_CalledTwice_ShouldBeIdempotent()
        {
            // Arrange
            using var engine = AudioEngineFactory.CreateDefault();

            // Act
            int result1 = engine.Start();
            int result2 = engine.Start();

            // Assert
            Assert.AreEqual(0, result1, "First Start should succeed");
            Assert.AreEqual(0, result2, "Second Start should be idempotent and return success");

            // Cleanup
            engine.Stop();
        }

        [TestMethod]
        public void Stop_CalledTwice_ShouldBeIdempotent()
        {
            // Arrange
            using var engine = AudioEngineFactory.CreateDefault();
            engine.Start();

            // Act
            int result1 = engine.Stop();
            int result2 = engine.Stop();

            // Assert
            Assert.AreEqual(0, result1, "First Stop should succeed");
            Assert.AreEqual(0, result2, "Second Stop should be idempotent and return success");
        }

        [TestMethod]
        public void GetStream_AfterInitialization_ShouldReturnNonZero()
        {
            // Arrange
            using var engine = AudioEngineFactory.CreateDefault();

            // Act
            IntPtr stream = engine.GetStream();

            // Assert
            Assert.AreNotEqual(IntPtr.Zero, stream, "GetStream should return a valid pointer after initialization");
        }

        [TestMethod]
        public void FramesPerBuffer_AfterInitialization_ShouldBePositive()
        {
            // Arrange
            using var engine = AudioEngineFactory.CreateDefault();

            // Act
            int framesPerBuffer = engine.FramesPerBuffer;

            // Assert
            Assert.IsTrue(framesPerBuffer > 0, "FramesPerBuffer should be positive after initialization");
        }

        [TestMethod]
        public void Factory_CreateLowLatency_ShouldSucceed()
        {
            // Arrange & Act
            using var engine = AudioEngineFactory.CreateLowLatency();

            // Assert
            Assert.IsNotNull(engine, "Engine should be created with low latency configuration");
            Assert.IsTrue(engine.FramesPerBuffer > 0, "FramesPerBuffer should be set");
        }

        [TestMethod]
        public void Factory_CreateHighLatency_ShouldSucceed()
        {
            // Arrange & Act
            using var engine = AudioEngineFactory.CreateHighLatency();

            // Assert
            Assert.IsNotNull(engine, "Engine should be created with high latency configuration");
            Assert.IsTrue(engine.FramesPerBuffer > 0, "FramesPerBuffer should be set");
        }

        [TestMethod]
        public void Factory_CreateWithInputEnabled_ShouldSucceed()
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

            // Act
            using var engine = AudioEngineFactory.Create(config);

            // Assert
            Assert.IsNotNull(engine, "Engine should be created with input enabled");
        }

        [TestMethod]
        public void Dispose_AfterInitialization_ShouldNotThrow()
        {
            // Arrange
            var engine = AudioEngineFactory.CreateDefault();

            // Act & Assert (no exception should be thrown)
            engine.Dispose();
        }

        [TestMethod]
        public void Dispose_AfterStarting_ShouldStopAndCleanup()
        {
            // Arrange
            var engine = AudioEngineFactory.CreateDefault();
            engine.Start();

            // Act & Assert (no exception should be thrown)
            engine.Dispose();
        }

        [TestMethod]
        public void Dispose_CalledTwice_ShouldNotThrow()
        {
            // Arrange
            var engine = AudioEngineFactory.CreateDefault();

            // Act & Assert (no exception should be thrown)
            engine.Dispose();
            engine.Dispose(); // Second dispose should be safe
        }

        [TestMethod]
        public void Factory_GetPlatformInfo_ShouldReturnInfo()
        {
            // Act
            string platformInfo = AudioEngineFactory.GetPlatformInfo();

            // Assert
            Assert.IsFalse(string.IsNullOrWhiteSpace(platformInfo), "Platform info should not be empty");
            Assert.IsTrue(platformInfo.Contains("Platform:"), "Platform info should contain platform information");
        }
    }
}
