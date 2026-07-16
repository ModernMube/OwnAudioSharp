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
    /// Signed 32-bit integer — the native wire format of many professional
    /// ASIO drivers (e.g. RME `Int32LSB`).
    I32,
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

/// Sample formats the stream builder can service, in descending order of
/// preference when the requested format is not offered by the device.
///
/// The engine converts every stream to/from f32 in the callback, so any of
/// these is transparent to the caller; F32 is preferred (no conversion), then
/// the integer formats by decreasing bit depth.
const FORMAT_FALLBACK_ORDER: [cpal::SampleFormat; 4] = [
    cpal::SampleFormat::F32,
    cpal::SampleFormat::I32,
    cpal::SampleFormat::I16,
    cpal::SampleFormat::U16,
];

/// Picks the sample format to open the stream with: the requested format when
/// the device offers it, otherwise the best convertible format the device does
/// offer (see [`FORMAT_FALLBACK_ORDER`]).
///
/// This makes format selection adaptive the same way channel selection already
/// is: ASIO drivers typically expose only their native wire format (RME
/// reports `Int32LSB` → `I32`), which would otherwise reject a perfectly
/// usable device just because the caller asked for the default F32.
fn choose_format(
    candidates: &[cpal::SupportedStreamConfigRange],
    requested: cpal::SampleFormat,
) -> Option<cpal::SampleFormat> {
    if candidates.iter().any(|r| r.sample_format() == requested) {
        return Some(requested);
    }
    FORMAT_FALLBACK_ORDER
        .iter()
        .copied()
        .find(|fmt| candidates.iter().any(|r| r.sample_format() == *fmt))
}

fn validate_config(
    supported: impl Iterator<Item = cpal::SupportedStreamConfigRange>,
    config: &StreamConfig,
) -> Result<(cpal::StreamConfig, cpal::SampleFormat)> {
    let target_fmt = to_cpal_format(config.sample_format);
    // In cpal 0.18, SampleRate is a plain u32 type alias.
    let target_rate: u32 = config.sample_rate;

    // Keep every range matching the channel count and sample rate in any
    // format the engine can convert; the format is adapted afterwards.
    let candidates: Vec<cpal::SupportedStreamConfigRange> = supported
        .filter(|r| {
            FORMAT_FALLBACK_ORDER.contains(&r.sample_format())
                && r.channels() == config.channels
                && r.min_sample_rate() <= target_rate
                && target_rate <= r.max_sample_rate()
        })
        .collect();

    match choose_format(&candidates, target_fmt) {
        Some(chosen_fmt) => {
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
                chosen_fmt,
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

    // Keep every range matching the sample rate in any convertible format; the
    // format and channel count are adapted afterwards, so a mono-only or
    // I32-only device still satisfies a stereo F32 request.
    let candidates: Vec<cpal::SupportedStreamConfigRange> = supported
        .filter(|r| {
            FORMAT_FALLBACK_ORDER.contains(&r.sample_format())
                && r.min_sample_rate() <= target_rate
                && target_rate <= r.max_sample_rate()
        })
        .collect();

    let Some(chosen_fmt) = choose_format(&candidates, target_fmt) else {
        return Err(AudioError::UnsupportedConfig(format!(
            "{}ch {}Hz {:?} not supported by device",
            config.channels, config.sample_rate, config.sample_format
        )));
    };

    // Prefer an exact channel match; otherwise take the closest count, breaking
    // ties toward fewer channels (mono upmix beats padding extra silent channels).
    let chosen = candidates
        .iter()
        .filter(|r| r.sample_format() == chosen_fmt)
        .find(|r| r.channels() == config.channels)
        .or_else(|| {
            candidates
                .iter()
                .filter(|r| r.sample_format() == chosen_fmt)
                .min_by_key(|r| {
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
                chosen_fmt,
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
        SampleFormat::I32 => cpal::SampleFormat::I32,
    }
}
