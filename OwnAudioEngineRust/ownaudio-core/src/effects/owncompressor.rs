//! OwnCompressor — log-domain compressor with look-ahead, soft knee, auto release,
//! peak/RMS detection, stereo link, mid/side, sidechain high-pass and parallel mix.
//!
//! ```text
//!   in ─> [L/R | M/S] ─┬───────────────> look-ahead line ──> VCA ─> [decode] ─> mix ─> out
//!                      └─> key ─> SC HPF ─> peak/RMS ─> dB ─> link ─> knee ─> hold ─> t90 ─┘
//! ```
//!
//! Buffers are sized in [`OwnCompressor::new`] for the longest look-ahead, so `process`
//! never allocates. The look-ahead is the effect's latency and the mixer follows it.

use super::owndsp::{self, SlidingMin, SvfCoeffs, TptSvf};
use super::{
    Effect, EffectType, LatencyParam, METER_CURRENT_GAIN, METER_INPUT_LEVEL, PARAM_ENABLED,
    PARAM_MIX,
};
use crate::denormal;
use crate::smoothing::{RampedParam, DEFAULT_SMOOTH_MS};

/// Param ID 2 — threshold in dBFS (-60 … 0).
pub const PARAM_THRESHOLD: u32 = 2;
/// Param ID 3 — ratio (1 … 100; 100 behaves like a limiter).
pub const PARAM_RATIO: u32 = 3;
/// Param ID 4 — knee width in dB (0 = hard … 24).
pub const PARAM_KNEE: u32 = 4;
/// Param ID 5 — attack time in ms, t90 definition (0.01 … 300).
pub const PARAM_ATTACK: u32 = 5;
/// Param ID 6 — release time in ms, t90 definition (5 … 5000).
pub const PARAM_RELEASE: u32 = 6;
/// Param ID 7 — programme-dependent auto release (≥ 0.5 = on).
pub const PARAM_AUTO_RELEASE: u32 = 7;
/// Param ID 8 — look-ahead in ms (0 … 10). Changes the reported latency.
pub const PARAM_LOOKAHEAD: u32 = 8;
/// Param ID 9 — detector: 0 = peak, 1 = RMS.
pub const PARAM_DETECTOR: u32 = 9;
/// Param ID 10 — topology: 0 = feed-forward, 1 = feedback.
pub const PARAM_TOPOLOGY: u32 = 10;
/// Param ID 11 — stereo link (0 = independent … 1 = fully linked).
pub const PARAM_STEREO_LINK: u32 = 11;
/// Param ID 12 — channel mode: 0 = left/right, 1 = mid/side.
pub const PARAM_CHANNEL_MODE: u32 = 12;
/// Param ID 13 — sidechain high-pass in Hz (0 = off, otherwise 20 … 500).
pub const PARAM_SIDECHAIN_HPF: u32 = 13;
/// Param ID 14 — makeup gain in dB (-24 … +24).
pub const PARAM_MAKEUP: u32 = 14;
/// Param ID 15 — automatic makeup gain (≥ 0.5 = on).
pub const PARAM_AUTO_MAKEUP: u32 = 15;
/// Param ID 16 — range: maximum gain reduction in dB (0 … 60).
pub const PARAM_RANGE: u32 = 16;

/// Most interleaved channels the effect takes; the first two are compressed, the rest
/// only ride the look-ahead line. Wider buffers pass through.
pub const MAX_CHANNELS: usize = 16;

const MAX_LOOKAHEAD_MS: f32 = 10.0;
const RMS_WINDOW_MS: f32 = 10.0;
const AUTO_RELEASE_MEMORY_MS: f32 = 1_000.0;
const AUTO_RELEASE_DEPTH_DB: f32 = 10.0;
const AUTO_RELEASE_SLOW_FACTOR: f32 = 4.0;
const UNITY_SNAP_DB: f32 = 1.0e-5;

