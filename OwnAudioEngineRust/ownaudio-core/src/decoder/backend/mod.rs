//! Decoder backend implementations and the backend-selection factory.
//!
//! Only the pure-Rust Symphonia backend lives here, and it is the only one in
//! the product - the managed layer's FFmpeg fallback went away in 4.0.  Keeps
//! the native binary free of any build- or load-time FFmpeg dependency, so it
//! loads on every platform out of the box.

pub(crate) mod resample;
pub(crate) mod symphonia_backend;

use crate::decoder::AudioDecoderBackend;
use crate::error::{AudioError, Result};

/// Opens `path` with the Symphonia decoder backend.
pub(crate) fn create_backend(
    path: &str,
    target_sample_rate: u32,
    target_channels: u32,
) -> Result<Box<dyn AudioDecoderBackend>> {
    match symphonia_backend::SymphoniaBackend::open(path, target_sample_rate, target_channels) {
        Ok(backend) => {
            log::info!("decoder: using Symphonia backend for '{path}'");
            Ok(Box::new(backend))
        }
        Err(e) => {
            log::error!("decoder: Symphonia backend failed for '{path}': {e}");
            Err(match e {
                AudioError::DecoderOpen(_)
                | AudioError::DecoderUnsupported(_)
                | AudioError::DecoderRead(_)
                | AudioError::DecoderSeek(_) => e,
                other => AudioError::DecoderOpen(other.to_string()),
            })
        }
    }
}
