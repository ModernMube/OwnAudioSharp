//! Android stub for MIDI port I/O.
//!
//! The [`crate::port`] module on desktop platforms is built on the `midir`
//! crate, which has no Android backend (Android exposes MIDI through the
//! platform `AMidi` / `MidiManager` APIs instead). Rather than drop the whole
//! crate on Android, this stub mirrors the public port API so that the file and
//! clock functionality — which is pure Rust and platform independent — remains
//! fully usable. Every port operation reports
//! [`MidiError::PlatformUnsupported`], and enumeration returns an empty list.

use crate::error::MidiError;

/// Matches the desktop callback signature so the FFI layer compiles unchanged.
pub type InputCallback = dyn Fn(u64, &[u8]) + Send + 'static;

/// Human-readable description of the unsupported capability.
const UNSUPPORTED: &str = "MIDI port I/O on Android";

/// Android stub for a MIDI input port. Never constructed successfully.
pub struct MidiInputPort;

impl MidiInputPort {
    /// Always fails with [`MidiError::PlatformUnsupported`] on Android.
    pub fn open(_port_name: &str) -> Result<Self, MidiError> {
        Err(MidiError::PlatformUnsupported(UNSUPPORTED.to_string()))
    }

    /// Always fails with [`MidiError::PlatformUnsupported`] on Android.
    pub fn create_virtual(_name: &str) -> Result<Self, MidiError> {
        Err(MidiError::PlatformUnsupported(UNSUPPORTED.to_string()))
    }

    /// Returns an empty name; the port can never be opened on Android.
    pub fn name(&self) -> &str {
        ""
    }

    /// Always false; the port can never be started on Android.
    pub fn is_started(&self) -> bool {
        false
    }

    /// Always fails with [`MidiError::PlatformUnsupported`] on Android.
    pub fn start(&mut self, _callback: Box<InputCallback>) -> Result<(), MidiError> {
        Err(MidiError::PlatformUnsupported(UNSUPPORTED.to_string()))
    }

    /// No-op; there is never an active connection on Android.
    pub fn stop(&mut self) {}
}

/// Android stub for a MIDI output port. Never constructed successfully.
pub struct MidiOutputPort;

impl MidiOutputPort {
    /// Always fails with [`MidiError::PlatformUnsupported`] on Android.
    pub fn open(_port_name: &str) -> Result<Self, MidiError> {
        Err(MidiError::PlatformUnsupported(UNSUPPORTED.to_string()))
    }

    /// Always fails with [`MidiError::PlatformUnsupported`] on Android.
    pub fn create_virtual(_name: &str) -> Result<Self, MidiError> {
        Err(MidiError::PlatformUnsupported(UNSUPPORTED.to_string()))
    }

    /// Returns an empty name; the port can never be opened on Android.
    pub fn name(&self) -> &str {
        ""
    }

    /// Always fails with [`MidiError::PlatformUnsupported`] on Android.
    pub fn send(&mut self, _status: u8, _data1: u8, _data2: u8) -> Result<(), MidiError> {
        Err(MidiError::PlatformUnsupported(UNSUPPORTED.to_string()))
    }

    /// Always fails with [`MidiError::PlatformUnsupported`] on Android.
    pub fn send_raw(&mut self, _data: &[u8]) -> Result<(), MidiError> {
        Err(MidiError::PlatformUnsupported(UNSUPPORTED.to_string()))
    }
}

/// Returns an empty list; port enumeration is unavailable on Android.
pub fn list_input_port_names() -> Vec<String> {
    Vec::new()
}

/// Returns an empty list; port enumeration is unavailable on Android.
pub fn list_output_port_names() -> Vec<String> {
    Vec::new()
}