/// Static curve: gain change in dB (≤ 0) for level `x_db`, quadratic soft knee.
#[inline]
fn gain_computer(
    x_db: f32,
    threshold_db: f32,
    inv_ratio: f32,
    knee_db: f32,
    inv_2knee: f32,
) -> f32 {
    // Same three-piece knee as the textbook, just folded into clamp + max so the
    // detector wobbling around the threshold doesn't trash the branch predictor.
    let over = x_db - threshold_db;
    let half = 0.5 * knee_db;
    let a = (over + half).clamp(0.0, knee_db);
    (inv_ratio - 1.0) * (a * a * inv_2knee + (over - half).max(0.0))
}

/// Log-domain compressor, see the module docs.
pub struct OwnCompressor {
    enabled: bool,
    sample_rate: f32,

    threshold_db: f32,
    ratio: f32,
    knee_db: f32,
    attack_ms: f32,
    release_ms: f32,
    auto_release: bool,
    lookahead_ms: f32,
    rms_detector: bool,
    feedback: bool,
    stereo_link: f32,
    mid_side: bool,
    sc_hpf_hz: f32,
    makeup_db: f32,
    auto_makeup: bool,
    range_db: f32,
    mix: f32,

    attack_coeff: f32,
    release_coeff: f32,
    release_slow_coeff: f32,
    memory_coeff: f32,
    rms_coeff: f32,
    sc_hpf: SvfCoeffs,
    lookahead_frames: usize,

    threshold_ramp: RampedParam,
    inv_ratio_ramp: RampedParam,
    makeup_ramp: RampedParam,
    mix_ramp: RampedParam,

    line: Vec<f32>,
    line_mask: usize,
    write_pos: usize,
    clock: u32,
    hold: [SlidingMin; 2],
    hpf: [TptSvf; 2],
    mean_square: [f32; 2],
    gr_db: [f32; 2],
    gr_memory_db: [f32; 2],
    fb_key: [f32; 2],

    meter_gain: f32,
    meter_input: f32,
}

impl OwnCompressor {
    /// Compressor on clean defaults: -18 dB, 4:1, 6 dB knee, 10 / 100 ms, peak, linked.
    pub fn new(sample_rate: f32) -> Self {
        let sample_rate = if sample_rate > 0.0 {
            sample_rate
        } else {
            44_100.0
        };
        let max_lookahead = (MAX_LOOKAHEAD_MS * 0.001 * sample_rate).ceil() as usize;
        let line_frames = (max_lookahead + 1).next_power_of_two();

        let mut comp = Self {
            enabled: true,
            sample_rate,
            threshold_db: -18.0,
            ratio: 4.0,
            knee_db: 6.0,
            attack_ms: 10.0,
            release_ms: 100.0,
            auto_release: false,
            lookahead_ms: 0.0,
            rms_detector: false,
            feedback: false,
            stereo_link: 1.0,
            mid_side: false,
            sc_hpf_hz: 0.0,
            makeup_db: 0.0,
            auto_makeup: false,
            range_db: 60.0,
            mix: 1.0,
            attack_coeff: 1.0,
            release_coeff: 1.0,
            release_slow_coeff: 1.0,
            memory_coeff: owndsp::tau_coeff(AUTO_RELEASE_MEMORY_MS, sample_rate),
            rms_coeff: owndsp::tau_coeff(RMS_WINDOW_MS, sample_rate),
            sc_hpf: SvfCoeffs::new(20.0, std::f32::consts::FRAC_1_SQRT_2, sample_rate),
            lookahead_frames: 0,
            threshold_ramp: RampedParam::new(-18.0, sample_rate, DEFAULT_SMOOTH_MS),
            inv_ratio_ramp: RampedParam::new(0.25, sample_rate, DEFAULT_SMOOTH_MS),
            makeup_ramp: RampedParam::new(0.0, sample_rate, DEFAULT_SMOOTH_MS),
            mix_ramp: RampedParam::new(1.0, sample_rate, DEFAULT_SMOOTH_MS),
            line: vec![0.0; line_frames * MAX_CHANNELS],
            line_mask: line_frames - 1,
            write_pos: 0,
            clock: 0,
            hold: [
                SlidingMin::new(max_lookahead + 1),
                SlidingMin::new(max_lookahead + 1),
            ],
            hpf: [TptSvf::default(); 2],
            mean_square: [0.0; 2],
            gr_db: [0.0; 2],
            gr_memory_db: [0.0; 2],
            fb_key: [0.0; 2],
            meter_gain: 1.0,
            meter_input: 0.0,
        };
        comp.update_ballistics();
        comp
    }

