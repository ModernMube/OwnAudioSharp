//! WSOLA time-stretch core — port of `TimeStretch.cs`.
//!
//! Changes tempo without altering pitch by chopping the input into overlapping
//! sequences and re-joining them at the best-correlating offset (Waveform
//! Similarity Overlap-Add).
//!
//! The C# original hand-vectorises the cross-correlation with AVX/SSE
//! intrinsics; this port keeps the scalar reference form with `f64`
//! accumulation, which the auto-vectorizer turns into SIMD while remaining the
//! exact numeric match for the RMS reference comparison.  All working buffers
//! are sized at configuration time; `put_samples` / `process` never allocate.

use crate::defaults;
use crate::fifo_buffer::FifoSampleBuffer;
use crate::MAX_CHANNELS;

/// Waveform-similarity overlap-add tempo changer.
pub struct TimeStretch {
    output_buffer: FifoSampleBuffer,
    input_buffer: FifoSampleBuffer,

    channels: usize,
    sample_req: usize,

    overlap_length: usize,
    seek_length: usize,
    seek_window_length: usize,
    sample_rate: usize,
    sequence_ms: usize,
    seek_window_ms: usize,
    overlap_ms: usize,

    tempo: f64,
    nominal_skip: f64,
    skip_fract: f64,

    quick_seek: bool,
    auto_seq_setting: bool,
    auto_seek_setting: bool,
    is_beginning: bool,

    /// Overlap tail of the previous sequence (`channels * overlap_length`).
    mid_buffer: Vec<f32>,
}

impl TimeStretch {
    /// Creates a tempo changer at 44.1 kHz stereo with default parameters.
    pub fn new() -> Self {
        let mut ts = TimeStretch {
            output_buffer: FifoSampleBuffer::new(2),
            input_buffer: FifoSampleBuffer::new(2),
            channels: 2,
            sample_req: 0,
            overlap_length: 0,
            seek_length: 0,
            seek_window_length: 0,
            sample_rate: 44100,
            sequence_ms: 0,
            seek_window_ms: 0,
            overlap_ms: defaults::OVERLAP_MS,
            tempo: 1.0,
            nominal_skip: 0.0,
            skip_fract: 0.0,
            quick_seek: false,
            auto_seq_setting: true,
            auto_seek_setting: true,
            is_beginning: true,
            mid_buffer: Vec::new(),
        };
        ts.set_parameters(
            44100,
            defaults::SEQUENCE_MS as i64,
            defaults::SEEKWINDOW_MS as i64,
            defaults::OVERLAP_MS as i64,
        );
        ts.set_tempo(1.0);
        ts.clear();
        ts
    }

    /// Sets the target tempo (`1.0` = unchanged, `<1` slower, `>1` faster).
    pub fn set_tempo(&mut self, new_tempo: f64) {
        self.tempo = new_tempo;
        self.calc_seq_parameters();
        self.nominal_skip =
            self.tempo * (self.seek_window_length as f64 - self.overlap_length as f64);
        let intskip = (self.nominal_skip + 0.5) as i64;
        let req = (intskip + self.overlap_length as i64).max(self.seek_window_length as i64)
            + self.seek_length as i64;
        self.sample_req = req.max(0) as usize;
    }

    /// Current tempo factor.
    #[inline]
    pub fn tempo(&self) -> f64 {
        self.tempo
    }

    /// Clears output and input, returning to the start-of-stream state.
    pub fn clear(&mut self) {
        self.output_buffer.clear();
        self.clear_input();
    }

    /// Clears only the input side (used by `flush`).
    pub fn clear_input(&mut self) {
        self.input_buffer.clear();
        self.clear_mid_buffer();
        self.is_beginning = true;
        self.skip_fract = 0.0;
    }

    /// Sets the channel count, re-initialising the overlap buffer.
    pub fn set_channels(&mut self, num_channels: usize) {
        if num_channels == 0 || num_channels > MAX_CHANNELS || self.channels == num_channels {
            return;
        }
        self.channels = num_channels;
        self.input_buffer.set_channels(num_channels);
        self.output_buffer.set_channels(num_channels);
        self.overlap_length = 0;
        self.set_parameters(self.sample_rate as i64, -1, -1, -1);
    }

