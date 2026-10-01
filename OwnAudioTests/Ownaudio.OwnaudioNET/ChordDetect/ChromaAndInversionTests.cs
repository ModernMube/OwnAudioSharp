using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Analysis;
using OwnaudioNET.Features.OwnChordDetect.Detectors;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.ChordDetect;

/// <summary>
/// Register weighting and log compression of the chromagram, and inversion naming from the bass
/// line — single note sets and whole songs.
/// </summary>
public sealed class ChromaAndInversionTests
{
    private static Note _makeNote(int pitch, float start, float end)
    {
        return new Note(start, end, pitch, 0.8f, null);
    }

    private static List<Note> _held(float start, float end, params int[] pitches)
    {
        return pitches.Select(p => _makeNote(p, start, end)).ToList();
    }

    /// <summary>
    /// The same A two octaves higher is melody territory and has to weigh less than in the
    /// middle of the voicing.
    /// </summary>
    [Fact]
    public void ComputeChromagram_HighNote_CountsLessThanTheSameNoteLower()
    {
        var detector = new ChordDetector();

        float middle = detector.ComputeChromagram(_held(0f, 1f, 60, 57))[9];
        float high = detector.ComputeChromagram(_held(0f, 1f, 60, 81))[9];

        high.Should().BeLessThan(middle);
    }

    /// <summary>
    /// A root doubled in three octaves has three times the energy of the third. Compressed, the
    /// third keeps about half the root's weight instead of a third.
    /// </summary>
    [Fact]
    public void ComputeChromagram_TripledRoot_DoesNotDrownTheThird()
    {
        var chroma = new ChordDetector().ComputeChromagram(_held(0f, 1f, 36, 48, 60, 64, 67));

        (chroma[4] / chroma[0]).Should().BeGreaterThan(0.45f);
    }

    /// <summary>
    /// A held E under a C major triad is a first inversion.
    /// </summary>
    [Fact]
    public void AnalyzeChord_HeldThirdInTheBass_NamesTheInversion()
    {
        var analysis = new ChordDetector().AnalyzeChord(_held(0f, 2f, 40, 60, 64, 67));

        analysis.ChordName.Should().Be("C/E");
    }

    /// <summary>
    /// Root in the bass, no slash.
    /// </summary>
    [Fact]
    public void AnalyzeChord_RootInTheBass_HasNoSlash()
    {
        var analysis = new ChordDetector().AnalyzeChord(_held(0f, 2f, 48, 60, 64, 67));

        analysis.ChordName.Should().Be("C");
    }

    /// <summary>
    /// A root–fifth bass alternating under the chord is still root position — neither note holds
    /// the bottom long enough to call it an inversion.
    /// </summary>
    [Fact]
    public void AnalyzeChord_AlternatingRootAndFifthBass_StaysRootPosition()
    {
        var notes = _held(0f, 2f, 60, 64, 67);
        notes.Add(_makeNote(36, 0f, 0.5f));
        notes.Add(_makeNote(31, 0.5f, 1f));
        notes.Add(_makeNote(36, 1f, 1.5f));
        notes.Add(_makeNote(31, 1.5f, 2f));

        var analysis = new ChordDetector().AnalyzeChord(notes);

        analysis.ChordName.Should().Be("C");
    }

    /// <summary>
    /// Through the song analyzer the inversion is named per segment: C over E, then F in root
    /// position.
    /// </summary>
    [Fact]
    public void AnalyzeSong_InvertedChord_IsNamedWithItsBass()
    {
        var notes = _held(0f, 4f, 40, 60, 64, 67);
        notes.AddRange(_held(4f, 8f, 41, 65, 69, 72));

        var chords = new SongChordAnalyzer(windowSize: 1.0f, hopSize: 0.5f).AnalyzeSong(notes);

        chords.Select(c => c.ChordName).Should().Equal("C/E", "F");
    }

    /// <summary>
    /// Ambiguous calls in Optimized mode are joined with a bar now — a slash would read as an
    /// inversion.
    /// </summary>
    [Fact]
    public void OptimizedMode_AmbiguousCall_IsNotWrittenLikeAnInversion()
    {
        var detector = new OptimizedChordDetector();

        var (chord, _, isAmbiguous, _) = detector.DetectChordAdvanced(_held(0f, 1f, 60, 64, 67, 69));

        isAmbiguous.Should().BeTrue();
        chord.Should().Contain(" | ").And.NotContain("/");
    }
}
