use cpal::traits::DeviceTrait;

use crate::{
    config::{validate_input_config, validate_output_config, StreamConfig},
    device::{resolve_input_device, resolve_output_device, AudioDeviceInfo},
    error::{AudioError, Result},
    stream::{InputStream, OutputStream},
};

/// Entry point for all audio I/O operations.
///
/// `AudioEngine` itself holds no OS resources — resources are allocated when
/// a stream is opened and released when the returned stream is dropped.
pub struct AudioEngine;

impl AudioEngine {
    /// Creates a new engine instance.
    ///
    /// This call is cheap and does not open any audio device.
    pub fn new() -> Result<Self> {
        Ok(AudioEngine)
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
        let cpal_device = resolve_output_device(device.map(|d| d.name.as_str()))?;
        let (stream_config, sample_format) = validate_output_config(&cpal_device, config)?;

        let err_fn = |e| eprintln!("[ownaudio-core] output stream error: {e}");

        // Pre-allocate the f32 conversion buffer.  If buffer_size_frames is
        // known we size it exactly; otherwise we use a conservative upper bound
        // (4096 frames × channels) that fits any realistic callback buffer.
        // On the first callback the buffer is resized once if the OS chose a
        // different size; after that the real-time path is allocation-free.
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
                        if tmp.len() != data.len() {
                            tmp.resize(data.len(), 0.0);
                        }
                        callback(&mut tmp);
                        crate::format::f32_to_i16(&tmp, data);
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
                        if tmp.len() != data.len() {
                            tmp.resize(data.len(), 0.0);
                        }
                        callback(&mut tmp);
                        crate::format::f32_to_u16(&tmp, data);
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
        let cpal_device = resolve_input_device(device.map(|d| d.name.as_str()))?;
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
                        if tmp.len() != data.len() {
                            tmp.resize(data.len(), 0.0);
                        }
                        crate::format::i16_to_f32(data, &mut tmp);
                        callback(&tmp);
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
                        if tmp.len() != data.len() {
                            tmp.resize(data.len(), 0.0);
                        }
                        crate::format::u16_to_f32(data, &mut tmp);
                        callback(&tmp);
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
        AudioEngine
    }
}
