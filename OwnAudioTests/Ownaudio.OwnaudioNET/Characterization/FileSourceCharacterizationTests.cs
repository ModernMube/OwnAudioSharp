using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using FluentAssertions;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Sources;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.Characterization;

/// <summary>
/// WS0 (plan 14 / D.0a) golden-master characterization of the legacy <see cref="FileSource"/>
/// playback behavior. These assertions capture the current, shipped behavior so the future
/// Rust-native chain can be proven behaviorally equivalent. They are written against a
/// deterministic in-memory decoder (<see cref="DeterministicSignalDecoder"/>) so they contain
/// no timing- or file-dependent flakiness, and are structured to later run in both the legacy
/// and the Rust-native mode (from D.2) against the same expectations.
/// </summary>
public sealed class FileSourceCharacterizationTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private FileSource? _source;

    /// <summary>
    /// Builds a playing <see cref="FileSource"/> over a deterministic decoder of the given
    /// duration, at identity tempo/pitch and unity volume unless a test changes them.
    /// </summary>
    private FileSource CreatePlayingSource(double durationSeconds)
    {
        long totalFrames = (long)(durationSeconds * SampleRate);
        var decoder = new DeterministicSignalDecoder(Channels, SampleRate, totalFrames);
        var source = new FileSource(decoder);
        source.Volume = 1.0f;
        _source = source;
        return source;
    }

    /// <summary>
    /// Reads exactly <paramref name="frames"/> frames and returns the actual frame count read.
    /// </summary>
    private static int ReadFrames(FileSource source, float[] buffer, int frames)
        => source.ReadSamples(buffer.AsSpan(0, frames * Channels), frames);

    public void Dispose() => _source?.Dispose();

    /// <summary>
    /// At identity tempo (SoundTouch bypassed) the decoded signal must reach the read buffer
    /// sample-for-sample, in order, starting at frame 0. This is the strongest parity anchor:
    /// the Rust-native path must reproduce the same samples.
    /// </summary>
    [Fact]
    public void SampleIdentity_AtIdentityTempo_PassesDecodedSignalThroughUnchanged()
    {
        var source = CreatePlayingSource(durationSeconds: 5.0);
        source.Play();

        const int frames = 256;
        var buffer = new float[frames * Channels];
        int read = ReadFrames(source, buffer, frames);

        read.Should().Be(frames, "a freshly pre-buffered source has ample data for a small read");

        for (int f = 0; f < frames; f++)
        {
            float expected = DeterministicSignalDecoder.SampleAt(f);
            buffer[f * Channels].Should().BeApproximately(expected, 1e-6f,
                because: $"left channel of frame {f} must equal the decoded signal");
            buffer[f * Channels + 1].Should().BeApproximately(expected, 1e-6f,
                because: $"right channel of frame {f} must equal the decoded signal");
        }
    }

    /// <summary>
    /// Seeking relocates the reported position to the seek target and resumes producing audio.
    /// The reported position converges to exactly the seek target (no reads have advanced it),
    /// then advances forward as playback continues. Sample-for-sample content identity right at
    /// the seek point is intentionally not asserted: the decode/pre-buffer pipeline introduces a
    /// small, timing-dependent content offset after a seek, so content fidelity is characterized
    /// by <see cref="SampleIdentity_AtIdentityTempo_PassesDecodedSignalThroughUnchanged"/> from
    /// the stream start, and seek is characterized here at the position/behavior level.
    /// </summary>
    [Fact]
    public void Seek_RelocatesReportedPositionAndResumesPlayback()
    {
        var source = CreatePlayingSource(durationSeconds: 5.0);
        source.Play();

        source.Seek(1.0).Should().BeTrue();

        // The decoder thread applies the seek and resets position to the target; no reads have
        // advanced it yet, so the reported position converges to exactly 1.0s.
        WaitUntil(() => Math.Abs(source.Position - 1.0) < 1e-3, timeoutMs: 1000)
            .Should().BeTrue("the seek target position should be reflected once the decoder applies it");

        // Allow the (cleared) buffer to refill from the seek point before reading.
        Thread.Sleep(150);

        const int frames = 256;
        var buffer = new float[frames * Channels];
        int read = ReadFrames(source, buffer, frames);
        read.Should().Be(frames);

        float maxMagnitude = 0f;
        for (int i = 0; i < frames * Channels; i++)
            maxMagnitude = Math.Max(maxMagnitude, Math.Abs(buffer[i]));
        maxMagnitude.Should().BeGreaterThan(0.01f, "playback must resume with real audio after the seek");

        source.Position.Should().BeGreaterThan(1.0,
            "reading after the seek advances the position forward from the seek target");
    }

    /// <summary>
    /// With looping enabled the source never latches end-of-stream: it keeps producing audio
    /// past the file length and stays in the Playing state (it wraps back to the start).
    /// </summary>
    [Fact]
    public void Loop_WrapsAtEndWithoutReachingEndOfStream()
    {
        var source = CreatePlayingSource(durationSeconds: 0.1); // 4800 frames
        source.Loop = true;
        source.Play();

        long fileFrames = (long)(0.1 * SampleRate);
        const int chunk = 512;
        var buffer = new float[chunk * Channels];

        long totalFramesRead = 0;
        // Request ~5x the file length worth of audio.
        for (int i = 0; i < (fileFrames / chunk) * 5 + 20; i++)
        {
            int read = ReadFrames(source, buffer, chunk);
            totalFramesRead += read;
            source.State.Should().NotBe(AudioState.EndOfStream,
                "a looping source must never enter the EndOfStream state");
            Thread.Sleep(3);
        }

        totalFramesRead.Should().BeGreaterThan(fileFrames,
            "the source must keep playing past the file length by wrapping around");
        source.State.Should().Be(AudioState.Playing);
        source.IsEndOfStream.Should().BeFalse();
    }

    /// <summary>
    /// Without looping the source reaches end-of-stream: a read eventually returns 0 frames and
    /// the state transitions to <see cref="AudioState.EndOfStream"/>.
    /// </summary>
    [Fact]
    public void NoLoop_ReachesEndOfStreamState()
    {
        var source = CreatePlayingSource(durationSeconds: 0.1);
        source.Loop = false;
        source.Play();

        const int chunk = 512;
        var buffer = new float[chunk * Channels];

        bool sawZeroRead = false;
        for (int i = 0; i < 200; i++)
        {
            int read = ReadFrames(source, buffer, chunk);
            if (read == 0)
            {
                sawZeroRead = true;
                break;
            }
            Thread.Sleep(3);
        }

        sawZeroRead.Should().BeTrue("a non-looping source must eventually stop producing frames");
        source.State.Should().Be(AudioState.EndOfStream);
        source.IsEndOfStream.Should().BeTrue();
    }

    /// <summary>
    /// The state-change event fires in the expected order for a Play → Stop cycle.
    /// </summary>
    [Fact]
    public void StateChanged_FiresPlayingThenStopped()
    {
        var source = CreatePlayingSource(durationSeconds: 5.0);

        var states = new List<AudioState>();
        source.StateChanged += (_, e) => states.Add(e.NewState);

        source.Play();
        source.Stop();

        states.Should().Equal(AudioState.Playing, AudioState.Stopped);
    }

    /// <summary>
    /// For a source without a native backend the analysis cursor exposed by <c>Position</c> advances
    /// by the raw number of decoded frames, independent of <c>Tempo</c>. As of 4.0 (plan L) tempo is
    /// applied natively on playback while <see cref="FileSource.ReadSamples"/> decodes raw PCM on
    /// demand, so the reported advance is <c>frames / sample-rate</c> regardless of the tempo setting.
    /// </summary>
    [Theory]
    [InlineData(1.0f)]
    [InlineData(0.8f)]
    [InlineData(1.2f)]
    public void Position_AdvancesByRawDecodedFrames_AcrossTempos(float tempo)
    {
        var source = CreatePlayingSource(durationSeconds: 10.0);
        source.Play();
        source.Tempo = tempo;

        const int chunk = 512;
        var buffer = new float[chunk * Channels];

        long totalFullFramesRead = 0;
        double totalPositionDelta = 0.0;
        int fullReads = 0;

        for (int i = 0; i < 60 && fullReads < 10; i++)
        {
            double before = source.Position;
            int read = ReadFrames(source, buffer, chunk);
            double after = source.Position;

            if (read == chunk)
            {
                totalFullFramesRead += read;
                totalPositionDelta += after - before;
                fullReads++;
            }
        }

        fullReads.Should().BeGreaterThan(0, "the source should produce full reads");

        double expected = (double)totalFullFramesRead / SampleRate;
        totalPositionDelta.Should().BeApproximately(expected, 0.01,
            because: $"the analysis cursor advances by raw decoded frames regardless of Tempo={tempo}");
    }

    /// <summary>
    /// Blocks until <paramref name="condition"/> is true or the timeout elapses.
    /// </summary>
    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return true;
            Thread.Sleep(5);
        }
        return condition();
    }
}
