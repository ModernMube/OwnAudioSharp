//! OwnDelay — tape style stereo delay: saturation, diffusion and low/high cut inside
//! the loop, glide or crossfade time changes, wow, ping-pong, ducking, width, freeze.
//!
//! ```text
//!   in ─┬─> (+) ─> drive ─> soft clip ─> tape ─> Hermite read (glide/xfade + LFO)
//!       │    ▲                                         │
//!       │    └── fb / cross-fb <── low/high cut <── diffusion ─> ducker ─> width ─┐
//!       └──────────────────────────────── dry ────────────────────────────────── mix ─> out
//! ```
//!
//! The tape is a power-of-two ring sized in [`OwnDelay::new`], so `process` never allocates.

use super::owndsp::{self, Diffuser, Lfo, SvfCoeffs, TptSvf};
use super::{Effect, EffectType, METER_CURRENT_GAIN, METER_INPUT_LEVEL, PARAM_ENABLED, PARAM_MIX};
use crate::denormal;
use crate::smoothing::{RampedParam, DEFAULT_SMOOTH_MS};

/// Param ID 2 — left delay time in ms (15 … 4000).
pub const PARAM_TIME_L: u32 = 2;
/// Param ID 3 — right delay time in ms (15 … 4000).
pub const PARAM_TIME_R: u32 = 3;
/// Param ID 4 — feedback (0 … 1).
pub const PARAM_FEEDBACK: u32 = 4;
/// Param ID 5 — cross feedback, L → R and R → L (0 … 1).
pub const PARAM_CROSS_FEEDBACK: u32 = 5;
/// Param ID 6 — time change mode: 0 = tape glide (pitch bend), 1 = crossfade.
pub const PARAM_TIME_MODE: u32 = 6;
/// Param ID 7 — glide time constant in ms (5 … 2000).
pub const PARAM_GLIDE: u32 = 7;
/// Param ID 8 — saturation drive in dB (0 … 24).
pub const PARAM_DRIVE: u32 = 8;
/// Param ID 9 — in-loop low cut in Hz (20 … 2000).
pub const PARAM_LOW_CUT: u32 = 9;
/// Param ID 10 — in-loop high cut in Hz (500 … 20000).
pub const PARAM_HIGH_CUT: u32 = 10;
/// Param ID 11 — diffusion amount (0 … 1).
pub const PARAM_DIFFUSION: u32 = 11;
/// Param ID 12 — wow modulation rate in Hz (0.05 … 10).
pub const PARAM_MOD_RATE: u32 = 12;
/// Param ID 13 — wow modulation depth in ms (0 … 5).
pub const PARAM_MOD_DEPTH: u32 = 13;
/// Param ID 14 — ducking amount (0 = off … 1).
pub const PARAM_DUCK_AMOUNT: u32 = 14;
/// Param ID 15 — ducking threshold in dBFS (-60 … 0).
pub const PARAM_DUCK_THRESHOLD: u32 = 15;
/// Param ID 16 — ducker attack in ms (0.5 … 200).
pub const PARAM_DUCK_ATTACK: u32 = 16;
/// Param ID 17 — ducker release in ms (10 … 2000).
pub const PARAM_DUCK_RELEASE: u32 = 17;
/// Param ID 18 — wet stereo width (0 = mono … 2).
pub const PARAM_WIDTH: u32 = 18;
/// Param ID 19 — freeze (≥ 0.5 = input muted, loop gain 1, filters bypassed).
pub const PARAM_FREEZE: u32 = 19;

/// Longest delay time in ms. Tape memory is about 1 MB per channel at 48 kHz.
pub const MAX_TIME_MS: f32 = 4_000.0;
const MIN_TIME_MS: f32 = 15.0;
const MAX_MOD_MS: f32 = 5.0;
/// Frames between filter coefficient refreshes.
pub const CONTROL_BLOCK: usize = 32;
const CROSSFADE_MS: f32 = 40.0;
const CROSSFADE_EPS: f64 = 0.5;
const DIFFUSER_MS: [[f32; 2]; 2] = [[4.13, 7.71], [4.57, 8.29]];
const MAX_DIFFUSION_G: f32 = 0.7;
const DUCK_RANGE_DB: f32 = 12.0;
const SAT_CEILING: f64 = 1.5;
const SAT_SCALE: f64 = 1.5 * SAT_CEILING;
const LOOP_Q: f32 = std::f32::consts::FRAC_1_SQRT_2;

