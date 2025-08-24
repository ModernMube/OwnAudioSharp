using Ownaudio;

namespace Ownaudiotest
{
    [TestClass]
    public class OwnAudioConstantsTests
    {
        [TestMethod]
        public void Constants_ShouldHaveValidValues()
        {
            // Arrange & Act
            var constantsType = typeof(OwnAudio).GetNestedType("Constants",
                System.Reflection.BindingFlags.NonPublic);

            if (constantsType != null)
            {
                var ffmpegFormat = constantsType.GetField("FFmpegSampleFormat");
                var paFormat = constantsType.GetField("PaSampleFormat");

                // Assert
                Assert.IsNotNull(ffmpegFormat, "FFmpegSampleFormat constant should exist");
                Assert.IsNotNull(paFormat, "PaSampleFormat constant should exist");
            }
            else
            {
                Assert.Inconclusive("Could not access Constants nested class via reflection");
            }
        }
    }
}
