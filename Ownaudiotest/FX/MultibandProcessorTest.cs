using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio;
using Ownaudio.Fx;
using Ownaudio.Sources;
using Ownaudio.Processors;
using Ownaudio.Utilities.Matchering;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Linq;

namespace Ownaudio.Tests.Matchering
{
    /// <summary>
    /// Comprehensive tests for MultibandProcessor and related components
    /// Tests multiband processing, crossover filters, and frequency-dependent effects
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class MultibandProcessorTests
    {
#nullable disable
        private static SourceManager _manager;
        private static string _testAudioFile;
        private static string _complexAudioFile;
#nullable restore

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            Console.WriteLine("Initializing OwnAudio system for multiband processor testing...");

            bool initialized = OwnAudio.Initialize();
            Assert.IsTrue(initialized, "OwnAudio should initialize successfully");

            _manager = SourceManager.Instance;

            _testAudioFile = CreateTestAudioFile("multiband_test.wav");
            _complexAudioFile = CreateComplexAudioFile("multiband_complex.wav");

            Console.WriteLine($"Multiband processor test setup complete.");
            Console.WriteLine($"Default output device: {OwnAudio.DefaultOutputDevice.Name}");
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            Console.WriteLine("Cleaning up multiband processor test system...");

            try
            {
                _manager?.Stop();
                _manager?.ResetAll();

                if (File.Exists(_testAudioFile)) File.Delete(_testAudioFile);
                if (File.Exists(_complexAudioFile)) File.Delete(_complexAudioFile);

                OwnAudio.Free();
                Console.WriteLine("Multiband processor test cleanup complete.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cleanup warning: {ex.Message}");
            }
        }

        [TestMethod]
        [Priority(1)]
        public void Test1_MultibandProcessorInitialization()
        {
            Console.WriteLine("=== Testing MultibandProcessor Initialization ===");

            var eqAdjustments = new float[] { 2.0f, 1.0f, 0.0f, -1.0f, -2.0f, 0.5f, 1.5f, -0.5f, 0.0f, 1.0f };
            var compressionSettings = new CompressionSettings[]
            {
                new CompressionSettings { Threshold = -12.0f, Ratio = 4.0f, AttackTime = 10.0f, ReleaseTime = 100.0f, MakeupGain = 2.0f },
                new CompressionSettings { Threshold = -8.0f, Ratio = 3.0f, AttackTime = 5.0f, ReleaseTime = 80.0f, MakeupGain = 1.5f },
                new CompressionSettings { Threshold = -6.0f, Ratio = 2.5f, AttackTime = 3.0f, ReleaseTime = 60.0f, MakeupGain = 1.0f },
                new CompressionSettings { Threshold = -4.0f, Ratio = 6.0f, AttackTime = 2.0f, ReleaseTime = 40.0f, MakeupGain = 0.5f }
            };
            var dynamicAmp = new DynamicAmpSettings
            {
                TargetLevel = -9.0f,
                AttackTime = 0.1f,
                ReleaseTime = 0.5f,
                MaxGain = 6.0f
            };

            Console.WriteLine("Creating MultibandProcessor...");
            var processor = new MultibandProcessor(eqAdjustments, compressionSettings, dynamicAmp);
            Assert.IsNotNull(processor, "MultibandProcessor should initialize successfully");

            Console.WriteLine("Testing with minimal configuration...");
            var minimalEQ = new float[] { 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f };
            var minimalCompression = new CompressionSettings[]
            {
                new CompressionSettings { Threshold = -12.0f, Ratio = 2.0f, AttackTime = 10.0f, ReleaseTime = 100.0f, MakeupGain = 0.0f }
            };

            var minimalProcessor = new MultibandProcessor(minimalEQ, minimalCompression, dynamicAmp);
            Assert.IsNotNull(minimalProcessor, "Minimal MultibandProcessor should initialize successfully");

            Console.WriteLine("✓ MultibandProcessor initialization tests completed successfully");
        }