/// Cubic soft clip, unity slope at zero, tops out at ±1.5 (+3.5 dBFS).
#[inline]
fn soft_clip(u: f64) -> f64 {
    let v = (u / SAT_SCALE).clamp(-1.0, 1.0);
    SAT_SCALE * (v - v * v * v / 3.0)
}

/// Antiderivative of [`soft_clip`].
#[inline]
fn soft_clip_integral(u: f64) -> f64 {
    let v = u / SAT_SCALE;
    let a = v.abs();
    let f = if a <= 1.0 {
        0.5 * v * v - v * v * v * v / 12.0
    } else {
        (2.0 / 3.0) * a - 0.25
    };
    SAT_SCALE * SAT_SCALE * f
}

/// First-order ADAA of [`soft_clip`]; `prev` holds the previous input.
#[inline]
fn soft_clip_adaa(u: f64, prev: &mut f64) -> f64 {
    // ADAA is the cheap trick instead of 2x oversampling: average the curve
    // over the step, aliasing mostly gone, zero FIR latency in the loop.
    let d = u - *prev;
    let y = if d.abs() > 1.0e-5 {
        (soft_clip_integral(u) - soft_clip_integral(*prev)) / d
    } else {
        soft_clip(0.5 * (u + *prev))
    };
    *prev = u;
    y
}

/// 4-point Hermite read `delay` samples behind `write`; keep `delay` in `2.0 ..= len - 3`.
#[inline]
fn read_hermite(tape: &[f32], mask: usize, write: usize, delay: f64) -> f32 {
    let di = delay as usize;
    let t = (delay - di as f64) as f32;
    let base = write.wrapping_sub(di);
    let ym1 = tape[base.wrapping_add(1) & mask];
    let y0 = tape[base & mask];
    let y1 = tape[base.wrapping_sub(1) & mask];
    let y2 = tape[base.wrapping_sub(2) & mask];
    let c1 = 0.5 * (y1 - ym1);
    let c2 = ym1 - 2.5 * y0 + 2.0 * y1 - 0.5 * y2;
    let c3 = 0.5 * (y2 - ym1) + 1.5 * (y0 - y1);
    ((c3 * t + c2) * t + c1) * t + y0
}

/// One tape channel with its heads and in-loop processing.
struct Channel {
    tape: Vec<f32>,
    target: f64,
    head_a: f64,
    head_b: f64,
    fading: bool,
    fade: f32,
    compensation: f64,
    diffusers: [Diffuser; 2],
    low_cut: TptSvf,
    high_cut: TptSvf,
    clip_prev: f64,
}

impl Channel {
    fn new(frames: usize, diffuser_ms: [f32; 2], sample_rate: f32) -> Self {
        let diffusers = [
            Diffuser::new(diffuser_ms[0], sample_rate),
            Diffuser::new(diffuser_ms[1], sample_rate),
        ];
        let compensation = (diffusers[0].len() + diffusers[1].len()) as f64;
        Self {
            tape: vec![0.0; frames],
            target: 0.0,
            head_a: 0.0,
            head_b: 0.0,
            fading: false,
            fade: 0.0,
            compensation,
            diffusers,
            low_cut: TptSvf::default(),
            high_cut: TptSvf::default(),
            clip_prev: 0.0,
        }
    }

    fn clear(&mut self) {
        self.tape.fill(0.0);
        self.fading = false;
        self.head_a = self.target;
        self.head_b = self.target;
        for d in &mut self.diffusers {
            d.reset();
        }
        self.low_cut.reset();
        self.high_cut.reset();
        self.clip_prev = 0.0;
    }
}

/// Tape style stereo delay, see the module docs.
pub struct OwnDelay {
    enabled: bool,
    started: bool,
    sample_rate: f32,

