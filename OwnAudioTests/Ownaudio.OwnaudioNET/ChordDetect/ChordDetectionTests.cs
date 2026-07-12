using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Analysis;
using OwnaudioNET.Features.OwnChordDetect.Detectors;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.ChordDetect;

/// <summary>
/// Tests for the musically informed chord detection improvements: the bass-root prior
/// that resolves shared-subset ambiguities (C6 vs Am7), the Viterbi progression decoding
/// in <see cref="SongChordAnalyzer"/>, and the persistent real-time detector whose
/// stability score accumulates across calls.
/// </summary>
public sealed class ChordDetectionTests
{
    /// <summary>
    /// Builds a note with the given pitch and time span using a fixed amplitude.
    /// </summary>
    /// <param name="pitch">MIDI pitch number.</param>
    /// <param name="start">Start time in seconds.</param>
    /// <param name="end">End time in seconds.</param>
    /// <returns>The constructed note.</returns>
    private static Note MakeNote(int pitch, float start, float end)
    {
        return new Note(start, end, pitch, 0.8f, null);
    }

    /// <summary>
    /// The pitch-class set {A, C, E, G} is shared by Am7 and C6; with A in the bass the
    /// bass-root prior must resolve the ambiguity to Am7.
    /// </summary>
    [Fact]
    public void AnalyzeChord_SharedSubsetWithABass_ResolvesToAm7()
    {
        var detector = new ChordDetector();
        var notes = new List<Note>
        {
            MakeNote(45, 0f, 2f),
            MakeNote(60, 0f, 2f),
            MakeNote(64, 0f, 2f),
            MakeNote(67, 0f, 2f),
        };

        var analysis = detector.AnalyzeChord(notes);

        analysis.ChordName.Should().Be("Am7");
    }

    /// <summary>
    /// The same pitch-class set {C, E, G, A} with C in the bass must resolve to C6.
    /// </summary>
    [Fact]
    public void AnalyzeChord_SharedSubsetWithCBass_ResolvesToC6()
    {
        var detector = new ChordDetector();
        var notes = new List<Note>
        {
            MakeNote(48, 0f, 2f),
            MakeNote(64, 0f, 2f),
            MakeNote(67, 0f, 2f),
            MakeNote(69, 0f, 2f),
        };

        var analysis = detector.AnalyzeChord(notes);

        analysis.ChordName.Should().Be("C6");
    }

    /// <summary>
    /// A brief low passing tone must not qualify as the bass: only notes lasting a
    /// meaningful fraction of the longest note count as root evidence.
    /// </summary>
    [Fact]
    public void ComputeBassPitchClass_IgnoresShortLowPassingTone()
    {
        var notes = new List<Note>
        {
            MakeNote(28, 0f, 0.1f),
            MakeNote(48, 0f, 2f),
            MakeNote(64, 0f, 2f),
            MakeNote(67, 0f, 2f),
        };

        int bassPitchClass = ChordDetector.ComputeBassPitchClass(notes);

        bassPitchClass.Should().Be(0);
    }

    /// <summary>
    /// A clean C - F - G progression of two-second triads must decode to exactly that
    /// chord sequence after Viterbi smoothing and adjacent-window merging.
    /// </summary>
    [Fact]
    public void AnalyzeSong_CleanProgression_DecodesInOrder()
    {
        var notes = new List<Note>
        {
            MakeNote(60, 0f, 2f),
            MakeNote(64, 0f, 2f),
            MakeNote(67, 0f, 2f),
            MakeNote(65, 2f, 4f),
            MakeNote(69, 2f, 4f),
            MakeNote(72, 2f, 4f),
            MakeNote(67, 4f, 6f),
            MakeNote(71, 4f, 6f),
            MakeNote(74, 4f, 6f),
        };

        var analyzer = new SongChordAnalyzer(windowSize: 1.0f, hopSize: 0.5f);
        var chords = analyzer.AnalyzeSong(notes);

        chords.Select(c => c.ChordName).Should().Equal("C", "F", "G");
    }

    /// <summary>
    /// A sustained C major triad with a short burst of chromatic noise in the middle must
    /// stay a single uninterrupted C segment: the Viterbi decoder makes a one-window
    /// deviation more expensive than absorbing the noise.
    /// </summary>
    [Fact]
    public void AnalyzeSong_NoisyMiddleWindow_StaysStable()
    {
        var notes = new List<Note>
        {
            MakeNote(60, 0f, 8f),
            MakeNote(64, 0f, 8f),
            MakeNote(67, 0f, 8f),
            MakeNote(61, 3.0f, 3.3f),
            MakeNote(66, 3.0f, 3.3f),
            MakeNote(70, 3.0f, 3.3f),
        };

        var analyzer = new SongChordAnalyzer(windowSize: 1.0f, hopSize: 0.5f);
        var chords = analyzer.AnalyzeSong(notes);

        chords.Should().NotBeEmpty();
        chords.Should().OnlyContain(c => c.ChordName == "C");
    }

    /// <summary>
    /// The real-time entry point must keep its detector (and thus its stability buffer)
    /// alive across calls: after three confident C major frames and one silent frame the
    /// stability must reflect three matches out of four buffered frames, which is only
    /// possible when history survives between calls.
    /// </summary>
    [Fact]
    public void DetectRealtime_StabilityAccumulatesAcrossCalls()
    {
        var cMajor = new List<Note>
        {
            MakeNote(48, 0f, 1f),
            MakeNote(60, 0f, 1f),
            MakeNote(64, 0f, 1f),
            MakeNote(67, 0f, 1f),
        };
        var silence = new List<Note>();

        global::OwnaudioNET.Features.OwnChordDetect.ChordDetect.DetectRealtime(cMajor, buffersize: 4);
        global::OwnaudioNET.Features.OwnChordDetect.ChordDetect.DetectRealtime(cMajor, buffersize: 4);
        global::OwnaudioNET.Features.OwnChordDetect.ChordDetect.DetectRealtime(cMajor, buffersize: 4);
        var (chord, stability) = global::OwnaudioNET.Features.OwnChordDetect.ChordDetect.DetectRealtime(silence, buffersize: 4);

        chord.Should().Be("C");
        stability.Should().BeApproximately(0.75f, 0.01f);
    }
}
