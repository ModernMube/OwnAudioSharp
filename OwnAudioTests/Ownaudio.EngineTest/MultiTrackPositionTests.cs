using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Audio.Tracks;

namespace Ownaudio.EngineTest;

/// <summary>
/// Tests for the managed <see cref="AudioTrack.RenderedFrames"/> / <see cref="AudioTrack.Position"/>
/// binding (D.1). The rendered position only advances on the audio thread during a real mix, so
/// these exercise the control-side contract (P/Invoke marshalling of the <c>u64</c> out-parameter,
/// the seek reset, and the position derivation) without opening an audio device; the advance
/// behavior itself is covered by the Rust core tests.
/// </summary>
[TestClass]
public class MultiTrackPositionTests
{
    private const float SampleRate = 48_000f;
    private const ushort Channels = 2;

    [TestMethod]
    public void NewTrack_RenderedFramesIsZero()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        Assert.AreEqual(0UL, track.RenderedFrames,
            "a track that has not been mixed yet has rendered nothing");
    }

    [TestMethod]
    public void NewTrack_PositionIsZero()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        Assert.AreEqual(TimeSpan.Zero, track.Position);
    }

    [TestMethod]
    public void Seek_ResetsRenderedPosition_WithoutError()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        // Exercises both the native no-op seek and the rendered-position reset P/Invoke path.
        track.Seek(TimeSpan.FromSeconds(1.5));

        Assert.AreEqual(0UL, track.RenderedFrames,
            "seeking resets the rendered-frame counter to zero");
        Assert.AreEqual(TimeSpan.Zero, track.Position);
    }

    [TestMethod]
    public void RenderedFrames_AfterDispose_IsZero()
    {
        var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();
        session.Dispose();

        Assert.AreEqual(0UL, track.RenderedFrames, "a disposed track reports a zero position");
        Assert.AreEqual(TimeSpan.Zero, track.Position);
    }
}
