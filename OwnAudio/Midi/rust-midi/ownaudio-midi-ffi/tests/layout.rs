//! ABI layout parity tests for the MIDI FFI structs and enums.
//!
//! These assertions are the authoritative source of truth for the `#[repr(C)]`
//! memory layout of every type crossing the FFI boundary. The C# side mirrors
//! the same numbers in its struct-layout parity tests. If either side drifts,
//! one of the two suites fails before any silent memory corruption can occur.

use std::mem::{align_of, offset_of, size_of};

use ownaudio_midi_ffi::{
    error_code::MidiErrorCode,
    types::{NativeMidiEvent, NativeMidiMessage},
};

#[test]
fn native_midi_message_size_and_align() {
    assert_eq!(size_of::<NativeMidiMessage>(), 16, "NativeMidiMessage size");
    assert_eq!(align_of::<NativeMidiMessage>(), 8, "NativeMidiMessage align");
}

#[test]
fn native_midi_message_field_offsets() {
    assert_eq!(offset_of!(NativeMidiMessage, status), 0, "status");
    assert_eq!(offset_of!(NativeMidiMessage, data1), 1, "data1");
    assert_eq!(offset_of!(NativeMidiMessage, data2), 2, "data2");
    assert_eq!(offset_of!(NativeMidiMessage, _pad), 3, "_pad");
    assert_eq!(offset_of!(NativeMidiMessage, timestamp_us), 8, "timestamp_us");
}

#[test]
fn native_midi_event_size_and_align() {
    assert_eq!(size_of::<NativeMidiEvent>(), 32, "NativeMidiEvent size");
    assert_eq!(align_of::<NativeMidiEvent>(), 8, "NativeMidiEvent align");
}

#[test]
fn native_midi_event_field_offsets() {
    assert_eq!(offset_of!(NativeMidiEvent, delta_time), 0, "delta_time");
    assert_eq!(offset_of!(NativeMidiEvent, event_type), 4, "event_type");
    assert_eq!(offset_of!(NativeMidiEvent, status), 5, "status");
    assert_eq!(offset_of!(NativeMidiEvent, data1), 6, "data1");
    assert_eq!(offset_of!(NativeMidiEvent, data2), 7, "data2");
    assert_eq!(offset_of!(NativeMidiEvent, meta_type), 8, "meta_type");
    assert_eq!(offset_of!(NativeMidiEvent, meta_data), 16, "meta_data");
    assert_eq!(offset_of!(NativeMidiEvent, meta_data_len), 24, "meta_data_len");
}

#[test]
fn error_code_is_c_int_wide() {
    assert_eq!(size_of::<MidiErrorCode>(), 4, "MidiErrorCode size");
    assert_eq!(align_of::<MidiErrorCode>(), 4, "MidiErrorCode align");
}

#[test]
fn error_code_discriminants() {
    assert_eq!(MidiErrorCode::Success as i32, 0);
    assert_eq!(MidiErrorCode::PortNotFound as i32, 1);
    assert_eq!(MidiErrorCode::ConnectionFailed as i32, 2);
    assert_eq!(MidiErrorCode::PortNotOpen as i32, 3);
    assert_eq!(MidiErrorCode::InvalidFile as i32, 4);
    assert_eq!(MidiErrorCode::NullPointer as i32, 5);
    assert_eq!(MidiErrorCode::InvalidHandle as i32, 6);
    assert_eq!(MidiErrorCode::PlatformUnsupported as i32, 7);
    assert_eq!(MidiErrorCode::InternalPanic as i32, 8);
    assert_eq!(MidiErrorCode::IoError as i32, 9);
}
