using Ownaudio;

namespace Ownaudiotest
{
    [TestClass]
    public class OwnAudioPlatformPathTests
    {
        [TestMethod]
        public void GetPlatformProvider_ShouldReturnCorrectProvider()
        {
            // Arrange & Act
            var method = typeof(OwnAudio).GetMethod("GetPlatformProvider",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            if (method != null)
            {
                var provider = method.Invoke(null, null);

                // Assert
                Assert.IsNotNull(provider, "Platform provider should not be null");

                var providerType = provider.GetType().Name;

                if (OperatingSystem.IsWindows())
                {
                    Assert.AreEqual("WindowsPathProvider", providerType, "Should return WindowsPathProvider on Windows");
                }
                else if (OperatingSystem.IsLinux())
                {
                    Assert.AreEqual("LinuxPathProvider", providerType, "Should return LinuxPathProvider on Linux");
                }
                else if (OperatingSystem.IsMacOS())
                {
                    Assert.AreEqual("OSXPathProvider", providerType, "Should return OSXPathProvider on macOS");
                }
                else if (OperatingSystem.IsAndroid())
                {
                    Assert.AreEqual("AndroidPathProvider", providerType, "Should return AndroidPathProvider on Android");
                }
            }
            else
            {
                Assert.Inconclusive("Could not access GetPlatformProvider method via reflection");
            }
        }

        [TestMethod]
        public void DetermineDesktopRelativeBase_ShouldReturnValidPath()
        {
            // Arrange & Act
            var method = typeof(OwnAudio).GetMethod("DetermineDesktopRelativeBase",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            if (method != null && !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS())
            {
                #nullable disable
                var result = (string)method.Invoke(null, null);
                #nullable restore

                // Assert
                if (result != null)
                {
                    Assert.IsTrue(result.Length > 0, "Relative base should not be empty");
                }
            }
            else
            {
                Assert.Inconclusive("Could not test DetermineDesktopRelativeBase on this platform");
            }
        }
    }
}
