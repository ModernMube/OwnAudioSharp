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

/// Wire length of a short message, status byte included. `None` for SysEx
/// (0xF0 / 0xF7, variable length) and for data bytes that aren't a status at all.
pub fn short_message_len(status: u8) -> Option<usize> {
    match status {
        0x80..=0xBF | 0xE0..=0xEF | 0xF2 => Some(3),
        0xC0..=0xDF | 0xF1 | 0xF3 => Some(2),
        0xF4..=0xF6 | 0xF8..=0xFF => Some(1),
        _ => None,
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

    #[test]
    fn system_messages_are_not_padded_to_three_bytes() {
        for status in [0xF6, 0xF8, 0xFA, 0xFB, 0xFC, 0xFE, 0xFF] {
            assert_eq!(short_message_len(status), Some(1), "0x{status:02X}");
        }
        assert_eq!(short_message_len(0xF1), Some(2));
        assert_eq!(short_message_len(0xF3), Some(2));
        assert_eq!(short_message_len(0xF2), Some(3));
    }

    #[test]
    fn channel_messages_keep_their_length() {
        assert_eq!(short_message_len(0x93), Some(3));
        assert_eq!(short_message_len(0xB0), Some(3));
        assert_eq!(short_message_len(0xEF), Some(3));
        assert_eq!(short_message_len(0xC5), Some(2));
        assert_eq!(short_message_len(0xD0), Some(2));
    }

    #[test]
    fn sysex_and_data_bytes_have_no_short_length() {
        assert_eq!(short_message_len(0xF0), None);
        assert_eq!(short_message_len(0xF7), None);
        assert_eq!(short_message_len(0x40), None);
    }
}