    fn update_ballistics(&mut self) {
        let fs = self.sample_rate;
        self.attack_coeff = owndsp::t90_coeff(self.attack_ms, fs);
        self.release_coeff = owndsp::t90_coeff(self.release_ms, fs);
        self.release_slow_coeff = owndsp::t90_coeff(self.release_ms * AUTO_RELEASE_SLOW_FACTOR, fs);
    }

    fn lookahead_latency(&self) -> LatencyParam {
        LatencyParam {
            param_id: PARAM_LOOKAHEAD,
            frames_per_unit: 0.001 * self.sample_rate,
            min_value: 0.0,
            max_value: MAX_LOOKAHEAD_MS,
            max_frames: self.line_mask as u32,
        }
    }

    #[inline]
    fn makeup_target(&self) -> f32 {
        // Auto makeup gives back half of what a full scale signal would lose,
        // a gut-feel number that doesn't blow up quiet material.
        let auto = if self.auto_makeup {
            -0.5 * self.threshold_db * (1.0 - 1.0 / self.ratio)
        } else {
            0.0
        };
        self.makeup_db + auto
    }

    fn settle_detector(&mut self) {
        self.hold.iter_mut().for_each(SlidingMin::reset);
        self.hpf.iter_mut().for_each(TptSvf::reset);
        self.mean_square = [0.0; 2];
        self.gr_db = [0.0; 2];
        self.gr_memory_db = [0.0; 2];
        self.fb_key = [0.0; 2];
        self.meter_gain = 1.0;
        self.meter_input = 0.0;
    }

    fn clear_state(&mut self) {
        self.line.fill(0.0);
        self.write_pos = 0;
        self.clock = 0;
        self.settle_detector();
    }

    /// Bypassed, the look-ahead line keeps running so the track stays where the mixer's
    /// delay compensation put it; the detector starts over from unity.
    fn bypass(&mut self, buffer: &mut [f32], stride: usize) {
        let (lookahead, mask) = (self.lookahead_frames, self.line_mask);
        for frame in buffer.chunks_exact_mut(stride) {
            let w = (self.write_pos & mask) * MAX_CHANNELS;
            let r = (self.write_pos.wrapping_sub(lookahead) & mask) * MAX_CHANNELS;
            self.line[w..w + stride].copy_from_slice(frame);
            frame.copy_from_slice(&self.line[r..r + stride]);
            self.write_pos = self.write_pos.wrapping_add(1);
        }
        self.settle_detector();
    }
}

impl Effect for OwnCompressor {
    fn effect_type(&self) -> EffectType {
        EffectType::OwnCompressor
    }

    #[allow(clippy::needless_range_loop)]
    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        self.threshold_ramp.begin_block();
        self.inv_ratio_ramp.begin_block();
        self.makeup_ramp.begin_block();
        self.mix_ramp.begin_block();

        let stride = channels as usize;
        if stride == 0 || stride > MAX_CHANNELS {
            return;
        }
        if !self.enabled {
            self.bypass(buffer, stride);
            return;
        }

        let stereo = stride >= 2;
        let detectors = if stereo { 2 } else { 1 };
        let lookahead = self.lookahead_frames;
        let use_hold = lookahead > 0 && !self.feedback;
        let window = (lookahead + 1) as u32;
        let mask = self.line_mask;
        let knee = self.knee_db;
        let inv_2knee = if knee > 0.0 { 0.5 / knee } else { 0.0 };
        let range = -self.range_db;
        let link = self.stereo_link;
        let hpf_on = self.sc_hpf_hz > 0.0;
        let sc_hpf = self.sc_hpf;
        let mid_side = self.mid_side && stereo;
        let (att, rel, rel_slow) = (
            self.attack_coeff,
            self.release_coeff,
            self.release_slow_coeff,
        );
        let auto_release = self.auto_release;
        let memory = self.memory_coeff;
        let rms = self.rms_detector;
        let rms_coeff = self.rms_coeff;

