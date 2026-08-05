//! Token stream to notes.
//!
//! MT3 transcribes one short segment at a time, and every segment starts by re-declaring the
//! pitches that were already sounding when it began — the "tied pitches" section, closed by a
//! [`Event::Tie`]. Held notes therefore survive segment boundaries, and the decoder has to carry
//! state across segments rather than treating each one in isolation. That state lives here.
//!
//! Modelled on Magenta's MT3 (`mt3/note_sequences.py`, Apache-2.0). Where the reference raises on
//! a malformed stream we are deliberately lenient: a checkpoint that emits a stray note-off is
//! worth a counter, not a failed transcription of the whole song.

use std::collections::HashMap;

use crate::vocab::{Event, Vocabulary};

/// Drums have no note-off, so they get a token duration instead. Same value MT3 uses.
const DRUM_DURATION: f64 = 0.01;

/// One transcribed note.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Note {
    /// Onset, seconds from the start of the track.
    pub start: f64,
    /// Offset, seconds. For drums this is `start + 0.01`.
    pub end: f64,
    /// MIDI pitch.
    pub pitch: u8,
    /// MIDI velocity, 1..=127.
    pub velocity: u8,
    /// MIDI program the note was played on. Meaningless when `is_drum`.
    pub program: u8,
    /// Percussion rather than a pitched instrument.
    pub is_drum: bool,
}

/// Notes that were still sounding, keyed the way MT3 keys them: pitch *and* program, so the same
/// pitch on two instruments stays two notes.
type ActiveKey = (u8, u8);

/// Onset time plus the velocity it started with.
type ActiveNote = (f64, u8);

/// Walks token streams and accumulates notes. One instance per track — feed it every segment in
/// order, then [`finish`](Self::finish).
pub struct NoteDecoder<'v> {
    vocab: &'v Vocabulary,
    velocity_bins: u8,

    notes: Vec<Note>,
    active: HashMap<ActiveKey, ActiveNote>,
    tied: Vec<ActiveKey>,

    current_time: f64,
    current_velocity: u8,
    current_program: u8,
    in_tie_section: bool,

    /// Tokens we could not make sense of. Worth logging once at the end; a healthy run has few.
    dropped: usize,
}

impl<'v> NoteDecoder<'v> {
    /// Fresh decoder for one track.
    pub fn new(vocab: &'v Vocabulary) -> Self {
        Self {
            vocab,
            velocity_bins: vocab.velocity_bins(),
            notes: Vec::new(),
            active: HashMap::new(),
            tied: Vec::new(),
            current_time: 0.0,
            current_velocity: 0,
            current_program: 0,
            in_tie_section: false,
            dropped: 0,
        }
    }

    /// How many tokens were skipped as nonsense so far.
    pub fn dropped_tokens(&self) -> usize {
        self.dropped
    }

    /// Feeds one segment's tokens. `segment_start` is where the segment sits on the track
    /// timeline, in seconds — every shift inside the segment is relative to it.
    pub fn push_segment(&mut self, tokens: &[u32], segment_start: f64) {
        self.current_time = segment_start;
        self.in_tie_section = true;
        self.tied.clear();

        for &token in tokens {
            match self.vocab.decode(token) {
                Some(Event::Eos) => break,
                Some(event) => self.apply(event, segment_start),
                None => self.dropped += 1,
            }
        }

        // A segment whose tie section never closed still told us which pitches carry over, so
        // leave them active rather than dropping them on the floor.
        self.in_tie_section = false;
    }

    fn apply(&mut self, event: Event, segment_start: f64) {
        match event {
            Event::Shift(steps) => {
                // A shift before the tie closed means the model skipped the tie token. Close it
                // ourselves, otherwise every held note would leak into the rest of the track.
                if self.in_tie_section {
                    self.close_tie_section();
                }
                let time = segment_start + steps as f64 * self.vocab.step_seconds();
                self.current_time = time.max(self.current_time);
            }

            Event::Velocity(bin) => self.current_velocity = bin,

            Event::Program(program) => self.current_program = program,

            Event::Tie => self.close_tie_section(),

            Event::Pitch(pitch) if self.in_tie_section => {
                let key = (pitch, self.current_program);
                if self.active.contains_key(&key) {
                    self.tied.push(key);
                } else {
                    self.dropped += 1;
                }
            }

            Event::Pitch(pitch) => {
                let key = (pitch, self.current_program);
                if self.current_velocity == 0 {
                    self.end_note(key);
                } else {
                    self.start_note(key);
                }
            }

            Event::Drum(pitch) if self.current_velocity > 0 => {
                self.notes.push(Note {
                    start: self.current_time,
                    end: self.current_time + DRUM_DURATION,
                    pitch,
                    velocity: self.velocity(self.current_velocity),
                    program: 0,
                    is_drum: true,
                });
            }

            Event::Drum(_) => self.dropped += 1,

            Event::Eos => {}
        }
    }

