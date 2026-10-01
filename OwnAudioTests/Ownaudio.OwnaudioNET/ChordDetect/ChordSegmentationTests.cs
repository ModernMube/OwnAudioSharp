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
/// Regressions for the song analyzer's note pruning and segment timing, and for custom
/// templates actually reaching the matcher.
/// </summary>
public sealed class ChordSegmentationTests
{
    private static Note _makeNote(int pitch, float start, float end, float amplitude = 0.8f)
    {
        return new Note(start, end, pitch, amplitude, null);
    }

    /// <summary>
    /// Overlapping analysis windows must not leak into the result: each segment ends where the
    /// next one starts at the latest, and the change lands near where it really happens.
    /// </summary>
    [Fact]
    public void AnalyzeSong_ChordChanges_SegmentsDoNotOverlap()
    {
        var notes = new List<Note>
        {
            _makeNote(60, 0f, 2f), _makeNote(64, 0f, 2f), _makeNote(67, 0f, 2f),
            _makeNote(65, 2f, 4f), _makeNote(69, 2f, 4f), _makeNote(72, 2f, 4f),
            _makeNote(67, 4f, 6f), _makeNote(71, 4f, 6f), _makeNote(74, 4f, 6f),
        };

        var chords = new SongChordAnalyzer(windowSize: 1.0f, hopSize: 0.5f).AnalyzeSong(notes);

        chords.Select(c => c.ChordName).Should().Equal("C", "F", "G");
        for (int i = 1; i < chords.Count; i++)
            chords[i].StartTime.Should().BeGreaterThanOrEqualTo(chords[i - 1].EndTime - 0.0001f);

        chords[0].EndTime.Should().BeApproximately(2f, 0.3f);
        chords[1].EndTime.Should().BeApproximately(4f, 0.3f);
    }

    /// <summary>
    /// A quiet stray note pushes the window under a strict threshold, so the analyzer has to
    /// prune. It must drop the stray note, not the A in the bass — dropping the bass would leave
    /// C-E-G and turn an Am7 into a C.
    /// </summary>
    [Fact]
    public void AnalyzeSong_Pruning_KeepsTheBassNote()
    {
        var notes = new List<Note>
        {
            _makeNote(45, 0f, 4f),
            _makeNote(60, 0f, 4f),
            _makeNote(64, 0f, 4f),
            _makeNote(67, 0f, 4f),
            _makeNote(73, 0f, 4f, 0.3f),
        };

        var chords = new SongChordAnalyzer(windowSize: 1.0f, hopSize: 0.5f, confidence: 0.95f).AnalyzeSong(notes);

        chords.Should().NotBeEmpty();
        chords.Should().OnlyContain(c => c.ChordName == "Am7");
    }

    /// <summary>
    /// A template added by hand has to take part in matching right away, and a key change that
    /// rebuilds the built-in templates must not throw it away.
    /// </summary>
    [Fact]
    public void AddChordTemplate_IsMatchedAndSurvivesAKeyChange()
    {
        var notes = new List<Note>
        {
            _makeNote(48, 0f, 2f),
            _makeNote(53, 0f, 2f),
            _makeNote(55, 0f, 2f),
            _makeNote(58, 0f, 2f),
        };

        var detector = new ChordDetector();
        detector.AddChordTemplate("C7sus4", new[] { 0, 5, 7, 10 });

        detector.AnalyzeChord(notes).ChordName.Should().Be("C7sus4");

        detector.SetKey(new MusicalKey("F", true, 0, 1,
            new[] { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" }));

        detector.AnalyzeChord(notes).ChordName.Should().Be("C7sus4");
    }
}
