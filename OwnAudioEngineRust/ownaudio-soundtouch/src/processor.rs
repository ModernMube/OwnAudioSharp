//! Top-level orchestrator — port of `SoundTouchProcessor.cs`.
//!
//! Coordinates the [`RateTransposer`] and [`TimeStretch`] stages to realise
//! independent tempo, pitch and rate control.  The effective rate and tempo are
//! derived from the three virtual parameters:
//!
//! ```text
//! effective_tempo = tempo / pitch
//! effective_rate  = pitch * rate
//! ```
//!
//! When `effective_rate <= 1` the transposer runs before the stretcher,
//! otherwise after; the active output stage is tracked by [`OutputMode`] and
//! samples already in flight are migrated across the crossover, mirroring the
//! C# `CalcEffectiveRateAndTempo` logic.

use crate::error::{ErrorCode, StResult};
use crate::rate_transposer::RateTransposer;
use crate::time_stretch::TimeStretch;
use crate::MAX_CHANNELS;

/// Setting IDs for [`SoundTouchProcessor::set_setting`] /
/// [`SoundTouchProcessor::get_setting`] (port of `SettingId.cs`).
#[repr(i32)]
#[derive(Debug, Copy, Clone, PartialEq, Eq)]
pub enum SettingId {
    /// Enable/disable the anti-alias filter (0 = disable).
    UseAntiAliasFilter = 0,
    /// Anti-alias filter length in taps (8..128).
    AntiAliasFilterLength = 1,
    /// Enable/disable the quick-seek tempo heuristic.
    UseQuickSeek = 2,
    /// Processing sequence length in milliseconds.
    SequenceDurationMs = 3,
    /// Seek-window length in milliseconds.
    SeekWindowDurationMs = 4,
    /// Sequence overlap length in milliseconds.
    OverlapDurationMs = 5,
    /// Read-only: nominal input sequence size in samples.
    NominalInputSequence = 6,
    /// Read-only: nominal output sequence size in samples.
    NominalOutputSequence = 7,
    /// Read-only: initial processing latency in samples.
    InitialLatency = 8,
}

/// Which pipeline stage currently feeds the output.
#[derive(Debug, Copy, Clone, PartialEq, Eq)]
enum OutputMode {
    /// Time-stretcher is the tail (effective rate `<= 1`).
    Stretch,
    /// Rate transposer is the tail (effective rate `> 1`).
    RateTransposer,
}

/// Main tempo/pitch/rate processor.
pub struct SoundTouchProcessor {
    rate_transposer: RateTransposer,
    stretch: TimeStretch,
    output_mode: OutputMode,

    is_sample_rate_set: bool,

    rate: f64,
    tempo: f64,
    pitch: f64,

    samples_expected_out: f64,
    samples_output: i64,

    channels: usize,

    effective_rate: f64,
    effective_tempo: f64,

    /// Reusable blank scratch used by [`Self::flush`] so end-of-stream flushing
    /// allocates nothing on the audio thread. Sized to `128 * channels` whenever
    /// the channel count is set; a flush that never runs leaves it empty.
    flush_scratch: Vec<f32>,
}

impl SoundTouchProcessor {
    /// Creates a processor with unity tempo/pitch/rate and no stream parameters
    /// set yet (sample rate and channel count must be configured before use).
    pub fn new() -> Self {
        let mut p = SoundTouchProcessor {
            rate_transposer: RateTransposer::new(),
            stretch: TimeStretch::new(),
            output_mode: OutputMode::Stretch,
            is_sample_rate_set: false,
            rate: 1.0,
            tempo: 1.0,
            pitch: 1.0,
            samples_expected_out: 0.0,
            samples_output: 0,
            channels: 0,
            effective_rate: 0.0,
            effective_tempo: 0.0,
            flush_scratch: Vec::new(),
        };
        p.calc_effective_rate_and_tempo();
        p.samples_expected_out = 0.0;
        p.samples_output = 0;
        p
    }

    // ---- configuration -------------------------------------------------------