    /// Ends everything that was sounding but did not get re-declared, and leaves tie mode.
    fn close_tie_section(&mut self) {
        if !self.in_tie_section {
            return;
        }
        self.in_tie_section = false;

        let ending: Vec<ActiveKey> = self
            .active
            .keys()
            .copied()
            .filter(|key| !self.tied.contains(key))
            .collect();

        for key in ending {
            self.end_note(key);
        }
    }

    fn start_note(&mut self, key: ActiveKey) {
        // Two onsets without an offset in between: treat the second as re-articulation and close
        // the first, which is what it sounds like.
        if self.active.contains_key(&key) {
            self.end_note(key);
        }
        self.active
            .insert(key, (self.current_time, self.current_velocity));
    }

    fn end_note(&mut self, key: ActiveKey) {
        let Some((start, velocity)) = self.active.remove(&key) else {
            self.dropped += 1;
            return;
        };

        // Zero-length notes come out of the model often enough at segment seams; they carry no
        // musical information and would only confuse the chromagram downstream.
        if self.current_time <= start {
            return;
        }

        let (pitch, program) = key;
        self.notes.push(Note {
            start,
            end: self.current_time,
            pitch,
            velocity: self.velocity(velocity),
            program,
            is_drum: false,
        });
    }

    /// Bin index to MIDI velocity. With 127 bins this is the identity, with 1 bin everything
    /// lands on full velocity.
    fn velocity(&self, bin: u8) -> u8 {
        if bin == 0 {
            return 0;
        }
        let scaled = (bin as f32 / self.velocity_bins as f32 * 127.0).round();
        scaled.clamp(1.0, 127.0) as u8
    }

