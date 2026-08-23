using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Audio.Tracks;

namespace Ownaudio.EngineTest;

/// <summary>
/// Destination-indexed routing, per-track source width and the master channel scope, from the
/// managed side: marshalling, length/range checks and the disposed no-ops. No audio device is
/// opened - the routed-mix arithmetic itself is covered by the Rust core unit tests.
/// </summary>
[TestClass]
public class OutputRouteTests
{
    private const float SampleRate = 48_000f;
    private const ushort Channels = 4;

    [TestMethod]
    public void SetOutputRoute_FanOut_DoesNotThrow()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        //Source channel 0 onto bus 0 and 2, channel 1 onto bus 3, nothing on bus 1.
        track.SetOutputRoute(new[] { 0, -1, 0, 1 });
    }

    [TestMethod]
    public void SetOutputRoute_WithGains_DoesNotThrow()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        track.SetOutputRoute(new[] { 0, 1 }, new[] { 1.0f, 0.5f });
    }

    [TestMethod]
    public void SetOutputRoute_GainLengthMismatch_Throws()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        Assert.ThrowsExactly<ArgumentException>(
            () => track.SetOutputRoute(new[] { 0, 1 }, new[] { 1.0f }));
    }

    [TestMethod]
    public void SetOutputRoute_EmptySpan_ClearsIt()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        track.SetOutputRoute(new[] { 0, 1 });
        track.SetOutputRoute(ReadOnlySpan<int>.Empty);
        track.ClearOutputRoute();
    }

    [TestMethod]
    public void SetOutputRoute_AfterDispose_IsNoOp()
    {
        var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();
        session.Dispose();

        track.SetOutputRoute(new[] { 0, 1 });
        track.ClearOutputRoute();
    }

    [TestMethod]
    public void SourceChannels_RoundTrips_AndDefaultsToFollowingTheBus()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        Assert.AreEqual(0, track.SourceChannels, "0 means follow the bus");

        track.SourceChannels = 2;
        Assert.AreEqual(2, track.SourceChannels);

        track.SourceChannels = 0;
        Assert.AreEqual(0, track.SourceChannels);
    }

    [TestMethod]
    public void SourceChannels_OutOfRange_Throws()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => track.SourceChannels = -1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => track.SourceChannels = 70_000);
    }

    [TestMethod]
    public void MasterChannelScope_DefaultsToEmpty_AndRoundTrips()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);

        CollectionAssert.AreEqual(Array.Empty<int>(), session.MasterChannelScope,
            "empty scope is the whole bus, which is what it always was");

        session.MasterChannelScope = new[] { 0, 1 };
        CollectionAssert.AreEqual(new[] { 0, 1 }, session.MasterChannelScope);

        session.MasterChannelScope = Array.Empty<int>();
        CollectionAssert.AreEqual(Array.Empty<int>(), session.MasterChannelScope);
    }

    [TestMethod]
    public void MasterChannelScope_ChannelOutsideTheSession_Throws()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => session.MasterChannelScope = new[] { 0, Channels });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => session.MasterChannelScope = new[] { -1 });
    }

    [TestMethod]
    public void MasterChannelScope_Null_MeansTheWholeBus()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        session.MasterChannelScope = new[] { 0, 1 };
        session.MasterChannelScope = null!;

        CollectionAssert.AreEqual(Array.Empty<int>(), session.MasterChannelScope);
    }

    [TestMethod]
    public void AddFileTrack_WithExplicitWidth_RejectsAMissingFile()
    {
        //The overload only differs in the decode width; the open path is the same, so this is
        //really just proving the new signature reaches native and reports failure the usual way.
        using var session = new MultiTrackSession(SampleRate, Channels);

        Assert.ThrowsExactly<ArgumentException>(() => session.AddFileTrack("   ", 2));
    }
}
