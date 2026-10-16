//! Pure-Rust streaming decoder built on [Symphonia](https://docs.rs/symphonia).
//!
//! Supports the formats enabled in `Cargo.toml`: WAV, MP3, FLAC, OGG/Vorbis,
//! AAC/M4A, ALAC/M4A and AIFF.  Always compiled in, and the only backend there
//! is - anything outside that list fails to open.

use std::fs::File;
use std::io::ErrorKind;
use std::path::Path;

use symphonia::core::audio::{AudioSpec, GenericAudioBufferRef};
use symphonia::core::codecs::audio::{AudioDecoder, AudioDecoderOptions};
use symphonia::core::errors::Error as SymError;
use symphonia::core::formats::probe::Hint;
use symphonia::core::formats::{FormatOptions, FormatReader, SeekMode, SeekTo, TrackType};
use symphonia::core::io::MediaSourceStream;
use symphonia::core::meta::MetadataOptions;
use symphonia::core::packet::Packet;
use symphonia::core::units::{Time, TimeBase};

use crate::decoder::stream_info::{AudioStreamInfo, DecoderReadResult};
use crate::decoder::AudioDecoderBackend;
use crate::error::{AudioError, Result};

use super::resample::StreamResampler;

/// Symphonia-backed streaming decoder.
pub(crate) struct SymphoniaBackend {
    format: Box<dyn FormatReader>,
    decoder: Box<dyn AudioDecoder>,
    track_id: u32,
    /// Tick length of the track's timestamps, `None` reads as one tick per source frame.
    time_base: Option<TimeBase>,

    source_channels: usize,
    source_rate: u32,
    output_channels: usize,
    output_rate: u32,

    stream_info: AudioStreamInfo,

    /// Container frame count scaled to the output rate, `None` when the container is silent.
    total_output_frames: Option<u64>,

    /// Reused interleaved-`f32` copy of the most recently decoded packet.
    sample_buf: Vec<f32>,

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

    /// Track timestamp the last seek asked for. Decoded frames before it are dropped,
    /// cleared once a packet reaches it.
    seek_ts: Option<i64>,
}

impl SymphoniaBackend {
    pub(crate) fn open(path: &str, target_sample_rate: u32, target_channels: u32) -> Result<Self> {
        let file = File::open(path).map_err(|e| AudioError::DecoderOpen(format!("{path}: {e}")))?;
        let mss = MediaSourceStream::new(Box::new(file), Default::default());

        let mut hint = Hint::new();
        if let Some(ext) = Path::new(path).extension().and_then(|e| e.to_str()) {
            hint.with_extension(ext);
        }

        let mut format = symphonia::default::get_probe()
            .probe(
                &hint,
                mss,
                FormatOptions::default(),
                MetadataOptions::default(),
            )
            .map_err(map_sym_err_open)?;

        let no_track =
            || AudioError::DecoderUnsupported(format!("{path}: no decodable audio track"));
        let track = format
            .default_track(TrackType::Audio)
            .ok_or_else(no_track)?;

        let track_id = track.id;
        let time_base = track.time_base;
        let num_frames = track.num_frames;
        let params = track
            .codec_params
            .as_ref()
            .and_then(|p| p.audio())
            .ok_or_else(no_track)?
            .clone();
        let bit_depth = params.bits_per_sample.unwrap_or(0);

        // Gapless is on by default: the decoder trims encoder delay and padding itself.
        let mut decoder = symphonia::default::get_codecs()
            .make_audio_decoder(&params, &AudioDecoderOptions::default())
            .map_err(map_sym_err_open)?;

        // MP4/M4A doesn't populate the channels on the track — only the decoder
        // knows the real layout (AAC's ASC, ALAC's magic cookie). Decode the
        // first packet up front so `source_channels`/`source_rate` reflect
        // reality instead of a guessed default.
        let peeked = peek_first_packet(format.as_mut(), decoder.as_mut(), track_id)?;

        let (source_channels, source_rate) = match &peeked {
            Some((_, spec)) => (spec.channels().count().max(1), spec.rate().max(1)),
            None => (
                params
                    .channels
                    .as_ref()
                    .map(|c| c.count())
                    .unwrap_or(2)
                    .max(1),
                params.sample_rate.unwrap_or(44_100).max(1),
            ),
        };

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

        let duration_ms = match num_frames {
            Some(frames) => frames.saturating_mul(1000) / source_rate as u64,
            None => AudioStreamInfo::UNKNOWN_DURATION,
        };

        let total_output_frames = num_frames.map(|frames| {
            ((frames as u128 * output_rate as u128 + source_rate as u128 / 2) / source_rate as u128)
                as u64
        });

        let resampler = if output_rate != source_rate {
            Some(StreamResampler::new(
                source_rate,
                output_rate,
                output_channels,
            )?)
        } else {
            None
        };

        let stream_info = AudioStreamInfo {
            channels: output_channels as u32,
            sample_rate: output_rate,
            duration_ms,
            bit_depth,
        };

        let mut this = Self {
            format,
            decoder,
            track_id,
            time_base,
            source_channels,
            source_rate,
            output_channels,
            output_rate,
            stream_info,
            total_output_frames,
            sample_buf: Vec::new(),
            resampler,
            residual: Vec::new(),
            residual_pos: 0,
            output_frame_pos: 0,
            eof: peeked.is_none(),
            seek_ts: None,
        };

        // Feed the packet consumed by `peek_first_packet` through the normal
        // channel/rate conversion pipeline so it isn't lost.
        if let Some((samples, _spec)) = peeked {
            push_decoded(
                &samples,
                this.source_channels,
                this.output_channels,
                this.resampler.as_mut(),
                &mut this.residual,
            );
        }

        Ok(this)
    }

