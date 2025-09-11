using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Sources;
using Ownaudio.Utilities.Matchering;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Ownaudio.Tests.Matchering
{
    /// <summary>
    /// Comprehensive tests for AudioMatchering functionality
    /// Tests spectrum analysis, EQ matching, and audio processing capabilities
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class AudioMatcheringTests
    {
#nullable disable
        private static AudioAnalyzer _analyzer;
        private static string _sourceAudioFile;
        private static string _targetAudioFile;
        private static string _outputDirectory;
        private static SourceManager _manager;
#nullable restore

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            Console.WriteLine("Initializing AudioMatchering test system...");

            bool initialized = OwnAudio.Initialize();
            Assert.IsTrue(initialized, "OwnAudio should initialize successfully");

            _manager = SourceManager.Instance;

            _analyzer = new AudioAnalyzer();
            _outputDirectory = Path.Combine(Path.GetTempPath(), "AudioMatcheringTests");

            if (!Directory.Exists(_outputDirectory))
                Directory.CreateDirectory(_outputDirectory);

            // Create test audio files with different characteristics
            _sourceAudioFile = CreateSourceAudioFile();
            _targetAudioFile = CreateTargetAudioFile();

            Console.WriteLine($"Test files created in: {_outputDirectory}");
            Console.WriteLine("AudioMatchering test setup complete.");
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            Console.WriteLine("Cleaning up AudioMatchering test system...");

            try
            {
                _manager?.Stop();
                _manager?.ResetAll();

                if (Directory.Exists(_outputDirectory))
                    Directory.Delete(_outputDirectory, true);

                OwnAudio.Free();

                Console.WriteLine("AudioMatchering test cleanup complete.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cleanup warning: {ex.Message}");
            }
        }

        [TestMethod]
        [Priority(1)]
        public void Test1_SpectrumAnalysisBasics()
        {
            Console.WriteLine("=== Testing Spectrum Analysis Basics ===");

            // Test source audio analysis
            Console.WriteLine("Analyzing source audio spectrum...");
            var sourceSpectrum = _analyzer.AnalyzeAudioFile(_sourceAudioFile);

            Assert.IsNotNull(sourceSpectrum, "Source spectrum should not be null");
            Assert.IsNotNull(sourceSpectrum.FrequencyBands, "Frequency bands should not be null");
            Assert.AreEqual(10, sourceSpectrum.FrequencyBands.Length, "Should have 10 frequency bands");

            // Verify spectrum values are in valid range
            foreach (var band in sourceSpectrum.FrequencyBands)
            {
                Assert.IsTrue(band >= 0.0f && band <= 1.0f, $"Band value {band} should be normalized (0-1)");
            }

            // Test dynamics values
            Assert.IsTrue(sourceSpectrum.RMSLevel > 0, "RMS level should be positive");
            Assert.IsTrue(sourceSpectrum.PeakLevel > 0, "Peak level should be positive");
            Assert.IsTrue(sourceSpectrum.PeakLevel >= sourceSpectrum.RMSLevel, "Peak should be >= RMS");

            Console.WriteLine($"Source analysis - RMS: {sourceSpectrum.RMSLevel:F3}, Peak: {sourceSpectrum.PeakLevel:F3}");
            Console.WriteLine($"Dynamic Range: {sourceSpectrum.DynamicRange:F1}dB, Loudness: {sourceSpectrum.Loudness:F1} LUFS");

            // Test target audio analysis
            Console.WriteLine("Analyzing target audio spectrum...");
            var targetSpectrum = _analyzer.AnalyzeAudioFile(_targetAudioFile);

            Assert.IsNotNull(targetSpectrum, "Target spectrum should not be null");
            Assert.AreEqual(10, targetSpectrum.FrequencyBands.Length, "Target should have 10 frequency bands");

            // Verify spectrums are different (since we created different test files)
            bool spectrumsDifferent = false;
            for (int i = 0; i < sourceSpectrum.FrequencyBands.Length; i++)
            {
                if (Math.Abs(sourceSpectrum.FrequencyBands[i] - targetSpectrum.FrequencyBands[i]) > 0.01f)
                {
                    spectrumsDifferent = true;
                    break;
                }
            }
            Assert.IsTrue(spectrumsDifferent, "Source and target spectrums should be different");

            Console.WriteLine("✓ Spectrum analysis basics completed successfully");
        }

        [TestMethod]
        [Priority(2)]
        public void Test2_FrequencyBandAccuracy()
        {
            Console.WriteLine("=== Testing Frequency Band Accuracy ===");

            // Create test files with specific frequency content
            var testFiles = CreateFrequencyTestFiles();

            foreach (var testFile in testFiles)
            {
                Console.WriteLine($"Testing frequency detection: {testFile.Key}Hz");
                var spectrum = _analyzer.AnalyzeAudioFile(testFile.Value);

                // Find the band that should contain this frequency
                int expectedBand = GetExpectedBandIndex(testFile.Key);
                
                if (expectedBand >= 0)
                {
                    // The expected band should have higher energy than average
                    float averageEnergy = spectrum.FrequencyBands.Average();
                    float expectedBandEnergy = spectrum.FrequencyBands[expectedBand];

                    Assert.IsTrue(expectedBandEnergy > averageEnergy * 0.8f, 
                        $"Band {expectedBand} should have elevated energy for {testFile.Key}Hz");

                    Console.WriteLine($"  {testFile.Key}Hz -> Band {expectedBand}: {expectedBandEnergy:F3} (avg: {averageEnergy:F3})");
                }

                // Cleanup test file
                File.Delete(testFile.Value);
            }

            Console.WriteLine("✓ Frequency band accuracy test completed successfully");
        }

        [TestMethod]
        [Priority(3)]
        public void Test3_EQCalculationLogic()
        {
            Console.WriteLine("=== Testing EQ Calculation Logic ===");

            // Create test spectrums with known differences
            var flatSpectrum = CreateFlatTestSpectrum();
            var bassHeavySpectrum = CreateBassHeavyTestSpectrum();
            var trebleHeavySpectrum = CreateTrebleHeavyTestSpectrum();

            // Test bass compensation
            Console.WriteLine("Testing bass compensation logic...");
            var sourceSpectrum = new AudioSpectrum
            {
                FrequencyBands = flatSpectrum,
                RMSLevel = 0.1f,
                PeakLevel = 0.3f,
                DynamicRange = 12.0f,
                Loudness = -20.0f
            };

            var targetSpectrum = new AudioSpectrum
            {
                FrequencyBands = bassHeavySpectrum,
                RMSLevel = 0.12f,
                PeakLevel = 0.35f,
                DynamicRange = 10.0f,
                Loudness = -18.0f
            };

            // Use reflection to test internal EQ calculation method
            var method = typeof(AudioAnalyzer).GetMethod("CalculateDirectEQAdjustments", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            Assert.IsNotNull(method, "CalculateDirectEQAdjustments method should exist");

            var eqAdjustments = (float[])method.Invoke(_analyzer, new object[] { sourceSpectrum, targetSpectrum });

            Assert.IsNotNull(eqAdjustments, "EQ adjustments should not be null");
            Assert.AreEqual(10, eqAdjustments.Length, "Should have 10 EQ adjustments");

            // Low frequencies should have positive adjustment (boost)
            Assert.IsTrue(eqAdjustments[0] > 0, "31Hz should have positive adjustment for bass-heavy target");
            Assert.IsTrue(eqAdjustments[1] > 0, "63Hz should have positive adjustment for bass-heavy target");

            // Test treble compensation
            Console.WriteLine("Testing treble compensation logic...");
            targetSpectrum.FrequencyBands = trebleHeavySpectrum;

            eqAdjustments = (float[])method.Invoke(_analyzer, new object[] { sourceSpectrum, targetSpectrum });

            // High frequencies should have positive adjustment
            Assert.IsTrue(eqAdjustments[8] > 0, "8kHz should have positive adjustment for treble-heavy target");
            Assert.IsTrue(eqAdjustments[9] > 0, "16kHz should have positive adjustment for treble-heavy target");

            // Verify adjustments are within reasonable range
            foreach (var adjustment in eqAdjustments)
            {
                Assert.IsTrue(Math.Abs(adjustment) <= 15.0f, $"EQ adjustment {adjustment} should be within ±15dB");
            }

            Console.WriteLine("✓ EQ calculation logic test completed successfully");
        }

        [TestMethod]
        [Priority(4)]
        public void Test4_DynamicsAnalysis()
        {
            Console.WriteLine("=== Testing Dynamics Analysis ===");

            // Create test files with different dynamic characteristics
            var quietFile = CreateQuietAudioFile();
            var loudFile = CreateLoudAudioFile();
            var compressedFile = CreateCompressedAudioFile();

            try
            {
                // Test quiet audio dynamics
                Console.WriteLine("Analyzing quiet audio dynamics...");
                var quietSpectrum = _analyzer.AnalyzeAudioFile(quietFile);
                
                Assert.IsTrue(quietSpectrum.RMSLevel < 0.1f, "Quiet file should have low RMS");
                Assert.IsTrue(quietSpectrum.Loudness < -30.0f, "Quiet file should have low loudness");

                // Test loud audio dynamics
                Console.WriteLine("Analyzing loud audio dynamics...");
                var loudSpectrum = _analyzer.AnalyzeAudioFile(loudFile);
                
                Assert.IsTrue(loudSpectrum.RMSLevel > quietSpectrum.RMSLevel, "Loud file should have higher RMS");
                Assert.IsTrue(loudSpectrum.Loudness > quietSpectrum.Loudness, "Loud file should have higher loudness");

                // Test compressed audio dynamics
                Console.WriteLine("Analyzing compressed audio dynamics...");
                var compressedSpectrum = _analyzer.AnalyzeAudioFile(compressedFile);
                
                Assert.IsTrue(compressedSpectrum.DynamicRange < 10.0f, "Compressed file should have low dynamic range");

                // Test dynamic amplification settings calculation
                var method = typeof(AudioAnalyzer).GetMethod("CalculateDynamicAmpSettings",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                Assert.IsNotNull(method, "CalculateDynamicAmpSettings method should exist");

                var ampSettings = (DynamicAmpSettings)method.Invoke(_analyzer, 
                    new object[] { quietSpectrum, loudSpectrum });

                Assert.IsNotNull(ampSettings, "Amp settings should not be null");
                Assert.IsTrue(ampSettings.TargetLevel >= -20.0f && ampSettings.TargetLevel <= -5.0f, 
                    "Target level should be in reasonable range");
                Assert.IsTrue(ampSettings.MaxGain > 0 && ampSettings.MaxGain <= 10.0f, 
                    "Max gain should be positive and reasonable");

                Console.WriteLine($"Dynamic amp settings - Target: {ampSettings.TargetLevel:F1}dB, Max gain: {ampSettings.MaxGain:F1}dB");
            }
            finally
            {
                // Cleanup test files
                File.Delete(quietFile);
                File.Delete(loudFile);
                File.Delete(compressedFile);
            }

            Console.WriteLine("✓ Dynamics analysis test completed successfully");
        }

        [TestMethod]
        [Priority(5)]
        public void Test5_CompleteEQMatching()
        {
            Console.WriteLine("=== Testing Complete EQ Matching Process ===");

            string outputFile = Path.Combine(_outputDirectory, "matched_output.wav");

            // Test complete EQ matching process
            Console.WriteLine("Running complete EQ matching...");
            
            try
            {
                _analyzer.ProcessEQMatching(_sourceAudioFile, _targetAudioFile, outputFile);

                // Verify output file was created
                Assert.IsTrue(File.Exists(outputFile), "Output file should be created");

                var outputInfo = new FileInfo(outputFile);
                Assert.IsTrue(outputInfo.Length > 1000, "Output file should have reasonable size");

                // Analyze the output to verify processing occurred
                Console.WriteLine("Analyzing processed output...");
                var outputSpectrum = _analyzer.AnalyzeAudioFile(outputFile);

                Assert.IsNotNull(outputSpectrum, "Output spectrum should not be null");
                Assert.IsTrue(outputSpectrum.RMSLevel > 0, "Output should have audio content");

                // Compare with original source
                var originalSpectrum = _analyzer.AnalyzeAudioFile(_sourceAudioFile);
                
                bool spectrumChanged = false;
                for (int i = 0; i < outputSpectrum.FrequencyBands.Length; i++)
                {
                    if (Math.Abs(outputSpectrum.FrequencyBands[i] - originalSpectrum.FrequencyBands[i]) > 0.05f)
                    {
                        spectrumChanged = true;
                        break;
                    }
                }

                Assert.IsTrue(spectrumChanged, "EQ processing should change the spectrum");

                Console.WriteLine($"Original loudness: {originalSpectrum.Loudness:F1} LUFS");
                Console.WriteLine($"Processed loudness: {outputSpectrum.Loudness:F1} LUFS");
            }
            catch (Exception ex)
            {
                Assert.Fail($"EQ matching failed: {ex.Message}");
            }

            Console.WriteLine("✓ Complete EQ matching test completed successfully");
        }

        [TestMethod]
        [Priority(6)]
        public void Test6_SafetyAndLimiting()
        {
            Console.WriteLine("=== Testing Safety and Limiting Features ===");

            // Create extreme test case that would normally cause clipping
            var extremeSourceFile = CreateExtremeSourceFile();
            var extremeTargetFile = CreateExtremeTargetFile();
            string safeOutputFile = Path.Combine(_outputDirectory, "safe_output.wav");

            try
            {
                Console.WriteLine("Testing safety limiting with extreme inputs...");
                _analyzer.ProcessEQMatching(extremeSourceFile, extremeTargetFile, safeOutputFile);

                Assert.IsTrue(File.Exists(safeOutputFile), "Safe output file should be created");

                // Analyze output for clipping and safety
                var safeSpectrum = _analyzer.AnalyzeAudioFile(safeOutputFile);
                
                // Peak level should be limited
                Assert.IsTrue(safeSpectrum.PeakLevel <= 1.0f, "Peak level should not exceed digital maximum");
                Assert.IsTrue(safeSpectrum.PeakLevel >= 0.1f, "Peak level should be reasonable after safety processing");

                Console.WriteLine($"Safe output peak level: {safeSpectrum.PeakLevel:F3}");
                Console.WriteLine($"Safe output RMS level: {safeSpectrum.RMSLevel:F3}");

                // Test that extreme EQ adjustments are controlled
                var sourceSpectrum = _analyzer.AnalyzeAudioFile(extremeSourceFile);
                var targetSpectrum = _analyzer.AnalyzeAudioFile(extremeTargetFile);

                var method = typeof(AudioAnalyzer).GetMethod("CalculateDirectEQAdjustments",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                var eqAdjustments = (float[])method.Invoke(_analyzer, new object[] { sourceSpectrum, targetSpectrum });

                // No single adjustment should be extremely high
                foreach (var adjustment in eqAdjustments)
                {
                    Assert.IsTrue(Math.Abs(adjustment) <= 12.0f, 
                        $"Extreme EQ adjustment {adjustment} should be limited for safety");
                }

                Console.WriteLine("Safety limiting working correctly");
            }
            finally
            {
                File.Delete(extremeSourceFile);
                File.Delete(extremeTargetFile);
            }

            Console.WriteLine("✓ Safety and limiting test completed successfully");
        }

        #region Helper Methods

        private static string CreateSourceAudioFile()
        {
            string filePath = Path.Combine(_outputDirectory, "source_test.wav");
            var samples = GenerateTestAudio(44100 * 3, new[] { 440.0f, 880.0f, 1760.0f }, 0.3f);
            WriteWaveFile(filePath, samples, 44100);
            return filePath;
        }

        private static string CreateTargetAudioFile()
        {
            string filePath = Path.Combine(_outputDirectory, "target_test.wav");
            // Different frequency content and levels
            var samples = GenerateTestAudio(44100 * 3, new[] { 220.0f, 440.0f, 2000.0f, 4000.0f }, 0.5f);
            WriteWaveFile(filePath, samples, 44100);
            return filePath;
        }

        private static Dictionary<float, string> CreateFrequencyTestFiles()
        {
            var testFiles = new Dictionary<float, string>();
            var testFrequencies = new[] { 50.0f, 125.0f, 500.0f, 1000.0f, 2000.0f, 8000.0f };

            foreach (var freq in testFrequencies)
            {
                string filePath = Path.Combine(_outputDirectory, $"test_{freq}Hz.wav");
                var samples = GenerateTestAudio(44100, new[] { freq }, 0.5f);
                WriteWaveFile(filePath, samples, 44100);
                testFiles[freq] = filePath;
            }

            return testFiles;
        }

        private static string CreateQuietAudioFile()
        {
            string filePath = Path.Combine(_outputDirectory, "quiet_test.wav");
            var samples = GenerateTestAudio(44100 * 2, new[] { 440.0f }, 0.05f);
            WriteWaveFile(filePath, samples, 44100);
            return filePath;
        }

        private static string CreateLoudAudioFile()
        {
            string filePath = Path.Combine(_outputDirectory, "loud_test.wav");
            var samples = GenerateTestAudio(44100 * 2, new[] { 440.0f }, 0.8f);
            WriteWaveFile(filePath, samples, 44100);
            return filePath;
        }

        private static string CreateCompressedAudioFile()
        {
            string filePath = Path.Combine(_outputDirectory, "compressed_test.wav");
            var samples = GenerateTestAudio(44100 * 2, new[] { 440.0f }, 0.6f);
            
            // Simulate compression by reducing dynamic range
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = Math.Sign(samples[i]) * (float)Math.Sqrt(Math.Abs(samples[i])) * 0.7f;
            }
            
            WriteWaveFile(filePath, samples, 44100);
            return filePath;
        }

        private static string CreateExtremeSourceFile()
        {
            string filePath = Path.Combine(_outputDirectory, "extreme_source.wav");
            // Very bass-heavy content
            var samples = GenerateTestAudio(44100 * 2, new[] { 40.0f, 80.0f, 160.0f }, 0.2f);
            WriteWaveFile(filePath, samples, 44100);
            return filePath;
        }

        private static string CreateExtremeTargetFile()
        {
            string filePath = Path.Combine(_outputDirectory, "extreme_target.wav");
            // Very treble-heavy content
            var samples = GenerateTestAudio(44100 * 2, new[] { 4000.0f, 8000.0f, 12000.0f }, 0.8f);
            WriteWaveFile(filePath, samples, 44100);
            return filePath;
        }

        private static string CreateEmptyAudioFile()
        {
            string filePath = Path.Combine(_outputDirectory, "empty_test.wav");
            var samples = new float[44100]; // All zeros
            WriteWaveFile(filePath, samples, 44100);
            return filePath;
        }

        private static string CreateCorruptedAudioFile()
        {
            string filePath = Path.Combine(_outputDirectory, "corrupted_test.wav");
            // Create invalid WAV data
            var corruptedData = new byte[] { 0x52, 0x49, 0x46, 0x46, 0xFF, 0xFF, 0xFF, 0xFF };
            File.WriteAllBytes(filePath, corruptedData);
            return filePath;
        }

        private static string CreateVeryShortAudioFile()
        {
            string filePath = Path.Combine(_outputDirectory, "very_short_test.wav");
            var samples = GenerateTestAudio(100, new[] { 440.0f }, 0.5f); // Only 100 samples
            WriteWaveFile(filePath, samples, 44100);
            return filePath;
        }

        private static float[] GenerateTestAudio(int sampleCount, float[] frequencies, float amplitude)
        {
            var samples = new float[sampleCount];
            var random = new Random(42); // Fixed seed for reproducible tests

            for (int i = 0; i < sampleCount; i++)
            {
                double time = (double)i / 44100.0;
                double sample = 0;

                foreach (var freq in frequencies)
                {
                    sample += Math.Sin(2.0 * Math.PI * freq * time) / frequencies.Length;
                }

                // Add slight noise for realism
                sample += (random.NextDouble() - 0.5) * 0.01;

                samples[i] = (float)(sample * amplitude);
            }

            return samples;
        }

        private static void WriteWaveFile(string filePath, float[] samples, int sampleRate)
        {
            var wavData = new List<byte>();

            // WAV header
            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            wavData.AddRange(BitConverter.GetBytes((uint)(36 + samples.Length * 2)));
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

            // Data chunk
            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("data"));
            wavData.AddRange(BitConverter.GetBytes((uint)(samples.Length * 2)));

            foreach (var sample in samples)
            {
                short sampleValue = (short)(Math.Max(-1.0f, Math.Min(1.0f, sample)) * 32767);
                wavData.AddRange(BitConverter.GetBytes(sampleValue));
            }

            File.WriteAllBytes(filePath, wavData.ToArray());
        }

        private static float[] CreateFlatTestSpectrum()
        {
            return Enumerable.Repeat(0.5f, 10).ToArray();
        }

        private static float[] CreateBassHeavyTestSpectrum()
        {
            return new float[] { 0.9f, 0.8f, 0.7f, 0.5f, 0.3f, 0.2f, 0.2f, 0.1f, 0.1f, 0.1f };
        }

        private static float[] CreateTrebleHeavyTestSpectrum()
        {
            return new float[] { 0.1f, 0.1f, 0.2f, 0.2f, 0.3f, 0.5f, 0.7f, 0.8f, 0.9f, 1.0f };
        }

        private static int GetExpectedBandIndex(float frequency)
        {
            var bands = new[] { 31.25f, 62.5f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };
            
            for (int i = 0; i < bands.Length; i++)
            {
                if (frequency <= bands[i] * 1.5f && frequency >= bands[i] * 0.5f)
                    return i;
            }
            
            return -1; // Frequency doesn't clearly fit in any band
        }

        #endregion
    }
}
