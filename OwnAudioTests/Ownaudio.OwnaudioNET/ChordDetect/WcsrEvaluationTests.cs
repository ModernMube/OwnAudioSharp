using System;
using System.Collections.Generic;
using FluentAssertions;
using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Analysis;
using OwnaudioNET.Features.OwnChordDetect.Evaluation;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.ChordDetect;

/// <summary>
/// Tests for the WCSR evaluation harness: the Harte-syntax and detector-name chord parsers,
/// the vocabulary-level reduction, the .lab annotation parser and the duration-weighted
/// recall computation, plus a synthetic end-to-end run through <see cref="SongChordAnalyzer"/>.
/// </summary>
public sealed class WcsrEvaluationTests
{
    /// <summary>
    /// Builds a timed chord estimate segment with a fixed confidence and no note names.
    /// </summary>
    /// <param name="start">The start time in seconds.</param>
    /// <param name="end">The end time in seconds.</param>
    /// <param name="name">The detector chord name.</param>
    /// <returns>The estimate segment.</returns>
    private static TimedChord Estimate(float start, float end, string name)
    {
        return new TimedChord(start, end, name, 0.9f, Array.Empty<string>());
    }

    /// <summary>
    /// Harte labels must parse to the correct root pitch class and detailed quality,
    /// including bare roots, bass inversions and the special N label.
    /// </summary>
    [Theory]
    [InlineData("C:maj", 0, "Major")]
    [InlineData("A:min7", 9, "Minor7")]
    [InlineData("Db", 1, "Major")]
    [InlineData("F#:7", 6, "Dominant7")]
    [InlineData("G:maj/3", 7, "Major")]
    [InlineData("B:hdim7", 11, "HalfDiminished7")]
    [InlineData("N", -1, "NoChord")]
    [InlineData("X", -1, "Unknown")]
    public void ParseHarte_ParsesRootAndQuality(string label, int expectedRoot, string expectedQuality)
    {
        var symbol = ChordSymbol.ParseHarte(label);

        symbol.RootPitchClass.Should().Be(expectedRoot);
        symbol.Quality.Should().Be(Enum.Parse<ChordQuality>(expectedQuality));
    }

    /// <summary>
    /// Detector chord names must parse to the correct root pitch class and detailed quality.
    /// </summary>
    [Theory]
    [InlineData("C", 0, "Major")]
    [InlineData("Am7", 9, "Minor7")]
    [InlineData("Bb", 10, "Major")]
    [InlineData("C#dim", 1, "Diminished")]
    [InlineData("Gmaj7", 7, "Major7")]
    [InlineData("Em7b5", 4, "HalfDiminished7")]
    [InlineData("Unknown", -1, "Unknown")]
    public void ParseDetectorName_ParsesRootAndQuality(string name, int expectedRoot, string expectedQuality)
    {
        var symbol = ChordSymbol.ParseDetectorName(name);

        symbol.RootPitchClass.Should().Be(expectedRoot);
        symbol.Quality.Should().Be(Enum.Parse<ChordQuality>(expectedQuality));
    }

    /// <summary>
    /// A perfectly matching estimate must score 1.0 at both vocabulary levels.
    /// </summary>
    [Fact]
    public void Evaluate_PerfectMatch_ScoresOne()
    {
        var reference = LabAnnotationParser.Parse("0.0 10.0 C:maj");
        var estimates = new List<TimedChord> { Estimate(0f, 10f, "C") };

        WcsrEvaluator.Evaluate(reference, estimates, ChordComparisonLevel.MajMin).Score.Should().Be(1f);
        WcsrEvaluator.Evaluate(reference, estimates, ChordComparisonLevel.Sevenths).Score.Should().Be(1f);
    }

    /// <summary>
    /// Enharmonically equivalent spellings (Db versus C#) must match, because comparison
    /// happens on pitch classes rather than note names.
    /// </summary>
    [Fact]
    public void Evaluate_EnharmonicRoots_Match()
    {
        var reference = LabAnnotationParser.Parse("0 10 Db:maj");
        var estimates = new List<TimedChord> { Estimate(0f, 10f, "C#") };

        WcsrEvaluator.Evaluate(reference, estimates, ChordComparisonLevel.MajMin).Score.Should().Be(1f);
    }

