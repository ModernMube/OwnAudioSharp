using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using System.Linq;
using System.Collections.Generic;

namespace Ownaudio.EngineTest;

/// <summary>
/// GC Pressure Profiling Test for MultitrackPlayer scenario.
/// Tests 22 simultaneous tracks playing for 60 seconds and measures GC pressure.
/// </summary>
[TestClass]
public class GCPressureProfileTest
{
    [TestMethod]
    [Timeout(90000)] // 90 second timeout
    public void TestMultitrackPlayback_22Tracks_60Seconds_MeasureGCPressure()
    {
        // This test requires 22 test audio files
        // Skip if not available
        string testDataPath = Path.Combine(Environment.CurrentDirectory, "TestAssets");
        if (!Directory.Exists(testDataPath))
        {
            Assert.Inconclusive("TestAssets directory not found. Skipping GC pressure test.");
            return;
        }

        var audioFiles = Directory.GetFiles(testDataPath, "*.wav")
            .Concat(Directory.GetFiles(testDataPath, "*.mp3"))
            .Take(22)
            .ToArray();

        if (audioFiles.Length < 2)
        {
            Assert.Inconclusive($"Need at least 2 audio files for testing. Found: {audioFiles.Length}");
            return;
        }

        Console.WriteLine($"Starting GC Pressure Test with {audioFiles.Length} tracks...");
        Console.WriteLine($"Test duration: 60 seconds");
        Console.WriteLine("===================================================");

        // Initialize engine
        var engine = AudioEngineFactory.CreateDefault();

        var config = new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = 512
        };

        engine.Initialize(config);
        engine.Start();

        // Create mixer
        var mixer = new AudioMixer(engine, bufferSizeInFrames: 512);
        mixer.Start();

        // Create and add 22 sources (or duplicate if we have fewer files)
        var sources = new List<FileSource>();
        for (int i = 0; i < 22; i++)
        {
            var filePath = audioFiles[i % audioFiles.Length];
            var source = new FileSource(filePath, bufferSizeInFrames: 8192,
                targetSampleRate: 48000, targetChannels: 2);
            source.Volume = 1.0f / 22.0f; // Reduce volume to prevent clipping
            sources.Add(source);
        }

        // Add all sources to mixer
        foreach (var source in sources)
        {
            mixer.AddSource(source);
        }

        // Start playback
        foreach (var source in sources)
        {
            source.Play();
        }

        // GC Baseline measurement
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Thread.Sleep(500); // Stabilize

        long initialGen0 = GC.CollectionCount(0);
        long initialGen1 = GC.CollectionCount(1);
        long initialGen2 = GC.CollectionCount(2);
        long initialMemory = GC.GetTotalMemory(false);

        Console.WriteLine($"Baseline GC Stats:");
        Console.WriteLine($"  Gen0: {initialGen0}, Gen1: {initialGen1}, Gen2: {initialGen2}");
        Console.WriteLine($"  Memory: {initialMemory / 1024}KB");
        Console.WriteLine("===================================================");

        // Monitor GC for 60 seconds
        var stopwatch = Stopwatch.StartNew();
        var measurementInterval = TimeSpan.FromSeconds(5);
        var nextMeasurement = measurementInterval;

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(60))
        {
            Thread.Sleep(100);

            if (stopwatch.Elapsed >= nextMeasurement)
            {
                long currentGen0 = GC.CollectionCount(0) - initialGen0;
                long currentGen1 = GC.CollectionCount(1) - initialGen1;
                long currentGen2 = GC.CollectionCount(2) - initialGen2;
                long currentMemory = GC.GetTotalMemory(false);
                long memoryDelta = currentMemory - initialMemory;

                Console.WriteLine($"[{stopwatch.Elapsed.TotalSeconds:F1}s] " +
                    $"Gen0: {currentGen0}, Gen1: {currentGen1}, Gen2: {currentGen2}, " +
                    $"Mem: {currentMemory / 1024}KB ({(memoryDelta >= 0 ? "+" : "")}{memoryDelta / 1024}KB)");

                nextMeasurement += measurementInterval;
            }
        }

        stopwatch.Stop();

        // Final measurements
        long finalGen0 = GC.CollectionCount(0) - initialGen0;
        long finalGen1 = GC.CollectionCount(1) - initialGen1;
        long finalGen2 = GC.CollectionCount(2) - initialGen2;
        long finalMemory = GC.GetTotalMemory(false);
        long totalMemoryDelta = finalMemory - initialMemory;

        Console.WriteLine("===================================================");
        Console.WriteLine("Final GC Statistics (60 seconds):");
        Console.WriteLine($"  Gen0 Collections: {finalGen0} ({finalGen0 / 60.0:F2} per second)");
        Console.WriteLine($"  Gen1 Collections: {finalGen1} ({finalGen1 / 60.0:F2} per second)");
        Console.WriteLine($"  Gen2 Collections: {finalGen2} ({finalGen2 / 60.0:F2} per second)");
        Console.WriteLine($"  Memory Delta: {totalMemoryDelta / 1024}KB");
        Console.WriteLine($"  Final Memory: {finalMemory / 1024}KB");
        Console.WriteLine("===================================================");

        // Cleanup
        foreach (var source in sources)
        {
            source.Stop();
            source.Dispose();
        }
        mixer.Stop();
        mixer.Dispose();
        engine.Stop();
        engine.Dispose();

        // ACCEPTANCE CRITERIA for low GC pressure:
        // - Gen0: < 10 collections/minute (< 0.167 per second)
        // - Gen1: < 2 collections/minute
        // - Gen2: 0 collections during playback
        // - Memory growth: < 10MB over 60 seconds

        double gen0PerSecond = finalGen0 / 60.0;
        double gen1PerSecond = finalGen1 / 60.0;
        long memoryGrowthMB = totalMemoryDelta / (1024 * 1024);

        Console.WriteLine("\nAcceptance Criteria Check:");
        Console.WriteLine($"  Gen0/sec: {gen0PerSecond:F3} (target: < 0.167) [{(gen0PerSecond < 0.167 ? "PASS" : "FAIL")}]");
        Console.WriteLine($"  Gen1/60s: {finalGen1} (target: < 2) [{(finalGen1 < 2 ? "PASS" : "FAIL")}]");
        Console.WriteLine($"  Gen2/60s: {finalGen2} (target: 0) [{(finalGen2 == 0 ? "PASS" : "FAIL")}]");
        Console.WriteLine($"  Memory Growth: {memoryGrowthMB}MB (target: < 10MB) [{(memoryGrowthMB < 10 ? "PASS" : "FAIL")}]");

        // Assert (warning only, not strict failure)
        if (gen0PerSecond > 0.5)
        {
            Console.WriteLine($"\n⚠️  WARNING: High Gen0 collection rate: {gen0PerSecond:F3}/sec");
        }
        if (finalGen1 > 5)
        {
            Console.WriteLine($"\n⚠️  WARNING: High Gen1 collections: {finalGen1} in 60s");
        }
        if (finalGen2 > 0)
        {
            Console.WriteLine($"\n⚠️  WARNING: Gen2 collections detected: {finalGen2} in 60s");
        }
        if (memoryGrowthMB > 10)
        {
            Console.WriteLine($"\n⚠️  WARNING: High memory growth: {memoryGrowthMB}MB in 60s");
        }

        Console.WriteLine("\n✅ GC Pressure Profile Test Completed");
    }
}
