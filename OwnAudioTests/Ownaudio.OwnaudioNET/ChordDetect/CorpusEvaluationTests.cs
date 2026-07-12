using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FluentAssertions;
using OwnaudioNET.Features.OwnChordDetect.Evaluation;
using Xunit;
using Xunit.Abstractions;
using ChordDetectApi = OwnaudioNET.Features.OwnChordDetect.ChordDetect;

namespace Ownaudio.OwnaudioNET.Tests.ChordDetect;

/// <summary>
/// Corpus evaluation harness: runs the full audio-to-chords pipeline on an annotated corpus
/// and reports per-song and aggregate WCSR at the MajMin and Sevenths vocabulary levels.
/// <para>
/// The harness activates only when the <c>OWNAUDIO_CHORDEVAL_DIR</c> environment variable
/// points to a directory of audio files (.wav/.mp3/.flac) with same-named .lab annotations
/// in Harte syntax (e.g. Isophonics/Beatles or Billboard ground truth). Run it with:
/// <c>OWNAUDIO_CHORDEVAL_DIR=/path/to/corpus dotnet test --filter CorpusEvaluation</c>.
/// A <c>wcsr-report.txt</c> summary is written next to the corpus.
/// </para>
/// </summary>
public sealed class CorpusEvaluationTests
{
    /// <summary>
    /// The environment variable naming the corpus directory.
    /// </summary>
    private const string CorpusVariable = "OWNAUDIO_CHORDEVAL_DIR";

    /// <summary>
    /// Audio file extensions the harness pairs with .lab annotations.
    /// </summary>
    private static readonly string[] AudioExtensions = { ".wav", ".mp3", ".flac" };

    /// <summary>
    /// xunit output sink for the per-song report.
    /// </summary>
    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Captures the xunit output sink.
    /// </summary>
    /// <param name="output">The xunit output sink.</param>
    public CorpusEvaluationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Evaluates every annotated song of the configured corpus and reports WCSR scores.
    /// Without the environment variable the test is a no-op, so CI stays unaffected.
    /// </summary>
    [Fact]
    public void EvaluateCorpus_WhenConfigured()
    {
        string? corpusDirectory = Environment.GetEnvironmentVariable(CorpusVariable);
        if (string.IsNullOrEmpty(corpusDirectory) || !Directory.Exists(corpusDirectory))
        {
            _output.WriteLine($"{CorpusVariable} not set or missing; corpus evaluation skipped.");
            return;
        }

        var report = new StringBuilder();
        report.AppendLine($"WCSR corpus evaluation — {DateTime.Now:yyyy-MM-dd HH:mm}");
        report.AppendLine($"Corpus: {corpusDirectory}");
        report.AppendLine();
        report.AppendLine($"{"Song",-40} {"MajMin",8} {"Sevenths",9} {"Scored s",9}");

        float totalMatchedMajMin = 0f, totalMatchedSevenths = 0f;
        float totalScoredMajMin = 0f, totalScoredSevenths = 0f;
        int evaluatedCount = 0;

        foreach (var audioPath in EnumerateAnnotatedAudio(corpusDirectory))
        {
            string labPath = Path.ChangeExtension(audioPath, ".lab");
            var reference = LabAnnotationParser.ParseFile(labPath);

            var (chords, _, _) = ChordDetectApi.DetectFromFile(audioPath);

            var majMin = WcsrEvaluator.Evaluate(reference, chords, ChordComparisonLevel.MajMin);
            var sevenths = WcsrEvaluator.Evaluate(reference, chords, ChordComparisonLevel.Sevenths);

            totalMatchedMajMin += majMin.MatchedDuration;
            totalScoredMajMin += majMin.ScoredDuration;
            totalMatchedSevenths += sevenths.MatchedDuration;
            totalScoredSevenths += sevenths.ScoredDuration;
            evaluatedCount++;

            report.AppendLine(
                $"{Path.GetFileNameWithoutExtension(audioPath),-40} " +
                $"{majMin.Score,8:P1} {sevenths.Score,9:P1} {majMin.ScoredDuration,9:F1}");
        }

        if (evaluatedCount == 0)
        {
            _output.WriteLine("No audio+.lab pairs found in the corpus directory.");
            return;
        }

        float aggregateMajMin = totalScoredMajMin > 0f ? totalMatchedMajMin / totalScoredMajMin : 0f;
        float aggregateSevenths = totalScoredSevenths > 0f ? totalMatchedSevenths / totalScoredSevenths : 0f;

        report.AppendLine();
        report.AppendLine($"Songs evaluated: {evaluatedCount}");
        report.AppendLine($"Aggregate WCSR (MajMin):   {aggregateMajMin:P1}");
        report.AppendLine($"Aggregate WCSR (Sevenths): {aggregateSevenths:P1}");

        string reportText = report.ToString();
        _output.WriteLine(reportText);
        File.WriteAllText(Path.Combine(corpusDirectory, "wcsr-report.txt"), reportText);

        totalScoredMajMin.Should().BeGreaterThan(0f);
    }

    /// <summary>
    /// Enumerates the audio files of the corpus that have a matching .lab annotation.
    /// </summary>
    /// <param name="corpusDirectory">The corpus directory.</param>
    /// <returns>The paths of annotated audio files in stable order.</returns>
    private static IEnumerable<string> EnumerateAnnotatedAudio(string corpusDirectory)
    {
        var paths = new List<string>();

        foreach (var path in Directory.EnumerateFiles(corpusDirectory))
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(AudioExtensions, extension) >= 0 &&
                File.Exists(Path.ChangeExtension(path, ".lab")))
            {
                paths.Add(path);
            }
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return paths;
    }
}
