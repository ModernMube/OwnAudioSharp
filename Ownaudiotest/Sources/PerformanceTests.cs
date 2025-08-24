using Ownaudio;
using Ownaudio.Sources;

namespace Ownaudiotest.Sources
{
    /// <summary>
    /// Performance and stress tests for realistic usage scenarios
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class AudioPerformanceTests
    {
#nullable disable
        private static SourceManager _manager;
#nullable restore

        [ClassInitialize]
        public static void Setup(TestContext context)
        {
            OwnAudio.Initialize();
            _manager = SourceManager.Instance;
        }

        [ClassCleanup]
        public static void Cleanup()
        {
            _manager?.ResetAll();
            OwnAudio.Free();


        }

        [TestMethod]
        [Timeout(10000)] // 10 second timeout
        public async Task Performance_RepeatedLoadAndPlay()
        {
            Console.WriteLine("=== Testing Repeated Load/Play Performance ===");

            // This tests memory leaks and performance degradation over time
            string testFile = CreateQuickTestFile();

            try
            {
                for (int i = 0; i < 5; i++) // Reduced iterations for reasonable test time
                {
                    Console.WriteLine($"Iteration {i + 1}/5");

                    // Load
                    await _manager.AddOutputSource(testFile, $"Track_{i}");

                    // Play briefly
                    _manager.Play();
                    await Task.Delay(50); // Very short playback
                    _manager.Stop();

                    // Remove
                    await _manager.RemoveOutputSource(0);

                    // Verify clean state
                    Assert.AreEqual(0, _manager.Sources.Count, $"Should be clean after iteration {i}");
                }

                Console.WriteLine("✓ Performance test completed successfully");
            }
            finally
            {
                _manager.ResetAll();

                if (File.Exists(testFile))
                    File.Delete(testFile);
            }
        }

        private string CreateQuickTestFile()
        {
            string tempFile = Path.GetTempFileName() + ".wav";
            var minimalWav = new byte[] {
                0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00,
                0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74, 0x20,
                0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
                0x44, 0xAC, 0x00, 0x00, 0x88, 0x58, 0x01, 0x00,
                0x02, 0x00, 0x10, 0x00, 0x64, 0x61, 0x74, 0x61,
                0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            };
            File.WriteAllBytes(tempFile, minimalWav);
            return tempFile;
        }
    }
}
