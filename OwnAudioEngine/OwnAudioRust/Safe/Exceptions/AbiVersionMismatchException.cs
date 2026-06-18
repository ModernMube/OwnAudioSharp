namespace Ownaudio.Safe.Exceptions;

/// <summary>
/// Thrown when the ABI version reported by the native binary does not match the
/// version the managed layer was compiled against.
/// </summary>
/// <remarks>
/// <para>
/// This exception is raised by <see cref="AudioEngine.Create()"/> immediately after
/// the native library is loaded.  It indicates that the <c>ownaudio_ffi</c> binary
/// on disk is from a different build than the managed wrapper — for example because
/// someone manually copied a native file without updating the NuGet package, or
/// because a deployment step was skipped.
/// </para>
/// <para>
/// To resolve: reinstall the <c>OwnAudioSharp.Basic</c> (or Full/Mobile) NuGet
/// package so that the native binary and the managed wrapper come from the same
/// package version.
/// </para>
/// </remarks>
public sealed class AbiVersionMismatchException : OwnAudioException
{
    #region Properties

    /// <summary>
    /// The ABI version reported by the native binary that was loaded.
    /// </summary>
    public uint NativeBinaryAbiVersion { get; }

    /// <summary>
    /// The ABI version the managed layer was compiled to expect.
    /// </summary>
    public uint ExpectedAbiVersion { get; }

    #endregion

    #region Construction

    /// <summary>
    /// Initializes a new instance of <see cref="AbiVersionMismatchException"/>.
    /// </summary>
    /// <param name="nativeVersion">ABI version returned by the native binary.</param>
    /// <param name="expectedVersion">ABI version expected by this managed assembly.</param>
    public AbiVersionMismatchException(uint nativeVersion, uint expectedVersion)
        : base(
            AudioEngineErrorCode.AbiVersionMismatch,
            $"Native binary ABI version mismatch: expected {expectedVersion}, got {nativeVersion}. "
            + "Reinstall the OwnAudioSharp NuGet package to obtain a matching native binary.")
    {
        NativeBinaryAbiVersion = nativeVersion;
        ExpectedAbiVersion = expectedVersion;
    }

    #endregion
}