    /// Enables/disables the quick (lower-CPU) seek heuristic.
    #[inline]
    pub fn enable_quick_seek(&mut self, enable: bool) {
        self.quick_seek = enable;
    }

    /// Whether quick seek is enabled.
    #[inline]
    pub fn is_quick_seek_enabled(&self) -> bool {
        self.quick_seek
    }

    /// Sets timing control parameters.  Pass `-1` to keep a value, `0` to switch
    /// it to the automatic (tempo-derived) setting, or a positive value to fix
    /// it.  `sample_rate <= 0` keeps the previous rate.
    pub fn set_parameters(
        &mut self,
        sample_rate: i64,
        sequence_ms: i64,
        seekwindow_ms: i64,
        overlap_ms: i64,
    ) {
        if sample_rate > 0 && sample_rate <= 192_000 {
            self.sample_rate = sample_rate as usize;
        }
        if overlap_ms > 0 {
            self.overlap_ms = overlap_ms as usize;
        }
        if sequence_ms > 0 {
            self.sequence_ms = sequence_ms as usize;
            self.auto_seq_setting = false;
        } else if sequence_ms == 0 {
            self.auto_seq_setting = true;
        }
        if seekwindow_ms > 0 {
            self.seek_window_ms = seekwindow_ms as usize;
            self.auto_seek_setting = false;
        } else if seekwindow_ms == 0 {
            self.auto_seek_setting = true;
        }

        self.calc_seq_parameters();
        self.calculate_overlap_length(self.overlap_ms);
        self.set_tempo(self.tempo);
    }

    /// Returns `(sample_rate, sequence_ms_or_auto, seek_window_ms_or_auto,
    /// overlap_ms)`, where auto settings report `0`.
    pub fn get_parameters(&self) -> (usize, usize, usize, usize) {
        let seq = if self.auto_seq_setting {
            defaults::USE_AUTO_SEQUENCE_LEN
        } else {
            self.sequence_ms
        };
        let seek = if self.auto_seek_setting {
            defaults::USE_AUTO_SEEKWINDOW_LEN
        } else {
            self.seek_window_ms
        };
        (self.sample_rate, seq, seek, self.overlap_ms)
    }

    /// Feeds `num` interleaved frames into the tempo changer.
    pub fn put_samples(&mut self, samples: &[f32], num: usize) {
        self.input_buffer.put_samples_from(samples, num);
        self.process_samples();
    }

    /// Nominal input frame requirement to trigger a processing batch.
    #[inline]
    pub fn input_sample_req(&self) -> usize {
        (self.nominal_skip + 0.5) as usize
    }

    /// Nominal output frame count produced per batch.
    #[inline]
    pub fn output_batch_size(&self) -> usize {
        self.seek_window_length - self.overlap_length
    }

    /// Approximate initial input-output latency in frames.
    #[inline]
    pub fn latency(&self) -> usize {
        self.sample_req
    }

    /// Read-only output buffer.
    #[inline]
    pub fn output(&self) -> &FifoSampleBuffer {
        &self.output_buffer
    }

    /// Mutable output buffer (pipeline hand-off).
    #[inline]
    pub fn output_mut(&mut self) -> &mut FifoSampleBuffer {
        &mut self.output_buffer
    }

    /// Mutable input buffer (pipeline hand-off).
    #[inline]
    pub fn input_mut(&mut self) -> &mut FifoSampleBuffer {
        &mut self.input_buffer
    }

    /// Frames currently unprocessed in the input.
    #[inline]
    pub fn unprocessed_samples(&self) -> usize {
        self.input_buffer.available_samples()
    }

    // ---- internals -----------------------------------------------------------

    fn accept_new_overlap_length(&mut self, new_overlap_length: usize) {
        let prev = self.overlap_length;
        self.overlap_length = new_overlap_length;
        if self.overlap_length > prev {
            let new_size = self.overlap_length * self.channels;
            self.mid_buffer = vec![0.0; new_size];
        }
    }

    fn calculate_overlap_length(&mut self, overlap_ms: usize) {
        let mut new_ovl = (self.sample_rate * overlap_ms) / 1000;
        if new_ovl < 16 {
            new_ovl = 16;
        }
        new_ovl -= new_ovl % 8;
        self.accept_new_overlap_length(new_ovl);
    }

    fn clear_mid_buffer(&mut self) {
        let n = (self.channels * self.overlap_length).min(self.mid_buffer.len());
        self.mid_buffer[..n].fill(0.0);
    }

