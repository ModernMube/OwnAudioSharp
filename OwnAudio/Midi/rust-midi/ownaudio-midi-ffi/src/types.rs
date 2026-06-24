//! Blittable structs that cross the FFI boundary by value.

/// C-compatible short MIDI message passed to and from the C# layer.
///
/// The explicit padding byte keeps the 8-byte `timestamp_us` field naturally
/// aligned and makes the layout identical to the managed `NativeMidiMessage`
/// mirror, which a parity test verifies on both sides.
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct NativeMidiMessage {
    /// Status byte (message type in the high nibble, channel in the low nibble).
    pub status: u8,
    /// First data byte.
    pub data1: u8,
    /// Second data byte.
    pub data2: u8,
    /// Padding for alignment; always zero.
    pub _pad: u8,
    /// Arrival timestamp in microseconds.
    pub timestamp_us: i64,
}

/// C-compatible MIDI file event used by both the parser query API (Rust fills
/// it) and the writer API (the C# layer fills it).
///
/// For events read from a parsed file, `meta_data` points into memory owned by
/// the originating [`crate::handles::MidiFileHandle`] and is valid until that
/// handle is destroyed. For events passed to the writer, `meta_data` is borrowed
/// for the duration of the call and copied internally.
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct NativeMidiEvent {
    /// Ticks since the previous event in the track.
    pub delta_time: i32,
    /// Event category: 0 = MIDI, 1 = Meta, 2 = SysEx.
    pub event_type: u8,
    /// Status byte.
    pub status: u8,
    /// First data byte.
    pub data1: u8,
    /// Second data byte.
    pub data2: u8,
    /// Meta event sub-type byte.
    pub meta_type: u8,
    /// Padding for pointer alignment; always zero.
    pub _pad0: u8,
    /// Padding for pointer alignment; always zero.
    pub _pad1: u8,
    /// Padding for pointer alignment; always zero.
    pub _pad2: u8,
    /// Pointer to the payload bytes, or null when there is none.
    pub meta_data: *const u8,
    /// Number of payload bytes referenced by `meta_data`.
    pub meta_data_len: usize,
}