    time_ms: [f32; 2],
    feedback: f32,
    cross_feedback: f32,
    crossfade_mode: bool,
    glide_ms: f32,
    drive_db: f32,
    low_cut_hz: f32,
    high_cut_hz: f32,
    diffusion: f32,
    mod_rate: f32,
    mod_depth_ms: f32,
    duck_amount: f32,
    duck_threshold_db: f32,
    duck_attack_ms: f32,
    duck_release_ms: f32,
    width: f32,
    freeze: bool,
    mix: f32,

    adaa: bool,
    glide_coeff: f64,
    fade_step: f32,
    duck_attack: f32,
    duck_release: f32,
    max_read: f64,

    mix_ramp: RampedParam,
    feedback_ramp: RampedParam,
    cross_ramp: RampedParam,
    drive_ramp: RampedParam,
    input_ramp: RampedParam,
    mod_ramp: RampedParam,
    width_ramp: RampedParam,
    duck_ramp: RampedParam,
    low_cut_ramp: RampedParam,
    high_cut_ramp: RampedParam,
    diffusion_ramp: RampedParam,

    channels: [Channel; 2],
    mask: usize,
    write_pos: usize,
    lfo: Lfo,
    low_cut: SvfCoeffs,
    high_cut: SvfCoeffs,
    duck_env: f32,

    meter_gain: f32,
    meter_input: f32,
}

impl OwnDelay {
    /// Delay on clean defaults: 375 / 500 ms, 45 % feedback, 80 Hz - 8 kHz loop, 30 % mix.
    pub fn new(sample_rate: f32) -> Self {
        let sample_rate = if sample_rate > 0.0 {
            sample_rate
        } else {
            44_100.0
        };
        let per_ms = sample_rate * 0.001;
        let diffuser_frames: f32 = DIFFUSER_MS[1].iter().sum::<f32>() * per_ms;
        let needed = ((MAX_TIME_MS + MAX_MOD_MS) * per_ms + diffuser_frames) as usize + 8;
        let frames = needed.next_power_of_two();

        let mut delay = Self {
            enabled: true,
            started: false,
            sample_rate,
            time_ms: [375.0, 500.0],
            feedback: 0.45,
            cross_feedback: 0.0,
            crossfade_mode: false,
            glide_ms: 150.0,
            drive_db: 0.0,
            low_cut_hz: 80.0,
            high_cut_hz: 8_000.0,
            diffusion: 0.0,
            mod_rate: 0.6,
            mod_depth_ms: 0.0,
            duck_amount: 0.0,
            duck_threshold_db: -30.0,
            duck_attack_ms: 10.0,
            duck_release_ms: 250.0,
            width: 1.0,
            freeze: false,
            mix: 0.3,
            adaa: false,
            glide_coeff: 1.0,
            fade_step: 1.0,
            duck_attack: 1.0,
            duck_release: 1.0,
            max_read: (frames - 4) as f64,
            mix_ramp: RampedParam::new(0.3, sample_rate, DEFAULT_SMOOTH_MS),
            feedback_ramp: RampedParam::new(0.45, sample_rate, DEFAULT_SMOOTH_MS),
            cross_ramp: RampedParam::new(0.0, sample_rate, DEFAULT_SMOOTH_MS),
            drive_ramp: RampedParam::new(1.0, sample_rate, DEFAULT_SMOOTH_MS),
            input_ramp: RampedParam::new(1.0, sample_rate, DEFAULT_SMOOTH_MS),
            mod_ramp: RampedParam::new(0.0, sample_rate, 50.0),
            width_ramp: RampedParam::new(1.0, sample_rate, DEFAULT_SMOOTH_MS),
            duck_ramp: RampedParam::new(0.0, sample_rate, DEFAULT_SMOOTH_MS),
            low_cut_ramp: RampedParam::new(80.0, sample_rate, 20.0),
            high_cut_ramp: RampedParam::new(8_000.0, sample_rate, 20.0),
            diffusion_ramp: RampedParam::new(0.0, sample_rate, 20.0),
            channels: [
                Channel::new(frames, DIFFUSER_MS[0], sample_rate),
                Channel::new(frames, DIFFUSER_MS[1], sample_rate),
            ],
            mask: frames - 1,
            write_pos: 0,
            lfo: Lfo::new(0.6, sample_rate),
            low_cut: SvfCoeffs::new(80.0, LOOP_Q, sample_rate),
            high_cut: SvfCoeffs::new(8_000.0, LOOP_Q, sample_rate),
            duck_env: 0.0,
            meter_gain: 1.0,
            meter_input: 0.0,
        };
        delay.update_coefficients();
        delay.set_time(0, 375.0);
        delay.set_time(1, 500.0);
        delay
    }

