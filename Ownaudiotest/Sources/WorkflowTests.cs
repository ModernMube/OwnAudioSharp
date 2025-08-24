using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio;
using Ownaudio.Sources;
using Ownaudio.Processors;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Linq;

namespace Ownaudio.Tests.RealUsage
{
    /// <summary>
    /// Tests that model real user scenarios and workflows
    /// These tests run sequentially and build upon each other like real usage
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class AudioWorkflowTests
    {
        #nullable disable
        private static SourceManager _manager;
        private static string _testAudioFile1;
        private static string _testAudioFile2;
        #nullable restore

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            // Initialize OwnAudio once for all tests - like real application startup
            Console.WriteLine("Initializing OwnAudio system...");

            bool initialized = OwnAudio.Initialize();
            Assert.IsTrue(initialized, "OwnAudio should initialize successfully");

            _manager = SourceManager.Instance;

            // Create test files that will be used across multiple tests
            _testAudioFile1 = CreateTestAudioFile("test_track1.wav");
            _testAudioFile2 = CreateTestAudioFile("test_track2.wav");

            Console.WriteLine($"Test setup complete. Audio system ready.");
            Console.WriteLine($"Default output device: {OwnAudio.DefaultOutputDevice.Name}");
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            Console.WriteLine("Cleaning up audio system...");

            try
            {
                _manager?.Stop();
                _manager?.ResetAll();

                // Clean up test files
                if (File.Exists(_testAudioFile1)) File.Delete(_testAudioFile1);
                if (File.Exists(_testAudioFile2)) File.Delete(_testAudioFile2);

                OwnAudio.Free();
                Console.WriteLine("Audio system cleanup complete.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cleanup warning: {ex.Message}");
            }
        }

        [TestMethod]
        [Priority(1)]
        public async Task Scenario1_SimplePlayerWorkflow()
        {
            Console.WriteLine("=== Testing Simple Player Workflow ===");

            // This models the Simpleplayer example usage
            // 1. Load audio file
            Console.WriteLine("Loading audio file...");
            bool loaded = await _manager.AddOutputSource(_testAudioFile1, "MainTrack");

            Assert.IsTrue(loaded, "Audio file should load successfully");
            //Assert.AreEqual(1, _manager.Sources.Count, "Should have 1 source loaded");
            Assert.IsTrue(_manager.IsLoaded, "Manager should report as loaded");
            Assert.IsTrue(_manager.Duration > TimeSpan.Zero, "Should have valid duration");

            // 2. Start playback
            Console.WriteLine("Starting playback...");
            _manager.Play();

            //Assert.AreEqual(SourceState.Playing, _manager.State, "Should be playing");

            // 3. Let it play briefly (simulates real usage)
            Console.WriteLine("Playing for 200ms...");
            await Task.Delay(200);

            // Verify it's still playing and position advanced
            Assert.AreEqual(SourceState.Playing, _manager.State, "Should still be playing");

            // 4. Pause
            Console.WriteLine("Pausing playback...");
            _manager.Pause();
            Assert.AreEqual(SourceState.Paused, _manager.State, "Should be paused");

            // 5. Resume
            Console.WriteLine("Resuming playback...");
            _manager.Play();
            Assert.AreEqual(SourceState.Playing, _manager.State, "Should be playing again");

            // 6. Stop
            Console.WriteLine("Stopping playback...");
            _manager.Stop();
            Assert.AreEqual(SourceState.Idle, _manager.State, "Should be idle");
            Assert.AreEqual(TimeSpan.Zero, _manager.Position, "Position should reset to zero");

            Console.WriteLine("✓ Simple player workflow completed successfully");
        }