    /// Decodes packets until `residual` holds at least one sample or EOF is hit.
    ///
    /// Returns `true` when no more data can be produced from the source.
    fn refill_residual(&mut self) -> Result<bool> {
        while self.residual_pos >= self.residual.len() {
            self.residual.clear();
            self.residual_pos = 0;

            let Some(packet) =
                next_track_packet(self.format.as_mut(), self.decoder.as_mut(), self.track_id)?
            else {
                return Ok(drain_tail(self.resampler.as_mut(), &mut self.residual));
            };

            // pts belongs to the first decoded frame, the decoder trims `trim_start` off it.
            let first_ts = packet
                .pts
                .get()
                .saturating_add(packet.trim_start.get() as i64);

            let decoded = match self.decoder.decode(&packet) {
                Ok(d) => d,
                // Decode errors on a single packet are recoverable: skip it.
                Err(SymError::DecodeError(_)) => continue,
                Err(SymError::IoError(e)) if e.kind() == ErrorKind::UnexpectedEof => {
                    return Ok(drain_tail(self.resampler.as_mut(), &mut self.residual));
                }
                Err(e) => return Err(map_sym_err_read(e)),
            };
            copy_interleaved(&decoded, &mut self.sample_buf);

            let mut interleaved = &self.sample_buf[..];

            if let Some(target) = self.seek_ts {
                let packet_frames = interleaved.len() / self.source_channels;
                let behind = ts_to_frames(
                    target.saturating_sub(first_ts),
                    self.time_base,
                    self.source_rate,
                );
                let drop_frames = behind.clamp(0, packet_frames as i64) as usize;
                if drop_frames < packet_frames {
                    self.seek_ts = None;
                }
                interleaved = &interleaved[drop_frames * self.source_channels..];
                if interleaved.is_empty() {
                    continue;
                }
            }

            push_decoded(
                interleaved,
                self.source_channels,
                self.output_channels,
                self.resampler.as_mut(),
                &mut self.residual,
            );
        }

        Ok(false)
    }
}

impl AudioDecoderBackend for SymphoniaBackend {
    fn stream_info(&self) -> AudioStreamInfo {
        self.stream_info
    }