    fn update_coefficients(&mut self) {
        let fs = self.sample_rate;
        self.glide_coeff = owndsp::tau_coeff(self.glide_ms, fs) as f64;
        self.fade_step = 1.0 / (CROSSFADE_MS * 0.001 * fs);
        self.duck_attack = owndsp::tau_coeff(self.duck_attack_ms, fs);
        self.duck_release = owndsp::tau_coeff(self.duck_release_ms, fs);
        self.lfo.set_rate(self.mod_rate, fs);
    }

    fn set_time(&mut self, ch: usize, ms: f32) {
        self.time_ms[ch] = ms.clamp(MIN_TIME_MS, MAX_TIME_MS);
        let samples = self.time_ms[ch] as f64 * 0.001 * self.sample_rate as f64;
        let line = &mut self.channels[ch];
        line.target = samples;
        if !self.started {
            line.head_a = samples;
            line.head_b = samples;
            line.fading = false;
        }
    }

    #[inline]
    fn read_channel(&mut self, ch: usize, modulation: f64) -> f32 {
        let (mask, write, max_read) = (self.mask, self.write_pos, self.max_read);
        let (glide, crossfade, step) = (self.glide_coeff, self.crossfade_mode, self.fade_step);
        let adaa_delay = if self.adaa { 0.5 } else { 0.0 };
        let line = &mut self.channels[ch];

        if crossfade {
            if !line.fading && (line.target - line.head_a).abs() > CROSSFADE_EPS {
                line.head_b = line.target;
                line.fading = true;
                line.fade = 0.0;
            }
        } else {
            line.head_a += (line.target - line.head_a) * glide;
        }

        // The diffusers and the ADAA half sample add their own delay after the read,
        // so we read that much earlier, otherwise every echo lands late.
        let offset = modulation - line.compensation - adaa_delay;
        let pos_a = (line.head_a + offset).clamp(2.0, max_read);
        let a = read_hermite(&line.tape, mask, write, pos_a);
        if !line.fading {
            return a;
        }

        let pos_b = (line.head_b + offset).clamp(2.0, max_read);
        let b = read_hermite(&line.tape, mask, write, pos_b);
        let (gb, ga) = (line.fade * std::f32::consts::FRAC_PI_2).sin_cos();
        line.fade += step;
        if line.fade >= 1.0 {
            line.head_a = line.head_b;
            line.fading = false;
        }
        a * ga + b * gb
    }

    fn refresh_filters(&mut self, frames: usize) {
        let fs = self.sample_rate;
        let settled_low = self.low_cut_ramp.is_settled(0.01);
        let settled_high = self.high_cut_ramp.is_settled(0.01);
        let lo = self.low_cut_ramp.advance_block(frames);
        let hi = self.high_cut_ramp.advance_block(frames);
        if !settled_low {
            self.low_cut = SvfCoeffs::new(lo, LOOP_Q, fs);
        }
        if !settled_high {
            self.high_cut = SvfCoeffs::new(hi, LOOP_Q, fs);
        }
    }
}

impl Effect for OwnDelay {
    fn effect_type(&self) -> EffectType {
        EffectType::OwnDelay
    }

    #[allow(clippy::needless_range_loop)]
    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        self.started = true;
        for ramp in [
            &mut self.mix_ramp,
            &mut self.feedback_ramp,
            &mut self.cross_ramp,
            &mut self.drive_ramp,
            &mut self.input_ramp,
            &mut self.mod_ramp,
            &mut self.width_ramp,
            &mut self.duck_ramp,
            &mut self.low_cut_ramp,
            &mut self.high_cut_ramp,
            &mut self.diffusion_ramp,
        ] {
            ramp.begin_block();
        }
        if !self.enabled || channels == 0 {
            return;
        }

