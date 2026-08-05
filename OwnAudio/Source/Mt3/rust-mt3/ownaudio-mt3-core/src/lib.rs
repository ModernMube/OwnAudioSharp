//! MT3 music transcription for OwnAudioSharp — audio in, notes with instrument labels out.
//!
//! Unlike the BasicPitch path this crate replaces for chord detection, MT3 is a sequence-to-
//! sequence transformer: the encoder turns a two-second slice of audio into hidden states, and the
//! decoder autoregressively spits out MIDI-like events, including which instrument played what.
//! That instrument split is the point — a bass run and a piano voicing no longer collapse into one
//! smear on the chromagram.
//!
//! The weights are not shipped with the library. Point [`ModelPaths`] at an exported encoder,
//! decoder pair and the `vocab.json` that came out of the same export.
//!
//! The token codec and the note state machine build without ONNX Runtime (`--no-default-features`),
//! which is how they stay unit-tested on platforms that have no runtime binaries.

pub mod error;
pub mod events;
pub mod preprocess;
pub mod vocab;

#[cfg(feature = "inference")]
mod session;
#[cfg(feature = "inference")]
mod transcriber;

pub use error::{Mt3Error, Result};
pub use events::{Note, NoteDecoder};
pub use vocab::{Event, Vocabulary};

#[cfg(feature = "inference")]
pub use transcriber::{ModelPaths, Mt3Transcriber, TranscribeOptions};
