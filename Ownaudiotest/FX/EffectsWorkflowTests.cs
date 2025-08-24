using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio;
using Ownaudio.Fx;
using Ownaudio.Sources;
using Ownaudio.Processors;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Linq;

namespace Ownaudio.Tests.Effects
{
    /// <summary>
    /// Standard tests for OwnAudio effects following real usage scenarios
    /// These tests run sequentially and build upon each other like real usage
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class EffectsWorkflowTests
    {
#nullable disable
        private static SourceManager _manager;
        private static string _testAudioFile;
#nullable restore

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            // Initialize OwnAudio once for all tests - like real application startup
            Console.WriteLine("Initializing OwnAudio system for effects testing...");

            bool initialized = OwnAudio.Initialize();
            Assert.IsTrue(initialized, "OwnAudio should initialize successfully");

            _manager = SourceManager.Instance;

            // Create test audio file for effects processing
            _testAudioFile = CreateTestAudioFile("effects_test.wav");

            Console.WriteLine($"Effects test setup complete. Audio system ready.");
            Console.WriteLine($"Default output device: {OwnAudio.DefaultOutputDevice.Name}");
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            Console.WriteLine("Cleaning up effects test system...");

            try
            {
                _manager?.Stop();
                _manager?.ResetAll();

                // Clean up test files
                if (File.Exists(_testAudioFile)) File.Delete(_testAudioFile);

                OwnAudio.Free();
                Console.WriteLine("Effects test cleanup complete.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cleanup warning: {ex.Message}");
            }
        }

        [TestMethod]
        [Priority(1)]
        public void Test1_BasicParameterValidation()
        {
            Console.WriteLine("=== Testing Basic Parameter Validation ===");

            // AutoGain parameter tests
            Console.WriteLine("Testing AutoGain parameters...");
            var autoGain = new AutoGain();

            // Test valid range
            autoGain.TargetLevel = 0.5f;
            Assert.AreEqual(0.5f, autoGain.TargetLevel, 0.001f, "TargetLevel should accept valid value");

            // Test boundary clamping
            autoGain.TargetLevel = -0.1f; // Invalid, should clamp to minimum
            Assert.IsTrue(autoGain.TargetLevel >= 0.01f, "TargetLevel should clamp to minimum valid value");

            autoGain.MaximumGain = 15.0f; // Invalid, should clamp to maximum
            Assert.IsTrue(autoGain.MaximumGain <= 10.0f, "MaximumGain should clamp to maximum valid value");

            // Compressor parameter tests
            Console.WriteLine("Testing Compressor parameters...");
            var compressor = new Compressor();

            compressor.Threshold = 0.7f;
            Assert.AreEqual(0.7f, compressor.Threshold, 0.001f, "Threshold should accept valid value");

            compressor.Ratio = 150.0f; // Should clamp to max
            Assert.IsTrue(compressor.Ratio <= 100.0f, "Ratio should clamp to maximum");

            // Delay parameter tests
            Console.WriteLine("Testing Delay parameters...");
            var delay = new Delay();

            delay.Time = 500;
            Assert.AreEqual(500, delay.Time, "Time should accept valid value");

            delay.Time = 10000; // Should clamp to max
            Assert.IsTrue(delay.Time <= 5000, "Time should clamp to maximum");

            delay.Repeat = 1.5f; // Should clamp to max
            Assert.IsTrue(delay.Repeat <= 1.0f, "Repeat should clamp to maximum");

            Console.WriteLine("✓ Parameter validation tests completed successfully");
        }

