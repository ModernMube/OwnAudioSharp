using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Core;

namespace Ownaudio.EngineTest
{
    /// <summary>
    /// Test suite for AudioConfig class.
    /// Validates configuration parameters and validation logic.
    /// </summary>
    [TestClass]
    public class AudioConfigTests
    {
        [TestMethod]
        public void DefaultConfig_ShouldHaveValidDefaults()
        {
            // Arrange & Act
            var config = AudioConfig.Default;

            // Assert
            Assert.AreEqual(48000, config.SampleRate, "Default sample rate should be 48000 Hz");
            Assert.AreEqual(2, config.Channels, "Default channels should be 2 (stereo)");
            Assert.AreEqual(512, config.BufferSize, "Default buffer size should be 512 frames");
            Assert.IsFalse(config.EnableInput, "Input should be disabled by default");
            Assert.IsTrue(config.EnableOutput, "Output should be enabled by default");
            Assert.IsNull(config.OutputDeviceId, "Output device ID should be null (default device)");
            Assert.IsNull(config.InputDeviceId, "Input device ID should be null (default device)");
        }

        [TestMethod]
        public void LowLatencyConfig_ShouldHaveLowLatencySettings()
        {
            // Arrange & Act
            var config = AudioConfig.LowLatency;

            // Assert
            Assert.AreEqual(48000, config.SampleRate);
            Assert.AreEqual(2, config.Channels);
            Assert.AreEqual(128, config.BufferSize, "Low latency buffer size should be 128 frames");
        }

        [TestMethod]
        public void HighLatencyConfig_ShouldHaveHighLatencySettings()
        {
            // Arrange & Act
            var config = AudioConfig.HighLatency;

            // Assert
            Assert.AreEqual(48000, config.SampleRate);
            Assert.AreEqual(2, config.Channels);
            Assert.AreEqual(2048, config.BufferSize, "High latency buffer size should be 2048 frames");
        }

        [TestMethod]
        public void Validate_ValidConfig_ShouldReturnTrue()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 44100,
                Channels = 2,
                BufferSize = 256,
                EnableOutput = true
            };

            // Act
            bool isValid = config.Validate();

            // Assert
            Assert.IsTrue(isValid, "Valid configuration should pass validation");
        }

        [TestMethod]
        public void Validate_InvalidSampleRate_ShouldReturnFalse()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = -1,
                Channels = 2,
                BufferSize = 512
            };

            // Act
            bool isValid = config.Validate();

            // Assert
            Assert.IsFalse(isValid, "Negative sample rate should fail validation");
        }

        [TestMethod]
        public void Validate_ZeroSampleRate_ShouldReturnFalse()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 0,
                Channels = 2,
                BufferSize = 512
            };

            // Act
            bool isValid = config.Validate();

            // Assert
            Assert.IsFalse(isValid, "Zero sample rate should fail validation");
        }

        [TestMethod]
        public void Validate_TooHighSampleRate_ShouldReturnFalse()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 200000,
                Channels = 2,
                BufferSize = 512
            };

            // Act
            bool isValid = config.Validate();

            // Assert
            Assert.IsFalse(isValid, "Sample rate above 192000 should fail validation");
        }

        [TestMethod]
        public void Validate_InvalidChannels_ShouldReturnFalse()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 0,
                BufferSize = 512
            };

            // Act
            bool isValid = config.Validate();

            // Assert
            Assert.IsFalse(isValid, "Zero channels should fail validation");
        }

        [TestMethod]
        public void Validate_TooManyChannels_ShouldReturnFalse()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 33,
                BufferSize = 512
            };

            // Act
            bool isValid = config.Validate();

            // Assert
            Assert.IsFalse(isValid, "More than 32 channels should fail validation");
        }

        [TestMethod]
        public void Validate_InvalidBufferSize_ShouldReturnFalse()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 0
            };

            // Act
            bool isValid = config.Validate();

            // Assert
            Assert.IsFalse(isValid, "Zero buffer size should fail validation");
        }

        [TestMethod]
        public void Validate_TooLargeBufferSize_ShouldReturnFalse()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 20000
            };

            // Act
            bool isValid = config.Validate();

            // Assert
            Assert.IsFalse(isValid, "Buffer size above 16384 should fail validation");
        }

        [TestMethod]
        public void Validate_BothInputOutputDisabled_ShouldReturnFalse()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 512,
                EnableInput = false,
                EnableOutput = false
            };

            // Act
            bool isValid = config.Validate();

            // Assert
            Assert.IsFalse(isValid, "Configuration with both input and output disabled should fail");
        }

        [TestMethod]
        public void Validate_OnlyInputEnabled_ShouldReturnTrue()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 512,
                EnableInput = true,
                EnableOutput = false
            };

            // Act
            bool isValid = config.Validate();

            // Assert
            Assert.IsTrue(isValid, "Configuration with only input enabled should be valid");
        }

        [TestMethod]
        public void Validate_MonoConfig_ShouldReturnTrue()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 1,
                BufferSize = 512,
                EnableOutput = true
            };

            // Act
            bool isValid = config.Validate();

            // Assert
            Assert.IsTrue(isValid, "Mono configuration should be valid");
        }

        [TestMethod]
        public void Validate_SurroundConfig_ShouldReturnTrue()
        {
            // Arrange
            var config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 6,
                BufferSize = 512,
                EnableOutput = true
            };

            // Act
            bool isValid = config.Validate();

            // Assert
            Assert.IsTrue(isValid, "5.1 surround configuration should be valid");
        }

        [TestMethod]
        public void ConfigWithDeviceId_ShouldRetainDeviceId()
        {
            // Arrange
            var config = new AudioConfig
            {
                OutputDeviceId = "test-device-id",
                InputDeviceId = "test-input-id"
            };

            // Act & Assert
            Assert.AreEqual("test-device-id", config.OutputDeviceId);
            Assert.AreEqual("test-input-id", config.InputDeviceId);
        }

        [TestMethod]
        public void CommonSampleRates_ShouldValidate()
        {
            // Arrange
            int[] commonSampleRates = { 8000, 11025, 16000, 22050, 44100, 48000, 88200, 96000, 176400, 192000 };

            // Act & Assert
            foreach (var sampleRate in commonSampleRates)
            {
                var config = new AudioConfig
                {
                    SampleRate = sampleRate,
                    Channels = 2,
                    BufferSize = 512
                };

                Assert.IsTrue(config.Validate(), $"Sample rate {sampleRate} should be valid");
            }
        }
    }
}
