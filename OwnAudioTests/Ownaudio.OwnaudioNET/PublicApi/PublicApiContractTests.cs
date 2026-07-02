using System.Reflection;
using System.Runtime.CompilerServices;
using OwnaudioNET;
using PublicApiGenerator;

namespace Ownaudio.OwnaudioNET.Tests.PublicApi;

/// <summary>
/// A1 safety net (Rust refactor, phase 0): freezes the current <c>OwnaudioNET</c>
/// public API surface as a machine-readable reference contract.
/// </summary>
/// <remarks>
/// The test uses <see cref="ApiGenerator"/> to produce a deterministic
/// textual representation of the entire public/protected surface of the <c>OwnaudioNET</c>
/// assembly, then compares it against the version-controlled
/// <c>OwnaudioNET.PublicApi.approved.txt</c> baseline. After the phase-6 cut-over this same
/// baseline proved the Rust-backed surface is byte-for-byte identical to the pre-refactor API.
/// To intentionally update the baseline, delete the <c>.approved.txt</c> file and re-run the
/// test (it regenerates), or copy the emitted <c>.received.txt</c> content over it.
/// </remarks>
public sealed class PublicApiContractTests
{
    /// <summary>
    /// Options used to produce the <c>OwnaudioNET</c> public API representation.
    /// Assembly-level attributes (version, build metadata) are intentionally excluded so the
    /// contract only captures the actual type and member surface.
    /// </summary>
    private static readonly ApiGeneratorOptions GeneratorOptions = new()
    {
        IncludeAssemblyAttributes = false,
    };

    /// <summary>
    /// Verifies that the current public surface of the <c>OwnaudioNET</c> assembly is
    /// byte-for-byte identical to the frozen baseline. Any unintended change to the surface
    /// (added, removed or modified public member) fails the test.
    /// </summary>
    [Fact]
    public void OwnaudioNET_PublicApi_MatchesFrozenBaseline()
    {
        Assembly assembly = typeof(OwnaudioNet).Assembly;
        string actual = Normalize(assembly.GeneratePublicApi(GeneratorOptions));

        string approvedPath = GetApprovedFilePath();
        string receivedPath = Path.ChangeExtension(approvedPath, ".received.txt");

        if (!File.Exists(approvedPath))
        {
            File.WriteAllText(approvedPath, actual);
            return;
        }

        string approved = Normalize(File.ReadAllText(approvedPath));

        if (!string.Equals(approved, actual, StringComparison.Ordinal))
        {
            File.WriteAllText(receivedPath, actual);

            Assert.Fail(
                "The OwnaudioNET public API surface differs from the frozen baseline (A1).\n" +
                $"Baseline : {approvedPath}\n" +
                $"Actual   : {receivedPath}\n" +
                BuildDiffSummary(approved, actual) +
                "\nIf the change is intentional, update the baseline (.approved.txt <- .received.txt content).");
        }

        if (File.Exists(receivedPath))
        {
            File.Delete(receivedPath);
        }
    }

    /// <summary>
    /// Returns the source-tree-relative path of the baseline file based on the compiler-embedded
    /// caller path, so the test can locate it independently of the working directory.
    /// </summary>
    /// <param name="callerFilePath">The compilation path of the calling source file (automatic).</param>
    private static string GetApprovedFilePath([CallerFilePath] string callerFilePath = "")
    {
        string directory = Path.GetDirectoryName(callerFilePath)!;
        return Path.Combine(directory, "OwnaudioNET.PublicApi.approved.txt");
    }

    /// <summary>
    /// Normalizes line endings and trims trailing blank lines so that cross-platform
    /// (CRLF/LF) differences do not produce a false diff in the baseline comparison.
    /// </summary>
    /// <param name="text">The API text to normalize.</param>
    private static string Normalize(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');

    /// <summary>
    /// Builds a short, line-based summary of the first few differences between the baseline and
    /// the current surface so the failure message is diagnostic on its own.
    /// </summary>
    /// <param name="approved">The frozen baseline text.</param>
    /// <param name="actual">The current, generated surface text.</param>
    private static string BuildDiffSummary(string approved, string actual)
    {
        HashSet<string> approvedLines = new(approved.Split('\n'), StringComparer.Ordinal);
        HashSet<string> actualLines = new(actual.Split('\n'), StringComparer.Ordinal);

        IEnumerable<string> removed = approvedLines
            .Where(line => !actualLines.Contains(line))
            .Select(line => "  - " + line.Trim());
        IEnumerable<string> added = actualLines
            .Where(line => !approvedLines.Contains(line))
            .Select(line => "  + " + line.Trim());

        const int MaxLines = 25;
        string[] diff = removed.Concat(added).Where(l => l.Length > 4).Take(MaxLines).ToArray();

        return diff.Length == 0
            ? "(ordering/whitespace difference only)"
            : "Differences (- baseline / + actual):\n" + string.Join("\n", diff);
    }
}
