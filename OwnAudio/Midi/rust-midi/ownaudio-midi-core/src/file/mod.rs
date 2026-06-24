//! Standard MIDI File (SMF) data model, parser and serializer.
//!
//! The data structures mirror the managed `MidiFile`, `MidiTrack` and
//! `MidiEvent` types so the C# layer can rebuild them faithfully from the parsed
//! native representation. The parser and writer reproduce the exact byte-level
//! behaviour of the original managed implementation to guarantee round-trip
//! compatibility with files produced by earlier versions of the library.

mod parser;
mod writer;

pub use parser::parse_midi_file;
pub use writer::write_midi_file;

/// Classifies the content of a [`MidiEventData`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MidiEventKind {
    /// A standard MIDI channel message (Note On/Off, Control Change, etc.).
    Midi = 0,

    /// A meta event carrying file-level information such as tempo or track name.
    Meta = 1,

    /// A System Exclusive message containing manufacturer-specific data.
    SysEx = 2,
}

impl MidiEventKind {
    /// Returns the C ABI discriminant byte for this event kind.
    pub fn as_u8(self) -> u8 {
        self as u8
    }
}

/// A single timed event within a track.
#[derive(Debug, Clone)]
pub struct MidiEventData {
    /// Number of MIDI ticks since the previous event in the track.
    pub delta_time: i32,

    /// The category of this event.
    pub kind: MidiEventKind,

    /// Status byte (0xFF for meta events, 0xF0 for SysEx).
    pub status: u8,

    /// First MIDI data byte (unused for meta and SysEx events).
    pub data1: u8,

    /// Second MIDI data byte (unused for meta and SysEx events).
    pub data2: u8,

    /// Meta event sub-type byte (for example 0x51 for tempo).
    pub meta_type: u8,

    /// Payload bytes for meta and SysEx events; empty for plain MIDI events.
    pub meta_data: Vec<u8>,
}

/// An ordered sequence of events forming one track.
#[derive(Debug, Clone, Default)]
pub struct MidiTrackData {
    /// The events in this track, in delta-time order.
    pub events: Vec<MidiEventData>,
}

/// A complete parsed MIDI file.
#[derive(Debug, Clone)]
pub struct MidiFileData {
    /// SMF format: 0 (single track), 1 (multi-track sync), 2 (multi-track async).
    pub format: u16,

    /// Number of ticks per quarter note (PPQ resolution).
    pub ticks_per_beat: u16,

    /// The ordered list of tracks.
    pub tracks: Vec<MidiTrackData>,
}