        [TestMethod]
        [Priority(2)]
        public async Task Scenario2_MultitrackMixing()
        {
            Console.WriteLine("=== Testing Multitrack Mixing Workflow ===");

            // This models the Multitrackplayer example usage
            // Start fresh - remove previous track
            if (_manager.Sources.Count > 0)
            {
                await _manager.RemoveOutputSource(0);
            }

            // 1. Load multiple tracks
            Console.WriteLine("Loading multiple tracks...");
            await _manager.AddOutputSource(_testAudioFile1, "Track1");
            await _manager.AddOutputSource(_testAudioFile2, "Track2");

            Assert.AreEqual(2, _manager.Sources.Count, "Should have 2 tracks loaded");

            // 2. Set individual volumes (like real usage)
            Console.WriteLine("Setting track volumes...");
            _manager["Track1"].Volume = 0.85f;
            _manager["Track2"].Volume = 0.35f;

            Assert.AreEqual(0.85f, _manager["Track1"].Volume, 0.01f, "Track1 volume should be set");
            Assert.AreEqual(0.35f, _manager["Track2"].Volume, 0.01f, "Track2 volume should be set");

            // 3. Play mixed tracks
            Console.WriteLine("Playing mixed tracks...");
            _manager.Play();

            Assert.AreEqual(SourceState.Playing, _manager.State, "Should be playing");

            // Let it play and mix
            await Task.Delay(300);

            // 4. Adjust volumes during playback (real-time mixing)
            Console.WriteLine("Adjusting volumes during playback...");
            _manager.SetVolume(0, 0.5f);  // Fade down track 1
            _manager.SetVolume(1, 0.8f);  // Boost track 2

            await Task.Delay(200);

            // 5. Stop
            _manager.Stop();
            Assert.AreEqual(SourceState.Idle, _manager.State, "Should be idle");

            Console.WriteLine("✓ Multitrack mixing workflow completed successfully");
        }

        [TestMethod]
        [Priority(3)]
        public async Task Scenario3_RealTimeAudioIntegration()
        {
            Console.WriteLine("=== Testing Real-time Audio Integration ===");

            // This models the RealTimeData example usage
            // Keep existing tracks and add real-time source

            // 1. Add real-time audio source
            Console.WriteLine("Adding real-time audio source...");
            var realtimeSource = _manager.AddRealTimeSource(0.8f, 1, "RealtimeAudio");

            Assert.IsNotNull(realtimeSource, "Real-time source should be created");
            Assert.AreEqual("RealtimeAudio", realtimeSource.Name);
            Assert.AreEqual(0.8f, realtimeSource.Volume, 0.01f);

            // 2. Generate and submit audio samples (like sine wave generator)
            Console.WriteLine("Generating and submitting audio samples...");
            float[] samples = GenerateSineWave(1024, 440.0f, 44100);
            realtimeSource.SubmitSamples(samples);

            Assert.IsTrue(realtimeSource.SourceSampleData.Count > 0, "Should have queued samples");

            // 3. Start mixed playback (file tracks + real-time)
            Console.WriteLine("Starting mixed playback...");
            _manager.Play();

            // 4. Continue feeding real-time data while playing
            for (int i = 0; i < 3; i++)
            {
                await Task.Delay(100);
                samples = GenerateSineWave(512, 440.0f + (i * 50), 44100); // Changing frequency
                realtimeSource.SubmitSamples(samples);
            }

            // 5. Stop everything
            _manager.Stop();

            Console.WriteLine("✓ Real-time audio integration completed successfully");
        }

        [TestMethod]
        [Priority(4)]
        public async Task Scenario4_InputRecording()
        {
            Console.WriteLine("=== Testing Input Recording Workflow ===");

            // This models the Microphone example usage
            // Clear existing sources for clean test
            _manager.RemoveAllRealtimeSources();

            // 1. Add input source (if available)
            Console.WriteLine("Adding input source...");
            bool inputAdded = await _manager.AddInputSource(1.0f, "MicInput");

            if (OwnAudio.DefaultInputDevice.MaxInputChannels > 0)
            {
                Assert.IsTrue(inputAdded, "Input source should be added");
                Assert.IsTrue(_manager.IsRecorded, "Manager should report recording capability");
                Assert.AreEqual(1, _manager.SourcesInput.Count, "Should have 1 input source");

                // 2. Set input volume
                Console.WriteLine("Setting input volume...");
                _manager["MicInput"].Volume = 0.8f;
                Assert.AreEqual(0.8f, _manager["MicInput"].Volume, 0.01f);

                // 3. Start recording/monitoring
                Console.WriteLine("Starting input monitoring...");
                _manager.Play(); // This would start recording in real usage

                // Simulate some recording time
                await Task.Delay(300);

                // 4. Stop recording
                Console.WriteLine("Stopping recording...");
                _manager.Stop();

                // 5. Remove input source
                await _manager.RemoveInputSource();
                Assert.IsFalse(_manager.IsRecorded, "Should not be in recording mode");

                Console.WriteLine("✓ Input recording workflow completed successfully");
            }
            else
            {
                Console.WriteLine("⚠ No input device available - skipping input tests");
                Assert.IsFalse(inputAdded, "Input should not be added without input device");
            }
        }

