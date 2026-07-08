using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Audio.Tracks;

namespace Ownaudio.EngineTest;

/// <summary>
/// Tests for the per-track output-channel routing binding
/// (<see cref="AudioTrack.SetOutputChannelMap"/> / <see cref="AudioTrack.ClearOutputChannelMap"/>).
/// These exercise the managed marshalling and native call path without opening an audio device;
/// the routed-mix arithmetic itself is covered by the Rust core unit tests.
/// </summary>
[TestClass]
public class ChannelRoutingTests
{
    private const float SampleRate = 48_000f;
    private const ushort Channels = 4;

    [TestMethod]
    public void SetOutputChannelMap_ValidMap_DoesNotThrow()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        track.SetOutputChannelMap(new[] { 2, 3 });
    }

    [TestMethod]
    public void SetOutputChannelMap_EmptySpan_ClearsRouting()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        track.SetOutputChannelMap(new[] { 0, 1 });
        track.SetOutputChannelMap(ReadOnlySpan<int>.Empty);
    }

    [TestMethod]
    public void ClearOutputChannelMap_AfterSet_DoesNotThrow()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        track.SetOutputChannelMap(new[] { 2, 3 });
        track.ClearOutputChannelMap();
    }

    [TestMethod]
    public void SetOutputChannelMap_NegativeIndex_Throws()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        Assert.ThrowsExactly<ArgumentException>(
            () => track.SetOutputChannelMap(new[] { 0, -1 }));
    }

    [TestMethod]
    public void SetOutputChannelMap_AfterDispose_IsNoOp()
    {
        var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();
        session.Dispose();

        // Set* methods on a disposed track are silent no-ops (matching SetStartDelayFrames).
        track.SetOutputChannelMap(new[] { 0, 1 });
        track.ClearOutputChannelMap();
    }
}