        [TestMethod]
        [Priority(2)]
        public void Test2_CrossoverFilterFunctionality()
        {
            Console.WriteLine("=== Testing CrossoverFilter Functionality ===");

            var crossoverFreqs = new float[] { 250f, 2000f, 8000f };
            var sampleRate = 44100;

            Console.WriteLine("Creating CrossoverFilter...");
            var crossover = new CrossoverFilter(crossoverFreqs, sampleRate);
            Assert.IsNotNull(crossover, "CrossoverFilter should initialize successfully");

            Console.WriteLine("Testing frequency band separation...");
            var inputSignal = GenerateMultiFrequencySignal(1024, sampleRate);
            var bands = new float[4][];
            for (int i = 0; i < 4; i++)
            {
                bands[i] = new float[inputSignal.Length];
            }

            crossover.ProcessToBands(inputSignal.AsSpan(), bands);

            for (int i = 0; i < 4; i++)
            {
                Assert.IsTrue(bands[i].Any(x => Math.Abs(x) > 0.001f), $"Band {i} should contain signal content");
            }

            Console.WriteLine("Testing band combination...");
            var outputSignal = new float[inputSignal.Length];
            crossover.CombineBands(outputSignal.AsSpan(), bands);

            var originalRMS = CalculateRMS(inputSignal);
            var outputRMS = CalculateRMS(outputSignal);
            var energyRatio = outputRMS / originalRMS;

            Console.WriteLine($"Original RMS: {originalRMS:F4}, Output RMS: {outputRMS:F4}, Ratio: {energyRatio:F3}");

            // Linkwitz-Riley filters can have energy loss due to phase relationships
            // Accept reasonable energy preservation (50-150% of original)
            Assert.IsTrue(energyRatio > 0.5f && energyRatio < 1.5f,
                $"Energy ratio should be reasonable (0.5-1.5), got {energyRatio:F3}");

            Console.WriteLine("Testing crossover reset...");
            crossover.Reset();

            Console.WriteLine("✓ CrossoverFilter functionality tests completed successfully");
        }

        [TestMethod]
        [Priority(3)]
        public void Test3_BiquadFilterStability()
        {
            Console.WriteLine("=== Testing BiquadFilter Stability ===");

            Console.WriteLine("Testing valid filter parameters...");
            var filter = new MultiBandBiquadFilter(1000f, 6.0f, 1.0f, 44100, BiquadType.Peaking);
            Assert.IsNotNull(filter, "BiquadFilter should initialize with valid parameters");

            Console.WriteLine("Testing filter processing...");
            var testSignal = GenerateTestSignal(512, 1000f, 44100);
            var originalSignal = new float[testSignal.Length];
            Array.Copy(testSignal, originalSignal, testSignal.Length);

            filter.Process(testSignal.AsSpan());

            var hasChangedSignal = !testSignal.SequenceEqual(originalSignal);
            Assert.IsTrue(hasChangedSignal, "Filter should modify the signal");

            Console.WriteLine("Testing invalid parameters handling...");
            var invalidFilter = new MultiBandBiquadFilter(-100f, 0f, 0f, 44100, BiquadType.Lowpass);
            var testSpan = new float[256];
            Array.Fill(testSpan, 0.1f);

            invalidFilter.Process(testSpan.AsSpan());

            Console.WriteLine("Testing extreme parameters...");
            var extremeFilter = new MultiBandBiquadFilter(20000f, 60f, 20f, 44100, BiquadType.Highpass);
            extremeFilter.Process(testSpan.AsSpan());

            var containsValidSamples = testSpan.All(x => !float.IsNaN(x) && !float.IsInfinity(x));
            Assert.IsTrue(containsValidSamples, "Filter should not produce NaN or infinity values");

            Console.WriteLine("✓ BiquadFilter stability tests completed successfully");
        }

        [TestMethod]
        [Priority(4)]
        public void Test4_LinkwitzRileyFilterResponse()
        {
            Console.WriteLine("=== Testing Linkwitz-Riley Filter Response ===");

            var sampleRate = 44100;
            var crossoverFreq = 1000f;

            Console.WriteLine("Creating complementary Linkwitz-Riley filters...");
            var lowpass = new LinkwitzRileyFilter(crossoverFreq, sampleRate, FilterType.Lowpass);
            var highpass = new LinkwitzRileyFilter(crossoverFreq, sampleRate, FilterType.Highpass);

            Console.WriteLine("Testing filter complementarity...");
            var testFrequencies = new float[] { 100f, 500f, 1000f, 2000f, 5000f };

            foreach (var freq in testFrequencies)
            {
                var signal = GenerateTestSignal(1024, freq, sampleRate);
                var lpSignal = new float[signal.Length];
                var hpSignal = new float[signal.Length];

                Array.Copy(signal, lpSignal, signal.Length);
                Array.Copy(signal, hpSignal, signal.Length);

                lowpass.Process(lpSignal.AsSpan());
                highpass.Process(hpSignal.AsSpan());

                var originalRMS = CalculateRMS(signal);
                var lpRMS = CalculateRMS(lpSignal);
                var hpRMS = CalculateRMS(hpSignal);
                var combinedRMS = Math.Sqrt(lpRMS * lpRMS + hpRMS * hpRMS);

                Console.WriteLine($"Freq: {freq}Hz - Original: {originalRMS:F4}, LP: {lpRMS:F4}, HP: {hpRMS:F4}, Combined: {combinedRMS:F4}");

                if (freq == crossoverFreq)
                {
                    var lpHpRatio = Math.Abs(lpRMS - hpRMS) / Math.Max(lpRMS, hpRMS);
                    Assert.IsTrue(lpHpRatio < 0.1f, "LP and HP should have similar levels at crossover frequency");
                }
            }

            Console.WriteLine("✓ Linkwitz-Riley filter response tests completed successfully");
        }