        [TestMethod]
        [Priority(2)]
        public void Test2_PresetFunctionality()
        {
            Console.WriteLine("=== Testing Preset Functionality ===");

            // Reverb preset tests
            Console.WriteLine("Testing Reverb presets...");
            var reverb = new Reverb(ReverbPreset.Default);
            float defaultRoomSize = reverb.RoomSize;
            float defaultWetLevel = reverb.WetLevel;

            reverb.SetPreset(ReverbPreset.Cathedral);
            Assert.AreNotEqual(defaultRoomSize, reverb.RoomSize, "Cathedral preset should change RoomSize");
            Assert.AreNotEqual(defaultWetLevel, reverb.WetLevel, "Cathedral preset should change WetLevel");

            reverb.SetPreset(ReverbPreset.SmallRoom);
            Assert.IsTrue(reverb.RoomSize < 0.5f, "SmallRoom should have smaller room size");

            // Compressor preset tests
            Console.WriteLine("Testing Compressor presets...");
            var compressor = new Compressor(CompressorPreset.Default);
            float defaultThreshold = compressor.Threshold;

            compressor.SetPreset(CompressorPreset.VocalAggressive);
            Assert.AreNotEqual(defaultThreshold, compressor.Threshold, "VocalAggressive preset should change threshold");
            Assert.IsTrue(compressor.Ratio > 5.0f, "VocalAggressive should have high ratio");

            // Equalizer preset tests
            Console.WriteLine("Testing Equalizer presets...");
            var equalizer = new Equalizer(EqualizerPreset.Default);
            Assert.AreEqual(0.0f, equalizer.Band0Gain, 0.001f, "Default preset should have flat response");

            equalizer.SetPreset(EqualizerPreset.Bass);
            Assert.IsTrue(equalizer.Band0Gain > 0.0f, "Bass preset should boost low frequencies");

            equalizer.SetPreset(EqualizerPreset.Voice);
            Assert.IsTrue(equalizer.Band4Gain > 0.0f || equalizer.Band5Gain > 0.0f, "Voice preset should boost mid frequencies");

            Console.WriteLine("✓ Preset functionality tests completed successfully");
        }

        [TestMethod]
        [Priority(3)]
        public async Task Test3_RealTimeEffectProcessing()
        {
            Console.WriteLine("=== Testing Real-time Effect Processing ===");

            // Load test audio file
            Console.WriteLine("Loading test audio file...");
            bool loaded = await _manager.AddOutputSource(_testAudioFile, "EffectsTest");
            Assert.IsTrue(loaded, "Test audio should load successfully");

            // Create test effect processor
            Console.WriteLine("Setting up effect processor...");
            var effectProcessor = new TestEffectProcessor();
            _manager["EffectsTest"].CustomSampleProcessor = effectProcessor;
            Assert.IsTrue(effectProcessor.IsEnabled, "Effect processor should be enabled");

            // Start playback to process audio through effects
            Console.WriteLine("Starting playback with effects...");
            _manager.Play();
            Assert.AreEqual(SourceState.Playing, _manager.State, "Should be playing");

            // Let effects process audio
            await Task.Delay(500);

            // Verify effects are processing samples
            Assert.IsTrue(effectProcessor.ProcessedSamples > 0, "Effects should process samples");
            Assert.IsTrue(effectProcessor.AverageInput > 0, "Should detect audio input");

            // Test effect parameters during playback
            Console.WriteLine("Testing real-time parameter changes...");
            effectProcessor.SetReverbWet(0.8f);
            effectProcessor.SetCompressorRatio(8.0f);

            await Task.Delay(200);

            // Stop playback
            Console.WriteLine("Stopping playback...");
            _manager.Stop();
            Assert.AreEqual(SourceState.Idle, _manager.State, "Should be idle");

            Console.WriteLine("✓ Real-time effect processing completed successfully");
        }

        [TestMethod]
        [Priority(4)]
        public async Task Test4_ComplexEffectChain()
        {
            Console.WriteLine("=== Testing Complex Effect Chain ===");

            // Clear previous test
            if (_manager.Sources.Count > 0)
            {
                await _manager.RemoveOutputSource(0);
            }

            // Load audio and setup complex chain
            Console.WriteLine("Setting up complex effect chain...");
            bool loaded = await _manager.AddOutputSource(_testAudioFile, "ComplexChain");
            Assert.IsTrue(loaded, "Audio should load for complex chain test");

            var complexProcessor = new ComplexEffectChain();
            _manager["ComplexChain"].CustomSampleProcessor = complexProcessor;

            // Test individual effects in the chain
            Console.WriteLine("Testing individual effects in chain...");

            // Start with all effects enabled
            complexProcessor.SetAllEnabled(true);
            _manager.Play();

            await Task.Delay(200);
            Assert.IsTrue(complexProcessor.ProcessedSamples > 0, "Complex chain should process samples");

            // Test enabling/disabling effects during playback
            Console.WriteLine("Testing dynamic effect toggling...");
            complexProcessor.SetReverbEnabled(false);
            complexProcessor.SetDelayEnabled(false);

            await Task.Delay(200);

            complexProcessor.SetCompressorEnabled(false);
            complexProcessor.SetEqualizerEnabled(false);

            await Task.Delay(200);

            // Re-enable all effects
            complexProcessor.SetAllEnabled(true);

            await Task.Delay(200);

            _manager.Stop();

            Console.WriteLine("✓ Complex effect chain test completed successfully");
        }

