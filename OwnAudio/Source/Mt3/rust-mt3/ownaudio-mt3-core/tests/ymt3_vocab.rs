//! Contract test against a real checkpoint's vocabulary.
//!
//! `tools/mt3/dump_vocab.py` reads the token codec straight out of a YourMT3 `.ckpt`; the
//! fixture here is its output for the T5 checkpoint this was developed against. If the script
//! and the loader ever disagree about the json shape, this fails instead of the model quietly
//! transcribing garbage.

use ownaudio_mt3_core::{Event, NoteDecoder, Vocabulary};

const YMT3: &str = include_str!("fixtures/ymt3_vocab.json");

fn vocab() -> Vocabulary {
    Vocabulary::from_json(YMT3).expect("the dumped vocabulary should load")
}

#[test]
fn matches_the_checkpoint_the_fixture_came_from() {
    let v = vocab();

    assert_eq!(v.sample_rate, 16000);
    assert_eq!(v.segment_seconds, 2.048);

    // 2.048 s at 16 kHz rounds to 32768, but the checkpoint was trained on 32767 and that one
    // sample is worth a whole extra encoder frame — so the export states it outright.
    assert_eq!(v.segment_samples(), 32767);
    assert!((v.segment_duration() - 2.0479375).abs() < 1e-9);
    assert_eq!(v.max_target_length, 1024);
    assert_eq!(v.step_seconds(), 0.01);
    assert_eq!(v.start_token(), 0);
    assert_eq!(v.eos_token(), 1);
}

#[test]
fn every_range_decodes_at_its_boundaries() {
    let v = vocab();

    assert_eq!(v.decode(3), Some(Event::Shift(0)));
    assert_eq!(v.decode(208), Some(Event::Shift(205)));
    assert_eq!(v.decode(209), Some(Event::Pitch(0)));
    assert_eq!(v.decode(336), Some(Event::Pitch(127)));
    assert_eq!(v.decode(337), Some(Event::Velocity(0)));
    assert_eq!(v.decode(338), Some(Event::Velocity(1)));
    assert_eq!(v.decode(339), Some(Event::Tie));
    assert_eq!(v.decode(340), Some(Event::Program(0)));
    assert_eq!(v.decode(467), Some(Event::Program(127)));
    assert_eq!(v.decode(468), Some(Event::Drum(0)));
    assert_eq!(v.decode(595), Some(Event::Drum(127)));

    // 596 tokens total, so anything above that is off the end of the vocabulary.
    assert_eq!(v.decode(596), None);
}

#[test]
fn binary_velocity_lands_on_full_midi_velocity() {
    let v = vocab();
    let mut decoder = NoteDecoder::new(&v);

    // This checkpoint has two velocity bins — note-off and note-on — so an onset has to come
    // out as a usable MIDI velocity rather than 1/127th of one.
    decoder.push_segment(&[339, 3 + 50, 338, 209 + 60, 3 + 150, 337, 209 + 60], 0.0);
    let notes = decoder.finish(2.048);

    assert_eq!(notes.len(), 1);
    assert_eq!(notes[0].pitch, 60);
    assert_eq!(notes[0].velocity, 127);
    assert!((notes[0].start - 0.5).abs() < 1e-9);
    assert!((notes[0].end - 1.5).abs() < 1e-9);
}

#[test]
fn a_program_change_labels_the_notes_that_follow() {
    let v = vocab();
    let mut decoder = NoteDecoder::new(&v);

    decoder.push_segment(
        &[
            339,      // tie section closes immediately
            3,        // shift 0
            340 + 33, // program 33, electric bass
            338,      // velocity on
            209 + 40, // pitch 40
            3 + 100,  // shift 1.0s
            337,      // velocity off
            209 + 40,
        ],
        0.0,
    );
    let notes = decoder.finish(2.048);

    assert_eq!(notes.len(), 1);
    assert_eq!(notes[0].program, 33);
    assert!(!notes[0].is_drum);
}

#[test]
fn drums_come_through_flagged() {
    let v = vocab();
    let mut decoder = NoteDecoder::new(&v);

    decoder.push_segment(&[339, 3 + 20, 338, 468 + 36], 0.0);
    let notes = decoder.finish(2.048);

    assert_eq!(notes.len(), 1);
    assert!(notes[0].is_drum);
    assert_eq!(notes[0].pitch, 36);
}