    /// Cross-correlation of `mixing` against `compare`, normalised by the energy
    /// of `mixing`; `norm` receives that energy for the accumulate variant.
    fn calc_cross_corr(&self, mixing: &[f32], compare: &[f32], norm: &mut f64) -> f64 {
        let length = self.channels * self.overlap_length;
        let mut corr = 0.0_f64;
        let mut nrm = 0.0_f64;
        for i in 0..length {
            let m = mixing[i] as f64;
            corr += m * compare[i] as f64;
            nrm += m * m;
        }
        *norm = nrm;
        corr / (if nrm < 1e-9 { 1.0 } else { nrm }).sqrt()
    }

    /// Incremental cross-correlation that reuses the running `norm` from the
    /// previous offset.  `mixing` is `&ref_slice[offset..]`; the cancelled and
    /// added energy samples are read directly around `offset`.
    fn calc_cross_corr_accumulate(
        &self,
        ref_slice: &[f32],
        offset: usize,
        compare: &[f32],
        norm: &mut f64,
    ) -> f64 {
        let length = self.channels * self.overlap_length;

        // Cancel the energy of the `channels` samples just before the window.
        for k in 1..=self.channels {
            let v = ref_slice[offset - k] as f64;
            *norm -= v * v;
        }

        let mut corr = 0.0_f64;
        for i in 0..length {
            corr += ref_slice[offset + i] as f64 * compare[i] as f64;
        }

        // Add the energy of the last `channels` samples of this window.
        for j in 0..self.channels {
            let v = ref_slice[offset + length - 1 - j] as f64;
            *norm += v * v;
        }

        corr / (if *norm < 1e-9 { 1.0 } else { *norm }).sqrt()
    }

    fn seek_best_overlap_position(&self, ref_pos: &[f32]) -> usize {
        // At unity tempo the nominal join (offset 0) reconstructs the signal exactly: consecutive
        // windowed sequences advance by `seek_window - overlap` and overlap-add back to the input.
        // Running the correlation search here would instead lock onto a shifted "best" match and
        // comb-filter the passthrough (an audible phaser), and — because the shift is sticky — a
        // track that was time-stretched and then returned to unity would never recover its clarity.
        // Skipping the search keeps unity transparent and lets such a track heal immediately.
        if (self.tempo - 1.0).abs() < 1e-6 {
            return 0;
        }
        if self.quick_seek {
            self.seek_best_overlap_position_quick(ref_pos)
        } else {
            self.seek_best_overlap_position_full(ref_pos)
        }
    }

    fn seek_best_overlap_position_full(&self, ref_pos: &[f32]) -> usize {
        let mut norm = 0.0_f64;
        let mut best_offs = 0usize;
        let mut best_corr = self.calc_cross_corr(ref_pos, &self.mid_buffer, &mut norm);
        best_corr = (best_corr + 0.1) * 0.75;

        for i in 1..self.seek_length {
            let mut corr = self.calc_cross_corr_accumulate(
                ref_pos,
                self.channels * i,
                &self.mid_buffer,
                &mut norm,
            );
            let tmp = ((2 * i) as f64 - self.seek_length as f64) / self.seek_length as f64;
            corr = (corr + 0.1) * (1.0 - (0.25 * tmp * tmp));
            if corr > best_corr {
                best_corr = corr;
                best_offs = i;
            }
        }
        best_offs
    }