    /// Closes whatever is still ringing at `track_end` and hands back the notes, sorted by onset.
    pub fn finish(mut self, track_end: f64) -> Vec<Note> {
        self.current_time = self.current_time.max(track_end);

        let leftovers: Vec<ActiveKey> = self.active.keys().copied().collect();
        for key in leftovers {
            self.end_note(key);
        }

        self.notes
            .sort_by(|a, b| a.start.total_cmp(&b.start).then(a.pitch.cmp(&b.pitch)));
        self.notes
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Token ids for the layout in [`crate::vocab`]'s test vocabulary.
    const SHIFT: u32 = 3;
    const PITCH: u32 = 1004;
    const VELOCITY: u32 = 1092;
    const TIE: u32 = 1220;
    const PROGRAM: u32 = 1221;
    const DRUM: u32 = 1349;

    fn shift(steps: u32) -> u32 {
        SHIFT + steps
    }
    fn pitch(p: u8) -> u32 {
        PITCH + p as u32 - 21
    }
    fn velocity(v: u8) -> u32 {
        VELOCITY + v as u32
    }
    fn program(p: u8) -> u32 {
        PROGRAM + p as u32
    }
    fn drum(p: u8) -> u32 {
        DRUM + p as u32 - 21
    }

    fn vocab() -> Vocabulary {
        Vocabulary::from_json(crate::vocab::tests::SAMPLE).unwrap()
    }

    #[test]
    fn one_note_inside_a_single_segment() {
        let v = vocab();
        let mut d = NoteDecoder::new(&v);

        d.push_segment(
            &[
                TIE,
                shift(50),
                velocity(100),
                pitch(60),
                shift(150),
                velocity(0),
                pitch(60),
            ],
            0.0,
        );
        let notes = d.finish(2.048);

        assert_eq!(notes.len(), 1);
        assert_eq!(notes[0].pitch, 60);
        assert_eq!(notes[0].velocity, 100);
        assert!((notes[0].start - 0.5).abs() < 1e-9);
        assert!((notes[0].end - 1.5).abs() < 1e-9);
    }

    #[test]
    fn a_tied_note_spans_two_segments() {
        let v = vocab();
        let mut d = NoteDecoder::new(&v);

        d.push_segment(&[TIE, shift(100), velocity(90), pitch(64)], 0.0);
        // Second segment re-declares pitch 64 as still sounding, then releases it.
        d.push_segment(&[pitch(64), TIE, shift(50), velocity(0), pitch(64)], 2.048);
        let notes = d.finish(4.096);

        assert_eq!(notes.len(), 1, "the tie should have kept it a single note");
        assert!((notes[0].start - 1.0).abs() < 1e-9);
        assert!((notes[0].end - 2.548).abs() < 1e-9);
    }

    #[test]
    fn a_note_not_re_declared_ends_at_the_segment_boundary() {
        let v = vocab();
        let mut d = NoteDecoder::new(&v);

        d.push_segment(&[TIE, shift(100), velocity(90), pitch(64)], 0.0);
        d.push_segment(&[TIE, shift(50)], 2.048);
        let notes = d.finish(4.096);

        assert_eq!(notes.len(), 1);
        assert!((notes[0].end - 2.048).abs() < 1e-9);
    }

    #[test]
    fn the_same_pitch_on_two_programs_stays_two_notes() {
        let v = vocab();
        let mut d = NoteDecoder::new(&v);

        d.push_segment(
            &[
                TIE,
                shift(0),
                program(0),
                velocity(100),
                pitch(60),
                program(32),
                velocity(100),
                pitch(60),
                shift(100),
                program(0),
                velocity(0),
                pitch(60),
            ],
            0.0,
        );
        let notes = d.finish(2.048);

        assert_eq!(notes.len(), 2);
        assert!(notes.iter().any(|n| n.program == 0));
        assert!(notes.iter().any(|n| n.program == 32));
    }

    #[test]
    fn drums_get_a_token_duration_and_no_offset() {
        let v = vocab();
        let mut d = NoteDecoder::new(&v);

        d.push_segment(&[TIE, shift(20), velocity(100), drum(36)], 0.0);
        let notes = d.finish(2.048);

        assert_eq!(notes.len(), 1);
        assert!(notes[0].is_drum);
        assert!((notes[0].end - notes[0].start - DRUM_DURATION).abs() < 1e-9);
    }

    #[test]
    fn a_missing_note_off_is_closed_at_the_end_of_the_track() {
        let v = vocab();
        let mut d = NoteDecoder::new(&v);

        d.push_segment(&[TIE, shift(10), velocity(80), pitch(55)], 0.0);
        let notes = d.finish(2.048);

        assert_eq!(notes.len(), 1);
        assert!((notes[0].end - 2.048).abs() < 1e-9);
    }

    #[test]
    fn a_stray_note_off_is_counted_not_fatal() {
        let v = vocab();
        let mut d = NoteDecoder::new(&v);

        d.push_segment(&[TIE, shift(10), velocity(0), pitch(55)], 0.0);
        let dropped = d.dropped_tokens();
        let notes = d.finish(2.048);

        assert!(notes.is_empty());
        assert_eq!(dropped, 1);
    }

    #[test]
    fn a_missing_tie_token_still_closes_the_section() {
        let v = vocab();
        let mut d = NoteDecoder::new(&v);

        d.push_segment(&[TIE, shift(100), velocity(90), pitch(64)], 0.0);
        // No TIE here — the shift has to close the section, or the note would never end.
        d.push_segment(&[shift(50), velocity(100), pitch(67)], 2.048);
        let notes = d.finish(4.096);

        assert_eq!(notes.len(), 2);
        let held = notes.iter().find(|n| n.pitch == 64).unwrap();
        assert!((held.end - 2.048).abs() < 1e-9);
    }

    #[test]
    fn shifts_never_run_backwards() {
        let v = vocab();
        let mut d = NoteDecoder::new(&v);

        d.push_segment(
            &[
                TIE,
                shift(100),
                velocity(100),
                pitch(60),
                shift(10),
                velocity(0),
                pitch(60),
            ],
            0.0,
        );
        let notes = d.finish(2.048);

        // The backwards shift is clamped, so the note is zero-length and gets dropped.
        assert!(notes.is_empty());
    }

    #[test]
    fn velocity_bins_scale_to_midi_velocity() {
        let v = vocab();
        let d = NoteDecoder::new(&v);

        assert_eq!(d.velocity(0), 0);
        assert_eq!(d.velocity(127), 127);
        assert_eq!(d.velocity(64), 64);
    }
}