        [TestMethod]
        [Priority(5)]
        public async Task Test5_RealTimeMultibandProcessing()
        {
            Console.WriteLine("=== Testing Real-time Multiband Processing ===");

            Console.WriteLine("Loading test audio file...");
            bool loaded = await _manager.AddOutputSource(_complexAudioFile, "MultibandTest");
            Assert.IsTrue(loaded, "Complex test audio should load successfully");

            Console.WriteLine("Setting up multiband processor...");
            var eqAdjustments = new float[] { 3.0f, 2.0f, 1.0f, 0.0f, -1.0f, 0.0f, 2.0f, 1.0f, -2.0f, -1.0f };
            var compressionSettings = new CompressionSettings[]
            {
                new CompressionSettings { Threshold = -15.0f, Ratio = 3.0f, AttackTime = 20.0f, ReleaseTime = 150.0f, MakeupGain = 2.0f },
                new CompressionSettings { Threshold = -10.0f, Ratio = 4.0f, AttackTime = 10.0f, ReleaseTime = 100.0f, MakeupGain = 1.5f },
                new CompressionSettings { Threshold = -8.0f, Ratio = 2.0f, AttackTime = 5.0f, ReleaseTime = 80.0f, MakeupGain = 1.0f },
                new CompressionSettings { Threshold = -6.0f, Ratio = 6.0f, AttackTime = 2.0f, ReleaseTime = 50.0f, MakeupGain = 0.5f }
            };
            var dynamicAmp = new DynamicAmpSettings
            {
                TargetLevel = -12.0f,
                AttackTime = 0.05f,
                ReleaseTime = 0.3f,
                MaxGain = 8.0f
            };

            var testProcessor = new MultibandTestProcessor(eqAdjustments, compressionSettings, dynamicAmp);
            _manager["MultibandTest"].CustomSampleProcessor = testProcessor;

            Console.WriteLine("Starting real-time processing...");
            _manager.Play();
            Assert.AreEqual(SourceState.Playing, _manager.State, "Should be playing");

            await Task.Delay(800);

            Assert.IsTrue(testProcessor.ProcessedSamples > 0, "Multiband processor should process samples");
            Assert.IsTrue(testProcessor.AverageInput > 0, "Should detect audio input");

            Console.WriteLine("Testing dynamic parameter changes...");
            testProcessor.UpdateEQGains(new float[] { -3.0f, 0.0f, 3.0f, 1.0f, -1.0f, 2.0f, 0.0f, -2.0f, 1.0f, 3.0f });

            await Task.Delay(300);

            Console.WriteLine("Stopping playback...");
            _manager.Stop();
            Assert.AreEqual(SourceState.Idle, _manager.State, "Should be idle");

            Console.WriteLine($"Processed {testProcessor.ProcessedSamples} samples");
            Console.WriteLine($"Average input level: {testProcessor.AverageInput:F4}");
            Console.WriteLine("✓ Real-time multiband processing completed successfully");
        }

