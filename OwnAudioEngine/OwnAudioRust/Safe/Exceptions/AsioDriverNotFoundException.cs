namespace Ownaudio.Safe.Exceptions;

/// <summary>
/// Thrown when ASIO support is compiled into the native binary but no ASIO driver is
/// installed on this machine.
/// </summary>
/// <remarks>
/// <para>
/// This exception is raised when <see cref="AudioEngine.Create(Ownaudio.Audio.HostApi?)"/> is called
/// with <see cref="Ownaudio.Audio.HostApi.Asio"/> and the native binary was built with
/// the <c>asio</c> Cargo feature, but no ASIO driver (e.g. ASIO4ALL, an RME or
/// Focusrite driver) is installed on the current system.
/// </para>
/// <para>
/// To resolve: install a compatible ASIO driver, or use the platform default host API
/// by calling <see cref="AudioEngine.Create()"/> without an explicit host API.
/// </para>
/// </remarks>
public sealed class AsioDriverNotFoundException : OwnAudioException
{
    #region Construction

    /// <summary>
    /// Initializes a new instance of <see cref="AsioDriverNotFoundException"/>.
    /// </summary>
    /// <param name="message">Human-readable description of the failure.</param>
    public AsioDriverNotFoundException(string message)
        : base(AudioEngineErrorCode.AsioDriverNotFound, message)
    {
    }

    #endregion
}
