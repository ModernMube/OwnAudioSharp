//! OwnDynamicAmp — loudness based level rider (BS.1770 / EBU R128 style) with a true-peak
//! look-ahead limiter, the grown-up twin of [`DynamicAmp`](super::DynamicAmp).
//!
//! ```text
//!   in ──┬──────────────────────────────> × leveler ─┬─> look-ahead line ─> × limiter ─> out
//!        └─> K-weight ─> 400 ms momentary ─> gates ─> program ─> goal ─> slew ─> smooth ─┘
//!                                                    └─> 4x true peak ─> hold ─> box ─> release
//! ```
//!
//! Buffers are sized in [`OwnDynamicAmp::new`], so `process` never allocates. The look-ahead
//! is the effect's latency and the mixer follows it.

use super::owndsp::{self, SlidingMin};
use super::{
    Effect, EffectType, LatencyParam, METER_CURRENT_GAIN, METER_INPUT_LEVEL, PARAM_ENABLED,
    PARAM_MIX,
};
use crate::denormal;

/// Param ID 2 — target programme loudness in LUFS (-40 … -5).
pub const PARAM_TARGET_LOUDNESS: u32 = 2;
/// Param ID 3 — loudness integration window in seconds (0.4 … 60).
pub const PARAM_WINDOW: u32 = 3;
/// Param ID 4 — maximum boost in dB (0 … 30).
pub const PARAM_MAX_BOOST: u32 = 4;
/// Param ID 5 — maximum cut in dB (0 … 30).
pub const PARAM_MAX_CUT: u32 = 5;
/// Param ID 6 — fastest upward gain movement in dB/s (0.1 … 20).
pub const PARAM_RISE_RATE: u32 = 6;
/// Param ID 7 — fastest downward gain movement in dB/s (0.1 … 40).
pub const PARAM_FALL_RATE: u32 = 7;
/// Param ID 8 — tolerance window around the target in dB (0 … 6).
pub const PARAM_TOLERANCE: u32 = 8;
/// Param ID 9 — gain curve smoothing time in ms (10 … 5000).
pub const PARAM_SMOOTHING: u32 = 9;
/// Param ID 10 — relative gate in LU below the programme loudness (-40 … -1).
pub const PARAM_RELATIVE_GATE: u32 = 10;
/// Param ID 11 — absolute freeze threshold in LUFS (-90 … -30).
pub const PARAM_FREEZE_THRESHOLD: u32 = 11;
/// Param ID 12 — true-peak ceiling in dBTP (-20 … 0).
pub const PARAM_CEILING: u32 = 12;
/// Param ID 13 — true-peak limiter on/off (≥ 0.5 = on).
pub const PARAM_LIMITER: u32 = 13;
/// Param ID 14 — look-ahead in ms (1 … 10). Sets the reported latency.
pub const PARAM_LOOKAHEAD: u32 = 14;
/// Param ID 15 — limiter release in ms (10 … 2000).
pub const PARAM_LIMITER_RELEASE: u32 = 15;
/// Param ID 16 — gain the rider starts from and returns to on reset, dB (-30 … +30).
pub const PARAM_INITIAL_GAIN: u32 = 16;

/// Meter 1002 — gated programme loudness estimate of the input, LUFS.
pub const METER_PROGRAM_LOUDNESS: u32 = 1002;
/// Meter 1003 — momentary (400 ms) loudness of the input, LUFS.
pub const METER_MOMENTARY_LOUDNESS: u32 = 1003;
/// Meter 1004 — deepest limiter gain of the last block, linear.
pub const METER_LIMITER_GAIN: u32 = 1004;

/// Most interleaved channels the effect processes; wider buffers pass through.
pub const MAX_CHANNELS: usize = 16;

const MIN_LOOKAHEAD_MS: f32 = 1.0;
const MAX_LOOKAHEAD_MS: f32 = 10.0;
const SUB_BLOCK_SECONDS: f64 = 0.1;
const MOMENTARY_SUB_BLOCKS: usize = 4;
const LUFS_OFFSET: f64 = -0.691;
const SILENT_LUFS: f32 = -120.0;
const TP_TAPS: usize = 8;
const TP_PHASES: usize = 3;
const TP_DELAY: usize = TP_TAPS / 2;
const ACQUIRE_BOOST: f32 = 3.0;

/// Power (mean square, channel summed) to LUFS.
#[inline]
fn power_to_lufs(power: f64) -> f32 {
    if power <= 1.0e-20 {
        SILENT_LUFS
    } else {
        (LUFS_OFFSET + 10.0 * power.log10()) as f32
    }
}

/// LUFS to power (mean square, channel summed).
#[inline]
fn lufs_to_power(lufs: f32) -> f64 {
    10f64.powf((lufs as f64 - LUFS_OFFSET) / 10.0)
}

/// Normalised biquad coefficients, `a0 == 1`.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct BiquadCoeffs {
    /// Feed-forward 0.
    pub b0: f64,
    /// Feed-forward 1.
    pub b1: f64,
    /// Feed-forward 2.
    pub b2: f64,
    /// Feedback 1.
    pub a1: f64,
    /// Feedback 2.
    pub a2: f64,
}

