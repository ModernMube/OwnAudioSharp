//! Standard MIDI File reader.
//!
//! Parses the MThd header and each MTrk chunk into the [`MidiFileData`] model,
//! handling running status, meta events, SysEx blocks and the two-byte channel
//! message types (Program Change and Channel Pressure).

use crate::error::MidiError;
use crate::file::{MidiEventData, MidiEventKind, MidiFileData, MidiTrackData};

/// Parses a complete Standard MIDI File from `data`.
///
/// Returns [`MidiError::InvalidFile`] if the header or any track chunk is
/// malformed.
pub fn parse_midi_file(data: &[u8]) -> Result<MidiFileData, MidiError> {
    if data.len() < 14 {
        return Err(MidiError::InvalidFile("file shorter than MThd header".into()));
    }

    if &data[0..4] != b"MThd" {
        return Err(MidiError::InvalidFile("missing MThd header".into()));
    }

    let chunk_length = read_u32_be(&data[4..8]);
    if chunk_length < 6 {
        return Err(MidiError::InvalidFile("invalid MThd chunk length".into()));
    }

    let format = read_u16_be(&data[8..10]);
    let track_count = read_u16_be(&data[10..12]);
    let ticks_per_beat = read_u16_be(&data[12..14]);

    let mut pos = 8 + chunk_length as usize;
    let mut tracks = Vec::with_capacity(track_count as usize);
    for _ in 0..track_count {
        let (track, next) = read_track(data, pos)?;
        tracks.push(track);
        pos = next;
    }

    Ok(MidiFileData {
        format,
        ticks_per_beat,
        tracks,
    })
}

/// Reads one MTrk chunk starting at `pos`, returning the parsed track and the
/// offset immediately after the chunk's data.
fn read_track(data: &[u8], pos: usize) -> Result<(MidiTrackData, usize), MidiError> {
    if pos + 8 > data.len() {
        return Err(MidiError::InvalidFile("truncated MTrk header".into()));
    }

    if &data[pos..pos + 4] != b"MTrk" {
        return Err(MidiError::InvalidFile("expected MTrk chunk".into()));
    }

    let length = read_u32_be(&data[pos + 4..pos + 8]) as usize;
    let body_start = pos + 8;
    let body_end = body_start + length;
    if body_end > data.len() {
        return Err(MidiError::InvalidFile("truncated MTrk body".into()));
    }

    let events = parse_events(&data[body_start..body_end]);
    Ok((MidiTrackData { events }, body_end))
}

/// Parses the raw bytes of a single track body into a list of events.
fn parse_events(data: &[u8]) -> Vec<MidiEventData> {
    let mut events = Vec::with_capacity(64);
    let mut pos = 0usize;
    let mut running_status: u8 = 0;

    while pos < data.len() {
        let delta = read_var_len(data, &mut pos) as i32;
        if pos >= data.len() {
            break;
        }

        let b = data[pos];

        if b == 0xFF {
            pos += 1;
            if pos >= data.len() {
                break;
            }
            let meta_type = data[pos];
            pos += 1;
            let meta_len = read_var_len(data, &mut pos);
            let end = (pos + meta_len).min(data.len());
            let meta_data = data[pos..end].to_vec();
            pos = end;
            events.push(MidiEventData {
                delta_time: delta,
                kind: MidiEventKind::Meta,
                status: 0xFF,
                data1: 0,
                data2: 0,
                meta_type,
                meta_data,
            });
            if meta_type == 0x2F {
                break;
            }
            continue;
        }

        if b == 0xF0 || b == 0xF7 {
            pos += 1;
            let sysex_len = read_var_len(data, &mut pos);
            let end = (pos + sysex_len).min(data.len());
            let mut sysex = Vec::with_capacity(sysex_len + 1);
            sysex.push(b);
            sysex.extend_from_slice(&data[pos..end]);
            pos = end;
            running_status = 0;
            events.push(MidiEventData {
                delta_time: delta,
                kind: MidiEventKind::SysEx,
                status: 0xF0,
                data1: 0,
                data2: 0,
                meta_type: 0,
                meta_data: sysex,
            });
            continue;
        }

        if (b & 0x80) != 0 {
            running_status = b;
            pos += 1;
        }

        if running_status == 0 {
            continue;
        }

        let message_type = running_status & 0xF0;
        let d1 = if pos < data.len() {
            let v = data[pos];
            pos += 1;
            v
        } else {
            0
        };

        if message_type == 0xC0 || message_type == 0xD0 {
            events.push(MidiEventData {
                delta_time: delta,
                kind: MidiEventKind::Midi,
                status: running_status,
                data1: d1,
                data2: 0,
                meta_type: 0,
                meta_data: Vec::new(),
            });
        } else {
            let d2 = if pos < data.len() {
                let v = data[pos];
                pos += 1;
                v
            } else {
                0
            };
            events.push(MidiEventData {
                delta_time: delta,
                kind: MidiEventKind::Midi,
                status: running_status,
                data1: d1,
                data2: d2,
                meta_type: 0,
                meta_data: Vec::new(),
            });
        }
    }

    events
}

/// Reads a MIDI variable-length quantity, advancing `pos`.
fn read_var_len(data: &[u8], pos: &mut usize) -> usize {
    let mut value: usize = 0;
    let mut i = 0;
    while i < 4 && *pos < data.len() {
        let b = data[*pos];
        *pos += 1;
        value = (value << 7) | (b & 0x7F) as usize;
        if (b & 0x80) == 0 {
            break;
        }
        i += 1;
    }
    value
}

/// Reads a big-endian `u16` from the first two bytes of `bytes`.
fn read_u16_be(bytes: &[u8]) -> u16 {
    ((bytes[0] as u16) << 8) | bytes[1] as u16
}

/// Reads a big-endian `u32` from the first four bytes of `bytes`.
fn read_u32_be(bytes: &[u8]) -> u32 {
    ((bytes[0] as u32) << 24)
        | ((bytes[1] as u32) << 16)
        | ((bytes[2] as u32) << 8)
        | bytes[3] as u32
}
