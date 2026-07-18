namespace OwnAudio.Midi.Internal;

/// <summary>
/// Mirror of the Rust MidiErrorCode enum. Values must match the native side.
/// </summary>
internal enum MidiErrorCode
{
    /// <summary>All good.</summary>
    Success = 0,

    /// <summary>No port with that name.</summary>
    PortNotFound = 1,

    /// <summary>Backend couldn't connect / open it.</summary>
    ConnectionFailed = 2,

    /// <summary>Port wasn't open for the op.</summary>
    PortNotOpen = 3,

    /// <summary>Bytes aren't a valid SMF.</summary>
    InvalidFile = 4,

    /// <summary>Null pointer arg.</summary>
    NullPointer = 5,

    /// <summary>Handle doesn't point anywhere useful.</summary>
    InvalidHandle = 6,

    /// <summary>Not supported on this OS.</summary>
    PlatformUnsupported = 7,

    /// <summary>Panic caught at the FFI boundary.</summary>
    InternalPanic = 8,

    /// <summary>I/O or other internal blow-up.</summary>
    IoError = 9,
}