    fn seek_best_overlap_position_quick(&self, ref_pos: &[f32]) -> usize {
        const SCANSTEP: usize = 16;
        const SCANWIND: usize = 8;

        let seek_length = self.seek_length as isize;
        let mut norm = 0.0_f64;

        let mut best_corr = f32::MIN;
        let mut best_corr2 = f32::MIN;
        let mut best_offs = SCANWIND;
        let mut best_offs2 = SCANWIND;

        let mut i = SCANSTEP;
        while (i as isize) < seek_length - SCANWIND as isize - 1 {
            let mut corr =
                self.calc_cross_corr(&ref_pos[self.channels * i..], &self.mid_buffer, &mut norm)
                    as f32;
            let tmp = ((2 * i) as f32 - seek_length as f32 - 1.0) / seek_length as f32;
            corr = (corr + 0.1) * (1.0 - (0.25 * tmp * tmp));
            if corr > best_corr {
                best_corr2 = best_corr;
                best_offs2 = best_offs;
                best_corr = corr;
                best_offs = i;
            } else if corr > best_corr2 {
                best_corr2 = corr;
                best_offs2 = i;
            }
            i += SCANSTEP;
        }

        // Refine around the best match.
        let end = (best_offs + SCANWIND + 1).min(self.seek_length);
        let mut i = best_offs.saturating_sub(SCANWIND);
        while i < end {
            if i != best_offs {
                let mut corr = self.calc_cross_corr(
                    &ref_pos[self.channels * i..],
                    &self.mid_buffer,
                    &mut norm,
                ) as f32;
                let tmp = ((2 * i) as f32 - seek_length as f32 - 1.0) / seek_length as f32;
                corr = (corr + 0.1) * (1.0 - (0.25 * tmp * tmp));
                if corr > best_corr {
                    best_corr = corr;
                    best_offs = i;
                }
            }
            i += 1;
        }

        // Refine around the second-best match.
        let end = (best_offs2 + SCANWIND + 1).min(self.seek_length);
        let mut i = best_offs2.saturating_sub(SCANWIND);
        while i < end {
            if i != best_offs2 {
                let mut corr = self.calc_cross_corr(
                    &ref_pos[self.channels * i..],
                    &self.mid_buffer,
                    &mut norm,
                ) as f32;
                let tmp = ((2 * i) as f32 - seek_length as f32 - 1.0) / seek_length as f32;
                corr = (corr + 0.1) * (1.0 - (0.25 * tmp * tmp));
                if corr > best_corr {
                    best_corr = corr;
                    best_offs = i;
                }
            }
            i += 1;
        }

        best_offs
    }

    /// Overlap-add the start of `input` (at `ovl_pos`) with `mid` into `output`.
    fn overlap(
        output: &mut [f32],
        input: &[f32],
        ovl_pos: usize,
        mid: &[f32],
        overlap_length: usize,
        channels: usize,
    ) {
        match channels {
            1 => Self::overlap_mono(output, &input[ovl_pos..], mid, overlap_length),
            2 => Self::overlap_stereo(output, &input[2 * ovl_pos..], mid, overlap_length),
            _ => Self::overlap_multi(
                output,
                &input[channels * ovl_pos..],
                mid,
                overlap_length,
                channels,
            ),
        }
    }

    fn overlap_mono(output: &mut [f32], input: &[f32], mid: &[f32], overlap_length: usize) {
        let mut m1 = 0.0f32;
        let mut m2 = overlap_length as f32;
        for i in 0..overlap_length {
            output[i] = ((input[i] * m1) + (mid[i] * m2)) / overlap_length as f32;
            m1 += 1.0;
            m2 -= 1.0;
        }
    }

    fn overlap_stereo(output: &mut [f32], input: &[f32], mid: &[f32], overlap_length: usize) {
        let f_scale = 1.0f32 / overlap_length as f32;
        let mut f1 = 0.0f32;
        let mut f2 = 1.0f32;
        let mut i = 0;
        while i < 2 * overlap_length {
            output[i] = (input[i] * f1) + (mid[i] * f2);
            output[i + 1] = (input[i + 1] * f1) + (mid[i + 1] * f2);
            f1 += f_scale;
            f2 -= f_scale;
            i += 2;
        }
    }

    fn overlap_multi(
        output: &mut [f32],
        input: &[f32],
        mid: &[f32],
        overlap_length: usize,
        channels: usize,
    ) {
        let f_scale = 1.0f32 / overlap_length as f32;
        let mut f1 = 0.0f32;
        let mut f2 = 1.0f32;
        let mut i = 0;
        for _ in 0..overlap_length {
            for _ in 0..channels {
                output[i] = (input[i] * f1) + (mid[i] * f2);
                i += 1;
            }
            f1 += f_scale;
            f2 -= f_scale;
        }
    }

