//! Standard MIDI File writer.
//!
//! Serializes a [`MidiFileData`] back into SMF bytes, applying running-status
//! compression and appending an End-of-Track meta event when the last event is
//! not already one. The byte layout matches the original managed writer.

use crate::error::MidiError;
use crate::file::{MidiEventData, MidiEventKind, MidiFileData, MidiTrackData};

/// Serializes a complete MIDI file to its SMF byte representation.
pub fn write_midi_file(file: &MidiFileData) -> Result<Vec<u8>, MidiError> {
    let mut out = Vec::with_capacity(64 + file.tracks.len() * 64);

    out.extend_from_slice(b"MThd");
    write_u32_be(&mut out, 6);
    write_u16_be(&mut out, file.format);
    write_u16_be(&mut out, file.tracks.len() as u16);
    write_u16_be(&mut out, file.ticks_per_beat);

    for track in &file.tracks {
        write_track(track, &mut out);
    }

    Ok(out)
}

/// Serializes a single track into an MTrk chunk appended to `out`.
fn write_track(track: &MidiTrackData, out: &mut Vec<u8>) {
    let mut body = Vec::with_capacity(64);
    let mut running_status: u8 = 0;

    for event in &track.events {
        write_var_len(&mut body, event.delta_time);

        match event.kind {
            MidiEventKind::Meta => {
                body.push(0xFF);
                body.push(event.meta_type);
                write_var_len(&mut body, event.meta_data.len() as i32);
                body.extend_from_slice(&event.meta_data);
                running_status = 0;
            }
            MidiEventKind::SysEx => {
                body.extend_from_slice(&event.meta_data);
                running_status = 0;
            }
            MidiEventKind::Midi => {
                if event.status != running_status {
                    body.push(event.status);
                    running_status = event.status;
                }
                body.push(event.data1);

                let message_type = event.status & 0xF0;
                if message_type != 0xC0 && message_type != 0xD0 {
                    body.push(event.data2);
                }
            }
        }
    }

    if !ends_with_end_of_track(track) {
        write_var_len(&mut body, 0);
        body.push(0xFF);
        body.push(0x2F);
        body.push(0x00);
    }

    out.extend_from_slice(b"MTrk");
    write_u32_be(out, body.len() as u32);
    out.extend_from_slice(&body);
}

/// Returns true when the track's final event is an End-of-Track meta event.
fn ends_with_end_of_track(track: &MidiTrackData) -> bool {
    matches!(
        track.events.last(),
        Some(MidiEventData {
            kind: MidiEventKind::Meta,
            meta_type: 0x2F,
            ..
        })
    )
}

/// Encodes a non-negative integer as a MIDI variable-length quantity. Negative
/// values are clamped to zero, matching the original managed behaviour.
fn write_var_len(out: &mut Vec<u8>, value: i32) {
    let mut value = if value < 0 { 0u32 } else { value as u32 };

    let mut buf = [0u8; 4];
    let mut len = 0usize;

    buf[len] = (value & 0x7F) as u8;
    len += 1;
    value >>= 7;
    while value > 0 {
        buf[len] = ((value & 0x7F) | 0x80) as u8;
        len += 1;
        value >>= 7;
    }

    for i in (0..len).rev() {
        out.push(buf[i]);
    }
}

/// Appends a big-endian `u16` to `out`.
fn write_u16_be(out: &mut Vec<u8>, value: u16) {
    out.push((value >> 8) as u8);
    out.push((value & 0xFF) as u8);
}

/// Appends a big-endian `u32` to `out`.
fn write_u32_be(out: &mut Vec<u8>, value: u32) {
    out.push((value >> 24) as u8);
    out.push((value >> 16) as u8);
    out.push((value >> 8) as u8);
    out.push((value & 0xFF) as u8);
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::file::parse_midi_file;

    fn note_track() -> MidiTrackData {
        MidiTrackData {
            events: vec![
                MidiEventData {
                    delta_time: 0,
                    kind: MidiEventKind::Midi,
                    status: 0x90,
                    data1: 60,
                    data2: 100,
                    meta_type: 0,
                    meta_data: Vec::new(),
                },
                MidiEventData {
                    delta_time: 480,
                    kind: MidiEventKind::Midi,
                    status: 0x80,
                    data1: 60,
                    data2: 0,
                    meta_type: 0,
                    meta_data: Vec::new(),
                },
            ],
        }
    }

    #[test]
    fn round_trip_preserves_events() {
        let original = MidiFileData {
            format: 1,
            ticks_per_beat: 480,
            tracks: vec![note_track()],
        };

        let bytes = write_midi_file(&original).unwrap();
        let parsed = parse_midi_file(&bytes).unwrap();

        assert_eq!(parsed.format, 1);
        assert_eq!(parsed.ticks_per_beat, 480);
        assert_eq!(parsed.tracks.len(), 1);

        let events = &parsed.tracks[0].events;
        assert_eq!(events[0].status, 0x90);
        assert_eq!(events[0].data1, 60);
        assert_eq!(events[0].data2, 100);
        assert_eq!(events[1].status, 0x80);
        assert_eq!(events[1].delta_time, 480);

        let last = events.last().unwrap();
        assert_eq!(last.kind, MidiEventKind::Meta);
        assert_eq!(last.meta_type, 0x2F);
    }

    #[test]
    fn tempo_meta_event_round_trips() {
        let track = MidiTrackData {
            events: vec![MidiEventData {
                delta_time: 0,
                kind: MidiEventKind::Meta,
                status: 0xFF,
                data1: 0,
                data2: 0,
                meta_type: 0x51,
                meta_data: vec![0x07, 0xA1, 0x20],
            }],
        };
        let original = MidiFileData {
            format: 0,
            ticks_per_beat: 96,
            tracks: vec![track],
        };

        let bytes = write_midi_file(&original).unwrap();
        let parsed = parse_midi_file(&bytes).unwrap();

        let tempo = &parsed.tracks[0].events[0];
        assert_eq!(tempo.meta_type, 0x51);
        assert_eq!(tempo.meta_data, vec![0x07, 0xA1, 0x20]);
    }
}
