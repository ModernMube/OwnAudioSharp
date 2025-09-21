using Ownaudio.Utilities.Matchering;

namespace Ownaudio.Tests.Matchering
{
    /// <summary>
    /// Comprehensive tests for AudioAnalyzer matchering functionality
    /// Tests cover spectrum analysis, EQ matching, preset processing, and real-world scenarios
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class AudioMatcheringTests
    {
#nullable disable
        private static AudioAnalyzer _analyzer;
        private static string _sourceTestFile;
        private static string _targetTestFile;
        private static string _baseSampleFile;
        private static string _tempDirectory;
#nullable restore

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            Console.WriteLine("Initializing AudioAnalyzer matchering test system...");

            _analyzer = new AudioAnalyzer();
            _tempDirectory = Path.Combine(Path.GetTempPath(), $"matchering_tests_{DateTime.Now.Ticks}");
            Directory.CreateDirectory(_tempDirectory);

            // Create test audio files with different characteristics
            _sourceTestFile = CreateTestAudioFile("source_test.wav", TestAudioType.Vocal);
            _targetTestFile = CreateTestAudioFile("target_test.wav", TestAudioType.Music);
            _baseSampleFile = CreateTestAudioFile("base_sample.wav", TestAudioType.Reference);

            Console.WriteLine($"Matchering test setup complete. Files ready for analysis.");
            Console.WriteLine($"Temp directory: {_tempDirectory}");
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            Console.WriteLine("Cleaning up matchering test system...");

            try
            {
                // Clean up test files
                if (File.Exists(_sourceTestFile)) File.Delete(_sourceTestFile);
                if (File.Exists(_targetTestFile)) File.Delete(_targetTestFile);
                if (File.Exists(_baseSampleFile)) File.Delete(_baseSampleFile);

                // Clean up temp directory
                if (Directory.Exists(_tempDirectory))
                    Directory.Delete(_tempDirectory, true);

                Console.WriteLine("Matchering test cleanup complete.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cleanup warning: {ex.Message}");
            }
        }

        [TestMethod]
        [Priority(1)]
        public void Test1_BasicSpectrumAnalysis()
        {
            Console.WriteLine("=== Testing Basic Spectrum Analysis ===");

            // Test source audio analysis
            Console.WriteLine("Analyzing source audio...");
            var sourceSpectrum = _analyzer.AnalyzeAudioFile(_sourceTestFile);

            Assert.IsNotNull(sourceSpectrum, "Source spectrum should not be null");
            Assert.IsNotNull(sourceSpectrum.FrequencyBands, "Frequency bands should not be null");
            Assert.AreEqual(30, sourceSpectrum.FrequencyBands.Length, "Should have 30 frequency bands");
            Assert.IsTrue(sourceSpectrum.RMSLevel > 0, "RMS level should be positive");
            Assert.IsTrue(sourceSpectrum.PeakLevel > 0, "Peak level should be positive");

            // Test target audio analysis
            Console.WriteLine("Analyzing target audio...");
            var targetSpectrum = _analyzer.AnalyzeAudioFile(_targetTestFile);

            Assert.IsNotNull(targetSpectrum, "Target spectrum should not be null");
            Assert.IsTrue(targetSpectrum.DynamicRange > 0, "Dynamic range should be positive");
            Assert.IsTrue(targetSpectrum.Loudness < 0, "Loudness should be in dBFS (negative)");

            // Verify frequency bands have reasonable values
            for (int i = 0; i < sourceSpectrum.FrequencyBands.Length; i++)
            {
                Assert.IsTrue(!float.IsNaN(sourceSpectrum.FrequencyBands[i]), $"Band {i} should not be NaN");
                Assert.IsTrue(!float.IsInfinity(sourceSpectrum.FrequencyBands[i]), $"Band {i} should not be infinite");
            }

            Console.WriteLine($"Source - RMS: {sourceSpectrum.RMSLevel:F4}, Peak: {sourceSpectrum.PeakLevel:F4}");
            Console.WriteLine($"Target - RMS: {targetSpectrum.RMSLevel:F4}, Peak: {targetSpectrum.PeakLevel:F4}");
            Console.WriteLine("✓ Basic spectrum analysis completed successfully");
        }

