using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Audio.Tracks;

namespace Ownaudio.EngineTest;

/// <summary>
/// Tests for the pre/post effect-chain tap FFI. Nothing renders without an audio device, so these
/// cover the handle plumbing and the empty-ring behaviour; the audio itself is covered by the
/// mixer-level Rust tests.
/// </summary>
[TestClass]
public class FxTapTests
{
    private const float SampleRate = 48_000f;
    private const ushort Channels = 2;

    [TestMethod]
    public void TrackTap_StartsAndStops()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        track.StartFxTap(4096);
        track.StopFxTap();
    }

    [TestMethod]
    public void TrackTap_ReadOnIdleTrack_ReturnsNothing()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();
        track.StartFxTap(4096);

        var pre = new float[512];
        var post = new float[512];

        Assert.AreEqual(0, track.ReadFxTap(pre, post), "nothing rendered yet, so the ring is empty");
    }

    [TestMethod]
    public void TrackTap_ReadWithoutTap_ReturnsNothing()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        var pre = new float[256];
        var post = new float[256];

        Assert.AreEqual(0, track.ReadFxTap(pre, post));
    }

    [TestMethod]
    public void TrackTap_StartTwice_ReplacesTheOldOne()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        track.StartFxTap(2048);
        track.StartFxTap(8192);
        track.StopFxTap();
    }

    [TestMethod]
    public void TrackTap_StopWithoutStart_IsNoOp()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        track.StopFxTap();
        track.StopFxTap();
    }

    [TestMethod]
    public void MasterTap_StartsReadsAndStops()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);

        session.StartMasterFxTap(4096);

        var pre = new float[512];
        var post = new float[512];
        Assert.AreEqual(0, session.ReadMasterFxTap(pre, post));

        session.StopMasterFxTap();
    }

    [TestMethod]
    public void MasterTap_EmptySpans_ReadNothing()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        session.StartMasterFxTap(4096);

        Assert.AreEqual(0, session.ReadMasterFxTap(Span<float>.Empty, Span<float>.Empty));
    }

    [TestMethod]
    public void DisposingTheSession_WithTapsRunning_IsClean()
    {
        var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        track.StartFxTap(4096);
        session.StartMasterFxTap(4096);

        session.Dispose();
    }
}