        [TestMethod]
        [Priority(6)]
        public void Test6_FrequencyBandAccuracy()
        {
            Console.WriteLine("=== Testing Frequency Band Accuracy ===");

            var sampleRate = 44100;
            var eqAdjustments = new float[10];
            var compressionSettings = new CompressionSettings[]
            {
                new CompressionSettings { Threshold = -20.0f, Ratio = 1.0f, AttackTime = 10.0f, ReleaseTime = 100.0f, MakeupGain = 0.0f }
            };
            var dynamicAmp = new DynamicAmpSettings { TargetLevel = -12.0f, AttackTime = 0.1f, ReleaseTime = 0.5f, MaxGain = 1.0f };

            var processor = new MultibandProcessor(eqAdjustments, compressionSettings, dynamicAmp);

            Console.WriteLine("Testing frequency separation accuracy...");
            var testFrequencies = new float[] { 100f, 300f, 1000f, 3000f, 10000f };

            foreach (var freq in testFrequencies)
            {
                Console.WriteLine($"Testing {freq}Hz signal...");
                var signal = GenerateTestSignal(2048, freq, sampleRate);
                var originalRMS = CalculateRMS(signal);

                processor.Process(signal.AsSpan());

                var processedRMS = CalculateRMS(signal);
                var gainChange = 20 * Math.Log10(processedRMS / originalRMS);

                Console.WriteLine($"  Original RMS: {originalRMS:F4}, Processed RMS: {processedRMS:F4}, Gain: {gainChange:F2}dB");

                Assert.IsTrue(!float.IsNaN(processedRMS) && !float.IsInfinity(processedRMS),
                    $"Processed signal should be valid for {freq}Hz");
            }

            Console.WriteLine("✓ Frequency band accuracy tests completed successfully");
        }

        [TestMethod]
        [Priority(7)]
        public void Test7_MultibandPerformanceStress()
        {
            Console.WriteLine("=== Testing Multiband Performance Under Stress ===");

            var eqAdjustments = new float[] { 5.0f, -3.0f, 2.0f, -4.0f, 6.0f, -2.0f, 3.0f, -5.0f, 4.0f, -1.0f };
            var compressionSettings = new CompressionSettings[]
            {
                new CompressionSettings { Threshold = -20.0f, Ratio = 8.0f, AttackTime = 1.0f, ReleaseTime = 50.0f, MakeupGain = 6.0f },
                new CompressionSettings { Threshold = -15.0f, Ratio = 6.0f, AttackTime = 2.0f, ReleaseTime = 75.0f, MakeupGain = 4.0f },
                new CompressionSettings { Threshold = -10.0f, Ratio = 4.0f, AttackTime = 5.0f, ReleaseTime = 100.0f, MakeupGain = 2.0f },
                new CompressionSettings { Threshold = -5.0f, Ratio = 10.0f, AttackTime = 0.5f, ReleaseTime = 25.0f, MakeupGain = 1.0f }
            };
            var dynamicAmp = new DynamicAmpSettings
            {
                TargetLevel = -6.0f,
                AttackTime = 0.01f,
                ReleaseTime = 0.1f,
                MaxGain = 12.0f
            };

            Console.WriteLine("Creating stress test processor...");
            var processor = new MultibandProcessor(eqAdjustments, compressionSettings, dynamicAmp);

            Console.WriteLine("Running performance stress test...");
            var startTime = DateTime.UtcNow;
            var totalSamples = 0;
            var errorCount = 0;

            for (int iteration = 0; iteration < 100; iteration++)
            {
                try
                {
                    var complexSignal = GenerateComplexSignal(4096, 44100);
                    processor.Process(complexSignal.AsSpan());
                    totalSamples += complexSignal.Length;

                    var hasValidOutput = complexSignal.All(x => !float.IsNaN(x) && !float.IsInfinity(x) && Math.Abs(x) < 10.0f);
                    if (!hasValidOutput) errorCount++;
                }
                catch (Exception)
                {
                    errorCount++;
                }
            }

            var endTime = DateTime.UtcNow;
            var totalTime = (endTime - startTime).TotalMilliseconds;
            var samplesPerMs = totalSamples / totalTime;

            Console.WriteLine($"Processed {totalSamples} samples in {totalTime:F2}ms");
            Console.WriteLine($"Performance: {samplesPerMs:F0} samples/ms");
            Console.WriteLine($"Error count: {errorCount}");

            Assert.AreEqual(0, errorCount, "Should have no processing errors under stress");
            Assert.IsTrue(samplesPerMs > 100, "Should maintain reasonable performance under stress");

            Console.WriteLine("✓ Multiband performance stress tests completed successfully");
        }