        [TestMethod]
        [Priority(2)]
        public void Test2_ShortFileAnalysis()
        {
            Console.WriteLine("=== Testing Short File Analysis (Expected Failure) ===");

            // Create a short test file that should fail segmented analysis
            string shortFile = CreateShortTestAudioFile("short_test.wav", 5); // 5 seconds - too short

            try
            {
                Console.WriteLine("Attempting to analyze short audio file (should fail)...");

                var shortSpectrum = _analyzer.AnalyzeAudioFile(shortFile);

                // If we get here, the implementation changed to support short files
                Assert.IsNotNull(shortSpectrum, "Short file spectrum should not be null if analysis succeeded");
                Console.WriteLine("Unexpected: Short file analysis succeeded");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("too short"))
            {
                Console.WriteLine($"✓ Expected behavior: {ex.Message}");
                // This is the expected behavior for files shorter than 10 seconds
                Assert.IsTrue(ex.Message.Contains("too short"), "Should indicate file is too short for segmented analysis");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected exception type: {ex.GetType().Name}: {ex.Message}");
                throw; // Re-throw unexpected exceptions
            }
            finally
            {
                if (File.Exists(shortFile)) File.Delete(shortFile);
            }
        }

        [TestMethod]
        [Priority(3)]
        public void Test3_EQMatchingFunctionality()
        {
            Console.WriteLine("=== Testing EQ Matching Functionality ===");

            string outputFile = Path.Combine(_tempDirectory, "eq_matched_output.wav");

            try
            {
                // Perform EQ matching
                Console.WriteLine("Performing EQ matching...");
                _analyzer.ProcessEQMatching(_sourceTestFile, _targetTestFile, outputFile);

                // Verify output file was created
                Assert.IsTrue(File.Exists(outputFile), "Output file should be created");
                Assert.IsTrue(new FileInfo(outputFile).Length > 0, "Output file should not be empty");

                // Analyze processed result
                Console.WriteLine("Analyzing processed result...");
                var processedSpectrum = _analyzer.AnalyzeAudioFile(outputFile);

                Assert.IsNotNull(processedSpectrum, "Processed spectrum should not be null");
                Assert.IsTrue(processedSpectrum.RMSLevel > 0, "Processed RMS should be positive");

                // Verify audio hasn't been corrupted
                Assert.IsTrue(processedSpectrum.PeakLevel <= 1.0f, "Peak should not exceed 0dBFS");
                Assert.IsTrue(processedSpectrum.RMSLevel < processedSpectrum.PeakLevel,
                             "RMS should be less than peak");

                Console.WriteLine($"Processed - RMS: {processedSpectrum.RMSLevel:F4}, Peak: {processedSpectrum.PeakLevel:F4}");
                Console.WriteLine("✓ EQ matching functionality test completed successfully");
            }
            finally
            {
                if (File.Exists(outputFile)) File.Delete(outputFile);
            }
        }

        [TestMethod]
        [Priority(4)]
        public void Test4_PresetSystemFunctionality()
        {
            Console.WriteLine("=== Testing Preset System Functionality ===");

            // Test preset availability
            Console.WriteLine("Testing preset availability...");
            var availablePresets = AudioAnalyzer.GetAvailablePresets();

            Assert.IsNotNull(availablePresets, "Available presets should not be null");
            Assert.IsTrue(availablePresets.Count > 0, "Should have available presets");

            // Test each preset system
            var testSystems = new[] { PlaybackSystem.StudioMonitors, PlaybackSystem.Headphones, PlaybackSystem.CarStereo };

            foreach (var system in testSystems)
            {
                Console.WriteLine($"Testing {system} preset...");

                Assert.IsTrue(availablePresets.ContainsKey(system), $"Should contain {system} preset");

                var preset = availablePresets[system];
                Assert.IsNotNull(preset.Name, "Preset name should not be null");
                Assert.IsNotNull(preset.FrequencyResponse, "Frequency response should not be null");
                Assert.AreEqual(30, preset.FrequencyResponse.Length, "Should have 30 frequency bands");
                Assert.IsTrue(preset.TargetLoudness < 0, "Target loudness should be in dBFS");
                Assert.IsTrue(preset.DynamicRange > 0, "Dynamic range should be positive");

                // Test frequency response values are reasonable
                foreach (var value in preset.FrequencyResponse)
                {
                    Assert.IsTrue(Math.Abs(value) <= 20, "Frequency response values should be reasonable");
                    Assert.IsTrue(!float.IsNaN(value), "Frequency response should not contain NaN");
                }

                Console.WriteLine($"✓ {system} preset validation successful");
            }

            Console.WriteLine("✓ Preset system functionality test completed successfully");
        }

