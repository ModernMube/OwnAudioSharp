using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Analysis;
using OwnaudioNET.Features.OwnChordDetect.Core;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.ChordDetect;

/// <summary>
/// Tests for modulation-aware key tracking: the sliding-window Viterbi key timeline in
/// <see cref="KeyDetector"/> and its integration into <see cref="SongChordAnalyzer"/>,
/// where chord naming follows the key active at each analysis window.
/// </summary>
public sealed class KeyModulationTests
{
    /// <summary>
    /// Builds a triad of two-second notes starting at the given time.
    /// </summary>
    /// <param name="notes">The note list to append to.</param>
    /// <param name="start">The chord start time in seconds.</param>
    /// <param name="pitches">The MIDI pitches of the chord tones.</param>
    private static void AddChord(List<Note> notes, float start, params int[] pitches)
    {
        foreach (int pitch in pitches)
            notes.Add(new Note(start, start + 2f, pitch, 0.8f, null));
    }

    /// <summary>
    /// Builds a 24-second song: twelve seconds of C major harmony (C, F, G) followed by
    /// twelve seconds of E-flat major harmony (Eb, Ab, Bb).
    /// </summary>
    /// <returns>The note list of the modulating song.</returns>
    private static List<Note> BuildModulatingSong()
    {
        var notes = new List<Note>();

        for (int repeat = 0; repeat < 2; repeat++)
        {
            float baseTime = repeat * 6f;
            AddChord(notes, baseTime + 0f, 60, 64, 67);
            AddChord(notes, baseTime + 2f, 65, 69, 72);
            AddChord(notes, baseTime + 4f, 67, 71, 74);
        }

        for (int repeat = 0; repeat < 2; repeat++)
        {
            float baseTime = 12f + repeat * 6f;
            AddChord(notes, baseTime + 0f, 63, 67, 70);
            AddChord(notes, baseTime + 2f, 68, 72, 75);
            AddChord(notes, baseTime + 4f, 70, 74, 77);
        }

        return notes;
    }

    /// <summary>
    /// The key timeline must report exactly one C major and one E-flat major segment,
    /// with the boundary near the actual modulation point, and no key flicker.
    /// </summary>
    [Fact]
    public void DetectKeyTimeline_Modulation_YieldsTwoStableSegments()
    {
        var detector = new KeyDetector();

        var timeline = detector.DetectKeyTimeline(BuildModulatingSong());

        timeline.Should().HaveCount(2);
        timeline[0].Key.KeyName.Should().Be("C");
        timeline[0].Key.IsMajor.Should().BeTrue();
        timeline[1].Key.KeyName.Should().Be("Eb");
        timeline[1].Key.IsMajor.Should().BeTrue();
        timeline[1].StartTime.Should().BeInRange(8f, 18f);
    }

    /// <summary>
    /// A song without modulation must yield a single timeline segment spanning the song,
    /// consistent with the previous global key detection.
    /// </summary>
    [Fact]
    public void DetectKeyTimeline_NoModulation_YieldsSingleSegment()
    {
        var notes = new List<Note>();
        for (int repeat = 0; repeat < 4; repeat++)
        {
            float baseTime = repeat * 6f;
            AddChord(notes, baseTime + 0f, 60, 64, 67);
            AddChord(notes, baseTime + 2f, 65, 69, 72);
            AddChord(notes, baseTime + 4f, 67, 71, 74);
        }

        var timeline = new KeyDetector().DetectKeyTimeline(notes);

        timeline.Should().HaveCount(1);
        timeline[0].Key.KeyName.Should().Be("C");
        timeline[0].StartTime.Should().Be(0f);
        timeline[0].EndTime.Should().Be(24f);
    }

    /// <summary>
    /// After the modulation the analyzer must name chords in the new key's spelling:
    /// flats in E-flat major (Eb, Ab, Bb) instead of the sharp names a single global
    /// C-oriented key would produce (D#, G#, A#).
    /// </summary>
    [Fact]
    public void AnalyzeSong_Modulation_NamesChordsInActiveKey()
    {
        var analyzer = new SongChordAnalyzer(windowSize: 1.0f, hopSize: 0.5f);

        var chords = analyzer.AnalyzeSong(BuildModulatingSong());

        analyzer.KeyTimeline.Should().NotBeNull();
        analyzer.KeyTimeline!.Count.Should().BeGreaterThanOrEqualTo(2);
        analyzer.KeyTimeline[0].Key.KeyName.Should().Be("C");
        analyzer.KeyTimeline[^1].Key.KeyName.Should().Be("Eb");

        var firstHalf = chords.Where(c => c.EndTime <= 10f).Select(c => c.ChordName).Distinct();
        firstHalf.Should().NotBeEmpty();
        firstHalf.Should().BeSubsetOf(new[] { "C", "F", "G" });

        var secondHalf = chords.Where(c => c.StartTime >= 14f).Select(c => c.ChordName).Distinct();
        secondHalf.Should().NotBeEmpty();
        secondHalf.Should().BeSubsetOf(new[] { "Eb", "Ab", "Bb" });
    }
}
