use std::sync::Arc;

use cpal::traits::DeviceTrait;

use crate::{
    config::{validate_input_config_adaptive, validate_output_config, StreamConfig},
    device::{resolve_input_device, resolve_output_device, AudioDeviceInfo},
    error::{AudioError, Result},
    stream::{InputStream, OutputStream},
    stream_error::StreamErrorState,
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
        Ok(AudioEngine {
            host: cpal::default_host(),
        })
    }

    /// Creates a new engine instance using an explicitly provided `cpal::Host`.
    ///
    /// This is used by the FFI layer to select non-default host APIs such as ASIO
    /// on Windows.  Prefer [`AudioEngine::new`] unless a specific host is required.
    pub fn new_with_host(host: cpal::Host) -> Result<Self> {
        Ok(AudioEngine { host })
    }

    /// Returns a list of all available output devices on this engine's host.
    ///
    /// Unlike [`crate::device::list_output_devices`], which always queries the
    /// platform default host, this respects the host the engine was created
    /// with, so an ASIO engine lists ASIO devices rather than WASAPI endpoints.
    pub fn list_output_devices(&self) -> Result<Vec<AudioDeviceInfo>> {
        crate::device::list_output_devices_on(&self.host)
    }

    /// Returns a list of all available input devices on this engine's host.
    ///
    /// Unlike [`crate::device::list_input_devices`], which always queries the
    /// platform default host, this respects the host the engine was created
    /// with, so an ASIO engine lists ASIO devices rather than WASAPI endpoints.
    pub fn list_input_devices(&self) -> Result<Vec<AudioDeviceInfo>> {
        crate::device::list_input_devices_on(&self.host)
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

        // Shared error state: the cpal error callback records device-lost /
        // backend failures here, and the returned stream hands the same Arc to
        // the control side so it can poll and surface the fault instead of it
        // being lost to an eprintln. The closure is moved into exactly one of the
        // mutually-exclusive sample-format arms below.
        let error_state = Arc::new(StreamErrorState::new());
        let err_fn = {
            let error_state = Arc::clone(&error_state);
            move |e: cpal::Error| {
                error_state.record(&e);
                eprintln!("[ownaudio-core] output stream error: {e}");
            }
        };

        // Pre-allocate the f32 conversion buffer once, at stream-init time.  If
        // buffer_size_frames is known we size it exactly; otherwise we use a
        // conservative upper bound (4096 frames × channels) that fits any
        // realistic callback buffer.  In the callback we only ever take a
        // sub-slice (`tmp[..data.len()]`); the buffer is grown solely if the OS
        // hands us a larger buffer than anticipated — a one-time amortized cost,
        // never a per-callback allocation in steady state.
        let pre_alloc =
            config.buffer_size_frames.unwrap_or(4096) as usize * config.channels as usize;

        // cpal 0.18: build_*_stream takes StreamConfig by value (it is Copy).
        //
        // Every data callback body is wrapped in `rt_guard::guard_output` so a
        // panic (from the user/FFI callback or a format-conversion bug) can
        // never unwind across the cpal/C audio-thread frame (UB).  On panic the
        // device buffer is filled with per-format silence.
        let stream = match sample_format {
            cpal::SampleFormat::F32 => cpal_device.build_output_stream(
                stream_config,
                move |data: &mut [f32], _| {
                    crate::rt_guard::guard_output(data, 0.0, |buf| callback(buf));
                },
                err_fn,
                None,
            )?,
            cpal::SampleFormat::I16 => {
                let mut tmp = vec![0f32; pre_alloc];
                cpal_device.build_output_stream(
                    stream_config,
                    move |data: &mut [i16], _| {
                        crate::rt_guard::guard_output(data, 0i16, |data| {
                            if data.len() > tmp.len() {
                                tmp.resize(data.len(), 0.0);
                            }
                            let buf = &mut tmp[..data.len()];
                            callback(buf);
                            crate::format::f32_to_i16(buf, data);
                        });
                    },
                    err_fn,
                    None,
                )?
            }
            cpal::SampleFormat::I32 => {
                let mut tmp = vec![0f32; pre_alloc];
                cpal_device.build_output_stream(
                    stream_config,
                    move |data: &mut [i32], _| {
                        crate::rt_guard::guard_output(data, 0i32, |data| {
                            if data.len() > tmp.len() {
                                tmp.resize(data.len(), 0.0);
                            }
                            let buf = &mut tmp[..data.len()];
                            callback(buf);
                            crate::format::f32_to_i32(buf, data);
                        });
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
                        // u16 silence is the mid-point (32768), not 0.
                        crate::rt_guard::guard_output(data, 32768u16, |data| {
                            if data.len() > tmp.len() {
                                tmp.resize(data.len(), 0.0);
                            }
                            let buf = &mut tmp[..data.len()];
                            callback(buf);
                            crate::format::f32_to_u16(buf, data);
                        });
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

        Ok(OutputStream {
            stream,
            error_state,
        })
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
        let (stream_config, sample_format, device_channels) =
            validate_input_config_adaptive(&cpal_device, config)?;

        // See `open_output_stream` for the rationale; the input path mirrors it.
        let error_state = Arc::new(StreamErrorState::new());
        let err_fn = {
            let error_state = Arc::clone(&error_state);
            move |e: cpal::Error| {
                error_state.record(&e);
                eprintln!("[ownaudio-core] input stream error: {e}");
            }
        };

        // `tmp` (used by the integer paths below) holds device-native frames, so
        // size it by the device channel count; `remap_scratch` holds the adapted
        // output, so size it by the requested count.
        let frames_hint = config.buffer_size_frames.unwrap_or(4096) as usize;
        let pre_alloc = frames_hint * device_channels as usize;

        // Adapt the device-native channel count to the requested one before the
        // samples reach the user callback, so a mono-only capture device still
        // feeds a stereo stream (and vice versa). Pre-sized so the remap allocates
        // nothing on the audio thread once running; an exact match copies nothing.
        let src_channels = device_channels as usize;
        let dst_channels = config.channels as usize;
        let mut remap_scratch: Vec<f32> = Vec::with_capacity(frames_hint * dst_channels);
        let mut adapted = move |data: &[f32]| {
            if src_channels == dst_channels {
                callback(data);
            } else {
                crate::format::remap_channels_into(
                    data,
                    src_channels,
                    dst_channels,
                    &mut remap_scratch,
                );
                callback(&remap_scratch);
            }
        };

        // Each input callback body is wrapped in `rt_guard::guard_input` so a
        // panic cannot unwind across the cpal/C audio-thread frame (UB).  Input
        // has no output buffer to sanitise, so the panic is simply swallowed.
        let stream = match sample_format {
            cpal::SampleFormat::F32 => cpal_device.build_input_stream(
                stream_config,
                move |data: &[f32], _| {
                    crate::rt_guard::guard_input(|| adapted(data));
                },
                err_fn,
                None,
            )?,
            cpal::SampleFormat::I16 => {
                let mut tmp = vec![0f32; pre_alloc];
                cpal_device.build_input_stream(
                    stream_config,
                    move |data: &[i16], _| {
                        crate::rt_guard::guard_input(|| {
                            if data.len() > tmp.len() {
                                tmp.resize(data.len(), 0.0);
                            }
                            let buf = &mut tmp[..data.len()];
                            crate::format::i16_to_f32(data, buf);
                            adapted(buf);
                        });
                    },
                    err_fn,
                    None,
                )?
            }
            cpal::SampleFormat::I32 => {
                let mut tmp = vec![0f32; pre_alloc];
                cpal_device.build_input_stream(
                    stream_config,
                    move |data: &[i32], _| {
                        crate::rt_guard::guard_input(|| {
                            if data.len() > tmp.len() {
                                tmp.resize(data.len(), 0.0);
                            }
                            let buf = &mut tmp[..data.len()];
                            crate::format::i32_to_f32(data, buf);
                            adapted(buf);
                        });
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
                        crate::rt_guard::guard_input(|| {
                            if data.len() > tmp.len() {
                                tmp.resize(data.len(), 0.0);
                            }
                            let buf = &mut tmp[..data.len()];
                            crate::format::u16_to_f32(data, buf);
                            adapted(buf);
                        });
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

        Ok(InputStream {
            stream,
            error_state,
        })
    }
}

impl Default for AudioEngine {
    fn default() -> Self {
        AudioEngine {
            host: cpal::default_host(),
        }
    }
}