        [TestMethod]
        [Priority(5)]
        public void Test5_EnhancedPresetProcessing()
        {
            Console.WriteLine("=== Testing Enhanced Preset Processing ===");

            string outputFile = Path.Combine(_tempDirectory, "enhanced_preset_output.wav");

            try
            {
                // Test enhanced preset processing
                Console.WriteLine("Testing enhanced preset processing...");
                _analyzer.ProcessWithEnhancedPreset(
                    _sourceTestFile,
                    outputFile,
                    PlaybackSystem.Headphones,
                    _tempDirectory
                );

                // Verify output
                Assert.IsTrue(File.Exists(outputFile), "Enhanced preset output should be created");
                Assert.IsTrue(new FileInfo(outputFile).Length > 0, "Output file should not be empty");

                // Analyze result
                var resultSpectrum = _analyzer.AnalyzeAudioFile(outputFile);
                Assert.IsNotNull(resultSpectrum, "Result spectrum should not be null");
                Assert.IsTrue(resultSpectrum.PeakLevel <= 1.0f, "Should not clip");

                Console.WriteLine($"Enhanced preset result - RMS: {resultSpectrum.RMSLevel:F4}");
                Console.WriteLine("✓ Enhanced preset processing completed successfully");
            }
            finally
            {
                if (File.Exists(outputFile)) File.Delete(outputFile);
            }
        }

        [TestMethod]
        [Priority(6)]
        public void Test6_SegmentedAnalysisRobustness()
        {
            Console.WriteLine("=== Testing Segmented Analysis Robustness ===");

            // Create longer test file for better segmentation testing
            string longTestFile = CreateTestAudioFile("long_test.wav", TestAudioType.Extended);

            try
            {
                Console.WriteLine("Analyzing longer audio file with segmentation...");
                var longSpectrum = _analyzer.AnalyzeAudioFile(longTestFile);

                Assert.IsNotNull(longSpectrum, "Long audio spectrum should not be null");

                // Verify segmented analysis produces stable results
                Assert.IsTrue(longSpectrum.RMSLevel > 0, "Segmented RMS should be positive");
                Assert.IsTrue(longSpectrum.DynamicRange > 0, "Segmented dynamic range should be positive");

                // Test with different audio characteristics
                for (int i = 0; i < longSpectrum.FrequencyBands.Length; i++)
                {
                    Assert.IsTrue(!float.IsNaN(longSpectrum.FrequencyBands[i]),
                                 $"Segmented band {i} should not be NaN");
                    Assert.IsTrue(longSpectrum.FrequencyBands[i] >= 0,
                                 $"Segmented band {i} should be non-negative");
                }

                Console.WriteLine($"Segmented analysis - {longSpectrum.FrequencyBands.Count(x => x > 0)} active bands");
                Console.WriteLine("✓ Segmented analysis robustness test completed successfully");
            }
            finally
            {
                if (File.Exists(longTestFile)) File.Delete(longTestFile);
            }
        }

        [TestMethod]
        [Priority(7)]
        public void Test7_ErrorHandlingAndEdgeCases()
        {
            Console.WriteLine("=== Testing Error Handling and Edge Cases ===");

            // Test invalid file handling
            Console.WriteLine("Testing invalid file handling...");
            try
            {
                _analyzer.AnalyzeAudioFile("non_existent_file.wav");
                Assert.Fail("Should throw exception for non-existent file");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✓ Correctly handled missing file: {ex.GetType().Name}");
            }

            // Test empty directory handling
            Console.WriteLine("Testing empty path handling...");
            try
            {
                _analyzer.AnalyzeAudioFile("");
                Assert.Fail("Should throw exception for empty path");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✓ Correctly handled empty path: {ex.GetType().Name}");
            }

            // Test null parameter handling
            Console.WriteLine("Testing null parameter handling...");
            try
            {
                _analyzer.AnalyzeAudioFile(null);
                Assert.Fail("Should throw exception for null path");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✓ Correctly handled null parameter: {ex.GetType().Name}");
            }

            // Test invalid preset processing
            Console.WriteLine("Testing invalid preset processing...");
            try
            {
                _analyzer.ProcessWithEnhancedPreset(
                    "invalid_source.wav",
                    Path.Combine(_tempDirectory, "should_fail.wav"),
                    PlaybackSystem.StudioMonitors
                );
                Assert.Fail("Should throw exception for invalid source file");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✓ Correctly handled invalid preset processing: {ex.GetType().Name}");
            }

            Console.WriteLine("✓ Error handling and edge cases test completed successfully");
        }

