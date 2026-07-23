//! Smoke tests for the MIDI FFI exports.
//!
//! These verify the error-handling contract on the FFI boundary: null pointers
//! are rejected, invalid handles are detected, and enumeration plus the
//! parse/serialize round trip work end to end without a real MIDI device.

use std::os::raw::c_char;
use std::ptr;

use ownaudio_midi_ffi::error_code::MidiErrorCode;
use ownaudio_midi_ffi::ffi_clock::*;
use ownaudio_midi_ffi::ffi_file::*;
use ownaudio_midi_ffi::ffi_port::*;
use ownaudio_midi_ffi::handles::{MidiClockHandle, MidiFileHandle, MidiWriterHandle};

const SUCCESS: i32 = MidiErrorCode::Success as i32;
const NULL_POINTER: i32 = MidiErrorCode::NullPointer as i32;
const INVALID_FILE: i32 = MidiErrorCode::InvalidFile as i32;

#[test]
fn list_input_ports_rejects_null_arguments() {
    let mut count: usize = 0;
    let code = ownaudio_midi_v1_list_input_ports(ptr::null_mut(), &mut count);
    assert_eq!(code, NULL_POINTER);
}

#[test]
fn list_output_ports_returns_an_array() {
    let mut names: *mut *mut c_char = ptr::null_mut();
    let mut count: usize = 0;
    let code = ownaudio_midi_v1_list_output_ports(&mut names, &mut count);
    assert_eq!(code, SUCCESS);
    ownaudio_midi_v1_free_port_names(names, count);
}

#[test]
fn port_fingerprint_is_stable_across_calls() {
    let mut in_count: usize = 0;
    let mut out_count: usize = 0;
    let mut fp1: u64 = 0;
    assert_eq!(
        ownaudio_midi_v1_port_fingerprint(&mut in_count, &mut out_count, &mut fp1),
        SUCCESS
    );

    // No topology change between two back-to-back calls, so the hash must match.
    let mut fp2: u64 = 0;
    assert_eq!(
        ownaudio_midi_v1_port_fingerprint(ptr::null_mut(), ptr::null_mut(), &mut fp2),
        SUCCESS
    );
    assert_eq!(fp1, fp2);
}

#[test]
fn port_fingerprint_tolerates_all_null_outputs() {
    assert_eq!(
        ownaudio_midi_v1_port_fingerprint(ptr::null_mut(), ptr::null_mut(), ptr::null_mut()),
        SUCCESS
    );
}

#[test]
fn clock_create_and_destroy() {
    let mut handle: *mut MidiClockHandle = ptr::null_mut();
    let code = ownaudio_midi_v1_clock_create(120.0, &mut handle);
    assert_eq!(code, SUCCESS);
    assert!(!handle.is_null());

    assert_eq!(ownaudio_midi_v1_clock_set_bpm(handle, 140.0), SUCCESS);
    ownaudio_midi_v1_clock_destroy(handle);
}

#[test]
fn clock_create_rejects_null_out_handle() {
    let code = ownaudio_midi_v1_clock_create(120.0, ptr::null_mut());
    assert_eq!(code, NULL_POINTER);
}

#[test]
fn file_parse_rejects_garbage() {
    let garbage = [0u8; 4];
    let mut handle: *mut MidiFileHandle = ptr::null_mut();
    let code = ownaudio_midi_v1_file_parse(garbage.as_ptr(), garbage.len(), &mut handle);
    assert_eq!(code, INVALID_FILE);
    assert!(handle.is_null());
}

#[test]
fn writer_round_trips_through_parser() {
    let mut writer: *mut MidiWriterHandle = ptr::null_mut();
    assert_eq!(ownaudio_midi_v1_writer_create(1, 480, &mut writer), SUCCESS);
    assert_eq!(ownaudio_midi_v1_writer_begin_track(writer), SUCCESS);

    let note_on = ownaudio_midi_ffi::types::NativeMidiEvent {
        delta_time: 0,
        event_type: 0,
        status: 0x90,
        data1: 60,
        data2: 100,
        meta_type: 0,
        _pad0: 0,
        _pad1: 0,
        _pad2: 0,
        meta_data: ptr::null(),
        meta_data_len: 0,
    };
    assert_eq!(ownaudio_midi_v1_writer_add_event(writer, note_on), SUCCESS);

    let mut data: *mut u8 = ptr::null_mut();
    let mut len: usize = 0;
    assert_eq!(
        ownaudio_midi_v1_writer_serialize(writer, &mut data, &mut len),
        SUCCESS
    );
    assert!(len > 14);

    let mut file: *mut MidiFileHandle = ptr::null_mut();
    assert_eq!(ownaudio_midi_v1_file_parse(data, len, &mut file), SUCCESS);

    let mut format: u16 = 0;
    assert_eq!(ownaudio_midi_v1_file_get_format(file, &mut format), SUCCESS);
    assert_eq!(format, 1);

    let mut track_count: usize = 0;
    assert_eq!(
        ownaudio_midi_v1_file_get_track_count(file, &mut track_count),
        SUCCESS
    );
    assert_eq!(track_count, 1);

    ownaudio_midi_v1_file_destroy(file);
    ownaudio_midi_v1_free_bytes(data, len);
    ownaudio_midi_v1_writer_destroy(writer);
}

