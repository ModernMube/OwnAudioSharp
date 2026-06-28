using System.Reflection;
using System.Runtime.CompilerServices;
using OwnaudioNET;
using PublicApiGenerator;

namespace Ownaudio.OwnaudioNET.Tests.PublicApi;

/// <summary>
/// A1 clone guard (Rust refactor, phase 3, item 3.6 / 0.2): continuously diffs the in-progress
/// <c>OwnaudioNET.RustNext</c> clone against the frozen <c>OwnaudioNET</c> public API baseline.
/// </summary>
/// <remarks>
/// <para>
/// The clone is built incrementally (Sources, Mixing, Effects, ... migrate one namespace at a time),
/// so this guard does not require the clone to be <i>complete</i>. Instead it enforces
/// <b>fidelity</b>: every public/protected surface line the clone <i>does</i> expose must match the
/// frozen contract exactly, after normalizing the temporary <c>OwnaudioNET.RustNext</c> namespace
/// prefix back to the root <c>OwnaudioNET</c>.
/// </para>
/// <para>
/// This catches any signature drift in already-cloned types while phase 3 is still under way.
/// Full structural equality (every baseline namespace present in the clone) is the separate
/// cut-over acceptance gate (3.7 / 6.1), not the responsibility of this test.
/// </para>
/// </remarks>
public sealed class RustNextPublicApiCloneTests
{
    /// <summary>
    /// The temporary clone namespace prefix that is normalized back to the root namespace before
    /// the surface is compared against the frozen baseline.
    /// </summary>
    private const string CloneNamespacePrefix = "OwnaudioNET.RustNext";

    /// <summary>
    /// The root namespace the clone prefix is normalized to.
    /// </summary>
    private const string RootNamespace = "OwnaudioNET";

    /// <summary>
    /// Options used to extract only the <c>OwnaudioNET.RustNext</c> clone surface from the assembly.
    /// </summary>
    private static readonly ApiGeneratorOptions CloneGeneratorOptions = new()
    {
        IncludeAssemblyAttributes = false,
        AllowNamespacePrefixes = new[] { CloneNamespacePrefix },
    };

    /// <summary>
    /// Verifies that every public surface line exposed by the <c>OwnaudioNET.RustNext</c> clone is,
    /// after namespace normalization, byte-for-byte present in the frozen A1 baseline. Any line that
    /// is not found in the baseline indicates the clone has drifted from the frozen contract.
    /// </summary>
    [Fact]
    public void RustNextClone_PublicApi_IsFaithfulToFrozenBaseline()
    {
        Assembly assembly = typeof(OwnaudioNet).Assembly;
        string cloneSurface = NormalizeCloneNamespace(assembly.GeneratePublicApi(CloneGeneratorOptions));

        string baseline = File.ReadAllText(GetApprovedFilePath());

        HashSet<string> baselineLines = new(MeaningfulLines(baseline), StringComparer.Ordinal);
        string[] divergent = MeaningfulLines(cloneSurface)
            .Where(line => !baselineLines.Contains(line))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            divergent.Length == 0,
            "The OwnaudioNET.RustNext clone diverges from the frozen A1 baseline.\n" +
            "These normalized clone surface lines are not present in the baseline:\n" +
            string.Join("\n", divergent.Select(l => "  + " + l)) +
            "\n(If the change is intentional the frozen baseline must be re-approved first.)");
    }

    /// <summary>
    /// Sanity check that the clone actually exposes a meaningful surface, so the fidelity assertion
    /// above cannot pass vacuously if the clone fails to build or is empty.
    /// </summary>
    [Fact]
    public void RustNextClone_ExposesClonedNamespaces()
    {
        Assembly assembly = typeof(OwnaudioNet).Assembly;
        string cloneSurface = NormalizeCloneNamespace(assembly.GeneratePublicApi(CloneGeneratorOptions));

        string[] clonedNamespaces = cloneSurface
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => line.StartsWith("namespace ", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains("namespace OwnaudioNET", clonedNamespaces);
        Assert.Contains("namespace OwnaudioNET.Sources", clonedNamespaces);
        Assert.Contains("namespace OwnaudioNET.Mixing", clonedNamespaces);
        Assert.Contains("namespace OwnaudioNET.Effects", clonedNamespaces);
    }

    /// <summary>
    /// Replaces the temporary clone namespace prefix with the root namespace everywhere it appears
    /// (namespace declarations and fully-qualified type references) so the clone surface can be
    /// compared directly against the root-namespace baseline.
    /// </summary>
    /// <param name="cloneSurface">The generated clone API text.</param>
    private static string NormalizeCloneNamespace(string cloneSurface)
        => cloneSurface
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace(CloneNamespacePrefix, RootNamespace);

    /// <summary>
    /// Extracts the meaningful surface lines (namespace, type and member declarations) from an API
    /// text, ignoring structural braces and blank lines so the comparison is robust against
    /// formatting and ordering noise.
    /// </summary>
    /// <param name="apiText">The API text to filter.</param>
    private static IEnumerable<string> MeaningfulLines(string apiText)
        => apiText
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line =>
                line.StartsWith("namespace ", StringComparison.Ordinal) ||
                line.StartsWith("public ", StringComparison.Ordinal) ||
                line.StartsWith("protected ", StringComparison.Ordinal) ||
                line.StartsWith("[", StringComparison.Ordinal));

    /// <summary>
    /// Returns the source-tree path of the shared frozen baseline file based on the compiler-embedded
    /// caller path, so the test can locate it independently of the working directory.
    /// </summary>
    /// <param name="callerFilePath">The compilation path of the calling source file (automatic).</param>
    private static string GetApprovedFilePath([CallerFilePath] string callerFilePath = "")
    {
        string directory = Path.GetDirectoryName(callerFilePath)!;
        return Path.Combine(directory, "OwnaudioNET.PublicApi.approved.txt");
    }
}
