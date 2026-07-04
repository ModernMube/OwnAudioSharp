using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using FluentAssertions;
using Ownaudio.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using Xunit;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace Ownaudio.OwnaudioNET.Tests.Mixing;

/// <summary>
/// GC-pressure acceptance test for the Rust-native file-playback chain (plan 14 / D.4 gate).
/// </summary>
/// <remarks>
/// <para>
/// Drives the Rust-native <see cref="AudioMixer"/> (the 4.0 default): several looping
/// <see cref="FileSource"/>s rendered entirely by the native mixer on the audio thread, with only a
/// control-rate managed sync tick. The refactor's goal is a native hot path with no managed
/// per-buffer work, so a healthy run produces no Gen2 collections, negligible managed heap growth,
/// and a near-zero Gen0 rate.
/// </para>
/// <para>
/// Requires a working native engine and output device; when none is available the test returns early
/// (treated as passing), matching the project's device-dependent test convention. The measurement
/// window defaults to a few seconds and can be tuned via the <c>OWNAUDIO_GC_TEST_SECONDS</c>
/// environment variable.
/// </para>
/// </remarks>
[Collection("RustNativeChain")]
public sealed class GCPressureRustNativeTest : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int BufferFrames = 512;
    private const int TrackCount = 8;
    private const int DefaultDurationSeconds = 3;

    // Acceptance thresholds for the steady-state native hot path (managed side only).
    private const int MaxGen2Collections = 0;
    private const double MaxManagedGrowthMb = 10.0;
    private const double MaxGen0PerSecond = 5.0;

    private readonly bool? _priorOverride;
    private readonly string _wavPath;

    public GCPressureRustNativeTest()
    {
        _priorOverride = RustNativeChain.Override;
        RustNativeChain.Override = true;
        _wavPath = WriteTempWav(Channels, SampleRate, frames: SampleRate); // 1 s loopable source
    }

    public void Dispose()
    {
        RustNativeChain.Override = _priorOverride;
        try
        {
            if (File.Exists(_wavPath))
                File.Delete(_wavPath);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void RustNativeMultitrackPlayback_SteadyState_ManagedSideIsAllocationFree()
    {
        if (!AudioEngineFactory.IsNativeEngineAvailable())
        {
            return;
        }

        var config = new AudioConfig
        {
            SampleRate = SampleRate,
            Channels = Channels,
            BufferSize = BufferFrames,
            EnableOutput = true,
            EnableInput = false,
        };

        IAudioEngine engine;
        try
        {
            engine = AudioEngineFactory.CreateEngine(config);
        }
        catch
        {
            return;
        }

        int durationSeconds = ResolveDurationSeconds();
        var sources = new List<FileSource>(TrackCount);

        try
        {
            using var mixer = new AudioMixer(engine, BufferFrames);

            for (int i = 0; i < TrackCount; i++)
            {
                var source = new FileSource(_wavPath)
                {
                    Loop = true,
                    Volume = 1.0f / TrackCount,
                };
                sources.Add(source);
                mixer.AddSource(source);
            }

            mixer.Start();
            foreach (FileSource source in sources)
            {
                source.Play();
            }

            // Let the native pipeline reach steady state before taking a forced-GC baseline.
            Thread.Sleep(1000);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(300);

            long baseGen0 = GC.CollectionCount(0);
            long baseGen1 = GC.CollectionCount(1);
            long baseGen2 = GC.CollectionCount(2);
            long baseMemory = GC.GetTotalMemory(forceFullCollection: false);

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(durationSeconds))
            {
                Thread.Sleep(100);
            }
            stopwatch.Stop();

            long gen0 = GC.CollectionCount(0) - baseGen0;
            long gen1 = GC.CollectionCount(1) - baseGen1;
            long gen2 = GC.CollectionCount(2) - baseGen2;
            long memoryGrowth = GC.GetTotalMemory(forceFullCollection: false) - baseMemory;

            foreach (FileSource source in sources)
            {
                source.Stop();
            }
            mixer.Stop();

            double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            double gen0PerSecond = gen0 / elapsedSeconds;
            double memoryGrowthMb = memoryGrowth / (1024.0 * 1024.0);

            Console.WriteLine("Rust-native GC pressure over {0:F1}s with {1} file tracks:", elapsedSeconds, TrackCount);
            Console.WriteLine("  Gen0: {0} ({1:F3}/s)  Gen1: {2}  Gen2: {3}", gen0, gen0PerSecond, gen1, gen2);
            Console.WriteLine("  Managed heap growth: {0:F2} MB", memoryGrowthMb);

            gen2.Should().BeLessThanOrEqualTo(MaxGen2Collections,
                "the Rust-native hot path does no managed per-buffer work, so steady-state playback must not trigger Gen2 collections");
            memoryGrowthMb.Should().BeLessThan(MaxManagedGrowthMb,
                "steady-state managed heap growth must stay negligible on the native path");
            gen0PerSecond.Should().BeLessThan(MaxGen0PerSecond,
                "the managed control-rate tick must not churn Gen0 during steady-state native playback");
        }
        finally
        {
            foreach (FileSource source in sources)
            {
                source.Dispose();
            }
            engine.Dispose();
        }
    }

    private static int ResolveDurationSeconds()
    {
        string? raw = Environment.GetEnvironmentVariable("OWNAUDIO_GC_TEST_SECONDS");
        if (int.TryParse(raw, out int seconds) && seconds > 0)
        {
            return Math.Clamp(seconds, 1, 100);
        }

        return DefaultDurationSeconds;
    }

    private static string WriteTempWav(int channels, int sampleRate, int frames)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"ownaudio_gc_rustnative_{Guid.NewGuid():N}.wav");

        int dataLen = frames * channels * 2;
        int byteRate = sampleRate * channels * 2;
        short blockAlign = (short)(channels * 2);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);

        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + dataLen);
        w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);
        w.Write((ushort)1);
        w.Write((ushort)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((ushort)blockAlign);
        w.Write((ushort)16);
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(dataLen);

        for (int i = 0; i < frames; i++)
        {
            short value = (short)((i % 1000) * 30);
            for (int c = 0; c < channels; c++)
            {
                w.Write(value);
            }
        }

        return path;
    }
}
