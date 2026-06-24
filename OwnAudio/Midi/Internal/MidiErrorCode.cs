namespace OwnAudio.Midi.Internal;

/// <summary>
/// Managed mirror of the native <c>MidiErrorCode</c> enum returned by every FFI
/// function. The discriminant values must stay in sync with the Rust definition.
/// </summary>
internal enum MidiErrorCode
{
    /// <summary>Operation succeeded.</summary>
    Success = 0,

    /// <summary>No MIDI port matched the requested name.</summary>
    PortNotFound = 1,

    /// <summary>The backend failed to connect to or open the port.</summary>
    ConnectionFailed = 2,

    /// <summary>An operation required an open port but the port was not open.</summary>
    PortNotOpen = 3,

    /// <summary>The supplied bytes are not a valid Standard MIDI File.</summary>
    InvalidFile = 4,

    /// <summary>A required pointer argument was null.</summary>
    NullPointer = 5,

    /// <summary>The supplied handle does not point to a valid object.</summary>
    InvalidHandle = 6,

    /// <summary>The requested capability is not supported on this platform.</summary>
    PlatformUnsupported = 7,

    /// <summary>A panic was caught at the FFI boundary.</summary>
    InternalPanic = 8,

    /// <summary>An I/O or other internal error occurred.</summary>
    IoError = 9,
}
