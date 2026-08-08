using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Analysis;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.ChordDetect;

/// <summary>
/// Covers the beat-driven sizing path of <see cref="SongChordAnalyzer"/>: with a tempo the
/// window, the hop and the minimum chord length all come from the quarter note and the
/// explicit arguments are ignored. None of the other suites pass a bpm, so this is the only
/// place that path is exercised.
/// </summary>
public sealed class BeatDrivenWindowTests
{
    private static Note _makeNote(int pitch, float start, float end)
    {
        return new Note(start, end, pitch, 0.8f, null);
    }

    /// <summary>
    /// Two triads back to back, the change at <paramref name="changeTime"/>.
    /// </summary>
    private static List<Note> _twoChords(float changeTime, float endTime)
    {
        return new List<Note>
        {
            _makeNote(60, 0f, changeTime),
            _makeNote(64, 0f, changeTime),
            _makeNote(67, 0f, changeTime),
            _makeNote(65, changeTime, endTime),
            _makeNote(69, changeTime, endTime),
            _makeNote(72, changeTime, endTime),
        };
    }

    /// <summary>
    /// A tempo has to beat the explicit sizes. Four-second window and minimum would swallow the
    /// whole thing into one segment; at 120 BPM the quarter note is half a second and both
    /// chords come out.
    /// </summary>
    [Fact]
    public void AnalyzeSong_WithTempo_IgnoresTheExplicitSizes()
    {
        var analyzer = new SongChordAnalyzer(
            windowSize: 4.0f,
            hopSize: 2.0f,
            minimumChordDuration: 4.0f,
            bpm: 120);

        var chords = analyzer.AnalyzeSong(_twoChords(2f, 4f));

        chords.Select(c => c.ChordName).Should().Equal("C", "F");
    }

    /// <summary>
    /// The window is a quarter note, not a half or a whole one. At 180 BPM that's 0.33s, so a
    /// chord change one second in lands within a hop of where it belongs — the old whole-note
    /// window was 1.33s and couldn't place it anywhere near.
    /// </summary>
    [Fact]
    public void AnalyzeSong_FastTempo_PlacesTheChangeOnTheBeat()
    {
        var analyzer = new SongChordAnalyzer(bpm: 180);

        var chords = analyzer.AnalyzeSong(_twoChords(1f, 2f));

        chords.Select(c => c.ChordName).Should().Equal("C", "F");
        chords[0].EndTime.Should().BeApproximately(1f, 0.2f);
    }

    /// <summary>
    /// The regression the hard-coded 0.5s minimum would cause: above 120 BPM a quarter note is
    /// shorter than that, so a chord this brief used to be dropped from the result entirely.
    /// </summary>
    [Fact]
    public void AnalyzeSong_FastTempo_KeepsAQuarterLongChord()
    {
        var notes = new List<Note>
        {
            _makeNote(60, 0f, 0.35f),
            _makeNote(64, 0f, 0.35f),
            _makeNote(67, 0f, 0.35f),
        };

        var analyzer = new SongChordAnalyzer(bpm: 180);
        var chords = analyzer.AnalyzeSong(notes);

        chords.Should().ContainSingle().Which.ChordName.Should().Be("C");
    }

    /// <summary>
    /// Same notes, same everything, only the tempo is gone — now the 0.5s minimum applies again
    /// and the chord is too short to survive.
    /// </summary>
    [Fact]
    public void AnalyzeSong_WithoutTempo_StillHonoursTheMinimum()
    {
        var notes = new List<Note>
        {
            _makeNote(60, 0f, 0.35f),
            _makeNote(64, 0f, 0.35f),
            _makeNote(67, 0f, 0.35f),
        };

        var analyzer = new SongChordAnalyzer(windowSize: 0.33f, hopSize: 0.165f, minimumChordDuration: 0.5f);
        var chords = analyzer.AnalyzeSong(notes);

        chords.Should().BeEmpty();
    }

    /// <summary>
    /// Slower tempo, longer quarter — the sizes really do scale rather than sitting at some
    /// fixed value. At 60 BPM the minimum is a full second, so the 0.35s chord goes away again.
    /// </summary>
    [Fact]
    public void AnalyzeSong_SlowTempo_DropsWhatIsShorterThanItsQuarter()
    {
        var notes = new List<Note>
        {
            _makeNote(60, 0f, 0.35f),
            _makeNote(64, 0f, 0.35f),
            _makeNote(67, 0f, 0.35f),
        };

        var analyzer = new SongChordAnalyzer(bpm: 60);
        var chords = analyzer.AnalyzeSong(notes);

        chords.Should().BeEmpty();
    }

    /// <summary>
    /// The old banding doubled the window at 100 BPM and again above 150, so one beat of tempo
    /// difference shifted the chord change by a good fraction of a second. Neighbours across
    /// those edges have to agree now.
    /// </summary>
    [Theory]
    [InlineData(99, 100)]
    [InlineData(150, 151)]
    public void AnalyzeSong_AcrossTheOldBandEdges_HasNoJump(int slower, int faster)
    {
        var below = new SongChordAnalyzer(bpm: slower).AnalyzeSong(_twoChords(2f, 4f));
        var above = new SongChordAnalyzer(bpm: faster).AnalyzeSong(_twoChords(2f, 4f));

        below.Select(c => c.ChordName).Should().Equal("C", "F");
        above.Select(c => c.ChordName).Should().Equal("C", "F");
        above[0].EndTime.Should().BeApproximately(below[0].EndTime, 0.1f);
    }
}