/// BS.1770-4 K-weighting for any sample rate: high shelf plus RLB high-pass.
pub fn k_weighting(sample_rate: f32) -> (BiquadCoeffs, BiquadCoeffs) {
    let fs = sample_rate as f64;

    // The standard only hands out 48 kHz coefficients, so we rebuild them from the
    // analog prototype. Same numbers at 48k, and 44.1k doesn't get a wonky shelf.
    let f0 = 1_681.974_450_955_533;
    let g = 3.999_843_853_973_347;
    let q = 0.707_175_236_955_419_6;
    let k = (std::f64::consts::PI * f0 / fs).tan();
    let vh = 10f64.powf(g / 20.0);
    let vb = vh.powf(0.499_666_774_154_541_6);
    let a0 = 1.0 + k / q + k * k;
    let shelf = BiquadCoeffs {
        b0: (vh + vb * k / q + k * k) / a0,
        b1: 2.0 * (k * k - vh) / a0,
        b2: (vh - vb * k / q + k * k) / a0,
        a1: 2.0 * (k * k - 1.0) / a0,
        a2: (1.0 - k / q + k * k) / a0,
    };

    let f0 = 38.135_470_876_024_44;
    let q = 0.500_327_037_323_877_3;
    let k = (std::f64::consts::PI * f0 / fs).tan();
    let a0 = 1.0 + k / q + k * k;
    let high_pass = BiquadCoeffs {
        b0: 1.0,
        b1: -2.0,
        b2: 1.0,
        a1: 2.0 * (k * k - 1.0) / a0,
        a2: (1.0 - k / q + k * k) / a0,
    };

    (shelf, high_pass)
}

/// f32 transposed direct form II runtime copy of a [`BiquadCoeffs`].
#[derive(Clone, Copy, Debug, Default)]
struct Biquad {
    b0: f32,
    b1: f32,
    b2: f32,
    a1: f32,
    a2: f32,
}

impl From<BiquadCoeffs> for Biquad {
    fn from(c: BiquadCoeffs) -> Self {
        Self {
            b0: c.b0 as f32,
            b1: c.b1 as f32,
            b2: c.b2 as f32,
            a1: c.a1 as f32,
            a2: c.a2 as f32,
        }
    }
}

/// Biquad memory.
#[derive(Clone, Copy, Debug, Default)]
struct BiquadState {
    z1: f32,
    z2: f32,
}

impl BiquadState {
    #[inline]
    fn tick(&mut self, x: f32, c: &Biquad) -> f32 {
        let y = c.b0 * x + self.z1;
        self.z1 = denormal::flush(c.b1 * x - c.a1 * y + self.z2);
        self.z2 = denormal::flush(c.b2 * x - c.a2 * y);
        y
    }
}

/// Windowed sinc taps for the three in-between phases of a 4x true-peak estimate.
fn true_peak_kernel() -> [[f32; TP_TAPS]; TP_PHASES] {
    let mut kernel = [[0.0f32; TP_TAPS]; TP_PHASES];
    let radius = TP_TAPS as f64 / 2.0 + 0.5;
    for (p, row) in kernel.iter_mut().enumerate() {
        let frac = (p + 1) as f64 / 4.0;
        let mut taps = [0.0f64; TP_TAPS];
        for (j, tap) in taps.iter_mut().enumerate() {
            let d = frac - (j as f64 - (TP_DELAY as f64 - 1.0));
            let x = std::f64::consts::PI * d;
            let sinc = if d.abs() < 1.0e-12 { 1.0 } else { x.sin() / x };
            let window = 0.5 + 0.5 * (std::f64::consts::PI * d / radius).cos();
            *tap = sinc * window;
        }
        let sum: f64 = taps.iter().sum();
        for (dst, src) in row.iter_mut().zip(taps.iter()) {
            *dst = (src / sum) as f32;
        }
    }
    kernel
}

/// Loudness-based level rider with a true-peak look-ahead limiter, see the module docs.
pub struct OwnDynamicAmp {
    enabled: bool,
    mix: f32,
    sample_rate: f32,

    target_lufs: f32,
    window_s: f32,
    max_boost_db: f32,
    max_cut_db: f32,
    rise_rate: f32,
    fall_rate: f32,
    tolerance_db: f32,
    smoothing_ms: f32,
    relative_gate_lu: f32,
    freeze_lufs: f32,
    ceiling_db: f32,
    limiter_on: bool,
    lookahead_ms: f32,
    limiter_release_ms: f32,
    initial_gain_db: f32,

    shelf: Biquad,
    high_pass: Biquad,
    sub_len: usize,
    ema_alpha: f64,
    freeze_power: f64,
    relative_gate_ratio: f64,
    rise_step: f32,
    fall_step: f32,
    smooth_coeff: f32,
    ceiling_lin: f32,
    release_coeff: f32,
    lookahead_frames: usize,
    hold_len: usize,
    tp_kernel: [[f32; TP_TAPS]; TP_PHASES],