    /// Sets the number of channels (1 = mono, 2 = stereo, …).
    pub fn set_channels(&mut self, channels: usize) -> StResult<()> {
        if channels == 0 || channels > MAX_CHANNELS {
            return Err(ErrorCode::InvalidChannelCount);
        }
        self.channels = channels;
        self.rate_transposer.set_channels(channels);
        self.stretch.set_channels(channels);
        // Pre-size the flush scratch on the (control-thread) configuration path so
        // end-of-stream flushing never allocates on the audio thread.
        let scratch_len = 128 * channels;
        if self.flush_scratch.len() != scratch_len {
            self.flush_scratch.resize(scratch_len, 0.0);
        }
        Ok(())
    }

    /// Current channel count.
    #[inline]
    pub fn channels(&self) -> usize {
        self.channels
    }

    /// Sets the sample rate in Hz.  Must be called before [`Self::put_samples`].
    pub fn set_sample_rate(&mut self, sample_rate: u32) -> StResult<()> {
        if sample_rate == 0 || sample_rate > 192_000 {
            return Err(ErrorCode::NotInitialized);
        }
        self.stretch.set_parameters(sample_rate as i64, -1, -1, -1);
        self.is_sample_rate_set = true;
        Ok(())
    }

    /// Current sample rate in Hz.
    #[inline]
    pub fn sample_rate(&self) -> usize {
        self.stretch.get_parameters().0
    }

    /// Sets the rate control (`1.0` = original).
    #[inline]
    pub fn set_rate(&mut self, rate: f64) {
        self.rate = rate;
        self.calc_effective_rate_and_tempo();
    }

    /// Current rate control value.
    #[inline]
    pub fn rate(&self) -> f64 {
        self.rate
    }

    /// Sets the rate as a percentage change (-50 .. +100).
    #[inline]
    pub fn set_rate_change(&mut self, percent: f64) {
        self.rate = 1.0 + (0.01 * percent);
        self.calc_effective_rate_and_tempo();
    }

    /// Sets the tempo control (`1.0` = original).
    #[inline]
    pub fn set_tempo(&mut self, tempo: f64) {
        self.tempo = tempo;
        self.calc_effective_rate_and_tempo();
    }

    /// Current tempo control value.
    #[inline]
    pub fn tempo(&self) -> f64 {
        self.tempo
    }

    /// Sets the tempo as a percentage change (-50 .. +100).
    #[inline]
    pub fn set_tempo_change(&mut self, percent: f64) {
        self.tempo = 1.0 + (0.01 * percent);
        self.calc_effective_rate_and_tempo();
    }

    /// Sets the pitch control (`1.0` = original, `<1` lower, `>1` higher).
    #[inline]
    pub fn set_pitch(&mut self, pitch: f64) {
        self.pitch = pitch;
        self.calc_effective_rate_and_tempo();
    }

    /// Current pitch control value.
    #[inline]
    pub fn pitch(&self) -> f64 {
        self.pitch
    }

    /// Sets the pitch change in octaves (-1.0 .. +1.0).
    #[inline]
    pub fn set_pitch_octaves(&mut self, octaves: f64) {
        self.pitch = (std::f64::consts::LN_2 * octaves).exp();
        self.calc_effective_rate_and_tempo();
    }

    /// Sets the pitch change in semitones (-12 .. +12).
    #[inline]
    pub fn set_pitch_semitones(&mut self, semitones: f64) {
        self.set_pitch_octaves(semitones / 12.0);
    }

    /// Input/output duration ratio: processing `n` input frames yields
    /// approximately `n * ratio` output frames.
    #[inline]
    pub fn input_output_sample_ratio(&self) -> f64 {
        1.0 / (self.effective_tempo * self.effective_rate)
    }

    /// Number of unprocessed input frames currently buffered.
    #[inline]
    pub fn unprocessed_sample_count(&self) -> usize {
        self.stretch.unprocessed_samples()
    }

    // ---- streaming -----------------------------------------------------------

