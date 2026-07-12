//! # ownaudio-midi-core
//!
//! Cross-platform MIDI processing core for OwnAudioSharp.
//!
//! This crate concentrates all MIDI logic that previously lived in the managed
//! `OwnAudio.Midi` package:
//!
//! - [`port`] — hardware and virtual MIDI input/output through the `midir`
//!   crate (WinMM, CoreMIDI, ALSA sequencer). On Android, where `midir` has no
//!   backend, this module is a stub that reports [`error::MidiError::PlatformUnsupported`]
//!   for every port operation; file and clock functionality remain available.
//! - [`clock`] — a high-resolution 24 PPQN timing clock.
//! - [`file`] — Standard MIDI File parsing and serialization.
//!
//! The crate exposes a plain Rust API; the stable C ABI used by the C# layer is
//! provided by the separate `ownaudio-midi-ffi` crate.

pub mod clock;
pub mod error;
pub mod file;
pub mod message;

#[cfg(not(target_os = "android"))]
pub mod port;

#[cfg(target_os = "android")]
#[path = "port_android.rs"]
pub mod port;

pub use clock::MidiClock;
pub use error::MidiError;
pub use file::{
    parse_midi_file, write_midi_file, MidiEventData, MidiEventKind, MidiFileData, MidiTrackData,
};
pub use message::MidiMessage;
pub use port::{
    list_input_port_names, list_output_port_names, MidiInputPort, MidiOutputPort,
};
