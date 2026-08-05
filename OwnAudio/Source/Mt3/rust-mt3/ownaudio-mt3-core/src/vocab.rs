//! The MT3 token vocabulary, loaded from the `vocab.json` the export script dumps.
//!
//! MT3 lays its token space out as three special ids (pad/eos/unk), then one contiguous block of
//! shift events, then the remaining event ranges in declaration order. Nothing about that layout
//! is baked in here: swapping to another YourMT3 variant with a different vocabulary means
//! shipping a different json, not rebuilding the library.
//!
//! The structure follows Magenta's MT3 (`mt3/event_codec.py`, Apache-2.0); no code from the
//! GPL-licensed YourMT3 repository is reproduced.

use serde::Deserialize;

use crate::error::{Mt3Error, Result};

/// One decoded token. Anything the codec cannot explain never becomes an `Event` in the first
/// place, so the note decoder only ever sees things it can act on.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Event {
    /// Absolute time within the segment, in codec steps.
    Shift(u32),
    /// Melodic pitch — a note on or off depending on the running velocity.
    Pitch(u8),
    /// Sets the running velocity. Zero means the next pitches are note-offs.
    Velocity(u8),
    /// Ends the tied-pitches section at the head of a segment.
    Tie,
    /// Sets the running MIDI program.
    Program(u8),
    /// Percussion hit, instantaneous.
    Drum(u8),
    /// End of sequence — stop generating.
    Eos,
}

/// Event kinds a range can carry, as spelled in the json.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "lowercase")]
enum RangeKind {
    Pitch,
    Velocity,
    Tie,
    Program,
    Drum,
}

/// One contiguous block of token ids, `offset` being the id of `min`.
#[derive(Debug, Clone, Copy, Deserialize)]
struct EventRange {
    kind: RangeKind,
    offset: u32,
    min: i32,
    max: i32,
}

impl EventRange {
    fn len(&self) -> u32 {
        (self.max - self.min + 1).max(0) as u32
    }

    fn contains(&self, token: u32) -> bool {
        token >= self.offset && token < self.offset + self.len()
    }

    fn decode(&self, token: u32) -> Event {
        let value = (self.min + (token - self.offset) as i32) as u8;
        match self.kind {
            RangeKind::Pitch => Event::Pitch(value),
            RangeKind::Velocity => Event::Velocity(value),
            RangeKind::Tie => Event::Tie,
            RangeKind::Program => Event::Program(value),
            RangeKind::Drum => Event::Drum(value),
        }
    }
}

/// The shift block: `steps` ids starting at `offset`, each worth `1 / steps_per_second`.
#[derive(Debug, Clone, Copy, Deserialize)]
struct ShiftRange {
    offset: u32,
    steps: u32,
    steps_per_second: u32,
}

/// The reserved ids at the bottom of the token space.
#[derive(Debug, Clone, Copy, Deserialize)]
struct SpecialTokens {
    pad: u32,
    eos: u32,
    #[allow(dead_code)]
    unk: u32,
}

/// Everything the decoder needs to know about the checkpoint it is talking to.
#[derive(Debug, Clone, Deserialize)]
pub struct Vocabulary {
    /// Rate the encoder expects, in Hz.
    pub sample_rate: u32,

    /// How much audio goes into one encoder pass.
    pub segment_seconds: f64,

    /// Samples in one segment, when the export knows better than `segment_seconds * rate`.
    ///
    /// It usually does: 2.048 s at 16 kHz rounds to 32768, but the checkpoint was trained on
    /// 32767, and that one sample is worth a whole extra encoder frame the decoder never saw.
    #[serde(default)]
    pub segment_samples: Option<usize>,

    /// Hard stop for the autoregressive loop, per segment.
    pub max_target_length: usize,

    /// Token that starts every decode. MT3 starts from pad.
    #[serde(default)]
    pub decoder_start_token: Option<u32>,

    special: SpecialTokens,
    shift: ShiftRange,
    ranges: Vec<EventRange>,
}

impl Vocabulary {
    /// Reads and validates a `vocab.json`.
    pub fn from_file(path: &str) -> Result<Self> {
        let text = std::fs::read_to_string(path).map_err(|e| match e.kind() {
            std::io::ErrorKind::NotFound => Mt3Error::ModelNotFound(path.to_string()),
            _ => Mt3Error::Io(e),
        })?;

        Self::from_json(&text)
    }

    /// Same, from an already-read string. Split out so the tests do not need files.
    pub fn from_json(text: &str) -> Result<Self> {
        let vocab: Self = serde_json::from_str(text).map_err(|e| Mt3Error::Vocab(e.to_string()))?;
        vocab.validate()?;
        Ok(vocab)
    }

    fn validate(&self) -> Result<()> {
        if self.sample_rate == 0 || self.segment_seconds <= 0.0 {
            return Err(Mt3Error::Vocab(
                "sample_rate and segment_seconds must be positive".into(),
            ));
        }
        if self.shift.steps_per_second == 0 || self.shift.steps == 0 {
            return Err(Mt3Error::Vocab("shift range is empty".into()));
        }
        if self.max_target_length == 0 {
            return Err(Mt3Error::Vocab("max_target_length must be positive".into()));
        }
        if !self.ranges.iter().any(|r| r.kind == RangeKind::Pitch) {
            return Err(Mt3Error::Vocab("no pitch range in vocabulary".into()));
        }

        // Overlapping ranges would silently mis-decode every token in the overlap, which is
        // exactly the kind of bug that looks like "the model is bad" instead of "the json is".
        let mut blocks: Vec<(u32, u32)> = self
            .ranges
            .iter()
            .map(|r| (r.offset, r.offset + r.len()))
            .collect();
        blocks.push((self.shift.offset, self.shift.offset + self.shift.steps));
        blocks.sort_unstable();

        for pair in blocks.windows(2) {
            if pair[0].1 > pair[1].0 {
                return Err(Mt3Error::Vocab(format!(
                    "token ranges overlap at {}..{} and {}..{}",
                    pair[0].0, pair[0].1, pair[1].0, pair[1].1
                )));
            }
        }

        Ok(())
    }

