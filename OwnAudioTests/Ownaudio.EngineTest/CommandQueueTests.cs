using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Audio.Effects;
using Ownaudio.Audio.Tracks;
using Ownaudio.Safe.Exceptions;

namespace Ownaudio.EngineTest;

/// <summary>
/// A full command queue, with no device to drain it: the refusal comes as its own exception,
/// and a refused call leaves nothing half done behind.
/// </summary>
[TestClass]
public class CommandQueueTests
{
    private const int SampleRate = 48_000;
    private const ushort Channels = 2;

    /// <summary>
    /// Fills the queue with resets, which cost a slot each and nothing else. Returns how many went in.
    /// </summary>
    private static int _fill(MultiTrackSession session, object effect, int limit = int.MaxValue)
    {
        int sent = 0;
        while (sent < limit && session.MasterEffects.Reset(effect)) sent++;
        return sent;
    }

    [TestMethod]
    public void AFullQueueThrowsItsOwnException()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        var compressor = session.MasterEffects.Add(EffectType.Compressor, SampleRate);
        _fill(session, compressor);

        Assert.ThrowsExactly<CommandQueueFullException>(() => session.AddTrack());
        Assert.AreEqual(0, session.Tracks.Count);
    }

    /// <summary>
    /// One slot left: the track goes in, its file source does not. The rollback cannot get its
    /// removal in either, and that must not bury the refusal the caller has to see.
    /// </summary>
    [TestMethod]
    public void AHalfAddedFileTrackStillReportsTheFullQueue()
    {
        int room;
        using (var probe = new MultiTrackSession(SampleRate, Channels))
            room = _fill(probe, probe.MasterEffects.Add(EffectType.Compressor, SampleRate));

        string wav = _silentWav();
        try
        {
            using var session = new MultiTrackSession(SampleRate, Channels);
            var compressor = session.MasterEffects.Add(EffectType.Compressor, SampleRate);
            Assert.AreEqual(room - 1, _fill(session, compressor, room - 1));

            Assert.ThrowsExactly<CommandQueueFullException>(() => session.AddFileTrack(wav, Channels));
            Assert.AreEqual(0, session.Tracks.Count);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    /// <summary>
    /// A tenth of a second of 16-bit stereo silence.
    /// </summary>
    private static string _silentWav()
    {
        string path = Path.Combine(Path.GetTempPath(), $"queue-{Guid.NewGuid():N}.wav");
        const int frames = SampleRate / 10;
        int data = frames * Channels * 2;

        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write("RIFF"u8.ToArray());
            writer.Write(36 + data);
            writer.Write("WAVEfmt "u8.ToArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)Channels);
            writer.Write(SampleRate);
            writer.Write(SampleRate * Channels * 2);
            writer.Write((short)(Channels * 2));
            writer.Write((short)16);
            writer.Write("data"u8.ToArray());
            writer.Write(data);
            writer.Write(new byte[data]);
        }

        return path;
    }
}
