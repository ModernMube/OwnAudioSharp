//! Cross-platform MIDI port handling built on the `midir` crate.
//!
//! `midir` provides a single abstraction over WinMM (Windows), CoreMIDI (macOS)
//! and the ALSA sequencer (Linux), including virtual ports on the Unix-like
//! platforms. This module wraps it with an open / start / stop / close lifecycle
//! that mirrors the managed `IMidiInputPort` and `IMidiOutputPort` contracts.

use midir::{MidiInput, MidiInputConnection, MidiOutput, MidiOutputConnection};

use crate::error::MidiError;

/// Client name advertised to the operating system for input connections.
const INPUT_CLIENT_NAME: &str = "OwnAudio.Midi.In";

/// Client name advertised to the operating system for output connections.
const OUTPUT_CLIENT_NAME: &str = "OwnAudio.Midi.Out";

/// Callback invoked for every complete incoming MIDI message.
///
/// The first argument is the timestamp in microseconds reported by the backend;
/// the second is the raw message bytes (already assembled into a complete
/// channel, system or System Exclusive message by `midir`).
pub type InputCallback = dyn Fn(u64, &[u8]) + Send + 'static;

/// Identifies how an input port obtains its data.
enum InputSource {
    /// A hardware or software port discovered by name through enumeration.
    Hardware(String),

    /// A virtual destination endpoint owned by this process (Unix only).
    Virtual(String),
}

/// A MIDI input port that delivers incoming messages through a callback.
pub struct MidiInputPort {
    name: String,
    source: InputSource,
    connection: Option<MidiInputConnection<()>>,
}

impl MidiInputPort {
    /// Opens a hardware input port identified by `port_name`.
    ///
    /// The connection itself is established lazily by [`MidiInputPort::start`];
    /// this constructor only verifies that a port with the requested name exists.
    pub fn open(port_name: &str) -> Result<Self, MidiError> {
        let client = MidiInput::new(INPUT_CLIENT_NAME)
            .map_err(|e| MidiError::ConnectionFailed(e.to_string()))?;

        let exists = client
            .ports()
            .iter()
            .any(|p| client.port_name(p).map(|n| n == port_name).unwrap_or(false));

        if !exists {
            return Err(MidiError::PortNotFound(port_name.to_string()));
        }

        Ok(Self {
            name: port_name.to_string(),
            source: InputSource::Hardware(port_name.to_string()),
            connection: None,
        })
    }

    /// Creates a virtual input port (destination endpoint) named `name`.
    ///
    /// The endpoint is created lazily by [`MidiInputPort::start`], because the
    /// `midir` virtual-port API requires the message callback at creation time.
    /// Returns [`MidiError::PlatformUnsupported`] on platforms without virtual
    /// port support.
    pub fn create_virtual(name: &str) -> Result<Self, MidiError> {
        if !cfg!(unix) {
            return Err(MidiError::PlatformUnsupported(
                "virtual MIDI input ports".to_string(),
            ));
        }

        Ok(Self {
            name: name.to_string(),
            source: InputSource::Virtual(name.to_string()),
            connection: None,
        })
    }

    /// Gets the display name of this port.
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns true while an active connection is delivering messages.
    pub fn is_started(&self) -> bool {
        self.connection.is_some()
    }

    /// Starts delivering incoming messages to `callback`.
    ///
    /// Calling this on an already-started port is a no-op.
    pub fn start(
        &mut self,
        callback: Box<InputCallback>,
    ) -> Result<(), MidiError> {
        if self.connection.is_some() {
            return Ok(());
        }

        let connection = match &self.source {
            InputSource::Hardware(name) => Self::connect_hardware(name, callback)?,
            InputSource::Virtual(name) => Self::connect_virtual(name, callback)?,
        };

        self.connection = Some(connection);
        Ok(())
    }

    /// Establishes a connection to a hardware port discovered by name.
    fn connect_hardware(
        name: &str,
        callback: Box<InputCallback>,
    ) -> Result<MidiInputConnection<()>, MidiError> {
        let mut client = MidiInput::new(INPUT_CLIENT_NAME)
            .map_err(|e| MidiError::ConnectionFailed(e.to_string()))?;
        client.ignore(midir::Ignore::None);

        let port = client
            .ports()
            .into_iter()
            .find(|p| client.port_name(p).map(|n| n == name).unwrap_or(false))
            .ok_or_else(|| MidiError::PortNotFound(name.to_string()))?;

        client
            .connect(
                &port,
                name,
                move |stamp, message, _| callback(stamp, message),
                (),
            )
            .map_err(|e| MidiError::ConnectionFailed(e.to_string()))
    }

