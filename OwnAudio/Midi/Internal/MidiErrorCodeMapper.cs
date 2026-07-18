namespace OwnAudio.Midi.Internal;

/// <summary>
/// Turns native error codes into the exceptions the public API has always thrown.
/// </summary>
internal static class MidiErrorCodeMapper
{
    /// <summary>
    /// Throws unless the code says Success. The operation name goes into the message.
    /// </summary>
    public static void ThrowIfError(int code, string operation)
    {
        var err = (MidiErrorCode)code;
        if (err == MidiErrorCode.Success) return;

        string message = $"{operation} failed: {err}";
        throw err switch
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
