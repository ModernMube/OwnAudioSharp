use cpal::traits::DeviceTrait;

use crate::error::{AudioError, Result};

/// Sample data format used in the audio stream.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SampleFormat {
    /// 32-bit IEEE float — preferred format for DSP work.
    F32,
    /// Signed 16-bit integer.
    I16,
    /// Unsigned 16-bit integer.
    U16,
}

/// Parameters for opening an audio stream.
#[derive(Debug, Clone)]
pub struct StreamConfig {
    /// Target sample rate in Hz (e.g. 44100, 48000).
    pub sample_rate: u32,
    /// Number of channels (1 = mono, 2 = stereo).
    pub channels: u16,
    /// Sample data format.
    pub sample_format: SampleFormat,
    /// Requested buffer size in frames; `None` lets Cpal pick the platform default.
    pub buffer_size_frames: Option<u32>,
}

impl StreamConfig {
    /// Constructs a sensible stereo F32 config for the given sample rate.
    pub fn stereo_f32(sample_rate: u32) -> Self {
        Self {
            sample_rate,
            channels: 2,
            sample_format: SampleFormat::F32,
            buffer_size_frames: None,
        }
    }
}

// ---------------------------------------------------------------------------
// Internal conversions — not part of the public API
// ---------------------------------------------------------------------------

/// Validates `config` against the output capabilities of `device` and returns
/// the Cpal representation if the config is supported.
pub(crate) fn validate_output_config(
    device: &cpal::Device,
    config: &StreamConfig,
) -> Result<(cpal::StreamConfig, cpal::SampleFormat)> {
    validate_config(device.supported_output_configs()?, config)
}

/// Validates `config` against the input capabilities of `device`, adapting the
/// channel count when the requested one is not supported.
///
/// Capture devices are frequently mono-only (built-in microphones) while the
/// engine runs a stereo session, so requiring an exact channel match would fail
/// to open a perfectly usable device. This picks the closest supported channel
/// count instead and reports it back, letting the caller remap captured frames
/// to `config.channels` with [`crate::format::remap_channels_into`].
///
/// Returns the Cpal stream config to open (whose `channels` is the device-native
/// count that will actually be captured), the sample format, and that same device
/// channel count.
pub(crate) fn validate_input_config_adaptive(
    device: &cpal::Device,
    config: &StreamConfig,
) -> Result<(cpal::StreamConfig, cpal::SampleFormat, u16)> {
    validate_config_adaptive(device.supported_input_configs()?, config)
}

fn validate_config(
    mut supported: impl Iterator<Item = cpal::SupportedStreamConfigRange>,
    config: &StreamConfig,
) -> Result<(cpal::StreamConfig, cpal::SampleFormat)> {
    let target_fmt = to_cpal_format(config.sample_format);
    // In cpal 0.18, SampleRate is a plain u32 type alias.
    let target_rate: u32 = config.sample_rate;

    let matched = supported.find(|r| {
        r.sample_format() == target_fmt
            && r.channels() == config.channels
            && r.min_sample_rate() <= target_rate
            && target_rate <= r.max_sample_rate()
    });

    match matched {
        Some(_range) => {
            let buffer_size = match config.buffer_size_frames {
                Some(frames) => cpal::BufferSize::Fixed(frames),
                None => cpal::BufferSize::Default,
            };
            Ok((
                cpal::StreamConfig {
                    channels: config.channels,
                    sample_rate: target_rate,
                    buffer_size,
                },
                target_fmt,
            ))
        }
        None => Err(AudioError::UnsupportedConfig(format!(
            "{}ch {}Hz {:?} not supported by device",
            config.channels, config.sample_rate, config.sample_format
        ))),
    }
}

fn validate_config_adaptive(
    supported: impl Iterator<Item = cpal::SupportedStreamConfigRange>,
    config: &StreamConfig,
) -> Result<(cpal::StreamConfig, cpal::SampleFormat, u16)> {
    let target_fmt = to_cpal_format(config.sample_format);
    let target_rate: u32 = config.sample_rate;

    // Keep every range matching the format and sample rate; the channel count is
    // adapted afterwards, so a mono-only device still satisfies a stereo request.
    let candidates: Vec<cpal::SupportedStreamConfigRange> = supported
        .filter(|r| {
            r.sample_format() == target_fmt
                && r.min_sample_rate() <= target_rate
                && target_rate <= r.max_sample_rate()
        })
        .collect();

    // Prefer an exact channel match; otherwise take the closest count, breaking
    // ties toward fewer channels (mono upmix beats padding extra silent channels).
    let chosen = candidates
        .iter()
        .find(|r| r.channels() == config.channels)
        .or_else(|| {
            candidates.iter().min_by_key(|r| {
                let ch = r.channels();
                let dist = (ch as i32 - config.channels as i32).unsigned_abs();
                (dist, ch)
            })
        });

    match chosen {
        Some(range) => {
            let device_channels = range.channels();
            let buffer_size = match config.buffer_size_frames {
                Some(frames) => cpal::BufferSize::Fixed(frames),
                None => cpal::BufferSize::Default,
            };
            Ok((
                cpal::StreamConfig {
                    channels: device_channels,
                    sample_rate: target_rate,
                    buffer_size,
                },
                target_fmt,
                device_channels,
            ))
        }
        None => Err(AudioError::UnsupportedConfig(format!(
            "{}ch {}Hz {:?} not supported by device",
            config.channels, config.sample_rate, config.sample_format
        ))),
    }
}

pub(crate) fn to_cpal_format(fmt: SampleFormat) -> cpal::SampleFormat {
    match fmt {
        SampleFormat::F32 => cpal::SampleFormat::F32,
        SampleFormat::I16 => cpal::SampleFormat::I16,
        SampleFormat::U16 => cpal::SampleFormat::U16,
    }
}
