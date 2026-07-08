using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Audio.Tracks;

namespace Ownaudio.EngineTest;

/// <summary>
/// Tests for the native memory-backed track binding
/// (<see cref="MultiTrackSession.AddMemoryTrack"/> / <see cref="MemoryTrack"/>). These exercise the
/// managed marshalling and native install/control path without opening an audio device; the actual
/// buffer serving (read/loop/seek) is covered by the Rust core <c>memory_source</c> unit tests.
/// </summary>
[TestClass]
public class NativeMemoryTrackTests
{
    private const float SampleRate = 48_000f;
    private const ushort Channels = 2;

    private static float[] Tone(int frames)
    {
        var samples = new float[frames * Channels];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.25f;
        }
        return samples;
    }

    [TestMethod]
    public void AddMemoryTrack_InstallsNativeSource()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);

        MemoryTrack memoryTrack = session.AddMemoryTrack(Tone(4_800), loop: false);

        Assert.AreEqual(1, session.Tracks.Count);
        Assert.IsNotNull(memoryTrack.Track);
        Assert.AreSame(memoryTrack.Track, session.Tracks[0]);
        Assert.IsFalse(memoryTrack.IsFinished);
    }

    [TestMethod]
    public void MemoryTrack_LoopSeekReload_DoNotThrow()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        MemoryTrack memoryTrack = session.AddMemoryTrack(Tone(4_800), loop: false);

        memoryTrack.Loop = true;
        Assert.IsTrue(memoryTrack.Loop);

        memoryTrack.Seek(TimeSpan.FromMilliseconds(10));
        memoryTrack.Reload(Tone(9_600));

        Assert.AreEqual(1, session.Tracks.Count);
    }

    [TestMethod]
    public void AddMemoryTrack_EmptyBuffer_InstallsSilentSource()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);

        MemoryTrack memoryTrack = session.AddMemoryTrack(ReadOnlySpan<float>.Empty, loop: false);

        Assert.AreEqual(1, session.Tracks.Count);
        Assert.IsNotNull(memoryTrack.Track);
    }

    [TestMethod]
    public void RemoveTrack_RemovesMemoryTrack()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        MemoryTrack memoryTrack = session.AddMemoryTrack(Tone(4_800), loop: false);

        session.RemoveTrack(memoryTrack.Track);

        Assert.AreEqual(0, session.Tracks.Count);
    }

    [TestMethod]
    public void AddInputTrack_NullEngine_Throws()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);

        Assert.ThrowsExactly<ArgumentNullException>(() => session.AddInputTrack(null!));
    }
}
