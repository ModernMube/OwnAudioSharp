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
/// D.2.e (plan 14 / WS2) tests for the Rust-native network drift correction: with the managed
/// mix/read path gone, <see cref="FileSource.ApplyRustNativeSync"/> nudges the backing track's
/// tempo (or hard-seeks) toward a network-controlled master clock, mirroring the managed soft-sync
/// three-zone behavior. Local (non-network) playback is left to the native sample-locked clock.
/// </summary>
/// <remarks>
/// Hardware-free: the track renders nothing without an output stream, so its position stays at the
/// seek base, which makes the drift deterministic for the assertions.
/// </remarks>
[Collection("RustNativeChain")]
public sealed class FileSourceRustNativeSyncTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly bool? _priorOverride;
    private readonly string _wavPath;

    /// <summary>
    /// Enables the Rust-native chain and writes a two-second temp WAV source.
    /// </summary>
    public FileSourceRustNativeSyncTests()
    {
        _priorOverride = RustNativeChain.Override;
        RustNativeChain.Override = true;
        _wavPath = WriteTempWav(Channels, SampleRate, frames: SampleRate * 2);
    }

    /// <summary>
    /// Restores the opt-in override and removes the temp WAV.
    /// </summary>
    public void Dispose()
    {
        RustNativeChain.Override = _priorOverride;
        DeleteQuietly(_wavPath);
    }

    /// <summary>
    /// Builds a playing, network-clock-attached source with the given clock timestamp.
    /// </summary>
    /// <param name="clock">The master clock to attach and drive.</param>
    /// <param name="networkControlled">Whether the clock is network controlled.</param>
    /// <param name="play">Whether to start playback (sets the Playing state).</param>
    /// <returns>The prepared source.</returns>
    private FileSource CreateAttachedSource(MasterClock clock, bool networkControlled, bool play)
    {
        clock.IsNetworkControlled = networkControlled;

        var source = new FileSource(_wavPath);
        if (play)
        {
            source.Play();
        }
        else
        {
            source.EnsureStandaloneRustBackend();
        }

        source.AttachToClock(clock);
        return source;
    }

    /// <summary>
    /// In the green zone (no drift) the track tempo is restored to the base tempo.
    /// </summary>
    [Fact]
    public void GreenZone_RestoresBaseTempo()
    {
        var clock = new MasterClock(SampleRate, Channels);
        using var source = CreateAttachedSource(clock, networkControlled: true, play: true);

        source.RustTrack!.Tempo = 1.1f;
        clock.SeekTo(0.0);

        source.ApplyRustNativeSync();

        source.RustTrack!.Tempo.Should().BeApproximately(1.0f, 0.0001f);
    }

    /// <summary>
    /// In the yellow zone with the master ahead, the track tempo is nudged up to catch up.
    /// </summary>
    [Fact]
    public void YellowZone_Behind_SpeedsUp()
    {
        var clock = new MasterClock(SampleRate, Channels);
        using var source = CreateAttachedSource(clock, networkControlled: true, play: true);

        clock.SeekTo(0.015);

        source.ApplyRustNativeSync();

        source.RustTrack!.Tempo.Should().BeGreaterThan(1.0f);
        source.RustTrack!.Tempo.Should().BeApproximately(1.01f, 0.001f);
    }

    /// <summary>
    /// In the red zone (large drift) the source hard-seeks to the master position.
    /// </summary>
    [Fact]
    public void RedZone_HardSeeksToMaster()
    {
        var clock = new MasterClock(SampleRate, Channels);
        using var source = CreateAttachedSource(clock, networkControlled: true, play: true);

        clock.SeekTo(0.5);

        source.ApplyRustNativeSync();

        source.Position.Should().BeApproximately(0.5, 0.05);
        source.RustTrack!.Tempo.Should().BeApproximately(1.0f, 0.0001f);
    }

    /// <summary>
    /// When the clock is not network controlled, the correction is skipped entirely.
    /// </summary>
    [Fact]
    public void NotNetworkControlled_IsSkipped()
    {
        var clock = new MasterClock(SampleRate, Channels);
        using var source = CreateAttachedSource(clock, networkControlled: false, play: true);

        source.RustTrack!.Tempo = 1.05f;
        clock.SeekTo(0.015);

        source.ApplyRustNativeSync();

        source.RustTrack!.Tempo.Should().BeApproximately(1.05f, 0.0001f);
    }

    /// <summary>
    /// When the source is not playing, the correction is skipped entirely.
    /// </summary>
    [Fact]
    public void NotPlaying_IsSkipped()
    {
        var clock = new MasterClock(SampleRate, Channels);
        using var source = CreateAttachedSource(clock, networkControlled: true, play: false);

        source.RustTrack!.Tempo = 1.05f;
        clock.SeekTo(0.015);

        source.ApplyRustNativeSync();

        source.RustTrack!.Tempo.Should().BeApproximately(1.05f, 0.0001f);
    }

    /// <summary>
    /// The mixer's drift-sync pass corrects every attached source against the network master clock.
    /// </summary>
    [Fact]
    public void MixerDriveSync_CorrectsAttachedSources()
    {
        var config = new AudioConfig { SampleRate = SampleRate, Channels = Channels, BufferSize = 512 };
        using var engine = AudioEngineFactory.CreateMockEngine(config);
        using var mixer = new AudioMixer(engine, 512);

        var source = new FileSource(_wavPath);
        mixer.AddSource(source);
        source.Play();

        mixer.MasterClock.IsNetworkControlled = true;
        mixer.MasterClock.SeekTo(0.015);

        mixer.DriveRustNativeSyncOnce();

        source.RustTrack!.Tempo.Should().BeGreaterThan(1.0f);
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