    kw: [[BiquadState; 2]; MAX_CHANNELS],
    sub_sum: f64,
    sub_count: usize,
    sub_ring: [f64; MOMENTARY_SUB_BLOCKS],
    sub_pos: usize,
    sub_filled: usize,
    ema_power: f64,
    ema_weight: f64,
    goal_db: f32,
    slew_db: f32,
    smooth1_db: f32,
    smooth2_db: f32,
    acquire: f32,

    tp_hist: [[f32; TP_TAPS]; MAX_CHANNELS],
    tp_pos: usize,
    prev_interval_peak: f32,
    line: Vec<f32>,
    line_mask: usize,
    write_pos: usize,
    clock: u32,
    hold: SlidingMin,
    box_ring: Vec<f32>,
    box_pos: usize,
    box_sum: f64,
    limiter_gain: f32,

    meter_gain: f32,
    meter_input: f32,
    meter_program: f32,
    meter_momentary: f32,
    meter_limiter: f32,
}

impl OwnDynamicAmp {
    /// Rider on streaming defaults: -14 LUFS, 8 s window, ±12 dB, -1 dBTP, 5 ms look-ahead.
    pub fn new(sample_rate: f32) -> Self {
        let sample_rate = if sample_rate > 0.0 {
            sample_rate
        } else {
            44_100.0
        };
        let max_lookahead = (MAX_LOOKAHEAD_MS * 0.001 * sample_rate).ceil() as usize;
        let line_frames = (max_lookahead + 1).next_power_of_two();
        let (shelf, high_pass) = k_weighting(sample_rate);

        let mut amp = Self {
            enabled: true,
            mix: 1.0,
            sample_rate,
            target_lufs: -14.0,
            window_s: 8.0,
            max_boost_db: 12.0,
            max_cut_db: 12.0,
            rise_rate: 1.5,
            fall_rate: 3.0,
            tolerance_db: 1.0,
            smoothing_ms: 300.0,
            relative_gate_lu: -10.0,
            freeze_lufs: -55.0,
            ceiling_db: -1.0,
            limiter_on: true,
            lookahead_ms: 5.0,
            limiter_release_ms: 150.0,
            initial_gain_db: 0.0,
            shelf: shelf.into(),
            high_pass: high_pass.into(),
            sub_len: ((SUB_BLOCK_SECONDS * sample_rate as f64).round() as usize).max(1),
            ema_alpha: 0.0,
            freeze_power: 0.0,
            relative_gate_ratio: 0.0,
            rise_step: 0.0,
            fall_step: 0.0,
            smooth_coeff: 1.0,
            ceiling_lin: 1.0,
            release_coeff: 1.0,
            lookahead_frames: TP_DELAY + 1,
            hold_len: 2,
            tp_kernel: true_peak_kernel(),
            kw: [[BiquadState::default(); 2]; MAX_CHANNELS],
            sub_sum: 0.0,
            sub_count: 0,
            sub_ring: [0.0; MOMENTARY_SUB_BLOCKS],
            sub_pos: 0,
            sub_filled: 0,
            ema_power: 0.0,
            ema_weight: 0.0,
            goal_db: 0.0,
            slew_db: 0.0,
            smooth1_db: 0.0,
            smooth2_db: 0.0,
            acquire: 1.0 + ACQUIRE_BOOST,
            tp_hist: [[0.0; TP_TAPS]; MAX_CHANNELS],
            tp_pos: 0,
            prev_interval_peak: 0.0,
            line: vec![0.0; line_frames * MAX_CHANNELS],
            line_mask: line_frames - 1,
            write_pos: 0,
            clock: 0,
            hold: SlidingMin::new(line_frames),
            box_ring: vec![1.0; line_frames],
            box_pos: 0,
            box_sum: 0.0,
            limiter_gain: 1.0,
            meter_gain: 1.0,
            meter_input: 0.0,
            meter_program: SILENT_LUFS,
            meter_momentary: SILENT_LUFS,
            meter_limiter: 1.0,
        };
        amp.update_detector();
        amp.update_ballistics();
        amp.update_limiter();
        amp.set_lookahead(amp.lookahead_ms);
        amp.clear_state();
        amp
    }

    fn update_detector(&mut self) {
        let steps = self.window_s as f64 / SUB_BLOCK_SECONDS;
        self.ema_alpha = (-1.0 / steps).exp();
        self.freeze_power = lufs_to_power(self.freeze_lufs);
        self.relative_gate_ratio = 10f64.powf(self.relative_gate_lu as f64 / 10.0);
    }

    fn update_ballistics(&mut self) {
        let fs = self.sample_rate;
        self.rise_step = self.rise_rate / fs;
        self.fall_step = self.fall_rate / fs;
        self.smooth_coeff = owndsp::tau_coeff(0.5 * self.smoothing_ms, fs);
    }

    fn update_limiter(&mut self) {
        self.ceiling_lin = owndsp::db_to_lin(self.ceiling_db);
        self.release_coeff = owndsp::t90_coeff(self.limiter_release_ms, self.sample_rate);
    }

