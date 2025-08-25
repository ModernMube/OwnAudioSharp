using Ownaudio;
using Ownaudio.Exceptions;

namespace Ownaudiotest
{
    [TestClass]
    public class OwnAudioDeviceTests
    {
        [TestInitialize]
        public void Setup()
        {
            // We try to initialize, but we don't require it to succeed.
            OwnAudio.Initialize();
        }

        [TestCleanup]
        public void Cleanup()
        {
            OwnAudio.Free();
        }

        [TestMethod]
        public void GetDevices_WhenInitialized_ShouldProvideDeviceInfo()
        {
            // Arrange
            if (!OwnAudio.IsPortAudioInitialized && !OwnAudio.IsMiniAudioInitialized)
            {
                Assert.Inconclusive("No audio engine initialized, cannot test device functionality");
                return;
            }

            // Act & Assert
            try
            {
                var outputDevices = OwnAudio.OutputDevices;
                var inputDevices = OwnAudio.InputDevices;

                Assert.IsNotNull(outputDevices, "Output devices collection should not be null");
                Assert.IsNotNull(inputDevices, "Input devices collection should not be null");

                if (outputDevices.Count > 0)
                {
                    var defaultOutput = OwnAudio.DefaultOutputDevice;
                    Assert.IsNotNull(defaultOutput, "Default output device should not be null when devices exist");
                    Assert.IsNotNull(defaultOutput.Name, "Device name should not be null");
                    Assert.IsTrue(defaultOutput.Name.Length > 0, "Device name should not be empty");
                }
            }
            catch (OwnaudioException)
            {
                Assert.Inconclusive("Audio system not properly initialized for device testing");
            }
        }

        [TestMethod]
        public void Free_ShouldResetInitializationFlags()
        {
            // Arrange
            OwnAudio.Initialize();
            bool wasPortAudioInit = OwnAudio.IsPortAudioInitialized;
            bool wasMiniAudioInit = OwnAudio.IsMiniAudioInitialized;

            // Act
            OwnAudio.Free();

            // Assert
            Assert.IsFalse(OwnAudio.IsPortAudioInitialized, "PortAudio should be uninitialized after Free()");
            Assert.IsFalse(OwnAudio.IsMiniAudioInitialized, "MiniAudio should be uninitialized after Free()");
        }
    }
}