        [TestMethod]
        [Priority(5)]
        public void Test5_EffectResetFunctionality()
        {
            Console.WriteLine("=== Testing Effect Reset Functionality ===");

            // Test Delay reset
            Console.WriteLine("Testing Delay reset...");
            var delay = new Delay(DelayPreset.ClassicEcho);
            delay.SampleRate = 44100;

            // Process samples to build up internal state
            float[] testSamples = GenerateTestSignal(1024, 440.0f);
            for (int i = 0; i < 10; i++)
            {
                delay.Process(testSamples.AsSpan());
            }

            // Reset and verify clean state
            delay.Reset();
            Array.Fill(testSamples, 0.1f); // Constant input
            delay.Process(testSamples.AsSpan());

            // After reset, delay should not immediately produce the full delayed signal
            Assert.IsTrue(Math.Abs(testSamples[0]) < 0.15f, "Delay should have minimal output immediately after reset");

            // Test Reverb reset
            Console.WriteLine("Testing Reverb reset...");
            var reverb = new Reverb(ReverbPreset.LargeHall);
            reverb.SampleRate = 44100;

            // Build up reverb tail
            Array.Fill(testSamples, 0.8f);
            for (int i = 0; i < 5; i++)
            {
                reverb.Process(testSamples.AsSpan());
            }

            reverb.Reset();
            Array.Fill(testSamples, 0.0f); // Silent input
            reverb.Process(testSamples.AsSpan());

            // After reset with silent input, output should be very low
            Assert.IsTrue(Math.Abs(testSamples[0]) < 0.01f, "Reverb should have minimal output after reset with silent input");

            // Test Compressor reset
            Console.WriteLine("Testing Compressor reset...");
            var compressor = new Compressor(CompressorPreset.VocalAggressive);
            compressor.SampleRate = 44100;

            // Build up compression state with loud signal
            Array.Fill(testSamples, 0.9f);
            for (int i = 0; i < 20; i++)
            {
                compressor.Process(testSamples.AsSpan());
            }

            compressor.Reset();
            Array.Fill(testSamples, 0.1f); // Quiet signal
            compressor.Process(testSamples.AsSpan());

            // After reset, quiet signal should pass through with minimal compression
            Assert.IsTrue(testSamples[0] > 0.08f, "Compressor should not heavily compress after reset");

            Console.WriteLine("✓ Effect reset functionality test completed successfully");
        }

        [TestMethod]
        [Priority(6)]
        public async Task Test6_EffectPerformanceAndStability()
        {
            Console.WriteLine("=== Testing Effect Performance and Stability ===");

            // Clear previous sources
            _manager.ResetAll();

            // Load test audio
            Console.WriteLine("Setting up performance test...");
            bool loaded = await _manager.AddOutputSource(_testAudioFile, "PerfTest");
            Assert.IsTrue(loaded, "Audio should load for performance test");

            var perfProcessor = new PerformanceTestProcessor();
            _manager["PerfTest"].CustomSampleProcessor = perfProcessor;

            // Run extended processing test
            Console.WriteLine("Running extended processing test...");
            _manager.Play();

            // Process for several seconds to test stability
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(100);

                // Simulate real-time parameter changes
                perfProcessor.RandomizeParameters();

                // Verify system is still stable
                Assert.AreEqual(SourceState.Playing, _manager.State, $"System should remain stable at iteration {i}");
            }

            _manager.Stop();

            // Verify performance metrics
            Assert.IsTrue(perfProcessor.ProcessedSamples > 100000, "Should process significant amount of samples");
            Assert.IsTrue(perfProcessor.AverageProcessingTime < 10.0, "Processing should be efficient (< 10ms average)");
            Assert.AreEqual(0, perfProcessor.ErrorCount, "Should have no processing errors");

