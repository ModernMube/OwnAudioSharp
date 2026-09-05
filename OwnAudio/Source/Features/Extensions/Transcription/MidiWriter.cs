using OwnAudio.Midi.File;
using Logger;
using Microsoft.ML.OnnxRuntime;
using System.Numerics.Tensors;

namespace OwnaudioNET.Features.Extensions;

/// <summary>
/// Writes the detected notes out as a standard MIDI file.
/// </summary>
public static class MidiWriter
{
    /// <summary>
    /// BPM the last export settled on.
    /// </summary>
    public static int DetectedTempo = 120;

    /// <summary>
    /// Note on/off events at 480 ticks per quarter, tempo meta up front, Rhodes patch on ch0.
    /// With enough notes we sniff the tempo instead of trusting bpm.
    /// </summary>
    /// <param name="notes"></param>
    /// <param name="outputPath">Where the .mid lands.</param>
    /// <param name="bpm">Fallback tempo if we can't detect one.</param>
    public static void GenerateMidiFile(List<Note> notes, string outputPath, int bpm = 120)
    {
        if (notes.Count > 10)
        {
            bpm = DetectTempo(notes);
            DetectedTempo = bpm;
        }

        Log.Info($"Generating MIDI file with BPM: {bpm}");
        Log.Info($"Number of notes: {notes.Count}");

        int microsecondsPerQuarterNote = 60_000_000 / bpm;

        var timedRaw = new List<(long absoluteTime, bool isMeta, byte metaType, byte[]? metaData, byte status, byte data1, byte data2)>();

        byte[] tempoData =
        [
            (byte)((microsecondsPerQuarterNote >> 16) & 0xFF),
            (byte)((microsecondsPerQuarterNote >> 8)  & 0xFF),
            (byte)( microsecondsPerQuarterNote        & 0xFF)
        ];
        timedRaw.Add((0, true, 0x51, tempoData, 0, 0, 0));
        timedRaw.Add((0, false, 0, null, 0xC0, 4, 0));

        double quarterNotesPerSecond = bpm / 60.0;
        foreach (var note in notes)
        {
            long startTicks = (long)(note.StartTime * 480 * quarterNotesPerSecond);
            long endTicks   = (long)(note.EndTime   * 480 * quarterNotesPerSecond);
            byte velocity   = (byte)Math.Clamp((int)(note.Amplitude * 100f), 1, 127);

            timedRaw.Add((startTicks, false, 0, null, 0x90, (byte)note.Pitch, velocity));
            timedRaw.Add((endTicks,   false, 0, null, 0x80, (byte)note.Pitch, 0));
        }

        timedRaw.Sort((a, b) => a.absoluteTime.CompareTo(b.absoluteTime));

        var midiEvents = new List<MidiEvent>(timedRaw.Count);
        long previousTime = 0;
        foreach (var (absoluteTime, isMeta, metaType, metaData, status, data1, data2) in timedRaw)
        {
            int deltaTime = (int)(absoluteTime - previousTime);
            midiEvents.Add(isMeta
                ? new MidiEvent(deltaTime, metaType, metaData!)
                : new MidiEvent(deltaTime, status, data1, data2));
            previousTime = absoluteTime;
        }

        var track    = new MidiTrack(midiEvents);
        var midiFile = new MidiFile(0, 480, [track]);

        MidiFileWriter.Write(midiFile, outputPath);

        Log.Info($"MIDI file saved: {outputPath}");
        Log.Info($"Time division: 480 ticks per quarter note");
        Log.Info($"Tempo: {bpm} BPM ({microsecondsPerQuarterNote} μs per quarter note)");
    }

    /// <summary>
    /// Guesses the tempo from onset gaps, then snaps to a round BPM if we're close.
    /// </summary>
    /// <param name="notes"></param>
    /// <returns></returns>
    public static int DetectTempo(List<Note> notes)
    {
        if (notes.Count < 2) return 120;

        var onsetTimes = notes.Select(n => n.StartTime).OrderBy(t => t).ToList();

        var intervals = new List<float>();
        for (int i = 1; i < onsetTimes.Count; i++)
        {
            float interval = onsetTimes[i] - onsetTimes[i - 1];
            if (interval > 0.05f && interval < 2.0f)
                intervals.Add(interval);
        }

        if (intervals.Count == 0) return 120;

        var beatCandidates = new Dictionary<int, int>();

        foreach (var interval in intervals)
        {
            for (int division = 1; division <= 4; division *= 2)
            {
                int bpm = (int)Math.Round(60.0f / (interval * division));
                if (bpm < 40 || bpm > 200) continue;

                for (int offset = -2; offset <= 2; offset++)
                {
                    int _candidate = bpm + offset;
                    if (_candidate < 40 || _candidate > 200) continue;

                    beatCandidates.TryGetValue(_candidate, out int hits);
                    beatCandidates[_candidate] = hits + 1;
                }
            }
        }

        if (beatCandidates.Count == 0) return 120;

        var detectedBpm = beatCandidates.OrderByDescending(kvp => kvp.Value).First().Key;

        int[] commonTempos = { 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160 };
        foreach (var commonTempo in commonTempos)
        {
            if (Math.Abs(detectedBpm - commonTempo) <= 3) return commonTempo;
        }

        return detectedBpm;
    }
}