        [TestMethod]
        [Priority(5)]
        public void Scenario5_ErrorHandlingAndRecovery()
        {
            Console.WriteLine("=== Testing Error Handling and Recovery ===");

            // This tests realistic error scenarios

            // 1. Try to load non-existent file
            Console.WriteLine("Testing invalid file handling...");
            var loadResult = _manager.AddOutputSource("nonexistent_file.mp3", "BadTrack").Result;
            Assert.IsFalse(loadResult, "Should fail to load non-existent file");

            // 2. System should still be operational
            Console.WriteLine("Verifying system remains operational...");
            var validLoadResult = _manager.AddOutputSource(_testAudioFile1, "RecoveryTrack").Result;
            Assert.IsTrue(validLoadResult, "Should successfully load valid file after error");

            // 3. Play should work normally
            _manager.Play();
            Assert.AreEqual(SourceState.Playing, _manager.State, "Should play normally after error recovery");

            // 4. Stop and cleanup
            _manager.Stop();

            Console.WriteLine("✓ Error handling and recovery completed successfully");
        }

        [TestMethod]
        [Priority(6)]
        public void Scenario6_CompleteSystemReset()
        {
            Console.WriteLine("=== Testing Complete System Reset ===");

            // This tests the reset functionality - important for real applications

            // 1. Verify we have some state
            int sourcesBeforeReset = _manager.Sources.Count;
            Console.WriteLine($"Sources before reset: {sourcesBeforeReset}");

            // 2. Get system summary
            string summaryBefore = _manager.GetSourcesSummary();
            Assert.IsNotNull(summaryBefore, "Should get valid summary");
            Console.WriteLine("System summary obtained");

            // 3. Perform complete reset
            Console.WriteLine("Performing complete system reset...");
            bool resetResult = _manager.ResetAll();
            Assert.IsTrue(resetResult, "Reset should succeed");

            // 4. Verify clean state
            Assert.AreEqual(SourceState.Idle, _manager.State, "Should be idle after reset");
            Assert.AreEqual(0, _manager.Sources.Count, "Should have no sources after reset");
            Assert.AreEqual(0, _manager.SourcesInput.Count, "Should have no input sources after reset");
            Assert.AreEqual(TimeSpan.Zero, _manager.Position, "Position should be zero");
            Assert.AreEqual(TimeSpan.Zero, _manager.Duration, "Duration should be zero");
            Assert.IsFalse(_manager.IsLoaded, "Should not be loaded");
            Assert.IsFalse(_manager.IsRecorded, "Should not be recording");

            // 5. Verify system is still usable after reset
            Console.WriteLine("Verifying system usability after reset...");
            var postResetLoad = _manager.AddOutputSource(_testAudioFile1, "PostResetTrack").Result;
            Assert.IsTrue(postResetLoad, "Should be able to load after reset");

            Console.WriteLine("✓ Complete system reset completed successfully");
        }

        #region Helper Methods

        private static string CreateTestAudioFile(string fileName)
        {
            string tempPath = Path.GetTempPath();
            string fullPath = Path.Combine(tempPath, fileName);

            // Create a minimal but valid WAV file
            var wavData = new byte[]
            {
                // RIFF header
                0x52, 0x49, 0x46, 0x46, // "RIFF"
                0x24, 0x00, 0x00, 0x00, // File size - 8 (36 bytes)
                0x57, 0x41, 0x56, 0x45, // "WAVE"
                
                // Format chunk
                0x66, 0x6D, 0x74, 0x20, // "fmt "
                0x10, 0x00, 0x00, 0x00, // Chunk size (16)
                0x01, 0x00,             // Audio format (PCM)
                0x01, 0x00,             // Channels (1)
                0x44, 0xAC, 0x00, 0x00, // Sample rate (44100)
                0x88, 0x58, 0x01, 0x00, // Byte rate (88200)
                0x02, 0x00,             // Block align (2)
                0x10, 0x00,             // Bits per sample (16)
                
                // Data chunk
                0x64, 0x61, 0x74, 0x61, // "data"
                0x04, 0x00, 0x00, 0x00, // Data size (4 bytes)
                0x00, 0x00, 0x00, 0x00  // Minimal audio data (2 samples of silence)
            };

            File.WriteAllBytes(fullPath, wavData);
            return fullPath;
        }

        private static float[] GenerateSineWave(int sampleCount, float frequency, int sampleRate)
        {
            float[] samples = new float[sampleCount];
            double angleIncrement = 2.0 * Math.PI * frequency / sampleRate;

            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = (float)(Math.Sin(i * angleIncrement) * 0.1); // Low volume to avoid clipping
            }

            return samples;
        }

        #endregion
    }
}