    /// Token the decoder is primed with. Falls back to pad, which is what MT3 uses.
    pub fn start_token(&self) -> u32 {
        self.decoder_start_token.unwrap_or(self.special.pad)
    }

    /// The end-of-sequence id, so the decode loop can stop without going through [`Self::decode`].
    pub fn eos_token(&self) -> u32 {
        self.special.eos
    }

    /// Seconds one shift step is worth.
    pub fn step_seconds(&self) -> f64 {
        1.0 / self.shift.steps_per_second as f64
    }

    /// How many velocity bins the checkpoint was trained with. 1 means on/off only, 127 means
    /// the bin *is* the MIDI velocity.
    pub fn velocity_bins(&self) -> u8 {
        self.ranges
            .iter()
            .find(|r| r.kind == RangeKind::Velocity)
            .map_or(1, |r| r.max.clamp(1, 127) as u8)
    }

    /// Samples in one segment at the model's rate — what the export said, or the rounded
    /// product if it did not say.
    pub fn segment_samples(&self) -> usize {
        self.segment_samples
            .unwrap_or_else(|| (self.segment_seconds * self.sample_rate as f64).round() as usize)
    }

    /// How far apart two segment starts are on the timeline. Derived from the sample count
    /// rather than `segment_seconds`, so the two cannot disagree and slowly skew every onset.
    pub fn segment_duration(&self) -> f64 {
        self.segment_samples() as f64 / self.sample_rate as f64
    }

    /// Turns a raw token id into an event, or `None` for pad/unk and anything out of range.
    ///
    /// Out-of-range is not an error: a model early in training happily emits garbage ids, and
    /// dropping them is what the reference implementation does too.
    pub fn decode(&self, token: u32) -> Option<Event> {
        if token == self.special.eos {
            return Some(Event::Eos);
        }
        if token >= self.shift.offset && token < self.shift.offset + self.shift.steps {
            return Some(Event::Shift(token - self.shift.offset));
        }

        self.ranges
            .iter()
            .find(|r| r.contains(token))
            .map(|r| r.decode(token))
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;

    /// Mirrors the default MT3 layout: 3 specials, 1001 shift steps, then the five ranges.
    pub(crate) const SAMPLE: &str = r#"{
        "sample_rate": 16000,
        "segment_seconds": 2.048,
        "max_target_length": 1024,
        "special": { "pad": 0, "eos": 1, "unk": 2 },
        "shift": { "offset": 3, "steps": 1001, "steps_per_second": 100 },
        "ranges": [
            { "kind": "pitch",    "offset": 1004, "min": 21, "max": 108 },
            { "kind": "velocity", "offset": 1092, "min": 0,  "max": 127 },
            { "kind": "tie",      "offset": 1220, "min": 0,  "max": 0   },
            { "kind": "program",  "offset": 1221, "min": 0,  "max": 127 },
            { "kind": "drum",     "offset": 1349, "min": 21, "max": 108 }
        ],
        "_vocab_size": 1437
    }"#;

    fn vocab() -> Vocabulary {
        Vocabulary::from_json(SAMPLE).expect("sample vocabulary should parse")
    }

    #[test]
    fn decodes_each_range_at_its_edges() {
        let v = vocab();

        assert_eq!(v.decode(1), Some(Event::Eos));
        assert_eq!(v.decode(3), Some(Event::Shift(0)));
        assert_eq!(v.decode(1003), Some(Event::Shift(1000)));
        assert_eq!(v.decode(1004), Some(Event::Pitch(21)));
        assert_eq!(v.decode(1091), Some(Event::Pitch(108)));
        assert_eq!(v.decode(1092), Some(Event::Velocity(0)));
        assert_eq!(v.decode(1219), Some(Event::Velocity(127)));
        assert_eq!(v.decode(1220), Some(Event::Tie));
        assert_eq!(v.decode(1221), Some(Event::Program(0)));
        assert_eq!(v.decode(1349), Some(Event::Drum(21)));
        assert_eq!(v.decode(1436), Some(Event::Drum(108)));
    }

    #[test]
    fn drops_pad_and_out_of_range_tokens() {
        let v = vocab();

        assert_eq!(v.decode(0), None);
        assert_eq!(v.decode(2), None);
        assert_eq!(v.decode(9999), None);
    }

    #[test]
    fn derived_numbers_match_the_json() {
        let v = vocab();

        assert_eq!(v.step_seconds(), 0.01);
        assert_eq!(v.segment_samples(), 32768);
        assert_eq!(v.start_token(), 0);
        assert_eq!(v.eos_token(), 1);
    }

    #[test]
    fn rejects_overlapping_ranges() {
        let broken = SAMPLE.replace(r#""offset": 1092"#, r#""offset": 1090"#);
        let err = Vocabulary::from_json(&broken).unwrap_err();

        assert!(matches!(err, Mt3Error::Vocab(msg) if msg.contains("overlap")));
    }

    #[test]
    fn rejects_a_vocabulary_without_pitches() {
        let broken = SAMPLE.replace(r#""kind": "pitch""#, r#""kind": "drum""#);
        let err = Vocabulary::from_json(&broken).unwrap_err();

        assert!(matches!(err, Mt3Error::Vocab(_)));
    }
}
