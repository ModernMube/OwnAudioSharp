using Ownaudio.Sources;

namespace Ownaudio.Tests
{
    [TestClass()]
    public class SourceManagerTests
    {
        [TestMethod()]
        public void Instance_Should_ReturnSameInstance()
        {
            // Arrange & Act
            var instance1 = SourceManager.Instance;
            var instance2 = SourceManager.Instance;

            // Assert
            Assert.AreSame(instance1, instance2);
        }

        [TestMethod()]
        public void AddOutputSource_Should_AddSource()
        {
            // Arrange
            var sourceManager = SourceManager.Instance;
            string testUrl = "test_audio_file.wav";

            // Act
            var result = sourceManager.AddOutputSource(testUrl).Result;

            // Assert
            Assert.IsTrue(result);
            CollectionAssert.Contains(sourceManager.Sources, testUrl);
        }

        [TestMethod()]
        public void RemoveOutputSource_Should_RemoveSource()
        {
            // Arrange
            var sourceManager = SourceManager.Instance;
            string testUrl = "test_audio_file.wav";
            sourceManager.AddOutputSource(testUrl).Wait();

            // Act
            var result = sourceManager.RemoveOutputSource(0).Result;

            // Assert
            Assert.IsTrue(result);
            CollectionAssert.DoesNotContain(sourceManager.Sources, testUrl);
        }

        [TestMethod()]
        public void Play_Should_StartPlayback()
        {
            // Arrange
            var sourceManager = SourceManager.Instance;
            string testUrl = "test_audio_file.wav";
            sourceManager.AddOutputSource(testUrl).Wait();

            // Act
            sourceManager.Play();

            // Assert
            Assert.AreEqual(SourceState.Playing, sourceManager.State);
        }

        [TestMethod()]
        public void Pause_Should_PausePlayback()
        {
            // Arrange
            var sourceManager = SourceManager.Instance;
            string testUrl = "test_audio_file.wav";
            sourceManager.AddOutputSource(testUrl).Wait();
            sourceManager.Play();

            // Act
            sourceManager.Pause();

            // Assert
            Assert.AreEqual(SourceState.Paused, sourceManager.State);
        }
    }
}

