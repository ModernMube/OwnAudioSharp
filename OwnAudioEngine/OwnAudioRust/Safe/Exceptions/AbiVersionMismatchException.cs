namespace Ownaudio.Safe.Exceptions;

/// <summary>
/// Native binary ABI doesn't match what the managed side was built against.
/// Usually a hand-copied .dll/.so, or a half-finished deploy. Fix = reinstall the NuGet package.
/// </summary>
public sealed class AbiVersionMismatchException : OwnAudioException
{
    /// <summary>
    /// What the loaded native binary reported.
    /// </summary>
    public uint NativeBinaryAbiVersion { get; }

    /// <summary>
    /// What we expected it to report.
    /// </summary>
    public uint ExpectedAbiVersion { get; }

    /// <param name="nativeVersion"></param>
    /// <param name="expectedVersion"></param>
    public AbiVersionMismatchException(uint nativeVersion, uint expectedVersion)
        : base(
            AudioEngineErrorCode.AbiVersionMismatch,
            $"Native binary ABI version mismatch: expected {expectedVersion}, got {nativeVersion}. "
            + "Reinstall the OwnAudioSharp NuGet package to obtain a matching native binary.")
    {
        NativeBinaryAbiVersion = nativeVersion;
        ExpectedAbiVersion = expectedVersion;
    }
}