    fn lookahead_latency(&self) -> LatencyParam {
        LatencyParam {
            param_id: PARAM_LOOKAHEAD,
            frames_per_unit: 0.001 * self.sample_rate,
            min_value: MIN_LOOKAHEAD_MS,
            max_value: MAX_LOOKAHEAD_MS,
            max_frames: self.line_mask as u32,
        }
    }

    fn set_lookahead(&mut self, ms: f32) {
        self.lookahead_ms = ms.clamp(MIN_LOOKAHEAD_MS, MAX_LOOKAHEAD_MS);
        let frames = self.lookahead_latency().frames_for(self.lookahead_ms) as usize;
        self.lookahead_frames = frames.max(TP_DELAY + 1);
        self.hold_len = self.lookahead_frames + 1 - TP_DELAY;
        self.clear_limiter();
    }

    fn clear_limiter(&mut self) {
        self.hold.reset();
        self.box_ring.fill(1.0);
        self.box_pos = 0;
        self.box_sum = self.hold_len as f64;
        self.limiter_gain = 1.0;
    }

    fn clear_state(&mut self) {
        self.kw = [[BiquadState::default(); 2]; MAX_CHANNELS];
        self.sub_sum = 0.0;
        self.sub_count = 0;
        self.sub_ring = [0.0; MOMENTARY_SUB_BLOCKS];
        self.sub_pos = 0;
        self.sub_filled = 0;
        self.ema_power = 0.0;
        self.ema_weight = 0.0;
        self.goal_db = self.initial_gain_db;
        self.slew_db = self.initial_gain_db;
        self.smooth1_db = self.initial_gain_db;
        self.smooth2_db = self.initial_gain_db;
        self.acquire = 1.0 + ACQUIRE_BOOST;
        self.tp_hist = [[0.0; TP_TAPS]; MAX_CHANNELS];
        self.tp_pos = 0;
        self.prev_interval_peak = 0.0;
        self.line.fill(0.0);
        self.write_pos = 0;
        self.clock = 0;
        self.clear_limiter();
        self.meter_gain = owndsp::db_to_lin(self.initial_gain_db);
        self.meter_input = 0.0;
        self.meter_program = SILENT_LUFS;
        self.meter_momentary = SILENT_LUFS;
        self.meter_limiter = 1.0;
    }

    /// Bypassed, the look-ahead line keeps running so the track stays where the mixer's
    /// delay compensation put it. The rider keeps its gain, the limiter starts over.
    fn bypass(&mut self, buffer: &mut [f32], stride: usize) {
        let (lookahead, mask) = (self.lookahead_frames, self.line_mask);
        for frame in buffer.chunks_exact_mut(stride) {
            let w = (self.write_pos & mask) * MAX_CHANNELS;
            let r = (self.write_pos.wrapping_sub(lookahead) & mask) * MAX_CHANNELS;
            self.line[w..w + stride].copy_from_slice(frame);
            frame.copy_from_slice(&self.line[r..r + stride]);
            self.write_pos = self.write_pos.wrapping_add(1);
        }
        self.clear_limiter();
        self.meter_limiter = 1.0;
    }

    fn end_sub_block(&mut self) {
        self.sub_ring[self.sub_pos] = self.sub_sum / self.sub_len as f64;
        self.sub_pos = (self.sub_pos + 1) % MOMENTARY_SUB_BLOCKS;
        self.sub_filled = (self.sub_filled + 1).min(MOMENTARY_SUB_BLOCKS);
        self.sub_sum = 0.0;
        self.sub_count = 0;

        // Re-sum the box every 100 ms, the running f64 sum creeps a hair each sample
        // and after an hour of audio that hair turns into a real gain error.
        self.box_sum = self.box_ring[..self.hold_len]
            .iter()
            .map(|&v| v as f64)
            .sum();

        if self.sub_filled < MOMENTARY_SUB_BLOCKS {
            return;
        }

        let momentary = self.sub_ring.iter().sum::<f64>() / MOMENTARY_SUB_BLOCKS as f64;
        self.meter_momentary = power_to_lufs(momentary);

        // Quiet verses, fades and pauses don't get a vote. Otherwise the rider would crank
        // up every pianissimo and pump the room noise in the gaps.
        let above_freeze = momentary >= self.freeze_power;
        let above_relative = self.ema_weight <= 0.0
            || momentary >= (self.ema_power / self.ema_weight) * self.relative_gate_ratio;

        if above_freeze && above_relative {
            let a = self.ema_alpha;
            self.ema_power = a * self.ema_power + (1.0 - a) * momentary;
            self.ema_weight = a * self.ema_weight + (1.0 - a);
            if self.ema_power < 1.0e-30 {
                self.ema_power = 0.0;
            }
        }

        if self.ema_weight > 0.0 {
            let program = power_to_lufs(self.ema_power / self.ema_weight);
            self.meter_program = program;
            if above_freeze && above_relative {
                let desired =
                    (self.target_lufs - program).clamp(-self.max_cut_db, self.max_boost_db);
                if (desired - self.goal_db).abs() > self.tolerance_db {
                    self.goal_db = desired;
                }
            }
            self.acquire = 1.0 + ACQUIRE_BOOST * (1.0 - self.ema_weight as f32).max(0.0);
        }
        self.goal_db = self.goal_db.clamp(-self.max_cut_db, self.max_boost_db);
    }
}