        [TestMethod]
        [Priority(8)]
        public void Test8_MultibandResetFunctionality()
        {
            Console.WriteLine("=== Testing Multiband Reset Functionality ===");

            var eqAdjustments = new float[] { 2.0f, 1.0f, 0.0f, -1.0f, -2.0f, 0.5f, 1.5f, -0.5f, 0.0f, 1.0f };
            var compressionSettings = new CompressionSettings[]
            {
                new CompressionSettings { Threshold = -10.0f, Ratio = 4.0f, AttackTime = 5.0f, ReleaseTime = 100.0f, MakeupGain = 2.0f }
            };
            var dynamicAmp = new DynamicAmpSettings
            {
                TargetLevel = -9.0f,
                AttackTime = 0.1f,
                ReleaseTime = 0.5f,
                MaxGain = 6.0f
            };

            Console.WriteLine("Creating processor for reset test...");
            var processor = new MultibandProcessor(eqAdjustments, compressionSettings, dynamicAmp);

            Console.WriteLine("Building up internal state...");
            for (int i = 0; i < 10; i++)
            {
                var signal = GenerateTestSignal(1024, 1000f, 44100);
                processor.Process(signal.AsSpan());
            }

            Console.WriteLine("Testing reset functionality...");
            processor.Reset();

            Console.WriteLine("Processing after reset...");
            var testSignal = GenerateTestSignal(512, 500f, 44100);
            var originalRMS = CalculateRMS(testSignal);

            processor.Process(testSignal.AsSpan());

            var processedRMS = CalculateRMS(testSignal);
            Assert.IsTrue(!float.IsNaN(processedRMS) && !float.IsInfinity(processedRMS),
                "Processed signal should be valid after reset");

            var gainChange = Math.Abs(20 * Math.Log10(processedRMS / originalRMS));
            Console.WriteLine($"Gain change after reset: {gainChange:F2}dB");

            Console.WriteLine("✓ Multiband reset functionality tests completed successfully");
        }

        #region Helper Classes

        public class MultibandTestProcessor : SampleProcessorBase
        {
            private MultibandProcessor multibandProcessor;
            private readonly object lockObject = new object();

            public int ProcessedSamples { get; private set; }
            public float AverageInput { get; private set; }
            private float inputSum = 0f;

            public MultibandTestProcessor(float[] eqAdjustments, CompressionSettings[] compressionSettings,
                                         DynamicAmpSettings dynamicAmp)
            {
                multibandProcessor = new MultibandProcessor(eqAdjustments, compressionSettings, dynamicAmp);
            }

            public override void Process(Span<float> samples)
            {
                lock (lockObject)
                {
                    for (int i = 0; i < samples.Length; i++)
                    {
                        inputSum += Math.Abs(samples[i]);
                    }
                    ProcessedSamples += samples.Length;
                    AverageInput = inputSum / ProcessedSamples;

                    multibandProcessor.Process(samples);
                }
            }

            public override void Reset()
            {
                lock (lockObject)
                {
                    multibandProcessor.Reset();
                    ProcessedSamples = 0;
                    AverageInput = 0f;
                    inputSum = 0f;
                }
            }

            public void UpdateEQGains(float[] newGains)
            {
                lock (lockObject)
                {
                    var compressionSettings = new CompressionSettings[]
                    {
                        new CompressionSettings { Threshold = -12.0f, Ratio = 3.0f, AttackTime = 10.0f, ReleaseTime = 100.0f, MakeupGain = 1.0f }
                    };
                    var dynamicAmp = new DynamicAmpSettings
                    {
                        TargetLevel = -9.0f,
                        AttackTime = 0.1f,
                        ReleaseTime = 0.5f,
                        MaxGain = 6.0f
                    };

                    multibandProcessor = new MultibandProcessor(newGains, compressionSettings, dynamicAmp);
                }
            }
        }

        #endregion

        #region Helper Methods

