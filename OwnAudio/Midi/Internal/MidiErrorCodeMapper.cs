namespace OwnAudio.Midi.Internal;

/// <summary>
/// Translates native <see cref="MidiErrorCode"/> values into dedicated managed
/// exceptions, keeping the public API's exception contract consistent with the
/// pre-refactor implementation.
/// </summary>
internal static class MidiErrorCodeMapper
{
    /// <summary>
    /// Throws the exception that corresponds to <paramref name="code"/> unless it
    /// indicates success.
    /// </summary>
    /// <param name="code">
    /// The native error code returned by an FFI call.
    /// </param>
    /// <param name="operation">
    /// The name of the operation, included in the exception message.
    /// </param>
    public static void ThrowIfError(int code, string operation)
    {
        var errorCode = (MidiErrorCode)code;
        if (errorCode == MidiErrorCode.Success)
        {
            return;
        }

        string message = $"{operation} failed: {errorCode}";
        throw errorCode switch
        {
            MidiErrorCode.PortNotFound => new ArgumentException(message),
            MidiErrorCode.PlatformUnsupported => new PlatformNotSupportedException(message),
            MidiErrorCode.PortNotOpen => new InvalidOperationException(message),
            MidiErrorCode.InvalidFile => new InvalidDataException(message),
            MidiErrorCode.NullPointer => new ArgumentNullException(operation, message),
            _ => new InvalidOperationException(message)
        };
    }
}
