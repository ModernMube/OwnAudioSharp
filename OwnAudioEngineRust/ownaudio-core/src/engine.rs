use cpal::traits::DeviceTrait;

use crate::{
    config::{validate_input_config, validate_output_config, StreamConfig},
    device::{resolve_input_device, resolve_output_device, AudioDeviceInfo},
    error::{AudioError, Result},
    stream::{InputStream, OutputStream},
};

/// Entry point for all audio I/O operations.
///
/// `AudioEngine` holds a `cpal::Host` that determines which OS audio backend
/// is used for device enumeration and stream creation.  Use [`AudioEngine::new`]
/// for the platform default host (WASAPI on Windows, CoreAudio on macOS, ALSA on Linux),
/// or [`AudioEngine::new_with_host`] to select a specific host explicitly.
pub struct AudioEngine {
    host: cpal::Host,
}

impl AudioEngine {
    /// Creates a new engine instance using the platform default audio host.
    ///
    /// This call is cheap and does not open any audio device.
    pub fn new() -> Result<Self> {
        Ok(AudioEngine { host: cpal::default_host() })
    }

    /// Creates a new engine instance using an explicitly provided `cpal::Host`.
    ///
    /// This is used by the FFI layer to select non-default host APIs such as ASIO
    /// on Windows.  Prefer [`AudioEngine::new`] unless a specific host is required.
    pub fn new_with_host(host: cpal::Host) -> Result<Self> {
        Ok(AudioEngine { host })
    }

    /// Opens an output stream on the given device (or the system default if
    /// `device` is `None`).
    ///
    /// `callback` is invoked on the audio real-time thread for every buffer.
    /// It receives a mutable slice of interleaved f32 samples that it must
    /// fill completely.
    ///
    /// # Real-time safety
    /// The callback runs on a high-priority OS thread. It must **not**:
    /// - allocate heap memory (`Vec::new`, `String`, `Box`, etc.)
    /// - acquire a non-real-time-safe lock
    /// - perform blocking I/O
    pub fn open_output_stream(
        &self,
        device: Option<&AudioDeviceInfo>,
        config: &StreamConfig,
        mut callback: impl FnMut(&mut [f32]) + Send + 'static,
    ) -> Result<OutputStream> {
        let cpal_device = resolve_output_device(&self.host, device.map(|d| d.name.as_str()))?;
        let (stream_config, sample_format) = validate_output_config(&cpal_device, config)?;

        let err_fn = |e| eprintln!("[ownaudio-core] output stream error: {e}");

        // Pre-allocate the f32 conversion buffer once, at stream-init time.  If
        // buffer_size_frames is known we size it exactly; otherwise we use a
        // conservative upper bound (4096 frames × channels) that fits any
        // realistic callback buffer.  In the callback we only ever take a
        // sub-slice (`tmp[..data.len()]`); the buffer is grown solely if the OS
        // hands us a larger buffer than anticipated — a one-time amortized cost,
        // never a per-callback allocation in steady state.
        let pre_alloc = config
            .buffer_size_frames
            .unwrap_or(4096) as usize
            * config.channels as usize;

        // cpal 0.18: build_*_stream takes StreamConfig by value (it is Copy).
        let stream = match sample_format {
            cpal::SampleFormat::F32 => cpal_device.build_output_stream(
                stream_config,
                move |data: &mut [f32], _| callback(data),
                err_fn,
                None,
            )?,
            cpal::SampleFormat::I16 => {
                let mut tmp = vec![0f32; pre_alloc];
                cpal_device.build_output_stream(
                    stream_config,
                    move |data: &mut [i16], _| {
                        if data.len() > tmp.len() {
                            tmp.resize(data.len(), 0.0);
                        }
                        let buf = &mut tmp[..data.len()];
                        callback(buf);
                        crate::format::f32_to_i16(buf, data);
                    },
                    err_fn,
                    None,
                )?
            }
            cpal::SampleFormat::U16 => {
                let mut tmp = vec![0f32; pre_alloc];
                cpal_device.build_output_stream(
                    stream_config,
                    move |data: &mut [u16], _| {
                        if data.len() > tmp.len() {
                            tmp.resize(data.len(), 0.0);
                        }
                        let buf = &mut tmp[..data.len()];
                        callback(buf);
                        crate::format::f32_to_u16(buf, data);
                    },
                    err_fn,
                    None,
                )?
            }
            other => {
                return Err(AudioError::UnsupportedConfig(format!(
                    "unhandled sample format: {other:?}"
                )))
            }
        };

        Ok(OutputStream { stream })
    }

    /// Opens an input stream on the given device (or the system default if
    /// `device` is `None`).
    ///
    /// `callback` receives a read-only slice of interleaved f32 samples for
    /// each captured buffer.
    ///
    /// # Real-time safety
    /// Same constraints apply as for [`open_output_stream`].
    pub fn open_input_stream(
        &self,
        device: Option<&AudioDeviceInfo>,
        config: &StreamConfig,
        mut callback: impl FnMut(&[f32]) + Send + 'static,
    ) -> Result<InputStream> {
        let cpal_device = resolve_input_device(&self.host, device.map(|d| d.name.as_str()))?;
        let (stream_config, sample_format) = validate_input_config(&cpal_device, config)?;

        let err_fn = |e| eprintln!("[ownaudio-core] input stream error: {e}");

        let pre_alloc = config
            .buffer_size_frames
            .unwrap_or(4096) as usize
            * config.channels as usize;

        let stream = match sample_format {
            cpal::SampleFormat::F32 => cpal_device.build_input_stream(
                stream_config,
                move |data: &[f32], _| callback(data),
                err_fn,
                None,
            )?,
            cpal::SampleFormat::I16 => {
                let mut tmp = vec![0f32; pre_alloc];
                cpal_device.build_input_stream(
                    stream_config,
                    move |data: &[i16], _| {
                        if data.len() > tmp.len() {
                            tmp.resize(data.len(), 0.0);
                        }
                        let buf = &mut tmp[..data.len()];
                        crate::format::i16_to_f32(data, buf);
                        callback(buf);
                    },
                    err_fn,
                    None,
                )?
            }
            cpal::SampleFormat::U16 => {
                let mut tmp = vec![0f32; pre_alloc];
                cpal_device.build_input_stream(
                    stream_config,
                    move |data: &[u16], _| {
                        if data.len() > tmp.len() {
                            tmp.resize(data.len(), 0.0);
                        }
                        let buf = &mut tmp[..data.len()];
                        crate::format::u16_to_f32(data, buf);
                        callback(buf);
                    },
                    err_fn,
                    None,
                )?
            }
            other => {
                return Err(AudioError::UnsupportedConfig(format!(
                    "unhandled sample format: {other:?}"
                )))
            }
        };

        Ok(InputStream { stream })
    }
}

impl Default for AudioEngine {
    fn default() -> Self {
        AudioEngine { host: cpal::default_host() }
    }
}