    /// Feeds `num` interleaved frames into the pipeline.
    ///
    /// Returns [`ErrorCode::NotInitialized`] if the sample rate has not been set
    /// or [`ErrorCode::InvalidChannelCount`] if the channel count is unset.
    pub fn put_samples(&mut self, samples: &[f32], num: usize) -> StResult<()> {
        if !self.is_sample_rate_set {
            return Err(ErrorCode::NotInitialized);
        }
        if self.channels == 0 {
            return Err(ErrorCode::InvalidChannelCount);
        }

        self.samples_expected_out += num as f64 / (self.effective_rate * self.effective_tempo);

        if self.effective_rate <= 1.0 {
            self.rate_transposer.put_samples(samples, num);
            // stretch.MoveSamples(rate_transposer)
            let avail = self.rate_transposer.output().available_samples();
            let (rt, st) = (&mut self.rate_transposer, &mut self.stretch);
            let src = rt.output().ptr_begin();
            st.put_samples(src, avail);
            rt.output_mut().receive_samples(avail);
        } else {
            self.stretch.put_samples(samples, num);
            // rate_transposer.MoveSamples(stretch)
            let avail = self.stretch.output().available_samples();
            let (rt, st) = (&mut self.rate_transposer, &mut self.stretch);
            let src = st.output().ptr_begin();
            rt.put_samples(src, avail);
            st.output_mut().receive_samples(avail);
        }
        Ok(())
    }

    /// Number of processed output frames available.
    #[inline]
    pub fn available_samples(&self) -> usize {
        self.output().available_samples()
    }

    /// Copies up to `max_samples` processed frames into `output`, returning the
    /// number written.
    pub fn receive_samples(&mut self, output: &mut [f32], max_samples: usize) -> usize {
        let result = self.output_mut().receive_samples_into(output, max_samples);
        self.samples_output += result as i64;
        result
    }

    /// Discards up to `max_samples` processed frames without copying them.
    pub fn receive_samples_discard(&mut self, max_samples: usize) -> usize {
        let result = self.output_mut().receive_samples(max_samples);
        self.samples_output += result as i64;
        result
    }

    /// Flushes the last samples out of the pipeline (end-of-stream).
    ///
    /// Allocation-free: it pumps blank frames through the pipeline using the
    /// pre-sized [`Self::flush_scratch`] buffer, so it is safe to call from the
    /// audio thread at end-of-stream without touching the allocator.
    pub fn flush(&mut self) {
        if self.channels == 0 {
            return;
        }

        let scratch_len = 128 * self.channels;
        if self.flush_scratch.len() != scratch_len {
            self.flush_scratch.resize(scratch_len, 0.0);
        }

        let mut num_still_expected = (self.samples_expected_out + 0.5) as i64 - self.samples_output;
        if num_still_expected < 0 {
            num_still_expected = 0;
        }
        let num_still_expected = num_still_expected as usize;

        // Move the scratch out so the blank frames can be fed while `self` is
        // borrowed mutably by `put_samples`; it is restored afterwards.
        let scratch = std::mem::take(&mut self.flush_scratch);
        let mut i = 0;
        while num_still_expected > self.available_samples() && i < 200 {
            let _ = self.put_samples(&scratch, 128);
            i += 1;
        }
        self.flush_scratch = scratch;

        self.output_mut()
            .adjust_amount_of_samples(num_still_expected);
        self.stretch.clear_input();
    }

    /// Clears all output and internal processing buffers.
    pub fn clear(&mut self) {
        self.samples_expected_out = 0.0;
        self.samples_output = 0;
        self.rate_transposer.clear();
        self.stretch.clear();
    }

    // ---- settings ------------------------------------------------------------

    /// Changes a processing setting.  Returns `true` if applied.
    pub fn set_setting(&mut self, setting: SettingId, value: i32) -> bool {
        let (sample_rate, sequence_ms, seek_window_ms, overlap_ms) = self.stretch.get_parameters();
        match setting {
            SettingId::UseAntiAliasFilter => {
                self.rate_transposer.enable_aa_filter(value != 0);
                true
            }
            SettingId::AntiAliasFilterLength => {
                self.rate_transposer
                    .set_aa_filter_length(value.max(0) as usize);
                true
            }
            SettingId::UseQuickSeek => {
                self.stretch.enable_quick_seek(value != 0);
                true
            }
            SettingId::SequenceDurationMs => {
                self.stretch.set_parameters(
                    sample_rate as i64,
                    value as i64,
                    seek_window_ms as i64,
                    overlap_ms as i64,
                );
                true
            }
            SettingId::SeekWindowDurationMs => {
                self.stretch.set_parameters(
                    sample_rate as i64,
                    sequence_ms as i64,
                    value as i64,
                    overlap_ms as i64,
                );
                true
            }
            SettingId::OverlapDurationMs => {
                self.stretch.set_parameters(
                    sample_rate as i64,
                    sequence_ms as i64,
                    seek_window_ms as i64,
                    value as i64,
                );
                true
            }
            _ => false,
        }
    }

