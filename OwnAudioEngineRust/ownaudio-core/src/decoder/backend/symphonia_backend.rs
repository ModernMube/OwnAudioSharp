//! Pure-Rust streaming decoder built on [Symphonia](https://docs.rs/symphonia).
//!
//! Supports the formats enabled in `Cargo.toml`: WAV, MP3, FLAC, OGG/Vorbis,
//! AAC/M4A and AIFF.  This backend is always compiled in and serves as the
//! fallback whenever the FFmpeg backend is unavailable.

use std::fs::File;
use std::path::Path;

use symphonia::core::audio::SampleBuffer;
use symphonia::core::codecs::{Decoder, DecoderOptions, CODEC_TYPE_NULL};
use symphonia::core::errors::Error as SymError;
use symphonia::core::formats::{FormatOptions, FormatReader, SeekMode, SeekTo};
use symphonia::core::io::MediaSourceStream;
use symphonia::core::meta::MetadataOptions;
use symphonia::core::probe::Hint;
use symphonia::core::units::Time;

use crate::decoder::stream_info::{AudioStreamInfo, DecoderReadResult};
use crate::decoder::AudioDecoderBackend;
use crate::error::{AudioError, Result};

use super::resample::StreamResampler;

/// Symphonia-backed streaming decoder.
pub(crate) struct SymphoniaBackend {
    format: Box<dyn FormatReader>,
    decoder: Box<dyn Decoder>,
    track_id: u32,

    source_channels: usize,
    output_channels: usize,
    output_rate: u32,

    stream_info: AudioStreamInfo,

    /// Reused interleaved-`f32` view over the most recently decoded packet.
    sample_buf: Option<SampleBuffer<f32>>,

    /// Optional sample-rate converter, present only when the requested output
    /// rate differs from the source rate.
    resampler: Option<StreamResampler>,

    /// Decoded, fully converted (channel + rate) interleaved samples that have
    /// not yet been handed to the caller.
    residual: Vec<f32>,
    /// Read cursor into `residual`.
    residual_pos: usize,

    /// Number of output frames produced so far (drives the PTS).
    output_frame_pos: u64,
    /// `true` once the source stream is exhausted.
    eof: bool,

    /// Source frames still to be discarded after a coarse seek so the output is
    /// sample-accurate (`required_ts - actual_ts` from the last seek).
    skip_source_frames: u64,
}

impl SymphoniaBackend {
    pub(crate) fn open(path: &str, target_sample_rate: u32, target_channels: u32) -> Result<Self> {
        let file = File::open(path)
            .map_err(|e| AudioError::DecoderOpen(format!("{path}: {e}")))?;
        let mss = MediaSourceStream::new(Box::new(file), Default::default());

        let mut hint = Hint::new();
        if let Some(ext) = Path::new(path).extension().and_then(|e| e.to_str()) {
            hint.with_extension(ext);
        }

        let fmt_opts = FormatOptions {
            enable_gapless: true,
            ..Default::default()
        };
        let meta_opts = MetadataOptions::default();

        let probed = symphonia::default::get_probe()
            .format(&hint, mss, &fmt_opts, &meta_opts)
            .map_err(map_sym_err_open)?;

        let format = probed.format;

        let track = format
            .tracks()
            .iter()
            .find(|t| t.codec_params.codec != CODEC_TYPE_NULL)
            .ok_or_else(|| {
                AudioError::DecoderUnsupported(format!("{path}: no decodable audio track"))
            })?;

        let track_id = track.id;
        let params = track.codec_params.clone();

        let source_channels = params.channels.map(|c| c.count()).unwrap_or(2).max(1);
        let source_rate = params.sample_rate.unwrap_or(44_100).max(1);
        let bit_depth = params.bits_per_sample.unwrap_or(0);

        let output_channels = if target_channels == 0 {
            source_channels
        } else {
            target_channels as usize
        };
        let output_rate = if target_sample_rate == 0 {
            source_rate
        } else {
            target_sample_rate
        };

        let duration_ms = match params.n_frames {
            Some(frames) if source_rate > 0 => frames.saturating_mul(1000) / source_rate as u64,
            _ => AudioStreamInfo::UNKNOWN_DURATION,
        };

        let decoder = symphonia::default::get_codecs()
            .make(&params, &DecoderOptions::default())
            .map_err(map_sym_err_open)?;

        let resampler = if output_rate != source_rate {
            Some(StreamResampler::new(source_rate, output_rate, output_channels)?)
        } else {
            None
        };

        let stream_info = AudioStreamInfo {
            channels: output_channels as u32,
            sample_rate: output_rate,
            duration_ms,
            bit_depth,
        };

        Ok(Self {
            format,
            decoder,
            track_id,
            source_channels,
            output_channels,
            output_rate,
            stream_info,
            sample_buf: None,
            resampler,
            residual: Vec::new(),
            residual_pos: 0,
            output_frame_pos: 0,
            eof: false,
            skip_source_frames: 0,
        })
    }

