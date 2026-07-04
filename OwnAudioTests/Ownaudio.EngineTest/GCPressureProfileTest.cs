using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Core;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;

namespace Ownaudio.EngineTest;

/// <summary>
/// GC-pressure acceptance test for the Rust refactor (TODO item 2.5).
/// </summary>
/// <remarks>
/// <para>
/// Drives the shipping managed path — <see cref="AudioMixer"/> pumping many
/// <see cref="SampleSource"/>s into the Rust-backed engine — for a sustained interval and asserts
/// that the steady-state hot path does not churn the garbage collector. The refactor's goal is a
/// zero-allocation managed pipeline feeding the native engine, so a healthy run produces no Gen2
/// collections, negligible managed heap growth and a near-zero Gen0 rate.
/// </para>
/// <para>
/// The test is self-contained: it synthesises one shared, looping sine buffer for all tracks (no
/// external audio files), and skips gracefully when the host has no output device able to open the
/// requested configuration (environmental, not an engine defect). The measurement window defaults to
/// 60 s and can be shortened for local iteration via the <c>OWNAUDIO_GC_TEST_SECONDS</c> environment
/// variable.
/// </para>
/// </remarks>
[TestClass]
public class GCPressureProfileTest
{
    private const int TrackCount = 22;
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int BufferFrames = 512;
    private const int DefaultDurationSeconds = 60;

    // Acceptance thresholds for the steady-state managed hot path.
    private const int MaxGen2Collections = 0;
    private const double MaxManagedGrowthMb = 10.0;
    private const double MaxGen0PerSecond = 1.0;

    [TestMethod]
    [TestCategory("LongRunning")]
    [Timeout(120000)]
    public void MultitrackPlayback_22Tracks_SteadyState_HotPathIsAllocationFree()
    {
        // This test characterizes the legacy managed pump (SampleSource → MixThread). As of 4.0 the
        // Rust-native chain is the default and bypasses the MixThread, so pin legacy here; the
        // Rust-native GC profile is covered by GCPressureRustNativeTest in the OwnaudioNET suite.
        bool? priorOverride = global::OwnaudioNET.Engine.RustNativeChain.Override;
        global::OwnaudioNET.Engine.RustNativeChain.Override = false;
        try
        {
            RunLegacyPumpProfile();
        }
        finally
        {
            global::OwnaudioNET.Engine.RustNativeChain.Override = priorOverride;
        }
    }

    private void RunLegacyPumpProfile()
    {
        int durationSeconds = ResolveDurationSeconds();

        var config = new AudioConfig
        {
            SampleRate = SampleRate,
            Channels = Channels,
            BufferSize = BufferFrames,
        };

        // Skip gracefully when no usable output device exists on this host (e.g. headless CI).
        using IAudioEngine engine = EngineTestSupport.CreateOrSkip(config);
        engine.Start();

        using var mixer = new AudioMixer(engine, bufferSizeInFrames: BufferFrames);

        // One shared, looping 2-second stereo sine feeds every track. SampleSource.ReadSamples is
        // allocation-free (span copies only), so any GC growth measured here comes from the
        // mixer/engine pump path, which is exactly what the acceptance criterion guards.
        float[] sine = GenerateStereoSine(seconds: 2, sampleRate: SampleRate, frequencyHz: 220.0);

        var sources = new List<SampleSource>(TrackCount);
        for (int i = 0; i < TrackCount; i++)
        {
            var source = new SampleSource(sine, config)
            {
                Loop = true,
                Volume = 1.0f / TrackCount,
            };
            sources.Add(source);
            mixer.AddSource(source);
        }

        mixer.Start();
        foreach (SampleSource source in sources)
        {
            source.Play();
        }

        // Let the pipeline reach steady state before taking a forced-GC baseline.
        Thread.Sleep(1000);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Thread.Sleep(500);

        long baseGen0 = GC.CollectionCount(0);
        long baseGen1 = GC.CollectionCount(1);
        long baseGen2 = GC.CollectionCount(2);
        long baseMemory = GC.GetTotalMemory(forceFullCollection: false);

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(durationSeconds))
        {
            Thread.Sleep(200);
        }
        stopwatch.Stop();

        long gen0 = GC.CollectionCount(0) - baseGen0;
        long gen1 = GC.CollectionCount(1) - baseGen1;
        long gen2 = GC.CollectionCount(2) - baseGen2;
        long memoryGrowth = GC.GetTotalMemory(forceFullCollection: false) - baseMemory;

        foreach (SampleSource source in sources)
        {
            source.Stop();
            source.Dispose();
        }
        mixer.Stop();
        engine.Stop();

        double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        double gen0PerSecond = gen0 / elapsedSeconds;
        double memoryGrowthMb = memoryGrowth / (1024.0 * 1024.0);

        Console.WriteLine("GC pressure over {0:F1}s with {1} tracks (Rust engine):", elapsedSeconds, TrackCount);
        Console.WriteLine("  Gen0: {0} ({1:F3}/s)  Gen1: {2}  Gen2: {3}", gen0, gen0PerSecond, gen1, gen2);
        Console.WriteLine("  Managed heap growth: {0:F2} MB", memoryGrowthMb);

        Assert.AreEqual(MaxGen2Collections, gen2,
            $"Gen2 collections during steady-state playback must be {MaxGen2Collections}, was {gen2} " +
            "(indicates large/long-lived allocations churning on the hot path).");

        Assert.IsTrue(memoryGrowthMb < MaxManagedGrowthMb,
            $"Steady-state managed heap growth must stay under {MaxManagedGrowthMb:F0} MB, was {memoryGrowthMb:F2} MB.");

        Assert.IsTrue(gen0PerSecond < MaxGen0PerSecond,
            $"Steady-state Gen0 rate must stay under {MaxGen0PerSecond:F1}/s, was {gen0PerSecond:F3}/s " +
            "(indicates per-buffer allocations on the managed pump path).");
    }

    /// <summary>
    /// Returns the measurement duration in seconds, honouring the <c>OWNAUDIO_GC_TEST_SECONDS</c>
    /// override (clamped to a sensible range) so local iteration can use a shorter window.
    /// </summary>
    private static int ResolveDurationSeconds()
    {
        string? raw = Environment.GetEnvironmentVariable("OWNAUDIO_GC_TEST_SECONDS");
        if (int.TryParse(raw, out int seconds) && seconds > 0)
        {
            return Math.Clamp(seconds, 1, 100);
        }

        return DefaultDurationSeconds;
    }

    /// <summary>
    /// Builds an interleaved stereo sine buffer of the requested length. Allocated once, before the
    /// baseline, so it never counts toward the measured hot-path pressure.
    /// </summary>
    private static float[] GenerateStereoSine(int seconds, int sampleRate, double frequencyHz)
    {
        int frames = seconds * sampleRate;
        var buffer = new float[frames * Channels];
        double step = 2.0 * Math.PI * frequencyHz / sampleRate;

        for (int frame = 0; frame < frames; frame++)
        {
            float value = (float)(0.25 * Math.Sin(frame * step));
            buffer[frame * Channels] = value;
            buffer[frame * Channels + 1] = value;
        }

        return buffer;
    }
}
