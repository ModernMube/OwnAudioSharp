//! Error types for the MIDI core crate.

/// Errors produced by MIDI port operations, file parsing and serialization.
#[derive(Debug, thiserror::Error)]
pub enum MidiError {
    /// No MIDI port matched the requested name.
    #[error("MIDI port not found: {0}")]
    PortNotFound(String),

    /// The underlying backend failed to connect to or open the port.
    #[error("Failed to connect to MIDI port: {0}")]
    ConnectionFailed(String),

    /// An operation required an open port but the port was not open.
    #[error("MIDI port is not open")]
    PortNotOpen,

    /// The byte stream did not contain a valid Standard MIDI File.
    #[error("Invalid MIDI file: {0}")]
    InvalidFile(String),

    /// The current platform does not support the requested capability
    /// (for example virtual ports under WinMM).
    #[error("Operation not supported on this platform: {0}")]
    PlatformUnsupported(String),

    /// An internal error not covered by the more specific variants.
    #[error("Internal error: {0}")]
    Internal(String),
}