    fn total_output_frames(&self) -> Option<u64> {
        self.total_output_frames
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
        let time = Time::try_from_secs_f64(seconds).ok_or_else(|| {
            AudioError::DecoderSeek(format!("frame {frame_position} is out of range"))
        })?;

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

        // The reader lands on a packet at or before the target (MP3 even backs up a few
        // for the bit reservoir); `refill_residual` drops the frames up to `required_ts`.
        self.seek_ts = Some(seeked.required_ts.get());

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

/// Next packet of `track_id`, `None` once the stream ends. A truncated tail
/// counts as the end too, so a cut-off file plays up to the cut.
fn next_track_packet(
    format: &mut dyn FormatReader,
    decoder: &mut dyn AudioDecoder,
    track_id: u32,
) -> Result<Option<Packet>> {
    loop {
        match format.next_packet() {
            Ok(Some(packet)) if packet.track_id == track_id => return Ok(Some(packet)),
            Ok(Some(_)) => {}
            Ok(None) => return Ok(None),
            Err(SymError::IoError(e)) if e.kind() == ErrorKind::UnexpectedEof => return Ok(None),
            Err(SymError::ResetRequired) => decoder.reset(),
            Err(e) => return Err(map_sym_err_read(e)),
        }
    }
}

/// Decodes packets for `track_id` until the first one that decodes, returning
/// its samples (interleaved `f32`) with its [`AudioSpec`], or `Ok(None)` if the
/// stream ends first. Used at `open()` to learn the real channel/sample-rate
/// before committing to a guess.
fn peek_first_packet(
    format: &mut dyn FormatReader,
    decoder: &mut dyn AudioDecoder,
    track_id: u32,
) -> Result<Option<(Vec<f32>, AudioSpec)>> {
    while let Some(packet) = next_track_packet(format, decoder, track_id)? {
        let decoded = match decoder.decode(&packet) {
            Ok(d) => d,
            Err(SymError::DecodeError(_)) => continue,
            Err(SymError::IoError(e)) if e.kind() == ErrorKind::UnexpectedEof => return Ok(None),
            Err(e) => return Err(map_sym_err_read(e)),
        };

        let mut samples = Vec::new();
        copy_interleaved(&decoded, &mut samples);
        return Ok(Some((samples, decoded.spec().clone())));
    }

    Ok(None)
}

/// Copies `decoded` into `out` as interleaved `f32`, reusing its allocation.
/// The copy wants the slice sized exactly, so `out` is resized first.
fn copy_interleaved(decoded: &GenericAudioBufferRef<'_>, out: &mut Vec<f32>) {
    out.resize(decoded.samples_interleaved(), 0.0);
    decoded.copy_to_slice_interleaved(out.as_mut_slice());
}

/// End of stream: pushes whatever the resampler still holds onto `residual`.
/// Returns `true` when that left nothing to play.
fn drain_tail(resampler: Option<&mut StreamResampler>, residual: &mut Vec<f32>) -> bool {
    if let Some(rs) = resampler {
        rs.flush(residual);
    }
    residual.is_empty()
}

/// Track timestamp ticks to source frames. Audio containers mostly tick at
/// `1 / rate` already, where this is the identity, but Matroska ticks on its
/// timestamp scale (1 ms by default).
fn ts_to_frames(ticks: i64, time_base: Option<TimeBase>, rate: u32) -> i64 {
    match time_base {
        Some(tb) => {
            (ticks as i128 * tb.numer.get() as i128 * rate as i128 / tb.denom.get() as i128) as i64
        }
        None => ticks,
    }
}

/// Converts `interleaved` to `output_channels` and pushes it (resampled via
/// `resampler` when present) onto `residual`. A free function, not a method,
/// so the caller's `sample_buf` borrow doesn't collide with `&mut self`.
fn push_decoded(
    interleaved: &[f32],
    source_channels: usize,
    output_channels: usize,
    resampler: Option<&mut StreamResampler>,
    residual: &mut Vec<f32>,
) {
    let mut converted: Vec<f32> =
        Vec::with_capacity(interleaved.len() / source_channels * output_channels);
    convert_channels(
        interleaved,
        source_channels,
        output_channels,
        &mut converted,
    );

    match resampler {
        Some(rs) => rs.push_interleaved(&converted, residual),
        None => residual.extend_from_slice(&converted),
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
