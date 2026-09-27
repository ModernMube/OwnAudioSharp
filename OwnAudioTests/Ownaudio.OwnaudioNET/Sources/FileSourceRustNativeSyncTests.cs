using System;
using System.IO;
using FluentAssertions;
using Ownaudio.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using OwnaudioNET.Synchronization;
using Xunit;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace Ownaudio.OwnaudioNET.Tests.Sources;

/// <summary>
/// The network follower's tempo trim on a Rust-native file source: the mixer's control tick puts
/// the clock's trim on top of the source's own tempo, and takes it off again. The follower never
/// seeks a source from here any more — the old per-source drift correction compared a wall-clock
/// position, which a tempo nudge can't move.
/// </summary>
/// <remarks>
/// Hardware-free: without an output stream nothing renders, so only the track's tempo is looked at.
/// </remarks>
[Collection("RustNativeChain")]
public sealed class FileSourceRustNativeSyncTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly bool? _priorOverride;
    private readonly string _wavPath;

    public FileSourceRustNativeSyncTests()
    {
        _priorOverride = RustNativeChain.Override;
        RustNativeChain.Override = true;
        _wavPath = WriteTempWav(Channels, SampleRate, frames: SampleRate * 2);
    }

    public void Dispose()
    {
        RustNativeChain.Override = _priorOverride;
        DeleteQuietly(_wavPath);
    }

    private FileSource CreateAttachedSource(MasterClock clock)
    {
        var source = new FileSource(_wavPath);
        source.Play();
        source.AttachToClock(clock);
        return source;
    }

    [Fact]
    public void Trim_RidesOnTopOfTheSourceTempo()
    {
        var clock = new MasterClock(SampleRate, Channels);
        using var source = CreateAttachedSource(clock);
        source.Tempo = 1.1f;

        clock.TempoTrim = 1.004f;
        source.ApplyRustNativeSync();

        source.RustTrack!.Tempo.Should().BeApproximately(1.1f * 1.004f, 1e-5f);
        source.Tempo.Should().Be(1.1f, "the trim never shows on the public tempo");
    }

    [Fact]
    public void TrimBackToOne_RestoresThePlainTempo()
    {
        var clock = new MasterClock(SampleRate, Channels);
        using var source = CreateAttachedSource(clock);

        clock.TempoTrim = 0.996f;
        source.ApplyRustNativeSync();
        clock.TempoTrim = 1f;
        source.ApplyRustNativeSync();

        source.RustTrack!.Tempo.Should().Be(1f);
    }

    [Fact]
    public void TempoSet_UnderATrim_KeepsTheTrim()
    {
        var clock = new MasterClock(SampleRate, Channels);
        using var source = CreateAttachedSource(clock);

        clock.TempoTrim = 1.002f;
        source.Tempo = 0.9f;

        source.RustTrack!.Tempo.Should().BeApproximately(0.9f * 1.002f, 1e-5f);
    }

    [Fact]
    public void MixerTick_TrimsEveryAttachedSource()
    {
        var config = new AudioConfig { SampleRate = SampleRate, Channels = Channels, BufferSize = 512 };
        using var engine = AudioEngineFactory.CreateMockEngine(config);
        using var mixer = new AudioMixer(engine, 512);

        var source = new FileSource(_wavPath);
        mixer.AddSource(source);
        source.Play();

        mixer.MasterClock.TempoTrim = 1.003f;
        mixer.DriveRustNativeSyncOnce();

        source.RustTrack!.Tempo.Should().BeApproximately(1.003f, 1e-5f);
    }

    /// <summary>
    /// Deletes a file, ignoring any I/O error.
    /// </summary>
    /// <param name="path">The file path to remove.</param>
    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    /// <summary>
    /// Writes a temporary 16-bit PCM WAV file and returns its path.
    /// </summary>
    /// <param name="channels">Channel count.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="frames">Number of audio frames to write.</param>
    /// <returns>The absolute path of the written WAV file.</returns>
    private static string WriteTempWav(int channels, int sampleRate, int frames)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"ownaudio_filesource_rustnative_sync_{Guid.NewGuid():N}.wav");

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
