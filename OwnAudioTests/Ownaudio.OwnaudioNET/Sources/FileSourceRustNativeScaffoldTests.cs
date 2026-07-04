using System;
using System.IO;
using FluentAssertions;
using Ownaudio.Audio.Tracks;
using OwnaudioNET.Engine;
using OwnaudioNET.Sources;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.Sources;

/// <summary>
/// D.2.b (plan 14 / WS2) tests for the <see cref="FileSource"/> Rust-native backend scaffold:
/// the mode is captured from <see cref="RustNativeChain"/> at construction, a standalone source
/// can lazily build (and idempotently reuse) a private single-track backend, and disposal tears
/// the backend down. The legacy path stays inert (no backend) so its behavior is unchanged.
/// </summary>
/// <remarks>
/// These tests exercise the native mixer/track via the loaded <c>ownaudio_ffi</c> library but
/// never open an output device, so they are hardware-free. The opt-in override is restored after
/// each test.
/// </remarks>
[Collection("RustNativeChain")]
public sealed class FileSourceRustNativeScaffoldTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly bool? _priorOverride;
    private readonly string _wavPath;

    /// <summary>
    /// Captures the opt-in override and writes a short deterministic temp WAV used as the file
    /// source under test.
    /// </summary>
    public FileSourceRustNativeScaffoldTests()
    {
        _priorOverride = RustNativeChain.Override;
        _wavPath = WriteTempWav(Channels, SampleRate, frames: SampleRate / 2);
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
    /// With the chain disabled, the source reports legacy mode and never builds a backend.
    /// </summary>
    [Fact]
    public void LegacyMode_ReportsDisabled_AndHasNoBackend()
    {
        RustNativeChain.Override = false;

        using var source = new FileSource(_wavPath);

        source.IsRustNativeChain.Should().BeFalse();
        source.RustTrack.Should().BeNull();
        Action ensure = () => source.EnsureStandaloneRustBackend();
        ensure.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// The mode is captured at construction and does not follow a later switch flip.
    /// </summary>
    [Fact]
    public void Mode_IsCapturedAtConstruction()
    {
        RustNativeChain.Override = true;
        using var source = new FileSource(_wavPath);
        RustNativeChain.Override = false;

        source.IsRustNativeChain.Should().BeTrue();
    }

    /// <summary>
    /// In Rust-native mode a standalone source lazily builds a valid backing track, and repeated
    /// calls return the very same track (idempotent).
    /// </summary>
    [Fact]
    public void RustNativeMode_BuildsStandaloneBackend_Idempotently()
    {
        RustNativeChain.Override = true;

        using var source = new FileSource(_wavPath);
        source.RustTrack.Should().BeNull();

        AudioTrack track = source.EnsureStandaloneRustBackend();
        track.Should().NotBeNull();
        source.RustTrack.Should().BeSameAs(track);

        AudioTrack again = source.EnsureStandaloneRustBackend();
        again.Should().BeSameAs(track);
    }

    /// <summary>
    /// Disposing the source tears down the owned standalone backend and clears the track reference.
    /// </summary>
    [Fact]
    public void Dispose_TearsDownOwnedBackend()
    {
        RustNativeChain.Override = true;

        var source = new FileSource(_wavPath);
        source.EnsureStandaloneRustBackend();
        source.RustTrack.Should().NotBeNull();

        source.Dispose();

        source.RustTrack.Should().BeNull();
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
            $"ownaudio_filesource_rustnative_{Guid.NewGuid():N}.wav");

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
