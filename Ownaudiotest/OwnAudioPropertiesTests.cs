using Ownaudio;
using Ownaudio.Exceptions;

namespace Ownaudiotest
{
    [TestClass]
    public class OwnAudioPropertiesTests
    {
        [TestCleanup]
        public void Cleanup()
        {
            OwnAudio.Free();
        }

        [TestMethod]
        public void IsFFmpegInitialized_InitialState_ShouldBeFalse()
        {
            // Act & Assert
            Assert.IsFalse(OwnAudio.IsFFmpegInitialized, "FFmpeg should not be initialized initially");
        }

        [TestMethod]
        public void IsPortAudioInitialized_InitialState_ShouldBeFalse()
        {
            // Act & Assert
            Assert.IsFalse(OwnAudio.IsPortAudioInitialized, "PortAudio should not be initialized initially");
        }

        [TestMethod]
        public void IsMiniAudioInitialized_InitialState_ShouldBeFalse()
        {
            // Act & Assert
            Assert.IsFalse(OwnAudio.IsMiniAudioInitialized, "MiniAudio should not be initialized initially");
        }

        [TestMethod]
        public void DefaultOutputDevice_WhenNotInitialized_ShouldThrowException()
        {
            // Act & Assert
            Assert.ThrowsException<OwnaudioException>(() =>
            {
                var device = OwnAudio.DefaultOutputDevice;
            }, "Should throw OwnaudioException when not initialized");
        }

        [TestMethod]
        public void DefaultInputDevice_WhenNotInitialized_ShouldThrowException()
        {
            // Act & Assert
            Assert.ThrowsException<OwnaudioException>(() =>
            {
                var device = OwnAudio.DefaultInputDevice;
            }, "Should throw OwnaudioException when not initialized");
        }

        [TestMethod]
        public void OutputDevices_WhenNotInitialized_ShouldThrowException()
        {
            // Act & Assert
            Assert.ThrowsException<OwnaudioException>(() =>
            {
                var devices = OwnAudio.OutputDevices;
            }, "Should throw OwnaudioException when not initialized");
        }

        [TestMethod]
        public void InputDevices_WhenNotInitialized_ShouldThrowException()
        {
            // Act & Assert
            Assert.ThrowsException<OwnaudioException>(() =>
            {
                var devices = OwnAudio.InputDevices;
            }, "Should throw OwnaudioException when not initialized");
        }
    }
}
