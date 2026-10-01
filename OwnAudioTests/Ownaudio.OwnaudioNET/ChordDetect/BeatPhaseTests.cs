using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Analysis;
using OwnaudioNET.Features.OwnChordDetect.Core;
using OwnaudioNET.Features.OwnChordDetect.Detectors;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.ChordDetect;

/// <summary>
/// The beat grid has to sit where the beats are and follow them when the tempo moves, and minor
/// keys have to treat the raised leading tone as their own.
/// </summary>
public sealed class BeatPhaseTests
{
    private static readonly string[] _sharpNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    private static readonly string[] _flatNames = { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" };

    private static readonly int[][] _progression =
    {
        new[] { 60, 64, 67 },
        new[] { 65, 69, 72 },
        new[] { 67, 71, 74 },
        new[] { 69, 72, 76 },
    };

    private static readonly string[] _progressionNames = { "C", "F", "G", "Am" };

    /// <summary>
    /// Every chord struck on each of its four beats, beat lengths as given. Returns the notes and
    /// the time each chord starts.
    /// </summary>
    private static (List<Note> notes, List<float> changes) _strum(float firstBeat, IReadOnlyList<float> beatLengths)
    {
        var notes = new List<Note>();
        var changes = new List<float>();
        float time = firstBeat;

        for (int beat = 0; beat < beatLengths.Count; beat++)
        {
            if (beat % 4 == 0) changes.Add(time);

            foreach (int pitch in _progression[beat / 4 % _progression.Length])
                notes.Add(new Note(time, time + beatLengths[beat] * 0.9f, pitch, 0.8f, null));

            time += beatLengths[beat];
        }

        changes.Add(time);
        return (notes, changes);
    }

    private static void _shouldChangeOnTheBeats(List<TimedChord> chords, List<float> changes, int chordCount)
    {
        chords.Select(c => c.ChordName).Should()
            .Equal(Enumerable.Range(0, chordCount).Select(i => _progressionNames[i % _progressionNames.Length]));

        for (int i = 0; i < chordCount; i++)
            chords[i].StartTime.Should().BeApproximately(changes[i], 0.03f, $"chord {i} starts on its beat");

        for (int i = 0; i + 1 < chordCount; i++)
            chords[i].EndTime.Should().BeApproximately(changes[i + 1], 0.03f, $"chord {i} ends on the next change");
    }

    /// <summary>
    /// The music starts 0.3 s in, well off the zero-based grid. The tracked beats find the phase,
    /// so every change is reported on its beat instead of a quarter of a beat beside it.
    /// </summary>
    [Fact]
    public void AnalyzeSong_BeatsOffsetFromZero_ChangesLandOnTheBeats()
    {
        var (notes, changes) = _strum(0.3f, Enumerable.Repeat(0.5f, 12).ToList());

        var chords = new SongChordAnalyzer(bpm: 120).AnalyzeSong(notes);

        _shouldChangeOnTheBeats(chords, changes, 3);
    }

    /// <summary>
    /// A song slowing from 120 to about 104 BPM. A fixed grid at the nominal tempo would be most
    /// of a beat off by the end; the tracked one stays on the changes the whole way.
    /// </summary>
    [Fact]
    public void AnalyzeSong_DriftingTempo_GridFollowsTheBeats()
    {
        var beatLengths = Enumerable.Range(0, 32).Select(i => 0.5f + 0.0025f * i).ToList();
        var (notes, changes) = _strum(0f, beatLengths);

        var chords = new SongChordAnalyzer(bpm: 120).AnalyzeSong(notes);

        _shouldChangeOnTheBeats(chords, changes, 8);
    }

    /// <summary>
    /// The major dominant belongs to a minor key through its raised leading tone, so in A minor
    /// E major has to get the in-key nudge that it doesn't get in C major.
    /// </summary>
    [Fact]
    public void ScaleMask_MinorKey_TreatsTheMajorDominantAsInKey()
    {
        var eMajor = new List<Note>
        {
            new Note(0f, 1f, 52, 0.8f, null),
            new Note(0f, 1f, 56, 0.8f, null),
            new Note(0f, 1f, 59, 0.8f, null),
        };

        float inAMinor = _scoreOf("E", eMajor, new MusicalKey("Am", false, 0, 0, _sharpNames));
        float inCMajor = _scoreOf("E", eMajor, new MusicalKey("C", true, 0, 0, _sharpNames));

        inAMinor.Should().BeGreaterThan(inCMajor);
    }

    /// <summary>
    /// Cb major isn't in the flat spelling table, which used to leave it without any scale at
    /// all — everything counted as in key. A C major triad is foreign to it.
    /// </summary>
    [Fact]
    public void ScaleMask_CbMajor_HasAScale()
    {
        var cMajor = new List<Note>
        {
            new Note(0f, 1f, 48, 0.8f, null),
            new Note(0f, 1f, 52, 0.8f, null),
            new Note(0f, 1f, 55, 0.8f, null),
        };

        float inCb = _scoreOf("C", cMajor, new MusicalKey("Cb", true, 0, 7, _flatNames));
        float inC = _scoreOf("C", cMajor, new MusicalKey("C", true, 0, 0, _sharpNames));

        inCb.Should().BeLessThan(inC);
    }

    private static float _scoreOf(string chordName, List<Note> notes, MusicalKey key)
    {
        var detector = new ChordDetector(DetectionMode.KeyAware);
        detector.SetKey(key);

        return detector.GetChordCandidates(notes, 32).Single(c => c.Name == chordName).Score;
    }
}
