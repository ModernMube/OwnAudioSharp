//! # ownaudio-core
//!
//! Cross-platform audio I/O library for OwnAudioSharp, built on top of [cpal].
//!
//! This crate provides the first layer of the OwnAudioSharp Rust refactor:
//! raw audio device enumeration and stream management.  DSP, mixing, and
//! ring buffers will be added in subsequent layers.
//!
//! ## Quick start
//!
//! ```no_run
//! use ownaudio_core::{AudioEngine, StreamConfig};
//!
//! let engine = AudioEngine::new().unwrap();
//! let config = StreamConfig::stereo_f32(48_000);
//!
//! let stream = engine
//!     .open_output_stream(None, &config, |buf| {
//!         buf.fill(0.0); // silence
//!     })
//!     .unwrap();
//!
//! stream.play().unwrap();
//! std::thread::sleep(std::time::Duration::from_secs(1));
//! // stream is stopped and destroyed when dropped
//! ```

pub mod config;
pub mod decoder;
pub mod denormal;
pub mod device;
pub mod effects;
pub mod engine;
pub mod error;
pub mod format;
pub mod mixer;
pub mod multitrack;
pub mod resampler;
pub mod ringbuffer;
pub(crate) mod rt_guard;
pub mod smoothing;
pub mod stream;
pub mod stream_error;

// Flat re-exports for convenient use without module paths.
pub use config::{SampleFormat, StreamConfig};
pub use decoder::{open_streaming, AudioStreamInfo, DecoderReadResult, StreamingTrack};
pub use device::{
    default_input_device, default_output_device, list_input_devices, list_input_devices_on,
    list_output_devices, list_output_devices_on, AudioDeviceInfo,
};
pub use effects::{Effect, EffectChain, EffectType};
pub use engine::AudioEngine;
pub use error::{AudioError, Result};
pub use mixer::Mixer;
pub use multitrack::{
    FileSourceControl, FileTrackSource, MemorySourceControl, MemoryTrackSource, MixerShared,
    MultiTrackMixer, SampleClock, Track, TrackShared, TrackSource, TrackState,
};
pub use resampler::Resampler;
pub use ringbuffer::{ring_buffer, RingBufferReader, RingBufferWriter};
pub use smoothing::SmoothedParam;
pub use stream::{InputStream, OutputStream};
pub use stream_error::{StreamErrorKind, StreamErrorState};
