//! C ABI error codes returned by every MIDI FFI function.

use ownaudio_midi_core::MidiError;

/// C-compatible error codes returned by every MIDI FFI function.
///
/// Zero always means success. All other values describe the failure category so
/// the C# layer can map them to dedicated exception types.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MidiErrorCode {
    /// Operation succeeded.
    Success = 0,
    /// No MIDI port matched the requested name.
    PortNotFound = 1,
    /// The backend failed to connect to or open the port.
    ConnectionFailed = 2,
    /// An operation required an open port but the port was not open.
    PortNotOpen = 3,
    /// The supplied bytes are not a valid Standard MIDI File.
    InvalidFile = 4,
    /// A required pointer argument was null.
    NullPointer = 5,
    /// The supplied handle does not point to a valid object.
    InvalidHandle = 6,
    /// The requested capability is not supported on this platform.
    PlatformUnsupported = 7,
    /// A panic was caught at the FFI boundary.
    InternalPanic = 8,
    /// An I/O or other internal error occurred.
    IoError = 9,
}

impl From<MidiError> for MidiErrorCode {
    fn from(err: MidiError) -> Self {
        match err {
            MidiError::PortNotFound(_) => Self::PortNotFound,
            MidiError::ConnectionFailed(_) => Self::ConnectionFailed,
            MidiError::PortNotOpen => Self::PortNotOpen,
            MidiError::InvalidFile(_) => Self::InvalidFile,
            MidiError::PlatformUnsupported(_) => Self::PlatformUnsupported,
            MidiError::Internal(_) => Self::IoError,
        }
    }
}
