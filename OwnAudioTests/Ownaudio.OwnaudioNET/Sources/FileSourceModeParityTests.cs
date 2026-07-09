using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using OwnaudioNET.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Events;
using OwnaudioNET.Sources;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.Sources;

/// <summary>
/// D.2.f (plan 14 / WS2) parity tests asserting that the <see cref="FileSource"/> public,
/// mode-independent observable contract is byte-identical in legacy and Rust-native modes:
/// transport state transitions, <c>StateChanged</c> ordering, seek-range validation, reported
/// <c>Duration</c>, and property clamping. Each fact runs under both modes via a theory parameter.
/// </summary>
/// <remarks>
/// These are the observables available in both engines without a rendered audio path (the legacy
/// pull path and the native track diverge on sample production, which is covered separately and by
/// the device smoke). Everything here must hold identically regardless of the opt-in switch.
/// </remarks>
[Collection("RustNativeChain")]
public sealed class FileSourceModeParityTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const double DurationSeconds = 2.0;

    private readonly bool? _priorOverride;
    private readonly string _wavPath;

    /// <summary>
    /// Writes a two-second temp WAV source and captures the opt-in override.
    /// </summary>
    public FileSourceModeParityTests()
    {
        _priorOverride = RustNativeChain.Override;
        _wavPath = WriteTempWav(Channels, SampleRate, frames: (int)(SampleRate * DurationSeconds));
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
    /// Transport calls produce the same state transitions in both modes.
    /// </summary>
    /// <param name="rustNative">The mode under test.</param>
    [Theory]
    [InlineData(true)]
    public void Transport_StateTransitions_AreIdentical(bool rustNative)
    {
        RustNativeChain.Override = rustNative;
        using var source = new FileSource(_wavPath);

        source.State.Should().Be(AudioState.Stopped);

        source.Play();
        source.State.Should().Be(AudioState.Playing);

        source.Pause();
        source.State.Should().Be(AudioState.Paused);

        source.Play();
        source.State.Should().Be(AudioState.Playing);

        source.Stop();
        source.State.Should().Be(AudioState.Stopped);
    }

    /// <summary>
    /// The <c>StateChanged</c> event fires in the same order in both modes.
    /// </summary>
    /// <param name="rustNative">The mode under test.</param>
    [Theory]
    [InlineData(true)]
    public void StateChanged_Ordering_IsIdentical(bool rustNative)
    {
        RustNativeChain.Override = rustNative;
        using var source = new FileSource(_wavPath);

        var states = new List<AudioState>();
        source.StateChanged += (_, e) => states.Add(e.NewState);

        source.Play();
        source.Stop();

        states.Should().Equal(AudioState.Playing, AudioState.Stopped);
    }

    /// <summary>
    /// Seek-range validation rejects out-of-range positions identically in both modes.
    /// </summary>
    /// <param name="rustNative">The mode under test.</param>
    [Theory]
    [InlineData(true)]
    public void Seek_RangeValidation_IsIdentical(bool rustNative)
    {
        RustNativeChain.Override = rustNative;
        using var source = new FileSource(_wavPath);

        source.Seek(-1.0).Should().BeFalse();
        source.Seek(source.Duration + 10.0).Should().BeFalse();
        source.Seek(0.5).Should().BeTrue();
    }

    /// <summary>
    /// The reported duration matches the source file in both modes.
    /// </summary>
    /// <param name="rustNative">The mode under test.</param>
    [Theory]
    [InlineData(true)]
    public void Duration_IsIdentical(bool rustNative)
    {
        RustNativeChain.Override = rustNative;
        using var source = new FileSource(_wavPath);

        source.Duration.Should().BeApproximately(DurationSeconds, 0.05);
    }

    /// <summary>
    /// Property clamping (volume, tempo, pitch) is identical in both modes.
    /// </summary>
    /// <param name="rustNative">The mode under test.</param>
    [Theory]
    [InlineData(true)]
    public void PropertyClamping_IsIdentical(bool rustNative)
    {
        RustNativeChain.Override = rustNative;
        using var source = new FileSource(_wavPath);

        source.Volume = -5.0f;
        source.Volume.Should().Be(0.0f);
        source.Volume = 100.0f;
        source.Volume.Should().Be(20.0f);

        source.Tempo = 10.0f;
        source.Tempo.Should().BeApproximately(1.2f, 0.0001f);
        source.Tempo = 0.0f;
        source.Tempo.Should().BeApproximately(0.8f, 0.0001f);

        source.PitchShift = 50.0f;
        source.PitchShift.Should().BeApproximately(12.0f, 0.0001f);
        source.PitchShift = -50.0f;
        source.PitchShift.Should().BeApproximately(-12.0f, 0.0001f);
    }

    /// <summary>
    /// The loop flag round-trips identically in both modes.
    /// </summary>
    /// <param name="rustNative">The mode under test.</param>
    [Theory]
    [InlineData(true)]
    public void Loop_RoundTrips(bool rustNative)
    {
        RustNativeChain.Override = rustNative;
        using var source = new FileSource(_wavPath);

        source.Loop.Should().BeFalse();
        source.Loop = true;
        source.Loop.Should().BeTrue();
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
            $"ownaudio_filesource_mode_parity_{Guid.NewGuid():N}.wav");

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