        let makeup_settled = self.makeup_ramp.is_settled(1.0e-6);
        let makeup_static = owndsp::db_to_lin(self.makeup_ramp.current());
        let mut block_min_gain = 1.0f32;
        let mut block_peak_db = f32::NEG_INFINITY;

        for frame in buffer.chunks_exact_mut(stride) {
            let threshold = self.threshold_ramp.advance();
            let inv_ratio = self.inv_ratio_ramp.advance();
            let makeup_lin = if makeup_settled {
                makeup_static
            } else {
                owndsp::db_to_lin(self.makeup_ramp.advance())
            };
            let mix = self.mix_ramp.advance();

            let in_l = frame[0];
            let in_r = if stereo { frame[1] } else { in_l };
            let x = if mid_side {
                [0.5 * (in_l + in_r), 0.5 * (in_l - in_r)]
            } else {
                [in_l, in_r]
            };

            // The line holds the raw frame, never the M/S pair, so bypass, a mode switch
            // or a look-ahead change later on read back exactly what went in.
            let w = (self.write_pos & mask) * MAX_CHANNELS;
            let r = (self.write_pos.wrapping_sub(lookahead) & mask) * MAX_CHANNELS;
            self.line[w..w + stride].copy_from_slice(frame);
            let dry = [self.line[r], self.line[r + usize::from(stereo)]];
            let delayed = if mid_side {
                [0.5 * (dry[0] + dry[1]), 0.5 * (dry[0] - dry[1])]
            } else {
                dry
            };

            // Feedback mode listens to last sample's compressed output, that's the
            // old-school vari-mu vibe. Feed-forward just listens to the input.
            let key = if self.feedback { self.fb_key } else { x };
            let mut level_db = [0.0f32; 2];
            for c in 0..detectors {
                let mut k = key[c];
                if hpf_on {
                    k = self.hpf[c].high_pass(k, &sc_hpf);
                }
                level_db[c] = if rms {
                    self.mean_square[c] = denormal::flush(
                        self.mean_square[c] + rms_coeff * (k * k - self.mean_square[c]),
                    );
                    0.5 * owndsp::lin_to_db(self.mean_square[c])
                } else {
                    owndsp::lin_to_db(k.abs())
                };
            }
            if !stereo {
                level_db[1] = level_db[0];
            }
            let loudest = level_db[0].max(level_db[1]);
            block_peak_db = block_peak_db.max(loudest);

            let mut gain = [1.0f32; 2];
            for c in 0..detectors {
                let detected = level_db[c] + link * (loudest - level_db[c]);
                let mut target =
                    gain_computer(detected, threshold, inv_ratio, knee, inv_2knee).max(range);
                if use_hold {
                    target = self.hold[c].push(self.clock, target, window);
                }

                // Auto release: the longer we've been squashing hard, the lazier the
                // release gets (up to 4x), so sustained bass doesn't pump the mix.
                let state = self.gr_db[c];
                let coeff = if target < state {
                    att
                } else if auto_release {
                    let depth = (-self.gr_memory_db[c] / AUTO_RELEASE_DEPTH_DB).clamp(0.0, 1.0);
                    rel + depth * (rel_slow - rel)
                } else {
                    rel
                };
                let mut next = state + coeff * (target - state);
                if next > -UNITY_SNAP_DB {
                    next = 0.0;
                }
                self.gr_db[c] = next;
                if auto_release {
                    self.gr_memory_db[c] = denormal::flush(
                        self.gr_memory_db[c] + memory * (next - self.gr_memory_db[c]),
                    );
                }
                gain[c] = owndsp::db_to_lin(next);
            }
            if !stereo {
                gain[1] = gain[0];
            }
            block_min_gain = block_min_gain.min(gain[0]).min(gain[1]);

            let compressed = [delayed[0] * gain[0], delayed[1] * gain[1]];
            self.fb_key = compressed;

            let wet = [compressed[0] * makeup_lin, compressed[1] * makeup_lin];
            let (wet_l, wet_r) = if mid_side {
                (wet[0] + wet[1], wet[0] - wet[1])
            } else {
                (wet[0], wet[1])
            };

            frame[0] = dry[0] + mix * (wet_l - dry[0]);
            if stereo {
                frame[1] = dry[1] + mix * (wet_r - dry[1]);
            }
            if stride > 2 {
                frame[2..].copy_from_slice(&self.line[r + 2..r + stride]);
            }

            self.write_pos = self.write_pos.wrapping_add(1);
            self.clock = self.clock.wrapping_add(1);
        }