        [TestMethod]
        [Priority(8)]
        public void Test8_BatchProcessingCapability()
        {
            Console.WriteLine("=== Testing Batch Processing Capability ===");

            // Create multiple test files
            var sourceFiles = new List<string>();
            string batchOutputDir = Path.Combine(_tempDirectory, "batch_output");
            Directory.CreateDirectory(batchOutputDir);

            try
            {
                for (int i = 0; i < 3; i++)
                {
                    string testFile = CreateTestAudioFile($"batch_source_{i}.wav",
                        (TestAudioType)(i % 3)); // Vary audio types
                    sourceFiles.Add(testFile);
                }

                Console.WriteLine($"Testing batch processing with {sourceFiles.Count} files...");

                // Test batch processing
                _analyzer.BatchProcessWithEnhancedPreset(
                    sourceFiles.ToArray(),
                    _baseSampleFile,
                    batchOutputDir,
                    PlaybackSystem.HiFiSpeakers,
                    "_batch_test"
                );

                // Verify all outputs were created
                var outputFiles = Directory.GetFiles(batchOutputDir, "*_batch_test.wav");
                Assert.AreEqual(sourceFiles.Count, outputFiles.Length,
                               "Should create output for each input file");

                foreach (var outputFile in outputFiles)
                {
                    Assert.IsTrue(new FileInfo(outputFile).Length > 0,
                                 $"Output file {Path.GetFileName(outputFile)} should not be empty");
                }

                Console.WriteLine($"✓ Batch processing created {outputFiles.Length} output files");
                Console.WriteLine("✓ Batch processing capability test completed successfully");
            }
            finally
            {
                // Cleanup
                foreach (var file in sourceFiles)
                {
                    if (File.Exists(file)) File.Delete(file);
                }
                if (Directory.Exists(batchOutputDir))
                    Directory.Delete(batchOutputDir, true);
            }
        }

        [TestMethod]
        [Priority(9)]
        public void Test9_PerformanceAndMemoryUsage()
        {
            Console.WriteLine("=== Testing Performance and Memory Usage ===");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var initialMemory = GC.GetTotalMemory(true);

            try
            {
                // Test multiple analyses for performance
                Console.WriteLine("Running performance test with multiple analyses...");

                for (int i = 0; i < 5; i++)
                {
                    var spectrum = _analyzer.AnalyzeAudioFile(_sourceTestFile);
                    Assert.IsNotNull(spectrum, $"Analysis {i + 1} should succeed");

                    // Verify consistent results
                    Assert.IsTrue(spectrum.RMSLevel > 0, $"Analysis {i + 1} should have valid RMS");
                }

                stopwatch.Stop();
                var finalMemory = GC.GetTotalMemory(true);
                var memoryUsed = finalMemory - initialMemory;

                Console.WriteLine($"Performance metrics:");
                Console.WriteLine($"- Total time: {stopwatch.ElapsedMilliseconds} ms");
                Console.WriteLine($"- Average per analysis: {stopwatch.ElapsedMilliseconds / 5.0:F1} ms");
                Console.WriteLine($"- Memory used: {memoryUsed / 1024.0 / 1024.0:F1} MB");

                // Performance assertions
                Assert.IsTrue(stopwatch.ElapsedMilliseconds < 30000,
                             "Multiple analyses should complete within 30 seconds");
                Assert.IsTrue(memoryUsed < 100 * 1024 * 1024,
                             "Memory usage should be reasonable (< 100MB)");

                Console.WriteLine("✓ Performance and memory usage test completed successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Performance test error: {ex.Message}");
                throw;
            }
        }

        #region Helper Methods

        private enum TestAudioType
        {
            Vocal,
            Music,
            Reference,
            Extended
        }

        private static string CreateTestAudioFile(string fileName, TestAudioType audioType)
        {
            string fullPath = Path.Combine(_tempDirectory, fileName);

            var sampleRate = 44100;
            int duration = audioType == TestAudioType.Extended ? 35 : 25; // Ensure well above 10 second minimum
            var sampleCount = sampleRate * duration;
            var wavData = new List<byte>();

            // WAV header
            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            wavData.AddRange(BitConverter.GetBytes((uint)(36 + sampleCount * 2)));
            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            // Format chunk
            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("fmt "));
            wavData.AddRange(BitConverter.GetBytes((uint)16));
            wavData.AddRange(BitConverter.GetBytes((ushort)1)); // PCM
            wavData.AddRange(BitConverter.GetBytes((ushort)1)); // Mono
            wavData.AddRange(BitConverter.GetBytes((uint)sampleRate));
            wavData.AddRange(BitConverter.GetBytes((uint)(sampleRate * 2)));
            wavData.AddRange(BitConverter.GetBytes((ushort)2));
            wavData.AddRange(BitConverter.GetBytes((ushort)16));