        let stride = channels as usize;
        let stereo = stride >= 2;
        let per_ms = self.sample_rate as f64 * 0.001;
        let freeze = self.freeze;
        let duck_threshold = self.duck_threshold_db;
        let (duck_att, duck_rel) = (self.duck_attack, self.duck_release);
        let mut block_min_duck = 1.0f32;
        let mut block_peak = 0.0f32;

        for control in buffer.chunks_mut(CONTROL_BLOCK * stride) {
            let frames = control.len() / stride;
            self.refresh_filters(frames);
            let diffusion_g = MAX_DIFFUSION_G * self.diffusion_ramp.advance_block(frames);
            let (low_cut, high_cut) = (self.low_cut, self.high_cut);

            for frame in control.chunks_exact_mut(stride) {
                let mix = self.mix_ramp.advance();
                let fb = self.feedback_ramp.advance();
                let cross = self.cross_ramp.advance();
                let drive = self.drive_ramp.advance();
                let input_gain = self.input_ramp.advance();
                let depth = self.mod_ramp.advance() as f64 * per_ms;
                let width = self.width_ramp.advance();
                let duck_amount = self.duck_ramp.advance();

                let in_l = frame[0];
                let in_r = if stereo { frame[1] } else { in_l };

                let peak = in_l.abs().max(in_r.abs());
                block_peak = block_peak.max(peak);
                let env_coeff = if peak > self.duck_env {
                    duck_att
                } else {
                    duck_rel
                };
                self.duck_env = denormal::flush(self.duck_env + env_coeff * (peak - self.duck_env));

                let (lfo_s, lfo_c) = self.lfo.advance();
                let mut wet = [0.0f32; 2];
                for (ch, lfo) in [(0usize, lfo_s), (1usize, lfo_c)] {
                    let raw = self.read_channel(ch, depth * lfo as f64);
                    let line = &mut self.channels[ch];
                    let mut y = line.diffusers[0].process(raw, diffusion_g);
                    y = line.diffusers[1].process(y, diffusion_g);
                    if !freeze {
                        y = line.high_cut.low_pass(y, &high_cut);
                        y = line.low_cut.high_pass(y, &low_cut);
                    }
                    wet[ch] = y;
                }

                // Loop gain never goes over 1, so cranking both knobs can't make it scream.
                // Freeze pins it at exactly 1 and the loop just spins forever.
                let sum = fb + cross;
                let (fb, cross) = if freeze && sum <= 1.0e-6 {
                    (1.0, 0.0)
                } else if freeze || sum > 1.0 {
                    (fb / sum, cross / sum)
                } else {
                    (fb, cross)
                };
                let feed = [wet[0] * fb + wet[1] * cross, wet[1] * fb + wet[0] * cross];
                let inputs = [in_l, in_r];
                let inv_drive = 1.0 / drive as f64;
                let adaa = self.adaa;
                for ch in 0..2 {
                    let line = &mut self.channels[ch];
                    let u = ((inputs[ch] * input_gain + feed[ch]) * drive) as f64;
                    let clipped = if adaa {
                        soft_clip_adaa(u, &mut line.clip_prev)
                    } else {
                        line.clip_prev = u;
                        soft_clip(u)
                    };
                    let y = clipped * inv_drive;
                    line.tape[self.write_pos & self.mask] = denormal::flush(y as f32);
                }
                self.write_pos = self.write_pos.wrapping_add(1);

                let duck = if duck_amount > 0.0 {
                    let over = (owndsp::lin_to_db(self.duck_env) - duck_threshold) / DUCK_RANGE_DB;
                    1.0 - duck_amount * over.clamp(0.0, 1.0)
                } else {
                    1.0
                };
                block_min_duck = block_min_duck.min(duck);

                let mid = 0.5 * (wet[0] + wet[1]);
                let side = 0.5 * (wet[0] - wet[1]) * width;
                let (wet_l, wet_r) = ((mid + side) * duck, (mid - side) * duck);

                if stereo {
                    frame[0] = in_l + mix * (wet_l - in_l);
                    frame[1] = in_r + mix * (wet_r - in_r);
                } else {
                    frame[0] = in_l + mix * (0.5 * (wet_l + wet_r) - in_l);
                }
            }
            self.lfo.renormalise();
        }