impl Effect for OwnDynamicAmp {
    fn effect_type(&self) -> EffectType {
        EffectType::OwnDynamicAmp
    }

    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        let stride = channels as usize;
        if stride == 0 || stride > MAX_CHANNELS {
            return;
        }
        if !self.enabled {
            self.bypass(buffer, stride);
            return;
        }

        let lookahead = self.lookahead_frames;
        let hold_len = self.hold_len;
        let mask = self.line_mask;
        let shelf = self.shelf;
        let high_pass = self.high_pass;
        let kernel = self.tp_kernel;
        let smooth = self.smooth_coeff;
        let ceiling = self.ceiling_lin;
        let limiter_on = self.limiter_on;
        let release = self.release_coeff;

        let mut block_input_peak = 0.0f32;
        let mut block_limiter_min = 1.0f32;

        for frame in buffer.chunks_exact_mut(stride) {
            let mut energy = 0.0f32;
            for (c, &x) in frame.iter().enumerate() {
                let stage = &mut self.kw[c];
                let shelved = stage[0].tick(x, &shelf);
                let k = stage[1].tick(shelved, &high_pass);
                energy += k * k;
                block_input_peak = block_input_peak.max(x.abs());
            }
            self.sub_sum += energy as f64;
            self.sub_count += 1;
            if self.sub_count >= self.sub_len {
                self.end_sub_block();
            }

            // Slew limit first, then two soft one-poles round off the corners. The first few
            // seconds get a speed boost so a fresh track isn't stuck at the wrong level.
            let rise = self.rise_step * self.acquire;
            let fall = self.fall_step * self.acquire;
            self.slew_db += (self.goal_db - self.slew_db).clamp(-fall, rise);
            self.smooth1_db += smooth * (self.slew_db - self.smooth1_db);
            self.smooth2_db += smooth * (self.smooth1_db - self.smooth2_db);
            let level_gain = owndsp::db_to_lin(self.smooth2_db);

            let w = self.write_pos & mask;
            let tp = self.tp_pos;
            let mut interval_peak = 0.0f32;
            for (c, &x) in frame.iter().enumerate() {
                let y = x * level_gain;
                self.line[w * MAX_CHANNELS + c] = y;
                let hist = &mut self.tp_hist[c];
                hist[tp] = y;
                let at = |j: usize| hist[(tp + 1 + j) % TP_TAPS];
                let mut peak = at(TP_DELAY - 1).abs().max(at(TP_DELAY).abs());
                for row in &kernel {
                    let mut acc = 0.0f32;
                    for (j, &h) in row.iter().enumerate() {
                        acc += h * at(j);
                    }
                    peak = peak.max(acc.abs());
                }
                interval_peak = interval_peak.max(peak);
            }
            self.tp_pos = (tp + 1) % TP_TAPS;

            let slot_peak = interval_peak.max(self.prev_interval_peak);
            self.prev_interval_peak = interval_peak;
            let required = if limiter_on && slot_peak > ceiling {
                ceiling / slot_peak
            } else {
                1.0
            };

            // Hold the lowest gain for the look-ahead, then box-average it. The ramp is exactly
            // as long as the line, so the gain bottoms out right when the peak comes out. No clip.
            let held = self.hold.push(self.clock, required, hold_len as u32);
            self.box_sum += held as f64 - self.box_ring[self.box_pos] as f64;
            self.box_ring[self.box_pos] = held;
            self.box_pos += 1;
            if self.box_pos >= hold_len {
                self.box_pos = 0;
            }
            let target = ((self.box_sum / hold_len as f64) as f32).min(1.0);
            self.limiter_gain = if target < self.limiter_gain {
                target
            } else if target >= 1.0 && self.limiter_gain > 1.0 - 1.0e-6 {
                1.0
            } else {
                self.limiter_gain + release * (target - self.limiter_gain)
            };
            block_limiter_min = block_limiter_min.min(self.limiter_gain);

            let r = self.write_pos.wrapping_sub(lookahead) & mask;
            let lim = self.limiter_gain;
            for (c, out) in frame.iter_mut().enumerate() {
                *out = self.line[r * MAX_CHANNELS + c] * lim;
            }

            self.write_pos = self.write_pos.wrapping_add(1);
            self.clock = self.clock.wrapping_add(1);
        }