/// Builds a plain channel event for the batch tests.
fn channel_event(delta: i32, status: u8, d1: u8, d2: u8) -> ownaudio_midi_ffi::types::NativeMidiEvent {
    ownaudio_midi_ffi::types::NativeMidiEvent {
        delta_time: delta,
        event_type: 0,
        status,
        data1: d1,
        data2: d2,
        meta_type: 0,
        _pad0: 0,
        _pad1: 0,
        _pad2: 0,
        meta_data: ptr::null(),
        meta_data_len: 0,
    }
}

#[test]
fn batch_add_events_round_trips_through_batch_read() {
    let mut writer: *mut MidiWriterHandle = ptr::null_mut();
    assert_eq!(ownaudio_midi_v1_writer_create(0, 480, &mut writer), SUCCESS);
    assert_eq!(ownaudio_midi_v1_writer_begin_track(writer), SUCCESS);

    // A tempo meta plus two channel events, added in one batch call.
    let tempo_payload = [0x07u8, 0xA1, 0x20]; // 500000 us/qn
    let tempo = ownaudio_midi_ffi::types::NativeMidiEvent {
        delta_time: 0,
        event_type: 1,
        status: 0xFF,
        data1: 0,
        data2: 0,
        meta_type: 0x51,
        _pad0: 0,
        _pad1: 0,
        _pad2: 0,
        meta_data: tempo_payload.as_ptr(),
        meta_data_len: tempo_payload.len(),
    };
    let events = [
        tempo,
        channel_event(0, 0x90, 60, 100),
        channel_event(480, 0x80, 60, 0),
    ];
    assert_eq!(
        ownaudio_midi_v1_writer_add_events(writer, events.as_ptr(), events.len()),
        SUCCESS
    );

    let mut data: *mut u8 = ptr::null_mut();
    let mut len: usize = 0;
    assert_eq!(
        ownaudio_midi_v1_writer_serialize(writer, &mut data, &mut len),
        SUCCESS
    );

    let mut file: *mut MidiFileHandle = ptr::null_mut();
    assert_eq!(ownaudio_midi_v1_file_parse(data, len, &mut file), SUCCESS);

    let mut count: usize = 0;
    assert_eq!(
        ownaudio_midi_v1_file_get_event_count(file, 0, &mut count),
        SUCCESS
    );
    // Our three events plus the auto-appended End-of-Track meta.
    assert_eq!(count, 4);

    let mut buf = vec![channel_event(0, 0, 0, 0); count];
    let mut written: usize = 0;
    assert_eq!(
        ownaudio_midi_v1_file_get_events(file, 0, buf.as_mut_ptr(), buf.len(), &mut written),
        SUCCESS
    );
    assert_eq!(written, count);

    // First event is the tempo meta and its payload survived the round trip.
    assert_eq!(buf[0].event_type, 1);
    assert_eq!(buf[0].meta_type, 0x51);
    assert_eq!(buf[0].meta_data_len, 3);
    let payload = unsafe { std::slice::from_raw_parts(buf[0].meta_data, buf[0].meta_data_len) };
    assert_eq!(payload, &tempo_payload);

    assert_eq!(buf[1].status, 0x90);
    assert_eq!(buf[2].status, 0x80);

    ownaudio_midi_v1_file_destroy(file);
    ownaudio_midi_v1_free_bytes(data, len);
    ownaudio_midi_v1_writer_destroy(writer);
}

#[test]
fn batch_read_rejects_null_arguments() {
    let mut written: usize = 0;
    assert_eq!(
        ownaudio_midi_v1_file_get_events(ptr::null_mut(), 0, ptr::null_mut(), 0, &mut written),
        NULL_POINTER
    );
}

#[test]
fn batch_read_stops_at_capacity() {
    let mut writer: *mut MidiWriterHandle = ptr::null_mut();
    assert_eq!(ownaudio_midi_v1_writer_create(0, 480, &mut writer), SUCCESS);
    assert_eq!(ownaudio_midi_v1_writer_begin_track(writer), SUCCESS);
    let events = [
        channel_event(0, 0x90, 60, 100),
        channel_event(240, 0x80, 60, 0),
    ];
    assert_eq!(
        ownaudio_midi_v1_writer_add_events(writer, events.as_ptr(), events.len()),
        SUCCESS
    );

    let mut data: *mut u8 = ptr::null_mut();
    let mut len: usize = 0;
    assert_eq!(
        ownaudio_midi_v1_writer_serialize(writer, &mut data, &mut len),
        SUCCESS
    );
    let mut file: *mut MidiFileHandle = ptr::null_mut();
    assert_eq!(ownaudio_midi_v1_file_parse(data, len, &mut file), SUCCESS);

    // Undersized buffer: only the first event should be copied.
    let mut buf = vec![channel_event(0, 0, 0, 0); 1];
    let mut written: usize = 0;
    assert_eq!(
        ownaudio_midi_v1_file_get_events(file, 0, buf.as_mut_ptr(), buf.len(), &mut written),
        SUCCESS
    );
    assert_eq!(written, 1);
    assert_eq!(buf[0].status, 0x90);

    ownaudio_midi_v1_file_destroy(file);
    ownaudio_midi_v1_free_bytes(data, len);
    ownaudio_midi_v1_writer_destroy(writer);
}