        self.meter_gain = block_min_duck;
        self.meter_input = block_peak;
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
            PARAM_TIME_L => self.set_time(0, value),
            PARAM_TIME_R => self.set_time(1, value),
            PARAM_FEEDBACK => {
                self.feedback = value.clamp(0.0, 1.0);
                self.feedback_ramp.set(self.feedback);
            }
            PARAM_CROSS_FEEDBACK => {
                self.cross_feedback = value.clamp(0.0, 1.0);
                self.cross_ramp.set(self.cross_feedback);
            }
            PARAM_TIME_MODE => {
                self.crossfade_mode = value >= 0.5;
                for line in &mut self.channels {
                    if line.fading {
                        line.head_a = line.head_b;
                        line.fading = false;
                    }
                }
            }
            PARAM_GLIDE => {
                self.glide_ms = value.clamp(5.0, 2_000.0);
                self.update_coefficients();
            }
            PARAM_DRIVE => {
                self.drive_db = value.clamp(0.0, 24.0);
                self.adaa = self.drive_db > 0.0;
                self.drive_ramp.set(owndsp::db_to_lin(self.drive_db));
            }
            PARAM_LOW_CUT => {
                self.low_cut_hz = value.clamp(20.0, 2_000.0);
                self.low_cut_ramp.set(self.low_cut_hz);
            }
            PARAM_HIGH_CUT => {
                self.high_cut_hz = value.clamp(500.0, 20_000.0);
                self.high_cut_ramp.set(self.high_cut_hz);
            }
            PARAM_DIFFUSION => {
                self.diffusion = value.clamp(0.0, 1.0);
                self.diffusion_ramp.set(self.diffusion);
            }
            PARAM_MOD_RATE => {
                self.mod_rate = value.clamp(0.05, 10.0);
                self.update_coefficients();
            }
            PARAM_MOD_DEPTH => {
                self.mod_depth_ms = value.clamp(0.0, MAX_MOD_MS);
                self.mod_ramp.set(self.mod_depth_ms);
            }
            PARAM_DUCK_AMOUNT => {
                self.duck_amount = value.clamp(0.0, 1.0);
                self.duck_ramp.set(self.duck_amount);
            }
            PARAM_DUCK_THRESHOLD => self.duck_threshold_db = value.clamp(-60.0, 0.0),
            PARAM_DUCK_ATTACK => {
                self.duck_attack_ms = value.clamp(0.5, 200.0);
                self.update_coefficients();
            }
            PARAM_DUCK_RELEASE => {
                self.duck_release_ms = value.clamp(10.0, 2_000.0);
                self.update_coefficients();
            }
            PARAM_WIDTH => {
                self.width = value.clamp(0.0, 2.0);
                self.width_ramp.set(self.width);
            }
            PARAM_FREEZE => {
                self.freeze = value >= 0.5;
                self.input_ramp.set(if self.freeze { 0.0 } else { 1.0 });
            }
            _ => return false,
        }
        if !self.started {
            self.low_cut = SvfCoeffs::new(self.low_cut_hz, LOOP_Q, self.sample_rate);
            self.high_cut = SvfCoeffs::new(self.high_cut_hz, LOOP_Q, self.sample_rate);
        }
        true
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        let flag = |b: bool| if b { 1.0 } else { 0.0 };
        Some(match param_id {
            PARAM_ENABLED => flag(self.enabled),
            PARAM_MIX => self.mix,
            PARAM_TIME_L => self.time_ms[0],
            PARAM_TIME_R => self.time_ms[1],
            PARAM_FEEDBACK => self.feedback,
            PARAM_CROSS_FEEDBACK => self.cross_feedback,
            PARAM_TIME_MODE => flag(self.crossfade_mode),
            PARAM_GLIDE => self.glide_ms,
            PARAM_DRIVE => self.drive_db,
            PARAM_LOW_CUT => self.low_cut_hz,
            PARAM_HIGH_CUT => self.high_cut_hz,
            PARAM_DIFFUSION => self.diffusion,
            PARAM_MOD_RATE => self.mod_rate,
            PARAM_MOD_DEPTH => self.mod_depth_ms,
            PARAM_DUCK_AMOUNT => self.duck_amount,
            PARAM_DUCK_THRESHOLD => self.duck_threshold_db,
            PARAM_DUCK_ATTACK => self.duck_attack_ms,
            PARAM_DUCK_RELEASE => self.duck_release_ms,
            PARAM_WIDTH => self.width,
            PARAM_FREEZE => flag(self.freeze),
            METER_CURRENT_GAIN => self.meter_gain,
            METER_INPUT_LEVEL => self.meter_input,
            _ => return None,
        })
    }

    fn reset(&mut self) {
        for line in &mut self.channels {
            line.clear();
        }
        self.write_pos = 0;
        self.lfo.reset();
        self.duck_env = 0.0;
        self.meter_gain = 1.0;
        self.meter_input = 0.0;
        self.mix_ramp.reset(self.mix);
        self.feedback_ramp.reset(self.feedback);
        self.cross_ramp.reset(self.cross_feedback);
        self.drive_ramp.reset(owndsp::db_to_lin(self.drive_db));
        self.input_ramp.reset(if self.freeze { 0.0 } else { 1.0 });
        self.mod_ramp.reset(self.mod_depth_ms);
        self.width_ramp.reset(self.width);
        self.duck_ramp.reset(self.duck_amount);
        self.low_cut_ramp.reset(self.low_cut_hz);
        self.high_cut_ramp.reset(self.high_cut_hz);
        self.diffusion_ramp.reset(self.diffusion);
        self.low_cut = SvfCoeffs::new(self.low_cut_hz, LOOP_Q, self.sample_rate);
        self.high_cut = SvfCoeffs::new(self.high_cut_hz, LOOP_Q, self.sample_rate);
    }

    fn is_enabled(&self) -> bool {
        self.enabled
    }

    fn set_enabled(&mut self, enabled: bool) {
        self.enabled = enabled;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const FS: f32 = 48_000.0;

    fn open_loop(d: &mut OwnDelay) {
        d.set_param(PARAM_LOW_CUT, 20.0);
        d.set_param(PARAM_HIGH_CUT, 20_000.0);
        d.set_param(PARAM_MIX, 1.0);
    }

    fn render(d: &mut OwnDelay, input: &[f32]) -> Vec<f32> {
        let mut buf = input.to_vec();
        for chunk in buf.chunks_mut(512 * 2) {
            d.process(chunk, 2);
        }
        buf
    }

    fn click(frames: usize, at: usize) -> Vec<f32> {
        let mut v = vec![0.0f32; frames * 2];
        v[at * 2] = 0.5;
        v[at * 2 + 1] = 0.5;
        v
    }

    fn argmax_left(buf: &[f32], from: usize, to: usize) -> usize {
        (from..to)
            .max_by(|&a, &b| buf[a * 2].abs().total_cmp(&buf[b * 2].abs()))
            .unwrap()
    }

    #[test]
    fn first_echo_lands_on_time() {
        let mut d = OwnDelay::new(FS);
        open_loop(&mut d);
        d.set_param(PARAM_TIME_L, 100.0);
        d.set_param(PARAM_DIFFUSION, 0.0);
        let out = render(&mut d, &click(12_000, 100));
        let peak = argmax_left(&out, 2_000, 8_000);
        assert!(
            (peak as i64 - 4_900).abs() <= 1,
            "echo at {peak}, expected 4900"
        );
    }

    #[test]
    fn driven_echo_still_lands_on_time() {
        let mut d = OwnDelay::new(FS);
        open_loop(&mut d);
        d.set_param(PARAM_TIME_L, 100.0);
        d.set_param(PARAM_DRIVE, 12.0);
        d.set_param(PARAM_DIFFUSION, 0.0);
        let mut input = click(12_000, 100);
        input.iter_mut().for_each(|s| *s *= 0.05);
        let out = render(&mut d, &input);
        let peak = argmax_left(&out, 2_000, 8_000);
        assert!(
            (peak as i64 - 4_900).abs() <= 1,
            "echo at {peak}, expected 4900"
        );
    }

    #[test]
    fn feedback_halves_each_repeat() {
        let mut d = OwnDelay::new(FS);
        open_loop(&mut d);
        d.set_param(PARAM_TIME_L, 50.0);
        d.set_param(PARAM_FEEDBACK, 0.5);
        let out = render(&mut d, &click(12_000, 0));
        let energy = |c: usize| -> f32 {
            (c * 2_400 - 200..c * 2_400 + 200)
                .map(|i| out[i * 2] * out[i * 2])
                .sum()
        };
        let ratio = (energy(2) / energy(1)).sqrt();
        assert!((ratio - 0.5).abs() < 0.02, "repeat ratio {ratio}");
    }

    #[test]
    fn runaway_settings_stay_bounded() {
        let mut d = OwnDelay::new(FS);
        d.set_param(PARAM_FEEDBACK, 1.0);
        d.set_param(PARAM_CROSS_FEEDBACK, 1.0);
        d.set_param(PARAM_DRIVE, 24.0);
        d.set_param(PARAM_MIX, 1.0);
        d.set_param(PARAM_TIME_L, 3.0);
        d.set_param(PARAM_TIME_R, 5.0);
        let mut seed = 1u32;
        let noise: Vec<f32> = (0..FS as usize * 4)
            .map(|_| {
                seed = seed.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
                (seed >> 8) as f32 / (1u32 << 24) as f32 * 2.0 - 1.0
            })
            .collect();
        let out = render(&mut d, &noise);
        assert!(out.iter().all(|s| s.is_finite() && s.abs() < 2.0));
    }

    #[test]
    fn tail_decays_to_true_zero() {
        let mut d = OwnDelay::new(FS);
        d.set_param(PARAM_FEEDBACK, 0.7);
        d.set_param(PARAM_TIME_L, 20.0);
        d.set_param(PARAM_TIME_R, 20.0);
        render(&mut d, &click(4_800, 0));
        let out = render(&mut d, &vec![0.0; FS as usize * 2 * 20]);
        assert!(out[out.len() - 64..].iter().all(|&s| s == 0.0));
    }

    #[test]
    fn freeze_holds_the_loop() {
        let mut d = OwnDelay::new(FS);
        open_loop(&mut d);
        d.set_param(PARAM_TIME_L, 30.0);
        d.set_param(PARAM_TIME_R, 30.0);
        let tone: Vec<f32> = (0..9_600)
            .flat_map(|i| {
                let v = 0.3 * (i as f32 * 0.05).sin();
                [v, v]
            })
            .collect();
        render(&mut d, &tone);
        d.set_param(PARAM_FREEZE, 1.0);
        let out = render(&mut d, &vec![0.0; 48_000 * 2]);
        let rms = |s: &[f32]| (s.iter().map(|x| x * x).sum::<f32>() / s.len() as f32).sqrt();
        let early = rms(&out[4_800..14_400]);
        let late = rms(&out[out.len() - 9_600..]);
        assert!(late > 0.8 * early, "freeze lost energy: {early} -> {late}");
    }

    #[test]
    fn crossfade_mode_switches_heads() {
        let mut d = OwnDelay::new(FS);
        d.set_param(PARAM_TIME_MODE, 1.0);
        render(&mut d, &click(4_800, 0));
        d.set_param(PARAM_TIME_L, 250.0);
        render(&mut d, &vec![0.0; 4_800 * 2]);
        assert!(!d.channels[0].fading);
        assert_eq!(d.channels[0].head_a, d.channels[0].target);
    }

    #[test]
    fn mono_and_extreme_modulation_stay_finite() {
        let mut d = OwnDelay::new(FS);
        d.set_param(PARAM_MOD_DEPTH, 5.0);
        d.set_param(PARAM_MOD_RATE, 10.0);
        d.set_param(PARAM_DIFFUSION, 1.0);
        d.set_param(PARAM_TIME_L, 1.0);
        let mut buf: Vec<f32> = (0..48_000).map(|i| (i as f32 * 0.01).sin()).collect();
        for chunk in buf.chunks_mut(333) {
            d.process(chunk, 1);
        }
        assert!(buf.iter().all(|s| s.is_finite()));
    }
}
