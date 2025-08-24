using Ownaudio;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ownaudiotest
{
    [TestClass]
    public class OwnAudioThreadSafetyTests
    {
        [TestCleanup]
        public void Cleanup()
        {
            OwnAudio.Free();
        }

        [TestMethod]
        public void Initialize_CalledConcurrently_ShouldBeSafe()
        {
            // Arrange
            int threadCount = 5;
            bool[] results = new bool[threadCount];
            var tasks = new System.Threading.Tasks.Task[threadCount];

            // Act
            for (int i = 0; i < threadCount; i++)
            {
                int index = i;
                tasks[i] = System.Threading.Tasks.Task.Run(() =>
                {
                    results[index] = OwnAudio.Initialize();
                });
            }

            System.Threading.Tasks.Task.WaitAll(tasks);

            // Assert
            Assert.IsTrue(results.Any(), "At least one initialization should complete");

            // Ellenőrizzük, hogy az inicializálás állapota konzisztens
            bool finalPortAudioState = OwnAudio.IsPortAudioInitialized;
            bool finalMiniAudioState = OwnAudio.IsMiniAudioInitialized;

            // Legalább az egyik engine-nek inicializáltnak kellene lennie, ha volt sikeres initialize
            if (results.Any(r => r))
            {
                Assert.IsTrue(finalPortAudioState || finalMiniAudioState,
                    "At least one audio engine should be initialized after successful Initialize() calls");
            }
        }
    }
}
