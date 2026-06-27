//! # ownaudio-soundtouch
//!
//! Self-contained Rust port of the C# **SoundTouch.Net** library
//! (`OwnAudio/Source/SoundTouch/`): WSOLA time-stretch, pitch-shift and
//! sample-rate transposition for the OwnAudioSharp Rust refactor.
//!
//! The crate is deliberately isolated from `ownaudio-core` — it has no external
//! dependencies and no coupling to the engine — so it can be embedded wherever
//! tempo/pitch/rate processing is needed and tested in isolation.
//!
//! ## Pipeline
//!
//! ```text
//! input ─▶ RateTransposer (resample + anti-alias) ─▶ TimeStretch (WSOLA) ─▶ output
//! ```
//!
//! The order of the two stages flips depending on whether the effective rate is
//! below or above unity, exactly as in the C# original, to minimise latency and
//! avoid clicks at the rate-crossover point.
//!
//! ## Real-time safety
//!
//! Every working buffer is sized at configuration time
//! ([`SoundTouchProcessor::set_sample_rate`], [`SoundTouchProcessor::set_channels`],
//! tempo/pitch/rate changes).  [`SoundTouchProcessor::put_samples`] and
//! [`SoundTouchProcessor::receive_samples`] perform no heap allocation in steady
//! state and never panic — invalid configuration is reported through
//! [`error::ErrorCode`] instead of exceptions.
//!
//! ## Quick start
//!
//! ```
//! use ownaudio_soundtouch::SoundTouchProcessor;
//!
//! let mut st = SoundTouchProcessor::new();
//! st.set_sample_rate(48_000).unwrap();
//! st.set_channels(2).unwrap();
//! st.set_tempo(1.25);            // 25 % faster, same pitch
//! st.set_pitch_semitones(3.0);   // +3 semitones, same tempo
//!
//! let input = vec![0.0f32; 4096 * 2];
//! st.put_samples(&input, 4096).unwrap();
//!
//! let mut out = vec![0.0f32; 4096 * 2];
//! let frames = st.receive_samples(&mut out, 4096);
//! let _ = frames;
//! ```

pub mod bpm_detect;
pub mod error;
pub mod fifo_buffer;
pub mod filter;
pub mod interpolate;
pub mod processor;
pub mod rate_transposer;
pub mod time_stretch;

pub use bpm_detect::BpmDetect;
pub use error::{ErrorCode, StResult};
pub use fifo_buffer::FifoSampleBuffer;
pub use processor::{SettingId, SoundTouchProcessor};
pub use rate_transposer::RateTransposer;
pub use time_stretch::TimeStretch;

/// Maximum supported channel count (`SOUNDTOUCH_MAX_CHANNELS`).
pub const MAX_CHANNELS: usize = 16;

/// Default processing parameters (port of `Defaults.cs`).
pub mod defaults {
    /// Automatic sequence-length sentinel.
    pub const USE_AUTO_SEQUENCE_LEN: usize = 0;
    /// Automatic seek-window-length sentinel.
    pub const USE_AUTO_SEEKWINDOW_LEN: usize = 0;
    /// Default processing sequence length (auto).
    pub const SEQUENCE_MS: usize = USE_AUTO_SEQUENCE_LEN;
    /// Default seek-window length (auto).
    pub const SEEKWINDOW_MS: usize = USE_AUTO_SEEKWINDOW_LEN;
    /// Default sequence overlap length in milliseconds.
    pub const OVERLAP_MS: usize = 8;
}