    /// Decodes packets until `residual` holds at least one sample or EOF is hit.
    ///
    /// Returns `true` when no more data can be produced from the source.
    fn refill_residual(&mut self) -> Result<bool> {
        while self.residual_pos >= self.residual.len() {
            self.residual.clear();
            self.residual_pos = 0;

            let packet = match self.format.next_packet() {
                Ok(p) => p,
                Err(SymError::IoError(e))
                    if e.kind() == std::io::ErrorKind::UnexpectedEof =>
                {
                    // Drain any tail held inside the resampler before reporting EOF.
                    if let Some(rs) = self.resampler.as_mut() {
                        rs.flush(&mut self.residual);
                    }
                    return Ok(self.residual.is_empty());
                }
                Err(SymError::ResetRequired) => {
                    self.decoder.reset();
                    continue;
                }
                Err(e) => return Err(map_sym_err_read(e)),
            };

            if packet.track_id() != self.track_id {
                continue;
            }

            let decoded = match self.decoder.decode(&packet) {
                Ok(d) => d,
                // Decode errors on a single packet are recoverable: skip it.
                Err(SymError::DecodeError(_)) => continue,
                Err(SymError::IoError(e))
                    if e.kind() == std::io::ErrorKind::UnexpectedEof =>
                {
                    if let Some(rs) = self.resampler.as_mut() {
                        rs.flush(&mut self.residual);
                    }
                    return Ok(self.residual.is_empty());
                }
                Err(e) => return Err(map_sym_err_read(e)),
            };

            let spec = *decoded.spec();
            let capacity = decoded.capacity() as u64;
            let sample_buf = self
                .sample_buf
                .get_or_insert_with(|| SampleBuffer::new(capacity, spec));
            sample_buf.copy_interleaved_ref(decoded);
            let mut interleaved = sample_buf.samples();

            // Discard leading source frames left over from a coarse seek so the
            // output starts exactly at the requested position.
            if self.skip_source_frames > 0 {
                let packet_frames = (interleaved.len() / self.source_channels) as u64;
                let drop_frames = self.skip_source_frames.min(packet_frames);
                self.skip_source_frames -= drop_frames;
                let offset = drop_frames as usize * self.source_channels;
                interleaved = &interleaved[offset..];
                if interleaved.is_empty() {
                    continue;
                }
            }

            // 1) Channel conversion into a scratch buffer.
            let mut converted: Vec<f32> = Vec::with_capacity(
                interleaved.len() / self.source_channels * self.output_channels,
            );
            convert_channels(
                interleaved,
                self.source_channels,
                self.output_channels,
                &mut converted,
            );

            // 2) Optional sample-rate conversion → residual.
            match self.resampler.as_mut() {
                Some(rs) => rs.push_interleaved(&converted, &mut self.residual),
                None => self.residual.extend_from_slice(&converted),
            }
        }

        Ok(false)
    }
}