            Console.WriteLine($"Processed {perfProcessor.ProcessedSamples} samples");
            Console.WriteLine($"Average processing time: {perfProcessor.AverageProcessingTime:F2} ms");
            Console.WriteLine("✓ Performance and stability test completed successfully");
        }

        [TestMethod]
        [Priority(7)]
        public void Test7_EffectErrorHandling()
        {
            Console.WriteLine("=== Testing Effect Error Handling ===");

            // Test invalid sample rate handling
            Console.WriteLine("Testing invalid sample rate handling...");
            var reverb = new Reverb();

            try
            {
                reverb.SampleRate = -1; // Invalid
                Assert.Fail("Should throw exception for invalid sample rate");
            }
            catch (ArgumentException)
            {
                Console.WriteLine("✓ Correctly rejected invalid sample rate");
            }

            // Test null span processing
            Console.WriteLine("Testing empty span processing...");
            var compressor = new Compressor();
            compressor.SampleRate = 44100;

            Span<float> emptySpan = Span<float>.Empty;
            compressor.Process(emptySpan); // Should not crash

            // Test very large spans
            Console.WriteLine("Testing large span processing...");
            var largeSpan = new float[100000];
            Array.Fill(largeSpan, 0.1f);

            var delay = new Delay();
            delay.SampleRate = 44100;
            delay.Process(largeSpan.AsSpan()); // Should not crash

            // Test rapid parameter changes
            Console.WriteLine("Testing rapid parameter changes...");
            var equalizer = new Equalizer();
            var testSpan = new float[512];
            Array.Fill(testSpan, 0.1f);

            for (int i = 0; i < 100; i++)
            {
                equalizer.Band0Gain = (float)(Math.Sin(i) * 10);
                equalizer.Band5Gain = (float)(Math.Cos(i) * 10);
                equalizer.Process(testSpan.AsSpan());
            }

            Console.WriteLine("✓ Error handling tests completed successfully");
        }

        #region Helper Classes

        public class TestEffectProcessor : SampleProcessorBase
        {
            private readonly Reverb reverb;
            private readonly Compressor compressor;
            private readonly Equalizer equalizer;

            public int ProcessedSamples { get; private set; }
            public float AverageInput { get; private set; }
            private float inputSum = 0f;

            public TestEffectProcessor()
            {
                reverb = new Reverb(ReverbPreset.VocalBooth);
                reverb.SampleRate = 44100;

                compressor = new Compressor(CompressorPreset.VocalGentle);
                compressor.SampleRate = 44100;

                equalizer = new Equalizer(EqualizerPreset.Voice);
            }

            public override void Process(Span<float> samples)
            {
                // Update statistics
                for (int i = 0; i < samples.Length; i++)
                {
                    inputSum += Math.Abs(samples[i]);
                }
                ProcessedSamples += samples.Length;
                AverageInput = inputSum / ProcessedSamples;

                // Apply effects
                equalizer.Process(samples);
                compressor.Process(samples);
                reverb.Process(samples);
            }

            public override void Reset()
            {
                reverb.Reset();
                compressor.Reset();
                equalizer.Reset();
                ProcessedSamples = 0;
                AverageInput = 0f;
                inputSum = 0f;
            }

            public void SetReverbWet(float wetLevel)
            {
                reverb.WetLevel = wetLevel;
            }

            public void SetCompressorRatio(float ratio)
            {
                compressor.Ratio = ratio;
            }
        }

        public class ComplexEffectChain : SampleProcessorBase
        {
            private readonly AutoGain autoGain;
            private readonly Compressor compressor;
            private readonly Equalizer equalizer;
            private readonly Chorus chorus;
            private readonly Delay delay;
            private readonly Reverb reverb;
            private readonly Limiter limiter;

            public int ProcessedSamples { get; private set; }

            public ComplexEffectChain()
            {
                autoGain = new AutoGain(AutoGainPreset.Voice);

                compressor = new Compressor(CompressorPreset.VocalGentle);
                compressor.SampleRate = 44100;

                equalizer = new Equalizer(EqualizerPreset.Voice);
                chorus = new Chorus(ChorusPreset.VocalSubtle, 44100);

                delay = new Delay(DelayPreset.SlapBack);
                delay.SampleRate = 44100;

                reverb = new Reverb(ReverbPreset.VocalBooth);
                reverb.SampleRate = 44100;

                limiter = new Limiter(44100, LimiterPreset.VocalSafety);
            }

            public override void Process(Span<float> samples)
            {
                ProcessedSamples += samples.Length;

                // Process through effect chain
                if (autoGain.IsEnabled) autoGain.Process(samples);
                if (compressor.IsEnabled) compressor.Process(samples);
                if (equalizer.IsEnabled) equalizer.Process(samples);
                if (chorus.IsEnabled) chorus.Process(samples);
                if (delay.IsEnabled) delay.Process(samples);
                if (reverb.IsEnabled) reverb.Process(samples);
                if (limiter.IsEnabled) limiter.Process(samples);
            }

            public override void Reset()
            {
                autoGain.Reset();
                compressor.Reset();
                equalizer.Reset();
                chorus.Reset();
                delay.Reset();
                reverb.Reset();
                limiter.Reset();
                ProcessedSamples = 0;
            }

            public void SetAllEnabled(bool enabled)
            {
                autoGain.IsEnabled = enabled;
                compressor.IsEnabled = enabled;
                equalizer.IsEnabled = enabled;
                chorus.IsEnabled = enabled;
                delay.IsEnabled = enabled;
                reverb.IsEnabled = enabled;
                limiter.IsEnabled = enabled;
            }

            public void SetReverbEnabled(bool enabled) => reverb.IsEnabled = enabled;
            public void SetDelayEnabled(bool enabled) => delay.IsEnabled = enabled;
            public void SetCompressorEnabled(bool enabled) => compressor.IsEnabled = enabled;
            public void SetEqualizerEnabled(bool enabled) => equalizer.IsEnabled = enabled;
        }

        public class PerformanceTestProcessor : SampleProcessorBase
        {
            private readonly Reverb reverb;
            private readonly Compressor compressor;
            private readonly Equalizer equalizer;
            private readonly Delay delay;
            private readonly Random random;

            public int ProcessedSamples { get; private set; }
            public double AverageProcessingTime { get; private set; }
            public int ErrorCount { get; private set; }

            private double totalProcessingTime = 0;
            private int processCount = 0;

            public PerformanceTestProcessor()
            {
                reverb = new Reverb(ReverbPreset.Default);
                reverb.SampleRate = 44100;

                compressor = new Compressor(CompressorPreset.Default);
                compressor.SampleRate = 44100;

                equalizer = new Equalizer(EqualizerPreset.Default);

                delay = new Delay(DelayPreset.Default);
                delay.SampleRate = 44100;

                random = new Random();
            }

            public override void Process(Span<float> samples)
            {
                var startTime = DateTime.UtcNow;

                try
                {
                    ProcessedSamples += samples.Length;

                    // Process through effects
                    compressor.Process(samples);
                    equalizer.Process(samples);
                    delay.Process(samples);
                    reverb.Process(samples);
                }
                catch (Exception)
                {
                    ErrorCount++;
                }

                var endTime = DateTime.UtcNow;
                totalProcessingTime += (endTime - startTime).TotalMilliseconds;
                processCount++;
                AverageProcessingTime = totalProcessingTime / processCount;
            }

            public override void Reset()
            {
                reverb.Reset();
                compressor.Reset();
                equalizer.Reset();
                delay.Reset();

                ProcessedSamples = 0;
                totalProcessingTime = 0;
                processCount = 0;
                AverageProcessingTime = 0;
                ErrorCount = 0;
            }

            public void RandomizeParameters()
            {
                // Randomly adjust parameters to test stability
                compressor.Threshold = (float)(random.NextDouble() * 0.8 + 0.1);
                compressor.Ratio = (float)(random.NextDouble() * 8 + 2);

                reverb.WetLevel = (float)(random.NextDouble() * 0.5 + 0.1);
                reverb.RoomSize = (float)(random.NextDouble() * 0.8 + 0.2);

                equalizer.Band2Gain = (float)((random.NextDouble() - 0.5) * 12);
                equalizer.Band5Gain = (float)((random.NextDouble() - 0.5) * 12);

                delay.Time = random.Next(50, 500);
                delay.Repeat = (float)(random.NextDouble() * 0.6 + 0.1);
            }
        }

        #endregion

        #region Helper Methods

        private static string CreateTestAudioFile(string fileName)
        {
            string tempPath = Path.GetTempPath();
            string fullPath = Path.Combine(tempPath, fileName);

            // Create a longer WAV file with actual audio content for effects testing
            var sampleRate = 44100;
            var duration = 2; // 2 seconds
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

            // Generate mixed frequency content for better effects testing
            for (int i = 0; i < sampleCount; i++)
            {
                double time = (double)i / sampleRate;
                double sample = Math.Sin(2.0 * Math.PI * 440.0 * time) * 0.3 +    // 440 Hz
                               Math.Sin(2.0 * Math.PI * 880.0 * time) * 0.2 +    // 880 Hz
                               Math.Sin(2.0 * Math.PI * 1320.0 * time) * 0.1;   // 1320 Hz

                // Add some envelope to make it more realistic
                double envelope = Math.Sin(Math.PI * time / duration);
                sample *= envelope;

                short sampleValue = (short)(sample * 16383);
                wavData.AddRange(BitConverter.GetBytes(sampleValue));
            }

            File.WriteAllBytes(fullPath, wavData.ToArray());
            return fullPath;
        }

        private static float[] GenerateTestSignal(int sampleCount, float frequency)
        {
            float[] samples = new float[sampleCount];
            double angleIncrement = 2.0 * Math.PI * frequency / 44100.0;

            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = (float)(Math.Sin(i * angleIncrement) * 0.3); // Moderate volume
            }

            return samples;
        }

        #endregion
    }
}