        self.meter_gain = block_min_gain;
        self.meter_input = if block_peak_db.is_finite() {
            owndsp::db_to_lin(block_peak_db)
        } else {
            0.0
        };
    }

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        if !value.is_finite() {
            return false;
        }
        match param_id {
            PARAM_ENABLED => self.enabled = value >= 0.5,
            PARAM_MIX => {
                self.mix = value.clamp(0.0, 1.0);
                self.mix_ramp.set(self.mix);
            }
            PARAM_THRESHOLD => {
                self.threshold_db = value.clamp(-60.0, 0.0);
                self.threshold_ramp.set(self.threshold_db);
                self.makeup_ramp.set(self.makeup_target());
            }
            PARAM_RATIO => {
                self.ratio = value.clamp(1.0, 100.0);
                self.inv_ratio_ramp.set(1.0 / self.ratio);
                self.makeup_ramp.set(self.makeup_target());
            }
            PARAM_KNEE => self.knee_db = value.clamp(0.0, 24.0),
            PARAM_ATTACK => {
                self.attack_ms = value.clamp(0.01, 300.0);
                self.update_ballistics();
            }
            PARAM_RELEASE => {
                self.release_ms = value.clamp(5.0, 5_000.0);
                self.update_ballistics();
            }
            PARAM_AUTO_RELEASE => self.auto_release = value >= 0.5,
            PARAM_LOOKAHEAD => {
                self.lookahead_ms = value.clamp(0.0, MAX_LOOKAHEAD_MS);
                self.lookahead_frames = self.lookahead_latency().frames_for(value) as usize;
            }
            PARAM_DETECTOR => self.rms_detector = value >= 0.5,
            PARAM_TOPOLOGY => self.feedback = value >= 0.5,
            PARAM_STEREO_LINK => self.stereo_link = value.clamp(0.0, 1.0),
            PARAM_CHANNEL_MODE => self.mid_side = value >= 0.5,
            PARAM_SIDECHAIN_HPF => {
                self.sc_hpf_hz = if value < 20.0 { 0.0 } else { value.min(500.0) };
                if self.sc_hpf_hz > 0.0 {
                    self.sc_hpf = SvfCoeffs::new(
                        self.sc_hpf_hz,
                        std::f32::consts::FRAC_1_SQRT_2,
                        self.sample_rate,
                    );
                }
            }
            PARAM_MAKEUP => {
                self.makeup_db = value.clamp(-24.0, 24.0);
                self.makeup_ramp.set(self.makeup_target());
            }
            PARAM_AUTO_MAKEUP => {
                self.auto_makeup = value >= 0.5;
                self.makeup_ramp.set(self.makeup_target());
            }
            PARAM_RANGE => self.range_db = value.clamp(0.0, 60.0),
            _ => return false,
        }
        true
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        let flag = |b: bool| if b { 1.0 } else { 0.0 };
        Some(match param_id {
            PARAM_ENABLED => flag(self.enabled),
            PARAM_MIX => self.mix,
            PARAM_THRESHOLD => self.threshold_db,
            PARAM_RATIO => self.ratio,
            PARAM_KNEE => self.knee_db,
            PARAM_ATTACK => self.attack_ms,
            PARAM_RELEASE => self.release_ms,
            PARAM_AUTO_RELEASE => flag(self.auto_release),
            PARAM_LOOKAHEAD => self.lookahead_ms,
            PARAM_DETECTOR => flag(self.rms_detector),
            PARAM_TOPOLOGY => flag(self.feedback),
            PARAM_STEREO_LINK => self.stereo_link,
            PARAM_CHANNEL_MODE => flag(self.mid_side),
            PARAM_SIDECHAIN_HPF => self.sc_hpf_hz,
            PARAM_MAKEUP => self.makeup_db,
            PARAM_AUTO_MAKEUP => flag(self.auto_makeup),
            PARAM_RANGE => self.range_db,
            METER_CURRENT_GAIN => self.meter_gain,
            METER_INPUT_LEVEL => self.meter_input,
            _ => return None,
        })
    }

    fn reset(&mut self) {
        self.clear_state();
        self.threshold_ramp.reset(self.threshold_db);
        self.inv_ratio_ramp.reset(1.0 / self.ratio);
        self.makeup_ramp.reset(self.makeup_target());
        self.mix_ramp.reset(self.mix);
    }

    /// Always on as far as the chain goes: bypass runs inside `process`, the look-ahead
    /// delay must not drop out from under the delay compensation.
    fn is_enabled(&self) -> bool {
        true
    }

    fn set_enabled(&mut self, enabled: bool) {
        self.enabled = enabled;
    }

    fn latency_samples(&self) -> u32 {
        self.lookahead_frames as u32
    }

    fn latency_param(&self) -> Option<LatencyParam> {
        Some(self.lookahead_latency())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const FS: f32 = 48_000.0;

    fn square(amp: f32, frames: usize) -> Vec<f32> {
        (0..frames)
            .flat_map(|i| {
                let v = if (i / 24) % 2 == 0 { amp } else { -amp };
                [v, v]
            })
            .collect()
    }

    fn run(comp: &mut OwnCompressor, buf: &mut [f32]) {
        for chunk in buf.chunks_mut(512 * 2) {
            comp.process(chunk, 2);
        }
    }

    #[test]
    fn static_curve_matches_ratio() {
        let mut c = OwnCompressor::new(FS);
        c.set_param(PARAM_THRESHOLD, -18.0);
        c.set_param(PARAM_RATIO, 4.0);
        c.set_param(PARAM_KNEE, 0.0);
        c.set_param(PARAM_ATTACK, 1.0);
        let mut buf = square(0.5, 48_000);
        run(&mut c, &mut buf);
        let out_db = 20.0 * buf[buf.len() - 2].abs().log10();
        let expected = -18.0 + (20.0 * 0.5f32.log10() + 18.0) / 4.0;
        assert!(
            (out_db - expected).abs() < 0.05,
            "out {out_db} dB, expected {expected} dB"
        );
    }

    #[test]
    fn below_threshold_is_bit_transparent() {
        let mut c = OwnCompressor::new(FS);
        c.set_param(PARAM_THRESHOLD, -6.0);
        c.set_param(PARAM_KNEE, 0.0);
        let input = square(0.1, 4_800);
        let mut buf = input.clone();
        run(&mut c, &mut buf);
        assert_eq!(buf, input);
    }

    #[test]
    fn lookahead_reports_latency_and_delays_audio() {
        let mut c = OwnCompressor::new(FS);
        c.set_param(PARAM_LOOKAHEAD, 5.0);
        c.set_param(PARAM_THRESHOLD, 0.0);
        assert_eq!(c.latency_samples(), 240);
        let mut buf = vec![0.0f32; 1_024];
        buf[0] = 0.25;
        buf[1] = 0.25;
        c.process(&mut buf, 2);
        assert_eq!(buf[240 * 2], 0.25);
        assert_eq!(buf[0], 0.0);
    }

    #[test]
    fn lookahead_catches_the_transient() {
        let mut c = OwnCompressor::new(FS);
        c.set_param(PARAM_LOOKAHEAD, 5.0);
        c.set_param(PARAM_ATTACK, 1.0);
        c.set_param(PARAM_THRESHOLD, -20.0);
        c.set_param(PARAM_RATIO, 20.0);
        c.set_param(PARAM_KNEE, 0.0);
        let mut buf = vec![0.0f32; 4_096 * 2];
        for s in buf[2_000 * 2..].iter_mut() {
            *s = 1.0;
        }
        run(&mut c, &mut buf);
        let first = buf[(2_000 + 240) * 2];
        assert!(first < 0.2, "first sample of the step leaked at {first}");
    }

    #[test]
    fn gain_reduction_returns_to_exact_unity() {
        let mut c = OwnCompressor::new(FS);
        c.set_param(PARAM_RELEASE, 20.0);
        let mut buf = square(0.9, 4_800);
        run(&mut c, &mut buf);
        let mut silence = vec![0.0f32; 48_000 * 2];
        run(&mut c, &mut silence);
        assert_eq!(c.gr_db, [0.0, 0.0]);
        assert_eq!(c.get_param(METER_CURRENT_GAIN), Some(1.0));
    }

    #[test]
    fn auto_release_slows_down_after_sustained_compression() {
        let release_after = |sustain: usize| {
            let mut c = OwnCompressor::new(FS);
            c.set_param(PARAM_AUTO_RELEASE, 1.0);
            c.set_param(PARAM_RELEASE, 50.0);
            c.set_param(PARAM_ATTACK, 1.0);
            let mut loud = square(0.9, sustain);
            run(&mut c, &mut loud);
            let mut quiet = vec![0.0f32; 4_800 * 2];
            run(&mut c, &mut quiet);
            c.gr_db[0]
        };
        let short = release_after(480);
        let long = release_after(96_000);
        assert!(
            long < short,
            "sustained {long} dB should recover slower than {short} dB"
        );
    }

    #[test]
    fn mix_zero_returns_delayed_dry() {
        let mut c = OwnCompressor::new(FS);
        c.set_param(PARAM_MIX, 0.0);
        c.set_param(PARAM_THRESHOLD, -40.0);
        let input = square(0.8, 4_800);
        let mut buf = input.clone();
        run(&mut c, &mut buf);
        assert_eq!(buf, input);
    }

    #[test]
    fn mid_side_and_feedback_stay_finite() {
        let mut c = OwnCompressor::new(FS);
        c.set_param(PARAM_CHANNEL_MODE, 1.0);
        c.set_param(PARAM_TOPOLOGY, 1.0);
        c.set_param(PARAM_DETECTOR, 1.0);
        c.set_param(PARAM_SIDECHAIN_HPF, 120.0);
        c.set_param(PARAM_STEREO_LINK, 0.3);
        let mut buf: Vec<f32> = (0..96_000)
            .map(|i| ((i as f32) * 0.031).sin() * if i % 2 == 0 { 0.9 } else { 0.2 })
            .collect();
        run(&mut c, &mut buf);
        assert!(buf.iter().all(|s| s.is_finite() && s.abs() <= 1.0));
    }

    #[test]
    fn mono_is_supported() {
        let mut c = OwnCompressor::new(FS);
        let mut buf = vec![0.9f32; 4_800];
        c.process(&mut buf, 1);
        assert!(buf[4_799] < 0.9);
    }

    #[test]
    fn bypass_keeps_the_lookahead_delay() {
        let mut c = OwnCompressor::new(FS);
        c.set_param(PARAM_LOOKAHEAD, 5.0);
        c.set_param(PARAM_THRESHOLD, -40.0);
        c.set_enabled(false);
        assert!(c.is_enabled(), "the chain has to keep driving the line");
        let input = square(0.8, 4_800);
        let mut buf = input.clone();
        run(&mut c, &mut buf);
        assert!(buf[..240 * 2].iter().all(|&s| s == 0.0));
        assert_eq!(&buf[240 * 2..], &input[..input.len() - 240 * 2]);
        assert_eq!(c.get_param(PARAM_ENABLED), Some(0.0));
    }

    #[test]
    fn channels_past_the_stereo_pair_ride_the_lookahead() {
        let mut c = OwnCompressor::new(FS);
        c.set_param(PARAM_LOOKAHEAD, 2.0);
        c.set_param(PARAM_THRESHOLD, 0.0);
        let mut buf = vec![0.0f32; 1_024 * 6];
        buf[..6].copy_from_slice(&[0.1, 0.1, 0.2, 0.3, 0.4, 0.5]);
        c.process(&mut buf, 6);
        assert_eq!(&buf[96 * 6..97 * 6], &[0.1, 0.1, 0.2, 0.3, 0.4, 0.5]);
        assert!(buf[..96 * 6].iter().all(|&s| s == 0.0));
    }
}
