using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OwnAudio.Midi.File;

namespace OwnAudio.MidiTest;

/// <summary>
/// Round-trip tests for <see cref="MidiFileWriter"/> and <see cref="MidiFileReader"/>.
/// These exercise the batch FFI path (writer_add_events / file_get_events) end to
/// end: a file written out and read back must come through structurally identical,
/// including meta and SysEx payloads that travel as variable-length blobs.
/// </summary>
[TestClass]
public sealed class MidiFileRoundTripTests
{
    #region Helpers

    /// <summary>
    /// Writes the given file to a temp path, reads it straight back, and deletes
    /// the temp file. The returned model is what the reader reconstructed.
    /// </summary>
    private static MidiFile WriteThenRead(MidiFile file)
    {
        string path = Path.GetTempFileName();
        try
        {
            MidiFileWriter.Write(file, path);
            return MidiFileReader.Read(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion

    #region Round-trip Tests

    /// <summary>
    /// A track of plain channel events survives the round trip with its deltas,
    /// status bytes and data bytes intact.
    /// </summary>
    [TestMethod]
    public void ChannelEvents_SurviveRoundTrip()
    {
        var events = new List<MidiEvent>
        {
            new MidiEvent(0, 0x90, 60, 100),
            new MidiEvent(480, 0x80, 60, 0),
            new MidiEvent(0, 0x90, 64, 90),
            new MidiEvent(480, 0x80, 64, 0),
        };
        var source = new MidiFile(0, 480, new[] { new MidiTrack(events) });

        MidiFile roundTripped = WriteThenRead(source);

        Assert.AreEqual(1, roundTripped.Tracks.Count);
        var read = roundTripped.Tracks[0].Events
            .Where(e => e.Type == MidiEventType.Midi)
            .ToList();

        Assert.AreEqual(events.Count, read.Count);
        for (int i = 0; i < events.Count; i++)
        {
            Assert.AreEqual(events[i].DeltaTime, read[i].DeltaTime);
            Assert.AreEqual(events[i].Status, read[i].Status);
            Assert.AreEqual(events[i].Data1, read[i].Data1);
            Assert.AreEqual(events[i].Data2, read[i].Data2);
        }
    }

    /// <summary>
    /// A tempo meta event's three payload bytes come back byte-for-byte, proving
    /// the variable-length blob makes it across the batch writer and reader.
    /// </summary>
    [TestMethod]
    public void TempoMeta_PayloadSurvivesRoundTrip()
    {
        // 500000 us/qn == 120 BPM.
        var tempo = new MidiEvent(0, 0x51, new byte[] { 0x07, 0xA1, 0x20 });
        var events = new List<MidiEvent> { tempo, new MidiEvent(0, 0x90, 60, 100) };
        var source = new MidiFile(0, 480, new[] { new MidiTrack(events) });

        MidiFile roundTripped = WriteThenRead(source);

        var readTempo = roundTripped.Tracks[0].Events.First(e => e.IsTempoChange);
        Assert.AreEqual(500_000, readTempo.GetTempoMicroseconds());
        CollectionAssert.AreEqual(tempo.MetaData, readTempo.MetaData);
    }

    /// <summary>
    /// A SysEx blob round-trips with its full payload, including the leading 0xF0
    /// and the trailing 0xF7.
    /// </summary>
    [TestMethod]
    public void SysEx_PayloadSurvivesRoundTrip()
    {
        var sysexPayload = new byte[] { 0xF0, 0x43, 0x12, 0x00, 0x01, 0x02, 0xF7 };
        var events = new List<MidiEvent> { new MidiEvent(0, sysexPayload) };
        var source = new MidiFile(0, 480, new[] { new MidiTrack(events) });

        MidiFile roundTripped = WriteThenRead(source);

        var readSysEx = roundTripped.Tracks[0].Events.First(e => e.Type == MidiEventType.SysEx);
        CollectionAssert.AreEqual(sysexPayload, readSysEx.MetaData);
    }

    /// <summary>
    /// Multiple tracks and their event counts survive the round trip, so the
    /// per-track batch calls stay independent and correctly ordered.
    /// </summary>
    [TestMethod]
    public void MultipleTracks_SurviveRoundTrip()
    {
        var trackA = new MidiTrack(new List<MidiEvent>
        {
            new MidiEvent(0, 0x90, 60, 100),
            new MidiEvent(480, 0x80, 60, 0),
        });
        var trackB = new MidiTrack(new List<MidiEvent>
        {
            new MidiEvent(0, 0xB0, 7, 127),
        });
        var source = new MidiFile(1, 480, new[] { trackA, trackB });

        MidiFile roundTripped = WriteThenRead(source);

        Assert.AreEqual(2, roundTripped.Tracks.Count);
        Assert.IsTrue(roundTripped.Tracks[0].Events.Any(e => e.Status == 0x90));
        Assert.IsTrue(roundTripped.Tracks[1].Events.Any(e => e.Status == 0xB0));
    }

    /// <summary>
    /// Format word and ticks-per-beat come back unchanged.
    /// </summary>
    [TestMethod]
    public void Header_SurvivesRoundTrip()
    {
        var events = new List<MidiEvent> { new MidiEvent(0, 0x90, 60, 100) };
        var source = new MidiFile(1, 96, new[] { new MidiTrack(events) });

        MidiFile roundTripped = WriteThenRead(source);

        Assert.AreEqual(1, roundTripped.Format);
        Assert.AreEqual(96, roundTripped.TicksPerBeat);
    }

    #endregion
}