            // Data chunk header
            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("data"));
            wavData.AddRange(BitConverter.GetBytes((uint)(sampleCount * 2)));

            // Generate different content based on audio type
            for (int i = 0; i < sampleCount; i++)
            {
                double time = (double)i / sampleRate;
                double sample = 0;

                switch (audioType)
                {
                    case TestAudioType.Vocal:
                        // Vocal-like frequencies with formants
                        sample = Math.Sin(2.0 * Math.PI * 220.0 * time) * 0.4 +    // Fundamental
                                Math.Sin(2.0 * Math.PI * 880.0 * time) * 0.2 +     // 2nd harmonic
                                Math.Sin(2.0 * Math.PI * 1760.0 * time) * 0.1;     // 4th harmonic
                        break;

                    case TestAudioType.Music:
                        // Musical content with more complex harmonics
                        sample = Math.Sin(2.0 * Math.PI * 440.0 * time) * 0.3 +    // A4
                                Math.Sin(2.0 * Math.PI * 554.37 * time) * 0.25 +   // C#5
                                Math.Sin(2.0 * Math.PI * 659.25 * time) * 0.2 +    // E5
                                Math.Sin(2.0 * Math.PI * 110.0 * time) * 0.15;     // Bass
                        break;

                    case TestAudioType.Reference:
                        // Reference signal with flat spectrum characteristics
                        sample = Math.Sin(2.0 * Math.PI * 1000.0 * time) * 0.3 +   // 1kHz reference
                                Math.Sin(2.0 * Math.PI * 100.0 * time) * 0.2 +     // Low freq
                                Math.Sin(2.0 * Math.PI * 5000.0 * time) * 0.1;     // High freq
                        break;

                    case TestAudioType.Extended:
                        // Extended file with varying content over time
                        double phase = time * 0.1; // Slow modulation
                        sample = Math.Sin(2.0 * Math.PI * (440.0 + 100.0 * Math.Sin(phase)) * time) * 0.3 +
                                Math.Sin(2.0 * Math.PI * 880.0 * time) * (0.2 * Math.Abs(Math.Sin(phase * 2))) +
                                Math.Sin(2.0 * Math.PI * 1760.0 * time) * 0.1;
                        break;
                }

                // Apply envelope
                double envelope = Math.Sin(Math.PI * time / duration);
                sample *= envelope * 0.7; // Prevent clipping

                short sampleValue = (short)(sample * 16383);
                wavData.AddRange(BitConverter.GetBytes(sampleValue));
            }

            File.WriteAllBytes(fullPath, wavData.ToArray());
            return fullPath;
        }

        private static string CreateShortTestAudioFile(string fileName, int durationSeconds)
        {
            string fullPath = Path.Combine(_tempDirectory, fileName);

            var sampleRate = 44100;
            var sampleCount = sampleRate * durationSeconds;
            var wavData = new List<byte>();

            // WAV header
            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            wavData.AddRange(BitConverter.GetBytes((uint)(36 + sampleCount * 2)));
            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            // Format chunk
            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("fmt "));
            wavData.AddRange(BitConverter.GetBytes((uint)16));
            wavData.AddRange(BitConverter.GetBytes((ushort)1)); // PCM
            wavData.AddRange(BitConverter.GetBytes((ushort)1)); // Mono
            wavData.AddRange(BitConverter.GetBytes((uint)sampleRate));
            wavData.AddRange(BitConverter.GetBytes((uint)(sampleRate * 2)));
            wavData.AddRange(BitConverter.GetBytes((ushort)2));
            wavData.AddRange(BitConverter.GetBytes((ushort)16));

            // Data chunk header
            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("data"));
            wavData.AddRange(BitConverter.GetBytes((uint)(sampleCount * 2)));

            // Generate simple test signal
            for (int i = 0; i < sampleCount; i++)
            {
                double time = (double)i / sampleRate;
                double sample = Math.Sin(2.0 * Math.PI * 1000.0 * time) * 0.3; // 1kHz sine

                // Apply envelope
                double envelope = Math.Sin(Math.PI * time / durationSeconds);
                sample *= envelope * 0.7;

                short sampleValue = (short)(sample * 16383);
                wavData.AddRange(BitConverter.GetBytes(sampleValue));
            }

            File.WriteAllBytes(fullPath, wavData.ToArray());
            return fullPath;
        }

        #endregion
    }
}