    /// <summary>
    /// A maj7 reference against a plain major estimate must match at the MajMin level
    /// (both reduce to major) but miss at the Sevenths level.
    /// </summary>
    [Fact]
    public void Evaluate_SeventhFolding_DependsOnLevel()
    {
        var reference = LabAnnotationParser.Parse("0 10 C:maj7");
        var estimates = new List<TimedChord> { Estimate(0f, 10f, "C") };

        WcsrEvaluator.Evaluate(reference, estimates, ChordComparisonLevel.MajMin).Score.Should().Be(1f);
        WcsrEvaluator.Evaluate(reference, estimates, ChordComparisonLevel.Sevenths).Score.Should().Be(0f);
    }

    /// <summary>
    /// Reference segments marked X (unknown) must be excluded from the scored duration
    /// instead of counting as misses.
    /// </summary>
    [Fact]
    public void Evaluate_UnknownReference_IsExcluded()
    {
        var reference = LabAnnotationParser.Parse("0 5 C:maj\n5 10 X");
        var estimates = new List<TimedChord> { Estimate(0f, 10f, "C") };

        var result = WcsrEvaluator.Evaluate(reference, estimates, ChordComparisonLevel.MajMin);

        result.ScoredDuration.Should().BeApproximately(5f, 0.001f);
        result.Score.Should().Be(1f);
    }

    /// <summary>
    /// An estimate covering only half the annotated time correctly must score 0.5.
    /// </summary>
    [Fact]
    public void Evaluate_HalfCorrect_ScoresHalf()
    {
        var reference = LabAnnotationParser.Parse("0 5 C:maj\n5 10 G:maj");
        var estimates = new List<TimedChord> { Estimate(0f, 10f, "C") };

        var result = WcsrEvaluator.Evaluate(reference, estimates, ChordComparisonLevel.MajMin);

        result.Score.Should().BeApproximately(0.5f, 0.001f);
    }

    /// <summary>
    /// Gaps in the estimate count as no-chord, so they must match reference N segments.
    /// </summary>
    [Fact]
    public void Evaluate_EstimateGap_MatchesNoChordReference()
    {
        var reference = LabAnnotationParser.Parse("0 5 C:maj\n5 10 N");
        var estimates = new List<TimedChord> { Estimate(0f, 5f, "C") };

        var result = WcsrEvaluator.Evaluate(reference, estimates, ChordComparisonLevel.MajMin);

        result.Score.Should().Be(1f);
    }

    /// <summary>
    /// The .lab parser must handle comments, blank lines and tab separators.
    /// </summary>
    [Fact]
    public void LabParser_HandlesCommentsAndWhitespace()
    {
        var segments = LabAnnotationParser.Parse("# header comment\n\n0.0\t2.5\tA:min\n2.5 5.0 E:7\n");

        segments.Should().HaveCount(2);
        segments[0].Symbol.RootPitchClass.Should().Be(9);
        segments[0].Symbol.Quality.Should().Be(ChordQuality.Minor);
        segments[1].StartTime.Should().Be(2.5f);
        segments[1].Symbol.Quality.Should().Be(ChordQuality.Dominant7);
    }

    /// <summary>
    /// End-to-end harness check on synthetic material: a clean C-F-G-C progression analyzed
    /// by <see cref="SongChordAnalyzer"/> must reach a high WCSR against its annotation.
    /// </summary>
    [Fact]
    public void Evaluate_SyntheticProgression_ScoresHigh()
    {
        var notes = new List<Note>();
        int[][] triads = { new[] { 60, 64, 67 }, new[] { 65, 69, 72 }, new[] { 67, 71, 74 }, new[] { 60, 64, 67 } };
        for (int i = 0; i < triads.Length; i++)
        {
            foreach (int pitch in triads[i])
                notes.Add(new Note(i * 2f, i * 2f + 2f, pitch, 0.8f, null));
        }

        var analyzer = new SongChordAnalyzer(windowSize: 1.0f, hopSize: 0.5f);
        var chords = analyzer.AnalyzeSong(notes);

        var reference = LabAnnotationParser.Parse("0 2 C:maj\n2 4 F:maj\n4 6 G:maj\n6 8 C:maj");
        var result = WcsrEvaluator.Evaluate(reference, chords, ChordComparisonLevel.MajMin);

        result.ScoredDuration.Should().BeApproximately(8f, 0.001f);
        result.Score.Should().BeGreaterThanOrEqualTo(0.8f);
    }
}