    /// Reads a processing setting.
    pub fn get_setting(&self, setting: SettingId) -> i32 {
        match setting {
            SettingId::UseAntiAliasFilter => i32::from(self.rate_transposer.is_aa_filter_enabled()),
            SettingId::AntiAliasFilterLength => self.rate_transposer.aa_filter_length() as i32,
            SettingId::UseQuickSeek => i32::from(self.stretch.is_quick_seek_enabled()),
            SettingId::SequenceDurationMs => self.stretch.get_parameters().1 as i32,
            SettingId::SeekWindowDurationMs => self.stretch.get_parameters().2 as i32,
            SettingId::OverlapDurationMs => self.stretch.get_parameters().3 as i32,
            SettingId::NominalInputSequence => {
                let size = self.stretch.input_sample_req();
                if self.effective_rate <= 1.0 {
                    ((size as f64 * self.effective_rate) + 0.5) as i32
                } else {
                    size as i32
                }
            }
            SettingId::NominalOutputSequence => {
                let size = self.stretch.output_batch_size();
                if self.effective_rate > 1.0 {
                    ((size as f64 / self.effective_rate) + 0.5) as i32
                } else {
                    size as i32
                }
            }
            SettingId::InitialLatency => {
                let mut latency = self.stretch.latency() as f64;
                let latency_tr = self.rate_transposer.latency() as f64;
                if self.effective_rate <= 1.0 {
                    latency = (latency + latency_tr) * self.effective_rate;
                } else {
                    latency += latency_tr / self.effective_rate;
                }
                (latency + 0.5) as i32
            }
        }
    }

    // ---- internals -----------------------------------------------------------

    #[inline]
    fn output(&self) -> &crate::fifo_buffer::FifoSampleBuffer {
        match self.output_mode {
            OutputMode::Stretch => self.stretch.output(),
            OutputMode::RateTransposer => self.rate_transposer.output(),
        }
    }

    #[inline]
    fn output_mut(&mut self) -> &mut crate::fifo_buffer::FifoSampleBuffer {
        match self.output_mode {
            OutputMode::Stretch => self.stretch.output_mut(),
            OutputMode::RateTransposer => self.rate_transposer.output_mut(),
        }
    }

    fn calc_effective_rate_and_tempo(&mut self) {
        let old_tempo = self.effective_tempo;
        let old_rate = self.effective_rate;

        self.effective_tempo = self.tempo / self.pitch;
        self.effective_rate = self.pitch * self.rate;

        if !is_double_equal(self.effective_rate, old_rate) {
            self.rate_transposer.set_rate(self.effective_rate);
        }
        if !is_double_equal(self.effective_tempo, old_tempo) {
            self.stretch.set_tempo(self.effective_tempo);
        }

        if self.effective_rate <= 1.0 {
            if self.output_mode != OutputMode::Stretch {
                // Output was the rate transposer: migrate its remaining output
                // into the stretch output and make the stretcher the tail.
                let (st, rt) = (&mut self.stretch, &mut self.rate_transposer);
                st.output_mut().move_samples_from(rt.output_mut());
                self.output_mode = OutputMode::Stretch;
            }
        } else if self.output_mode != OutputMode::RateTransposer {
            // Output was the stretcher: migrate its remaining output into the
            // transposer output, then feed the stretcher's unprocessed input
            // into the transposer, and make the transposer the tail.
            {
                let (rt, st) = (&mut self.rate_transposer, &mut self.stretch);
                rt.output_mut().move_samples_from(st.output_mut());
            }
            let avail = self.stretch.input_mut().available_samples();
            {
                let (rt, st) = (&mut self.rate_transposer, &mut self.stretch);
                let src = st.input_mut().ptr_begin();
                rt.put_samples(src, avail);
            }
            self.stretch.input_mut().receive_samples(avail);
            self.output_mode = OutputMode::RateTransposer;
        }
    }
}

impl Default for SoundTouchProcessor {
    fn default() -> Self {
        Self::new()
    }
}

#[inline]
fn is_double_equal(a: f64, b: f64) -> bool {
    (a - b).abs() < f64::EPSILON
}
