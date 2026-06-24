//! Short MIDI message representation shared across the core crate.

/// A single short (channel or system real-time) MIDI message.
///
/// Timestamps are expressed in microseconds, matching the resolution that
/// `midir` reports on its input callbacks, so no unit conversion is needed
/// when the message crosses the FFI boundary.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MidiMessage {
    /// Status byte encoding the message type in the high nibble and, for
    /// channel messages, the channel in the low nibble.
    pub status: u8,

    /// First data byte, such as a note number or controller number.
    pub data1: u8,

    /// Second data byte, such as a velocity or controller value.
    pub data2: u8,

    /// Arrival timestamp in microseconds.
    pub timestamp_us: i64,
}

impl MidiMessage {
    /// Creates a new message from the given status, data bytes and timestamp.
    pub fn new(status: u8, data1: u8, data2: u8, timestamp_us: i64) -> Self {
        Self {
            status,
            data1,
            data2,
            timestamp_us,
        }
    }

    /// Returns the message type derived from the high nibble of the status byte.
    pub fn message_type(&self) -> u8 {
        self.status & 0xF0
    }

    /// Returns the zero-based channel number (0–15) of a channel message.
    pub fn channel(&self) -> u8 {
        self.status & 0x0F
    }

    /// Returns true for a Note On message with non-zero velocity.
    pub fn is_note_on(&self) -> bool {
        self.message_type() == 0x90 && self.data2 > 0
    }

    /// Returns true for a Note Off message, including Note On with velocity zero.
    pub fn is_note_off(&self) -> bool {
        self.message_type() == 0x80 || (self.message_type() == 0x90 && self.data2 == 0)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn type_and_channel_are_extracted_from_status() {
        let msg = MidiMessage::new(0x95, 60, 100, 0);
        assert_eq!(msg.message_type(), 0x90);
        assert_eq!(msg.channel(), 5);
    }

    #[test]
    fn note_on_with_velocity_is_note_on() {
        let msg = MidiMessage::new(0x90, 60, 64, 0);
        assert!(msg.is_note_on());
        assert!(!msg.is_note_off());
    }

    #[test]
    fn note_on_with_zero_velocity_is_note_off() {
        let msg = MidiMessage::new(0x90, 60, 0, 0);
        assert!(!msg.is_note_on());
        assert!(msg.is_note_off());
    }

    #[test]
    fn explicit_note_off_is_note_off() {
        let msg = MidiMessage::new(0x80, 60, 64, 0);
        assert!(!msg.is_note_on());
        assert!(msg.is_note_off());
    }
}
