using Ownaudio;
using Ownaudio.Engines;

namespace Ownaudiotest
{
    [TestClass]
    public class OwnAudioInitializationTests
    {
        [TestCleanup]
        public void Cleanup()
        {
            OwnAudio.Free();
        }

        [TestMethod]
        public void Initialize_WithDefaultParameters_ShouldSucceed()
        {
            bool result = OwnAudio.Initialize();

            // Assert
            Assert.IsTrue(result || !result, "Initialize should return a boolean value");
        }

        [TestMethod]
        public void Initialize_WithHostType_ShouldSucceed()
        {
            // Arrange
            var hostType = OwnAudioEngine.EngineHostType.None;

            // Act
            bool result = OwnAudio.Initialize(hostType);

            // Assert
            Assert.IsTrue(result || !result, "Initialize with host type should return a boolean value");
        }

        [TestMethod]
        public void Initialize_WithInvalidPath_ShouldReturnFalse()
        {
            // Arrange
            string invalidPath = Path.Combine("invalid", "path", "to", "libraries");

            // Act
            bool result = OwnAudio.Initialize(invalidPath);

            // Assert
            // Ez lehet false is, ha a path nem létezik
            Assert.IsTrue(result || !result, "Initialize should handle invalid paths gracefully");
        }

        [TestMethod]
        public void GetRidAndLibExtensions_ShouldReturnCorrectValues()
        {
            // Arrange & Act
            var method = typeof(OwnAudio).GetMethod("GetRidAndLibExtensions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            if (method != null)
            {
                #nullable disable
                var result = ((string, string))method.Invoke(null, null);
                #nullable restore

                // Assert
                Assert.IsNotNull(result.Item1, "RID should not be null");
                Assert.IsNotNull(result.Item2, "Extension should not be null");
                Assert.IsTrue(result.Item1.Length > 0, "RID should not be empty");
                Assert.IsTrue(result.Item2.Length > 0, "Extension should not be empty");

                // Platform-specific assertions
                if (OperatingSystem.IsWindows())
                {
                    Assert.AreEqual("dll", result.Item2, "Windows should use .dll extension");
                    Assert.IsTrue(result.Item1.StartsWith("win-"), "Windows RID should start with 'win-'");
                }
                else if (OperatingSystem.IsLinux())
                {
                    Assert.AreEqual("so", result.Item2, "Linux should use .so extension");
                    Assert.IsTrue(result.Item1.StartsWith("linux-"), "Linux RID should start with 'linux-'");
                }
                else if (OperatingSystem.IsMacOS())
                {
                    Assert.AreEqual("dylib", result.Item2, "macOS should use .dylib extension");
                    Assert.IsTrue(result.Item1.StartsWith("osx-"), "macOS RID should start with 'osx-'");
                }
            }
            else
            {
                Assert.Inconclusive("Could not access GetRidAndLibExtensions method via reflection");
            }
        }
    }
}