        self.meter_gain = owndsp::db_to_lin(self.smooth2_db);
        self.meter_input = block_input_peak;
        self.meter_limiter = block_limiter_min;
    }

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        if !value.is_finite() {
            return false;
        }
        match param_id {
            PARAM_ENABLED => self.enabled = value >= 0.5,
            PARAM_MIX => self.mix = value.clamp(0.0, 1.0),
            PARAM_TARGET_LOUDNESS => self.target_lufs = value.clamp(-40.0, -5.0),
            PARAM_WINDOW => {
                self.window_s = value.clamp(0.4, 60.0);
                self.update_detector();
            }
            PARAM_MAX_BOOST => self.max_boost_db = value.clamp(0.0, 30.0),
            PARAM_MAX_CUT => self.max_cut_db = value.clamp(0.0, 30.0),
            PARAM_RISE_RATE => {
                self.rise_rate = value.clamp(0.1, 20.0);
                self.update_ballistics();
            }
            PARAM_FALL_RATE => {
                self.fall_rate = value.clamp(0.1, 40.0);
                self.update_ballistics();
            }
            PARAM_TOLERANCE => self.tolerance_db = value.clamp(0.0, 6.0),
            PARAM_SMOOTHING => {
                self.smoothing_ms = value.clamp(10.0, 5_000.0);
                self.update_ballistics();
            }
            PARAM_RELATIVE_GATE => {
                self.relative_gate_lu = value.clamp(-40.0, -1.0);
                self.update_detector();
            }
            PARAM_FREEZE_THRESHOLD => {
                self.freeze_lufs = value.clamp(-90.0, -30.0);
                self.update_detector();
            }
            PARAM_CEILING => {
                self.ceiling_db = value.clamp(-20.0, 0.0);
                self.update_limiter();
            }
            PARAM_LIMITER => self.limiter_on = value >= 0.5,
            PARAM_LOOKAHEAD => self.set_lookahead(value),
            PARAM_LIMITER_RELEASE => {
                self.limiter_release_ms = value.clamp(10.0, 2_000.0);
                self.update_limiter();
            }
            PARAM_INITIAL_GAIN => {
                self.initial_gain_db = value.clamp(-30.0, 30.0);
                self.goal_db = self.initial_gain_db;
                self.slew_db = self.initial_gain_db;
                self.smooth1_db = self.initial_gain_db;
                self.smooth2_db = self.initial_gain_db;
                self.meter_gain = owndsp::db_to_lin(self.initial_gain_db);
            }
            _ => return false,
        }
        true
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        let flag = |b: bool| if b { 1.0 } else { 0.0 };
        Some(match param_id {
            PARAM_ENABLED => flag(self.enabled),
            PARAM_MIX => self.mix,
            PARAM_TARGET_LOUDNESS => self.target_lufs,
            PARAM_WINDOW => self.window_s,
            PARAM_MAX_BOOST => self.max_boost_db,
            PARAM_MAX_CUT => self.max_cut_db,
            PARAM_RISE_RATE => self.rise_rate,
            PARAM_FALL_RATE => self.fall_rate,
            PARAM_TOLERANCE => self.tolerance_db,
            PARAM_SMOOTHING => self.smoothing_ms,
            PARAM_RELATIVE_GATE => self.relative_gate_lu,
            PARAM_FREEZE_THRESHOLD => self.freeze_lufs,
            PARAM_CEILING => self.ceiling_db,
            PARAM_LIMITER => flag(self.limiter_on),
            PARAM_LOOKAHEAD => self.lookahead_ms,
            PARAM_LIMITER_RELEASE => self.limiter_release_ms,
            PARAM_INITIAL_GAIN => self.initial_gain_db,
            METER_CURRENT_GAIN => self.meter_gain,
            METER_INPUT_LEVEL => self.meter_input,
            METER_PROGRAM_LOUDNESS => self.meter_program,
            METER_MOMENTARY_LOUDNESS => self.meter_momentary,
            METER_LIMITER_GAIN => self.meter_limiter,
            _ => return None,
        })
    }

    fn reset(&mut self) {
        self.clear_state();
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

    fn sine(freq: f32, amp: f32, seconds: f32, channels: usize) -> Vec<f32> {
        let frames = (seconds * FS) as usize;
        let step = std::f64::consts::TAU * freq as f64 / FS as f64;
        (0..frames)
            .flat_map(|i| std::iter::repeat_n(amp * (step * i as f64).sin() as f32, channels))
            .collect()
    }

    fn db(x: f32) -> f32 {
        20.0 * x.max(1.0e-12).log10()
    }

    fn run(amp: &mut OwnDynamicAmp, buf: &mut [f32], channels: u16, block: usize) {
        for chunk in buf.chunks_mut(block * channels as usize) {
            amp.process(chunk, channels);
        }
    }

    fn gain_db(amp: &OwnDynamicAmp) -> f32 {
        db(amp.get_param(METER_CURRENT_GAIN).unwrap())
    }

    #[test]
    fn k_weighting_matches_bs1770_at_48k() {
        let (shelf, hp) = k_weighting(48_000.0);
        let close = |a: f64, b: f64| (a - b).abs() < 1.0e-8;
        assert!(close(shelf.b0, 1.535_124_859_586_97));
        assert!(close(shelf.b1, -2.691_696_189_406_38));
        assert!(close(shelf.b2, 1.198_392_810_852_85));
        assert!(close(shelf.a1, -1.690_659_293_182_41));
        assert!(close(shelf.a2, 0.732_480_774_215_85));
        assert!(close(hp.a1, -1.990_047_454_833_98));
        assert!(close(hp.a2, 0.990_072_250_366_21));
    }

    #[test]
    fn full_scale_997_hz_reads_minus_3_01_lufs() {
        let mut amp = OwnDynamicAmp::new(FS);
        let mut buf = sine(997.0, 1.0, 2.0, 1);
        run(&mut amp, &mut buf, 1, 512);
        let m = amp.get_param(METER_MOMENTARY_LOUDNESS).unwrap();
        assert!((m + 3.01).abs() < 0.05, "momentary {m} LUFS");
    }

    #[test]
    fn quiet_source_is_brought_up_to_target() {
        let mut amp = OwnDynamicAmp::new(FS);
        amp.set_param(PARAM_TARGET_LOUDNESS, -14.0);
        amp.set_param(PARAM_MAX_BOOST, 20.0);
        let mut buf = sine(997.0, 10f32.powf(-30.0 / 20.0), 40.0, 2);
        run(&mut amp, &mut buf, 2, 512);
        let g = gain_db(&amp);
        assert!((g - 16.0).abs() <= 1.1, "gain {g} dB, expected about +16");
    }

    #[test]
    fn loud_source_is_pulled_down_to_target() {
        let mut amp = OwnDynamicAmp::new(FS);
        amp.set_param(PARAM_TARGET_LOUDNESS, -14.0);
        let mut buf = sine(997.0, 10f32.powf(-4.0 / 20.0), 30.0, 2);
        run(&mut amp, &mut buf, 2, 512);
        let g = gain_db(&amp);
        assert!((g + 10.0).abs() <= 1.1, "gain {g} dB, expected about -10");
    }

    #[test]
    fn boost_never_exceeds_max_boost() {
        let mut amp = OwnDynamicAmp::new(FS);
        amp.set_param(PARAM_MAX_BOOST, 6.0);
        let mut buf = sine(997.0, 10f32.powf(-40.0 / 20.0), 30.0, 2);
        run(&mut amp, &mut buf, 2, 512);
        let g = gain_db(&amp);
        assert!(g <= 6.0 + 1.0e-3 && g > 5.0, "gain {g} dB");
    }

    #[test]
    fn material_inside_tolerance_is_bit_transparent() {
        let mut amp = OwnDynamicAmp::new(FS);
        amp.set_param(PARAM_TARGET_LOUDNESS, -14.0);
        amp.set_param(PARAM_TOLERANCE, 1.0);
        let input = sine(997.0, 10f32.powf(-14.5 / 20.0), 10.0, 2);
        let mut buf = input.clone();
        run(&mut amp, &mut buf, 2, 480);
        let lat = amp.latency_samples() as usize * 2;
        assert_eq!(&buf[lat..], &input[..input.len() - lat]);
    }

    #[test]
    fn quiet_passage_below_relative_gate_keeps_the_gain() {
        let mut amp = OwnDynamicAmp::new(FS);
        amp.set_param(PARAM_TARGET_LOUDNESS, -14.0);
        let mut loud = sine(997.0, 10f32.powf(-20.0 / 20.0), 30.0, 2);
        run(&mut amp, &mut loud, 2, 512);
        let before = gain_db(&amp);
        let mut quiet = sine(997.0, 10f32.powf(-36.0 / 20.0), 10.0, 2);
        run(&mut amp, &mut quiet, 2, 512);
        let after = gain_db(&amp);
        assert!(
            (after - before).abs() < 0.3,
            "a pianissimo passage moved the gain from {before} to {after} dB"
        );
    }

    #[test]
    fn silence_freezes_the_gain() {
        let mut amp = OwnDynamicAmp::new(FS);
        let mut music = sine(440.0, 0.05, 30.0, 2);
        run(&mut amp, &mut music, 2, 512);
        let before = gain_db(&amp);
        let mut silence = vec![0.0f32; (FS as usize) * 20 * 2];
        run(&mut amp, &mut silence, 2, 512);
        let after = gain_db(&amp);
        assert!((after - before).abs() < 0.3, "{before} -> {after} dB");
    }

    #[test]
    fn true_peak_stays_under_the_ceiling() {
        let mut amp = OwnDynamicAmp::new(FS);
        amp.set_param(PARAM_MAX_BOOST, 0.0);
        amp.set_param(PARAM_MAX_CUT, 0.0);
        amp.set_param(PARAM_CEILING, -1.0);
        let frames = (FS * 2.0) as usize;
        let mut buf: Vec<f32> = (0..frames)
            .flat_map(|i| {
                let phase = std::f64::consts::FRAC_PI_2 * i as f64 + std::f64::consts::FRAC_PI_4;
                let v = phase.sin() as f32;
                [v, v]
            })
            .collect();
        run(&mut amp, &mut buf, 2, 512);
        let tail = &buf[buf.len() - 4_800..];
        let sample_peak = tail.iter().fold(0.0f32, |m, v| m.max(v.abs()));
        let true_peak = sample_peak / std::f32::consts::FRAC_1_SQRT_2;
        assert!(db(true_peak) <= -0.9, "true peak {} dBTP", db(true_peak));
        assert!(
            db(true_peak) >= -1.6,
            "limiter pulled too hard: {} dBTP",
            db(true_peak)
        );
    }

    #[test]
    fn sudden_peak_never_passes_the_ceiling() {
        let mut amp = OwnDynamicAmp::new(FS);
        amp.set_param(PARAM_MAX_BOOST, 0.0);
        amp.set_param(PARAM_MAX_CUT, 0.0);
        let mut buf = vec![0.0f32; 48_000 * 2];
        for s in buf[20_000 * 2..20_100 * 2].iter_mut() {
            *s = 1.0;
        }
        run(&mut amp, &mut buf, 2, 256);
        let peak = buf.iter().fold(0.0f32, |m, v| m.max(v.abs()));
        assert!(db(peak) <= -1.0 + 1.0e-3, "sample peak {} dB", db(peak));
    }

    #[test]
    fn output_does_not_depend_on_block_size() {
        let input = sine(220.0, 0.1, 6.0, 2);
        let mut a = input.clone();
        let mut b = input.clone();
        let mut fx_a = OwnDynamicAmp::new(FS);
        let mut fx_b = OwnDynamicAmp::new(FS);
        run(&mut fx_a, &mut a, 2, 64);
        run(&mut fx_b, &mut b, 2, 1_001);
        assert_eq!(a, b);
    }

    #[test]
    fn lookahead_reports_latency_and_delays_audio() {
        let mut amp = OwnDynamicAmp::new(FS);
        amp.set_param(PARAM_LOOKAHEAD, 5.0);
        assert_eq!(amp.latency_samples(), 240);
        let mut buf = vec![0.0f32; 1_024];
        buf[0] = 0.25;
        buf[1] = 0.25;
        amp.process(&mut buf, 2);
        assert_eq!(buf[240 * 2], 0.25);
        assert_eq!(buf[0], 0.0);
    }

    #[test]
    fn initial_gain_seeds_and_reset_returns_to_it() {
        let mut amp = OwnDynamicAmp::new(FS);
        amp.set_param(PARAM_INITIAL_GAIN, 6.0);
        assert!((gain_db(&amp) - 6.0).abs() < 1.0e-3);
        let mut buf = sine(997.0, 0.3, 5.0, 2);
        run(&mut amp, &mut buf, 2, 512);
        amp.reset();
        assert!((gain_db(&amp) - 6.0).abs() < 1.0e-3);
    }

    #[test]
    fn reset_reproduces_the_first_pass() {
        let input = sine(330.0, 0.2, 3.0, 2);
        let mut amp = OwnDynamicAmp::new(FS);
        let mut first = input.clone();
        run(&mut amp, &mut first, 2, 512);
        amp.reset();
        let mut second = input.clone();
        run(&mut amp, &mut second, 2, 512);
        assert_eq!(first, second);
    }

    #[test]
    fn mono_and_multichannel_stay_finite() {
        for channels in [1u16, 6] {
            let mut amp = OwnDynamicAmp::new(44_100.0);
            let mut seed = 0x1234_5678u32;
            let mut buf: Vec<f32> = (0..44_100 * 4 * channels as usize)
                .map(|_| {
                    seed = seed.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
                    (seed >> 8) as f32 / (1u32 << 24) as f32 * 2.0 - 1.0
                })
                .collect();
            run(&mut amp, &mut buf, channels, 333);
            assert!(buf.iter().all(|s| s.is_finite() && s.abs() <= 1.0));
        }
    }

    #[test]
    fn params_clamp_and_unknown_or_nan_are_rejected() {
        let mut amp = OwnDynamicAmp::new(FS);
        amp.set_param(PARAM_TARGET_LOUDNESS, 3.0);
        assert_eq!(amp.get_param(PARAM_TARGET_LOUDNESS), Some(-5.0));
        amp.set_param(PARAM_LOOKAHEAD, 0.0);
        assert_eq!(amp.get_param(PARAM_LOOKAHEAD), Some(1.0));
        assert_eq!(amp.latency_samples(), 48);
        amp.set_param(PARAM_CEILING, 3.0);
        assert_eq!(amp.get_param(PARAM_CEILING), Some(0.0));
        assert!(!amp.set_param(PARAM_WINDOW, f32::NAN));
        assert!(!amp.set_param(999, 1.0));
        assert!(!amp.set_param(METER_CURRENT_GAIN, 1.0));
        assert_eq!(amp.get_param(999), None);
    }

    #[test]
    fn disabled_is_a_pure_lookahead_delay() {
        let mut amp = OwnDynamicAmp::new(FS);
        amp.set_enabled(false);
        assert!(amp.is_enabled(), "the chain has to keep driving the line");
        let input = sine(440.0, 0.5, 0.1, 2);
        let mut buf = input.clone();
        run(&mut amp, &mut buf, 2, 512);
        let lat = amp.latency_samples() as usize * 2;
        assert!(buf[..lat].iter().all(|&s| s == 0.0));
        assert_eq!(&buf[lat..], &input[..input.len() - lat]);
        assert_eq!(amp.get_param(PARAM_ENABLED), Some(0.0));
    }
}