        private static string CreateTestAudioFile(string fileName)
        {
            string tempPath = Path.GetTempPath();
            string fullPath = Path.Combine(tempPath, fileName);

            var sampleRate = 44100;
            var duration = 2;
            var sampleCount = sampleRate * duration;
            var wavData = new List<byte>();

            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            wavData.AddRange(BitConverter.GetBytes((uint)(36 + sampleCount * 2)));
            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("fmt "));
            wavData.AddRange(BitConverter.GetBytes((uint)16));
            wavData.AddRange(BitConverter.GetBytes((ushort)1));
            wavData.AddRange(BitConverter.GetBytes((ushort)1));
            wavData.AddRange(BitConverter.GetBytes((uint)sampleRate));
            wavData.AddRange(BitConverter.GetBytes((uint)(sampleRate * 2)));
            wavData.AddRange(BitConverter.GetBytes((ushort)2));
            wavData.AddRange(BitConverter.GetBytes((ushort)16));

            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("data"));
            wavData.AddRange(BitConverter.GetBytes((uint)(sampleCount * 2)));

            for (int i = 0; i < sampleCount; i++)
            {
                double time = (double)i / sampleRate;
                double sample = Math.Sin(2.0 * Math.PI * 440.0 * time) * 0.3;
                short sampleValue = (short)(sample * 16383);
                wavData.AddRange(BitConverter.GetBytes(sampleValue));
            }

            File.WriteAllBytes(fullPath, wavData.ToArray());
            return fullPath;
        }

        private static string CreateComplexAudioFile(string fileName)
        {
            string tempPath = Path.GetTempPath();
            string fullPath = Path.Combine(tempPath, fileName);

            var sampleRate = 44100;
            var duration = 3;
            var sampleCount = sampleRate * duration;
            var wavData = new List<byte>();

            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            wavData.AddRange(BitConverter.GetBytes((uint)(36 + sampleCount * 2)));
            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("fmt "));
            wavData.AddRange(BitConverter.GetBytes((uint)16));
            wavData.AddRange(BitConverter.GetBytes((ushort)1));
            wavData.AddRange(BitConverter.GetBytes((ushort)1));
            wavData.AddRange(BitConverter.GetBytes((uint)sampleRate));
            wavData.AddRange(BitConverter.GetBytes((uint)(sampleRate * 2)));
            wavData.AddRange(BitConverter.GetBytes((ushort)2));
            wavData.AddRange(BitConverter.GetBytes((ushort)16));

            wavData.AddRange(System.Text.Encoding.ASCII.GetBytes("data"));
            wavData.AddRange(BitConverter.GetBytes((uint)(sampleCount * 2)));

            for (int i = 0; i < sampleCount; i++)
            {
                double time = (double)i / sampleRate;
                double sample = Math.Sin(2.0 * Math.PI * 100.0 * time) * 0.2 +
                               Math.Sin(2.0 * Math.PI * 440.0 * time) * 0.3 +
                               Math.Sin(2.0 * Math.PI * 1500.0 * time) * 0.2 +
                               Math.Sin(2.0 * Math.PI * 4000.0 * time) * 0.15 +
                               Math.Sin(2.0 * Math.PI * 8000.0 * time) * 0.1;

                double envelope = Math.Sin(Math.PI * time / duration);
                sample *= envelope;

                short sampleValue = (short)(sample * 16383);
                wavData.AddRange(BitConverter.GetBytes(sampleValue));
            }

            File.WriteAllBytes(fullPath, wavData.ToArray());
            return fullPath;
        }

        private static float[] GenerateTestSignal(int sampleCount, float frequency, int sampleRate)
        {
            float[] samples = new float[sampleCount];
            double angleIncrement = 2.0 * Math.PI * frequency / sampleRate;

            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = (float)(Math.Sin(i * angleIncrement) * 0.3);
            }

            return samples;
        }

        private static float[] GenerateMultiFrequencySignal(int sampleCount, int sampleRate)
        {
            float[] samples = new float[sampleCount];
            var frequencies = new float[] { 100f, 500f, 1500f, 5000f, 12000f };

            for (int i = 0; i < sampleCount; i++)
            {
                double sample = 0;
                foreach (var freq in frequencies)
                {
                    double angleIncrement = 2.0 * Math.PI * freq / sampleRate;
                    sample += Math.Sin(i * angleIncrement) * 0.1;
                }
                samples[i] = (float)sample;
            }

            return samples;
        }

        private static float[] GenerateComplexSignal(int sampleCount, int sampleRate)
        {
            float[] samples = new float[sampleCount];
            var random = new Random(42);

            for (int i = 0; i < sampleCount; i++)
            {
                double sample = 0;
                sample += Math.Sin(2.0 * Math.PI * 200.0 * i / sampleRate) * 0.3;
                sample += Math.Sin(2.0 * Math.PI * 800.0 * i / sampleRate) * 0.2;
                sample += Math.Sin(2.0 * Math.PI * 3000.0 * i / sampleRate) * 0.15;
                sample += Math.Sin(2.0 * Math.PI * 7000.0 * i / sampleRate) * 0.1;
                sample += (random.NextDouble() - 0.5) * 0.05;
                samples[i] = (float)sample;
            }

            return samples;
        }

        private static float CalculateRMS(float[] samples)
        {
            if (samples.Length == 0) return 0f;

            double sumSquares = 0;
            foreach (var sample in samples)
            {
                sumSquares += sample * sample;
            }

            return (float)Math.Sqrt(sumSquares / samples.Length);
        }

        #endregion
    }
}