    fn calc_seq_parameters(&mut self) {
        const AUTOSEQ_TEMPO_LOW: f64 = 0.5;
        const AUTOSEQ_TEMPO_TOP: f64 = 2.0;
        const AUTOSEQ_AT_MIN: f64 = 90.0;
        const AUTOSEQ_AT_MAX: f64 = 40.0;
        const AUTOSEQ_K: f64 =
            (AUTOSEQ_AT_MAX - AUTOSEQ_AT_MIN) / (AUTOSEQ_TEMPO_TOP - AUTOSEQ_TEMPO_LOW);
        const AUTOSEQ_C: f64 = AUTOSEQ_AT_MIN - (AUTOSEQ_K * AUTOSEQ_TEMPO_LOW);

        const AUTOSEEK_AT_MIN: f64 = 20.0;
        const AUTOSEEK_AT_MAX: f64 = 15.0;
        const AUTOSEEK_K: f64 =
            (AUTOSEEK_AT_MAX - AUTOSEEK_AT_MIN) / (AUTOSEQ_TEMPO_TOP - AUTOSEQ_TEMPO_LOW);
        const AUTOSEEK_C: f64 = AUTOSEEK_AT_MIN - (AUTOSEEK_K * AUTOSEQ_TEMPO_LOW);

        fn check_limits(x: f64, mi: f64, ma: f64) -> f64 {
            x.clamp(mi, ma)
        }

        if self.auto_seq_setting {
            let seq = check_limits(
                AUTOSEQ_C + (AUTOSEQ_K * self.tempo),
                AUTOSEQ_AT_MAX,
                AUTOSEQ_AT_MIN,
            );
            self.sequence_ms = (seq + 0.5) as usize;
        }

        if self.auto_seek_setting {
            let seek = check_limits(
                AUTOSEEK_C + (AUTOSEEK_K * self.tempo),
                AUTOSEEK_AT_MAX,
                AUTOSEEK_AT_MIN,
            );
            self.seek_window_ms = (seek + 0.5) as usize;
        }

        self.seek_window_length = (self.sample_rate * self.sequence_ms) / 1000;
        if self.seek_window_length < 2 * self.overlap_length {
            self.seek_window_length = 2 * self.overlap_length;
        }
        self.seek_length = (self.sample_rate * self.seek_window_ms) / 1000;
    }

    fn process_samples(&mut self) {
        let channels = self.channels;

        while self.input_buffer.available_samples() >= self.sample_req && self.sample_req > 0 {
            let overlap_length = self.overlap_length;
            let mut offset;

            if !self.is_beginning {
                // Scan for the best overlapping position and overlap-add the tail
                // of the previous sequence (in `mid_buffer`).
                offset = {
                    let ref_pos = self.input_buffer.ptr_begin();
                    self.seek_best_overlap_position(ref_pos)
                };

                {
                    let out = self.output_buffer.ptr_end(overlap_length);
                    let inp = self.input_buffer.ptr_begin();
                    Self::overlap(out, inp, offset, &self.mid_buffer, overlap_length, channels);
                }
                self.output_buffer.commit_put(overlap_length);
                offset += overlap_length;
            } else {
                self.is_beginning = false;
                let skip = ((self.tempo * overlap_length as f64)
                    + (0.5 * self.seek_length as f64)
                    + 0.5) as i64;
                self.skip_fract -= skip as f64;
                if self.skip_fract <= -self.nominal_skip {
                    self.skip_fract = -self.nominal_skip;
                }
                offset = 0;
            }

            // Guard against buffer overrun (should not happen in practice).
            if self.input_buffer.available_samples()
                < offset + self.seek_window_length - overlap_length
            {
                continue;
            }

            // Copy the sequence body from input to output.
            let temp = self.seek_window_length - (2 * overlap_length);
            {
                let inp = self.input_buffer.ptr_begin();
                let src = &inp[channels * offset..];
                self.output_buffer.put_samples_from(src, temp);
            }

            // Stash the sequence tail into `mid_buffer` for the next overlap-add.
            {
                let n = channels * overlap_length;
                let start = channels * (offset + temp);
                let inp = self.input_buffer.ptr_begin();
                self.mid_buffer[..n].copy_from_slice(&inp[start..start + n]);
            }

            // Advance the input by the (fractional-corrected) skip amount.
            self.skip_fract += self.nominal_skip;
            let ovl_skip = self.skip_fract as i64;
            self.skip_fract -= ovl_skip as f64;
            if ovl_skip > 0 {
                self.input_buffer.receive_samples(ovl_skip as usize);
            }
        }
    }
}

impl Default for TimeStretch {
    fn default() -> Self {
        Self::new()
    }
}