impl AudioDecoderBackend for SymphoniaBackend {
    fn stream_info(&self) -> AudioStreamInfo {
        self.stream_info
    }

    fn read_frames(&mut self, buffer: &mut [f32]) -> Result<DecoderReadResult> {
        let pts_ms = self.output_frame_pos as f64 * 1000.0 / self.output_rate as f64;
        let mut written = 0usize;

        while written < buffer.len() {
            if self.residual_pos >= self.residual.len() {
                if self.eof {
                    break;
                }
                let done = self.refill_residual()?;
                if done {
                    self.eof = true;
                    if self.residual_pos >= self.residual.len() {
                        break;
                    }
                }
            }

            let available = self.residual.len() - self.residual_pos;
            let want = (buffer.len() - written).min(available);
            buffer[written..written + want]
                .copy_from_slice(&self.residual[self.residual_pos..self.residual_pos + want]);
            self.residual_pos += want;
            written += want;
        }

        let frames = (written / self.output_channels.max(1)) as u64;
        self.output_frame_pos += frames;

        Ok(DecoderReadResult {
            samples_written: written,
            pts_ms,
            is_eof: self.eof && self.residual_pos >= self.residual.len(),
        })
    }

    fn seek(&mut self, frame_position: u64) -> Result<()> {
        let seconds = frame_position as f64 / self.output_rate as f64;
        let time = Time::new(seconds.trunc() as u64, seconds.fract());

        let seeked = self
            .format
            .seek(
                SeekMode::Accurate,
                SeekTo::Time {
                    time,
                    track_id: Some(self.track_id),
                },
            )
            .map_err(map_sym_err_seek)?;

        // Symphonia positions the reader at the start of the packet containing
        // the target (`actual_ts`); discard the remaining source frames up to
        // `required_ts` to make the seek sample-accurate.
        self.skip_source_frames = seeked.required_ts.saturating_sub(seeked.actual_ts);

        self.decoder.reset();
        if let Some(rs) = self.resampler.as_mut() {
            rs.reset();
        }
        self.residual.clear();
        self.residual_pos = 0;
        self.output_frame_pos = frame_position;
        self.eof = false;
        Ok(())
    }
}

/// Converts an interleaved buffer from `src_ch` channels to `dst_ch` channels,
/// appending the result to `out`.
fn convert_channels(src: &[f32], src_ch: usize, dst_ch: usize, out: &mut Vec<f32>) {
    if src_ch == dst_ch {
        out.extend_from_slice(src);
        return;
    }

    let frames = src.len() / src_ch;

    if src_ch == 1 {
        // Mono upmix: duplicate the single channel across all outputs.
        for &s in &src[..frames] {
            for _ in 0..dst_ch {
                out.push(s);
            }
        }
    } else if dst_ch == 1 {
        // Downmix to mono: average all source channels.
        let inv = 1.0 / src_ch as f32;
        for f in 0..frames {
            let mut acc = 0.0f32;
            for c in 0..src_ch {
                acc += src[f * src_ch + c];
            }
            out.push(acc * inv);
        }
    } else {
        // Generic mapping: copy common channels, silence any extra outputs.
        let common = src_ch.min(dst_ch);
        for f in 0..frames {
            let base = f * src_ch;
            for c in 0..dst_ch {
                out.push(if c < common { src[base + c] } else { 0.0 });
            }
        }
    }
}

fn map_sym_err_open(e: SymError) -> AudioError {
    match e {
        SymError::Unsupported(msg) => AudioError::DecoderUnsupported(msg.to_string()),
        other => AudioError::DecoderOpen(other.to_string()),
    }
}

fn map_sym_err_read(e: SymError) -> AudioError {
    AudioError::DecoderRead(e.to_string())
}

fn map_sym_err_seek(e: SymError) -> AudioError {
    AudioError::DecoderSeek(e.to_string())
}
