using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Audio.Tracks;

namespace Ownaudio.EngineTest;

/// <summary>
/// Integration tests for the Rust-backed track audio-feed path
/// (<see cref="AudioTrack.Write"/> into the native lock-free ring buffer).
/// These exercise the managed binding without opening an audio device.
/// </summary>
[TestClass]
public class MultiTrackFeedTests
{
    private const float SampleRate = 48_000f;
    private const ushort Channels = 2;

    /// <summary>Buffer is sized for two seconds of interleaved audio (see AudioTrack).</summary>
    private static int ExpectedCapacity =>
        (int)(SampleRate * Channels * 2.0f);

    [TestMethod]
    public void NewTrack_ExposesFullFreeCapacity()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        Assert.AreEqual(ExpectedCapacity, track.FreeSampleCount,
            "a fresh track should expose its full ring-buffer capacity as free");
    }

    [TestMethod]
    public void Write_AcceptsSamples_AndReducesFreeCount()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        var samples = new float[1024];
        int before = track.FreeSampleCount;
        int accepted = track.Write(samples);

        Assert.AreEqual(samples.Length, accepted, "all samples should fit in an empty buffer");
        Assert.AreEqual(before - accepted, track.FreeSampleCount, "free count should drop by accepted");
    }

    [TestMethod]
    public void Write_BeyondCapacity_AppliesBackpressure()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        int capacity = track.FreeSampleCount;
        var oversized = new float[capacity + 4096];

        int accepted = track.Write(oversized);
        Assert.AreEqual(capacity, accepted, "only the free capacity should be accepted");
        Assert.AreEqual(0, track.FreeSampleCount, "buffer should now be full");

        // A further write is rejected (non-blocking back-pressure), not an error.
        int again = track.Write(new float[256]);
        Assert.AreEqual(0, again, "a full buffer accepts nothing");
    }

    [TestMethod]
    public void Write_EmptySpan_IsNoOp()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        Assert.AreEqual(0, track.Write(ReadOnlySpan<float>.Empty));
    }

    [TestMethod]
    public void Write_AfterDispose_Throws()
    {
        var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();
        session.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => track.Write(new float[16]));
    }
}