    /// Creates and connects a virtual destination endpoint (Unix only).
    #[cfg(unix)]
    fn connect_virtual(
        name: &str,
        callback: Box<InputCallback>,
    ) -> Result<MidiInputConnection<()>, MidiError> {
        use midir::os::unix::VirtualInput;

        let mut client = MidiInput::new(INPUT_CLIENT_NAME)
            .map_err(|e| MidiError::ConnectionFailed(e.to_string()))?;
        client.ignore(midir::Ignore::None);

        client
            .create_virtual(name, move |stamp, message, _| callback(stamp, message), ())
            .map_err(|e| MidiError::ConnectionFailed(e.to_string()))
    }

    /// Fallback used on platforms without virtual port support.
    #[cfg(not(unix))]
    fn connect_virtual(
        _name: &str,
        _callback: Box<InputCallback>,
    ) -> Result<MidiInputConnection<()>, MidiError> {
        Err(MidiError::PlatformUnsupported(
            "virtual MIDI input ports".to_string(),
        ))
    }

    /// Stops delivery of incoming messages, closing the active connection.
    pub fn stop(&mut self) {
        if let Some(connection) = self.connection.take() {
            connection.close();
        }
    }
}

/// A MIDI output port that sends short messages and SysEx data.
pub struct MidiOutputPort {
    name: String,
    connection: MidiOutputConnection,
}

impl MidiOutputPort {
    /// Opens a hardware output port identified by `port_name`.
    pub fn open(port_name: &str) -> Result<Self, MidiError> {
        let client = MidiOutput::new(OUTPUT_CLIENT_NAME)
            .map_err(|e| MidiError::ConnectionFailed(e.to_string()))?;

        let port = client
            .ports()
            .into_iter()
            .find(|p| client.port_name(p).map(|n| n == port_name).unwrap_or(false))
            .ok_or_else(|| MidiError::PortNotFound(port_name.to_string()))?;

        let connection = client
            .connect(&port, port_name)
            .map_err(|e| MidiError::ConnectionFailed(e.to_string()))?;

        Ok(Self {
            name: port_name.to_string(),
            connection,
        })
    }

    /// Creates a virtual output port (source endpoint) named `name`.
    ///
    /// Returns [`MidiError::PlatformUnsupported`] on platforms without virtual
    /// port support.
    pub fn create_virtual(name: &str) -> Result<Self, MidiError> {
        Self::create_virtual_impl(name)
    }

    /// Creates and connects a virtual source endpoint (Unix only).
    #[cfg(unix)]
    fn create_virtual_impl(name: &str) -> Result<Self, MidiError> {
        use midir::os::unix::VirtualOutput;

        let client = MidiOutput::new(OUTPUT_CLIENT_NAME)
            .map_err(|e| MidiError::ConnectionFailed(e.to_string()))?;

        let connection = client
            .create_virtual(name)
            .map_err(|e| MidiError::ConnectionFailed(e.to_string()))?;

        Ok(Self {
            name: name.to_string(),
            connection,
        })
    }

    /// Fallback used on platforms without virtual port support.
    #[cfg(not(unix))]
    fn create_virtual_impl(_name: &str) -> Result<Self, MidiError> {
        Err(MidiError::PlatformUnsupported(
            "virtual MIDI output ports".to_string(),
        ))
    }

    /// Gets the display name of this port.
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Sends a short MIDI message, transmitting two bytes for Program Change and
    /// Channel Pressure and three bytes for all other channel message types.
    pub fn send(&mut self, status: u8, data1: u8, data2: u8) -> Result<(), MidiError> {
        let message_type = status & 0xF0;
        let result = if message_type == 0xC0 || message_type == 0xD0 {
            self.connection.send(&[status, data1])
        } else {
            self.connection.send(&[status, data1, data2])
        };
        result.map_err(|e| MidiError::ConnectionFailed(e.to_string()))
    }

    /// Sends a raw byte sequence such as a System Exclusive message.
    pub fn send_raw(&mut self, data: &[u8]) -> Result<(), MidiError> {
        self.connection
            .send(data)
            .map_err(|e| MidiError::ConnectionFailed(e.to_string()))
    }
}

/// Returns the names of all available MIDI input ports on this system.
pub fn list_input_port_names() -> Vec<String> {
    let client = match MidiInput::new(INPUT_CLIENT_NAME) {
        Ok(c) => c,
        Err(_) => return Vec::new(),
    };
    client
        .ports()
        .iter()
        .filter_map(|p| client.port_name(p).ok())
        .collect()
}

/// Returns the names of all available MIDI output ports on this system.
pub fn list_output_port_names() -> Vec<String> {
    let client = match MidiOutput::new(OUTPUT_CLIENT_NAME) {
        Ok(c) => c,
        Err(_) => return Vec::new(),
    };
    client
        .ports()
        .iter()
        .filter_map(|p| client.port_name(p).ok())
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn enumeration_never_panics_and_returns_a_list() {
        let _ = list_input_port_names();
        let _ = list_output_port_names();
    }

    #[test]
    fn opening_a_missing_port_reports_not_found() {
        let result = MidiInputPort::open("__ownaudio_nonexistent_port__");
        assert!(matches!(result, Err(MidiError::PortNotFound(_))));
    }
}
