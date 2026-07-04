using System;
using System.IO;
using FluentAssertions;
using Ownaudio.Audio.Tracks;
using OwnaudioNET.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Sources;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.Sources;

/// <summary>
/// D.2.c (plan 14 / WS2) tests for the <see cref="FileSource"/> control-surface rebinding in
/// Rust-native mode: gain/tempo/pitch/loop/seek/position/transport route onto the backing
/// <see cref="AudioTrack"/> (and its feeder), while the legacy managed path is unaffected.
/// </summary>
/// <remarks>
/// The tests build the standalone backend and assert control state on the native track without
/// opening an output device, so they are hardware-free. Rendered <see cref="FileSource.Position"/>
/// growth is not asserted because no output stream renders the track here; only the seek base is.
/// </remarks>
[Collection("RustNativeChain")]
public sealed class FileSourceRustNativeControlTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly bool? _priorOverride;
    private readonly string _wavPath;

    /// <summary>
    /// Enables the Rust-native chain and writes a multi-second temp WAV used as the source file.
    /// </summary>
    public FileSourceRustNativeControlTests()
    {
        _priorOverride = RustNativeChain.Override;
        RustNativeChain.Override = true;
        _wavPath = WriteTempWav(Channels, SampleRate, frames: SampleRate * 3);
    }

    /// <summary>
    /// Restores the opt-in override and removes the temp WAV.
    /// </summary>
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

    /// <summary>
    /// Control values set before the backend exists are applied to the track on creation.
    /// </summary>
    [Fact]
    public void PendingControlState_IsAppliedOnBackendCreation()
    {
        using var source = new FileSource(_wavPath)
        {
            Volume = 2.0f,
            Loop = true,
        };
        source.SetTempoSmooth(1.15f);
        source.SetPitchSmooth(4.0f);

        AudioTrack track = source.EnsureStandaloneRustBackend();

        track.Gain.Should().BeApproximately(2.0f, 0.0001f);
        track.Tempo.Should().BeApproximately(1.15f, 0.0001f);
        track.PitchSemitones.Should().BeApproximately(4.0f, 0.0001f);
        source.RustFileTrack!.Loop.Should().BeTrue();
    }

    /// <summary>
    /// Tempo changes after the backend exists route live onto the track.
    /// </summary>
    [Fact]
    public void Tempo_RoutesToTrack_WhenBackendExists()
    {
        using var source = new FileSource(_wavPath);
        source.EnsureStandaloneRustBackend();

        source.Tempo = 1.15f;
        source.RustTrack!.Tempo.Should().BeApproximately(1.15f, 0.0001f);

        source.SetTempoSmooth(0.85f);
        source.RustTrack!.Tempo.Should().BeApproximately(0.85f, 0.0001f);
    }

    /// <summary>
    /// Pitch changes after the backend exists route live onto the track.
    /// </summary>
    [Fact]
    public void Pitch_RoutesToTrack_WhenBackendExists()
    {
        using var source = new FileSource(_wavPath);
        source.EnsureStandaloneRustBackend();

        source.PitchShift = -5.0f;
        source.RustTrack!.PitchSemitones.Should().BeApproximately(-5.0f, 0.0001f);
    }

    /// <summary>
    /// <see cref="FileSource.Play"/> builds the backend and moves the source to the playing state.
    /// </summary>
    [Fact]
    public void Play_BuildsBackend_AndSetsPlayingState()
    {
        using var source = new FileSource(_wavPath);
        source.RustTrack.Should().BeNull();

        source.Play();

        source.RustTrack.Should().NotBeNull();
        source.State.Should().Be(AudioState.Playing);
    }

    /// <summary>
    /// Pause and Stop route to the track and update the source state.
    /// </summary>
    [Fact]
    public void PauseAndStop_UpdateState()
    {
        using var source = new FileSource(_wavPath);
        source.Play();
        source.State.Should().Be(AudioState.Playing);

        source.Pause();
        source.State.Should().Be(AudioState.Paused);

        source.Stop();
        source.State.Should().Be(AudioState.Stopped);
    }

    /// <summary>
    /// Position is zero before the backend exists, and reports the seek base after a seek.
    /// </summary>
    [Fact]
    public void Seek_SetsPositionBase()
    {
        using var source = new FileSource(_wavPath);
        source.Position.Should().Be(0.0);

        source.EnsureStandaloneRustBackend();

        bool ok = source.Seek(1.5);

        ok.Should().BeTrue();
        source.Position.Should().BeApproximately(1.5, 0.05);
    }

    /// <summary>
    /// A seek beyond the stream duration is rejected without touching the backend.
    /// </summary>
    [Fact]
    public void Seek_OutOfRange_IsRejected()
    {
        using var source = new FileSource(_wavPath);
        source.EnsureStandaloneRustBackend();

        source.Seek(-1.0).Should().BeFalse();
        source.Seek(source.Duration + 10.0).Should().BeFalse();
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
            $"ownaudio_filesource_rustnative_ctrl_{Guid.NewGuid():N}.wav");

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
