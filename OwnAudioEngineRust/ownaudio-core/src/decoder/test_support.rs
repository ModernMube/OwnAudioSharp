//! Test-only helpers shared across the decoder unit tests.

use std::io::Write;
use std::path::PathBuf;

/// Writes a 16-bit PCM WAV file with the given interleaved `i16` samples and
/// returns its path.  The file is created under the OS temp directory with a
/// unique name and is removed by [`TempWav`]'s `Drop`.
pub(crate) struct TempWav {
    pub path: PathBuf,
}

impl TempWav {
    /// Creates a mono or multi-channel 16-bit PCM WAV from interleaved samples.
    pub(crate) fn write(channels: u16, sample_rate: u32, interleaved: &[i16]) -> Self {
        use std::sync::atomic::{AtomicU64, Ordering};
        static COUNTER: AtomicU64 = AtomicU64::new(0);
        let id = COUNTER.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!(
            "ownaudio_test_{}_{}.wav",
            std::process::id(),
            id
        ));

        let data_len = (interleaved.len() * 2) as u32;
        let byte_rate = sample_rate * channels as u32 * 2;
        let block_align = channels * 2;

        let mut buf: Vec<u8> = Vec::with_capacity(44 + data_len as usize);
        buf.extend_from_slice(b"RIFF");
        buf.extend_from_slice(&(36 + data_len).to_le_bytes());
        buf.extend_from_slice(b"WAVE");
        buf.extend_from_slice(b"fmt ");
        buf.extend_from_slice(&16u32.to_le_bytes());
        buf.extend_from_slice(&1u16.to_le_bytes()); // PCM
        buf.extend_from_slice(&channels.to_le_bytes());
        buf.extend_from_slice(&sample_rate.to_le_bytes());
        buf.extend_from_slice(&byte_rate.to_le_bytes());
        buf.extend_from_slice(&block_align.to_le_bytes());
        buf.extend_from_slice(&16u16.to_le_bytes()); // bits per sample
        buf.extend_from_slice(b"data");
        buf.extend_from_slice(&data_len.to_le_bytes());
        for &s in interleaved {
            buf.extend_from_slice(&s.to_le_bytes());
        }

        let mut file = std::fs::File::create(&path).expect("create temp wav");
        file.write_all(&buf).expect("write temp wav");
        file.flush().expect("flush temp wav");

        TempWav { path }
    }

    /// The file path as a `&str`.
    pub(crate) fn path_str(&self) -> &str {
        self.path.to_str().unwrap()
    }
}

impl Drop for TempWav {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.path);
    }
}

/// Generates a mono ramp of `frames` samples from 0 upward (wrapping), useful
/// for verifying sample-accurate decode and seek positions.
pub(crate) fn mono_ramp(frames: usize) -> Vec<i16> {
    (0..frames).map(|i| ((i % 1000) as i16) * 30).collect()
}

/// Generates a strictly increasing mono ramp where sample value equals the
/// frame index.  Requires `frames < i16::MAX` so each value maps uniquely back
/// to its frame — used for seek-accuracy verification.
pub(crate) fn linear_ramp(frames: usize) -> Vec<i16> {
    assert!(frames < i16::MAX as usize);
    (0..frames).map(|i| i as i16).collect()
}
