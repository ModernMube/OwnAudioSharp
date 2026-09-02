//! OwnReverb — a 16-line FDN reverb with input diffusion, modulated delays and a
//! built-in sidechain ducker.
//!
//! Unlike [`super::reverb::Reverb`] (a Freeverb port kept for numerical parity with
//! the old managed effect) this one is designed from scratch for a modern, musical
//! tail.  The signal path:
//!
//! ```text
//!   in ─┬─> pre-delay ─> 4× all-pass diffusion (L/R) ─> FDN core (16 lines)
//!       │                                                 │ Hadamard feedback
//!       │                                        damping + LFO-modulated taps
//!       ├─> early reflections (16 taps) ──────────────────┤
//!       │                                                 ▼
//!       │                                         mid/side width
//!       └─> ducker envelope ──> wet VCA ──> wet/dry mix ──> out
//! ```
//!
//! The core is a proper feedback delay network: 16 incommensurate delay lines whose
//! outputs are rotated back through a normalised 16×16 Hadamard matrix.  The matrix
//! is orthogonal, so it neither pumps energy into the loop nor bleeds it out — the
//! decay is set purely by the per-line gain `g_i = 10^(-3·d_i / (T60·fs))`, which is
//! what makes the RT60 parameter mean what it says.  Each line then runs through a
//! one-pole low-pass plus a low-shelf cut, so highs and lows die at their own rate
//! the way they do in a real room, and its read tap rides a slow quadrature LFO with
//! 4-point Hermite interpolation, which is where the "bloom" comes from: without it
//! a 16-line FDN parks on its own eigenmodes and rings metallically.
//!
//! Everything is sized at construction for the widest setting of `SIZE`, so changing
//! size or decay on the fly never touches the allocator — but it *does* move the read
//! taps, so automating `SIZE` mid-tail will click.  Treat it as a setup control.
//! Budget roughly 450 kB of delay memory per instance at 48 kHz.
//!
//! Every value that recirculates is denormal-flushed; a 20 s tail decaying into
//! silence would otherwise leave the whole network in the subnormal range and stall
//! the audio thread exactly as the track fades out.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use crate::denormal;
use crate::smoothing::{RampedParam, DEFAULT_SMOOTH_MS};

/// Param ID 2 — pre-delay in ms (0 … 250).
pub const PARAM_PRE_DELAY: u32 = 2;
/// Param ID 3 — RT60 decay time in seconds (0.1 … 20).
pub const PARAM_DECAY: u32 = 3;
/// Param ID 4 — room size, a multiplier on every delay length (0.25 … 2.0).
pub const PARAM_SIZE: u32 = 4;
/// Param ID 5 — high-frequency damping (0.0 … 1.0); higher darkens the tail.
pub const PARAM_DAMPING: u32 = 5;
/// Param ID 6 — low-frequency damping (0.0 … 1.0); higher thins the tail out.
pub const PARAM_LOW_DAMPING: u32 = 6;
/// Param ID 7 — input diffusion (0.0 … 1.0), the all-pass coefficient.
pub const PARAM_DIFFUSION: u32 = 7;
/// Param ID 8 — modulation rate in Hz (0.05 … 5.0).
pub const PARAM_MOD_RATE: u32 = 8;
/// Param ID 9 — modulation depth (0.0 … 1.0), up to ±3 ms.
pub const PARAM_MOD_DEPTH: u32 = 9;
/// Param ID 10 — stereo width of the wet signal (0.0 = mono … 2.0).
pub const PARAM_WIDTH: u32 = 10;
/// Param ID 11 — early reflection level (0.0 … 1.0).
pub const PARAM_EARLY_LEVEL: u32 = 11;
/// Param ID 12 — late (FDN tail) level (0.0 … 1.0).
pub const PARAM_LATE_LEVEL: u32 = 12;
/// Param ID 13 — ducking depth (0.0 = off … 1.0), driven by the dry signal.
pub const PARAM_DUCK_DEPTH: u32 = 13;
/// Param ID 14 — ducker attack in ms (1 … 200).
pub const PARAM_DUCK_ATTACK: u32 = 14;
/// Param ID 15 — ducker release in ms (10 … 2000).
pub const PARAM_DUCK_RELEASE: u32 = 15;
/// Param ID 16 — freeze (≥ 0.5 holds the tail forever and mutes the input).
pub const PARAM_FREEZE: u32 = 16;

/// Delay lines in the FDN core.  Sixteen is the sweet spot: dense enough that the
/// tail stops sounding like discrete echoes, and a 16×16 Hadamard rotation is only
/// 64 add/subtract operations.
const N: usize = 16;

/// Base delay lengths in ms at `SIZE == 1.0`, roughly log-spaced and deliberately
/// incommensurate so the modal frequencies never line up into a resonant comb.
const BASE_DELAY_MS: [f32; N] = [
    19.7, 23.9, 27.1, 31.3, 35.9, 39.7, 43.1, 47.9, 52.3, 57.1, 61.7, 67.3, 73.1, 79.9, 87.1, 97.3,
];

/// Sign pattern for the input injection — spreading the same source across the lines
/// with mixed polarity keeps the first few milliseconds from summing into a click.
const INJECT_SIGN: [f32; N] = [
    1.0, -1.0, 1.0, 1.0, -1.0, -1.0, 1.0, -1.0, 1.0, 1.0, -1.0, 1.0, -1.0, 1.0, -1.0, -1.0,
];

/// Output tap signs; a different pattern from the injection so the two ends of the
/// network stay decorrelated.
const TAP_SIGN: [f32; N] = [
    1.0, 1.0, -1.0, -1.0, 1.0, -1.0, -1.0, 1.0, -1.0, 1.0, 1.0, -1.0, -1.0, -1.0, 1.0, 1.0,
];

/// All-pass diffuser lengths in ms — left chain then right, deliberately unequal so
/// the two sides of the tank see decorrelated input.
const DIFFUSER_MS: [[f32; 4]; 2] = [[4.77, 3.59, 12.73, 9.31], [5.19, 3.93, 13.71, 10.13]];

/// Early reflection taps as `(time_ms, gain)`, left channel then right.  Times scale
/// with `SIZE`; alternating gain polarity keeps the pattern from ringing.
const ER_TAPS: [[(f32, f32); 8]; 2] = [
    [
        (10.3, 0.90),
        (19.1, -0.68),
        (29.9, 0.54),
        (43.7, -0.43),
        (59.9, 0.34),
        (78.3, -0.27),
        (98.7, 0.21),
        (121.3, -0.16),
    ],
    [
        (13.7, 0.84),
        (23.3, -0.62),
        (35.1, 0.50),
        (48.9, -0.40),
        (65.3, 0.31),
        (84.1, -0.25),
        (105.7, 0.19),
        (129.7, -0.15),
    ],
];

const MIN_SIZE: f32 = 0.25;
const MAX_SIZE: f32 = 2.0;
const MAX_PRE_DELAY_MS: f32 = 250.0;
const MAX_MOD_MS: f32 = 3.0;
/// Corner of the low-shelf cut inside the loop.
const LOW_DAMP_HZ: f32 = 200.0;
/// How much of the low band the maximum `LOW_DAMPING` removes per pass.
const LOW_DAMP_MAX_CUT: f32 = 0.85;

/// `1/sqrt(N)` — keeps the injection level constant as it fans out over the lines.
const INPUT_SCALE: f32 = 0.25;
/// `1/sqrt(N/2)` — eight lines land on each output channel.
const OUTPUT_SCALE: f32 = 0.353_553_4;

/// Tapped mono delay line, used for both the pre-delay and the early reflections —
/// they read the same history at different offsets, so they share one buffer.
struct TapLine {
    buf: Vec<f32>,
    w: usize,
}

impl TapLine {
    fn new(len: usize) -> Self {
        Self {
            buf: vec![0.0; len.max(2)],
            w: 0,
        }
    }

    /// Stores `x` at the current position; `tap(0)` returns it again.
    #[inline]
    fn write(&mut self, x: f32) {
        self.buf[self.w] = x;
    }

    /// Reads the sample written `n` frames ago, clamped to the line length.
    #[inline]
    fn tap(&self, n: usize) -> f32 {
        let len = self.buf.len();
        let n = n.min(len - 1);
        let i = self.w + len - n;
        self.buf[if i >= len { i - len } else { i }]
    }

    #[inline]
    fn advance(&mut self) {
        self.w += 1;
        if self.w >= self.buf.len() {
            self.w = 0;
        }
    }

    fn clear(&mut self) {
        self.buf.iter_mut().for_each(|s| *s = 0.0);
        self.w = 0;
    }
}

/// Schroeder all-pass section — flat magnitude response, scrambled phase.  Four of
/// these in series turn a transient into a dense burst before it ever reaches the
/// tank, which is the difference between a snare sounding like a snare in a room and
/// sounding like a spring.
struct Diffuser {
    buf: Vec<f32>,
    idx: usize,
}

impl Diffuser {
    fn new(len: usize) -> Self {
        Self {
            buf: vec![0.0; len.max(1)],
            idx: 0,
        }
    }

    #[inline]
    fn process(&mut self, x: f32, g: f32) -> f32 {
        let stored = self.buf[self.idx];
        let v = x + g * stored;
        self.buf[self.idx] = denormal::flush(v);
        self.idx += 1;
        if self.idx >= self.buf.len() {
            self.idx = 0;
        }
        stored - g * v
    }

    fn clear(&mut self) {
        self.buf.iter_mut().for_each(|s| *s = 0.0);
        self.idx = 0;
    }
}

/// In-loop tone shaping: a one-pole low-pass for air absorption, then a shelving cut
/// that pulls the low band down so the bass does not outlive everything else.
#[derive(Default, Clone, Copy)]
struct LoopDamp {
    hf: f32,
    lf: f32,
}

impl LoopDamp {
    #[inline]
    fn process(&mut self, x: f32, hf_coeff: f32, lf_coeff: f32, lf_cut: f32) -> f32 {
        self.hf = denormal::flush(self.hf + hf_coeff * (x - self.hf));
        self.lf = denormal::flush(self.lf + lf_coeff * (self.hf - self.lf));
        self.hf - self.lf * lf_cut
    }
}

/// Quadrature sine oscillator — two multiply-adds per sample instead of a `sin()`
/// call, which matters when sixteen of them run per frame.  The rotation drifts in
/// amplitude over time, so [`renormalise`](Self::renormalise) is called once a block.
#[derive(Clone, Copy)]
struct Lfo {
    s: f32,
    c: f32,
}

impl Lfo {
    fn at_phase(phase: f32) -> Self {
        Self {
            s: phase.sin(),
            c: phase.cos(),
        }
    }

    #[inline]
    fn advance(&mut self, sin_inc: f32, cos_inc: f32) -> f32 {
        let s = self.s * cos_inc + self.c * sin_inc;
        self.c = self.c * cos_inc - self.s * sin_inc;
        self.s = s;
        s
    }

    fn renormalise(&mut self) {
        let k = 1.5 - 0.5 * (self.s * self.s + self.c * self.c);
        self.s *= k;
        self.c *= k;
    }
}

/// Peak envelope follower on the dry signal, driving the wet VCA.
#[derive(Default)]
struct Ducker {
    env: f32,
}

impl Ducker {
    #[inline]
    fn follow(&mut self, dry: f32, attack: f32, release: f32) -> f32 {
        let level = dry.abs();
        let coeff = if level > self.env { attack } else { release };
        self.env = denormal::flush(self.env + coeff * (level - self.env));
        self.env
    }
}

/// Normalised in-place 16-point Hadamard transform (fast Walsh–Hadamard butterfly).
///
/// The `1/sqrt(16)` scaling makes it orthogonal, which is the whole reason for using
/// it: the feedback rotation is then energy-preserving, so the decay time is decided
/// by the per-line gains alone and the loop can neither blow up nor die early.
#[inline]
fn hadamard(v: &mut [f32; N]) {
    let mut h = 1;
    while h < N {
        let mut i = 0;
        while i < N {
            for j in i..i + h {
                let (a, b) = (v[j], v[j + h]);
                v[j] = a + b;
                v[j + h] = a - b;
            }
            i += h << 1;
        }
        h <<= 1;
    }
    for x in v.iter_mut() {
        *x *= 0.25;
    }
}

/// 4-point Hermite read at a fractional delay.  `delay` must already sit inside
/// `2.0 ..= len - 3`, which the caller guarantees by clamping.
#[inline]
fn read_hermite(line: &[f32], w: usize, delay: f32) -> f32 {
    let len = line.len();
    let d = delay as usize;
    let t = delay - d as f32;

    // Walk from the oldest of the four taps forward, so each step is a single
    // increment with one wrap check.
    let mut i = w + len - (d + 2);
    if i >= len {
        i -= len;
    }
    let y3 = line[i];
    let mut next = || {
        i += 1;
        if i >= len {
            i = 0;
        }
        line[i]
    };
    let y2 = next();
    let y1 = next();
    let y0 = next();

    let c1 = 0.5 * (y2 - y0);
    let c2 = y0 - 2.5 * y1 + 2.0 * y2 - 0.5 * y3;
    let c3 = 0.5 * (y3 - y0) + 1.5 * (y1 - y2);
    ((c3 * t + c2) * t + c1) * t + y1
}

/// One-pole low-pass coefficient for a corner at `fc` Hz.
fn one_pole_coeff(fc: f32, sample_rate: f32) -> f32 {
    let c = 1.0 - (-std::f32::consts::TAU * fc / sample_rate).exp();
    c.clamp(f32::MIN_POSITIVE, 1.0)
}

/// Attack/release coefficient for a `ms` millisecond time constant.
fn env_coeff(ms: f32, sample_rate: f32) -> f32 {
    let c = 1.0 - (-1.0 / (ms * 0.001 * sample_rate)).exp();
    c.clamp(f32::MIN_POSITIVE, 1.0)
}

/// Modern algorithmic reverb built on a 16-line feedback delay network.
pub struct OwnReverb {
    enabled: bool,
    sample_rate: f32,

    pre_delay_ms: f32,
    decay_s: f32,
    size: f32,
    damping: f32,
    low_damping: f32,
    diffusion: f32,
    mod_rate: f32,
    mod_depth: f32,
    width: f32,
    early_level: f32,
    late_level: f32,
    duck_depth: f32,
    duck_attack_ms: f32,
    duck_release_ms: f32,
    freeze: bool,
    mix: f32,

    // Every FDN line lives in one flat buffer; `offsets`/`lens` slice it up.
    lines: Vec<f32>,
    offsets: [usize; N],
    lens: [usize; N],
    write_pos: [usize; N],
    damp: [LoopDamp; N],
    lfo: [Lfo; N],

    input_line: TapLine,
    diffusers: [[Diffuser; 4]; 2],
    ducker: Ducker,

    // Derived on the control thread, read on the audio thread.
    delay_samples: [f32; N],
    gains: [f32; N],
    mod_samples: f32,
    lfo_sin_inc: f32,
    lfo_cos_inc: f32,
    hf_coeff: f32,
    lf_coeff: f32,
    lf_cut: f32,
    duck_attack: f32,
    duck_release: f32,
    allpass_g: f32,
    mix_ramp: RampedParam,
}

impl OwnReverb {
    /// Builds an [`OwnReverb`] sized for `sample_rate`, landing on a medium hall:
    /// 20 ms pre-delay, 2.5 s decay, moderate damping and a touch of movement.
    pub fn new(sample_rate: f32) -> Self {
        let sample_rate = if sample_rate > 0.0 {
            sample_rate
        } else {
            44_100.0
        };
        let per_ms = sample_rate * 0.001;
        let mod_margin = (MAX_MOD_MS * per_ms).ceil() as usize;

        // Size each line for the longest delay it can ever be asked for, plus room
        // for the modulation sweep and the Hermite window.
        let mut offsets = [0usize; N];
        let mut lens = [0usize; N];
        let mut total = 0usize;
        for i in 0..N {
            let len = (BASE_DELAY_MS[i] * MAX_SIZE * per_ms).ceil() as usize + mod_margin + 8;
            offsets[i] = total;
            lens[i] = len;
            total += len;
        }

        let longest_er = ER_TAPS[1][7].0;
        let input_len = ((MAX_PRE_DELAY_MS + longest_er * MAX_SIZE) * per_ms).ceil() as usize + 4;

        let lfo: [Lfo; N] =
            std::array::from_fn(|i| Lfo::at_phase(i as f32 * std::f32::consts::TAU / N as f32));
        let diffusers: [[Diffuser; 4]; 2] = std::array::from_fn(|ch| {
            std::array::from_fn(|k| Diffuser::new((DIFFUSER_MS[ch][k] * per_ms) as usize))
        });

        let mut reverb = Self {
            enabled: true,
            sample_rate,
            pre_delay_ms: 20.0,
            decay_s: 2.5,
            size: 1.0,
            damping: 0.5,
            low_damping: 0.15,
            diffusion: 0.7,
            mod_rate: 0.8,
            mod_depth: 0.4,
            width: 1.2,
            early_level: 0.35,
            late_level: 1.0,
            duck_depth: 0.0,
            duck_attack_ms: 12.0,
            duck_release_ms: 250.0,
            freeze: false,
            mix: 0.3,
            lines: vec![0.0; total],
            offsets,
            lens,
            write_pos: [0; N],
            damp: [LoopDamp::default(); N],
            lfo,
            input_line: TapLine::new(input_len),
            diffusers,
            ducker: Ducker::default(),
            delay_samples: [0.0; N],
            gains: [0.0; N],
            mod_samples: 0.0,
            lfo_sin_inc: 0.0,
            lfo_cos_inc: 1.0,
            hf_coeff: 1.0,
            lf_coeff: 0.0,
            lf_cut: 0.0,
            duck_attack: 0.0,
            duck_release: 0.0,
            allpass_g: 0.0,
            mix_ramp: RampedParam::new(0.3, sample_rate, DEFAULT_SMOOTH_MS),
        };
        reverb.update_coefficients();
        reverb
    }

    /// Recomputes everything the audio thread reads.  Cold path — `powf`/`exp` are
    /// fine here, they never run per sample.
    fn update_coefficients(&mut self) {
        let fs = self.sample_rate;

        for ((base_ms, delay), gain) in BASE_DELAY_MS
            .iter()
            .zip(&mut self.delay_samples)
            .zip(&mut self.gains)
        {
            let d = base_ms * 0.001 * self.size * fs;
            *delay = d;
            // g_i = 10^(-3 d_i / (T60 fs)) — the textbook RT60 mapping, so a line
            // that is twice as long loses twice as much per pass and every line
            // reaches -60 dB at the same moment.
            *gain = 10f32.powf(-3.0 * d / (self.decay_s * fs));
        }

        self.mod_samples = MAX_MOD_MS * 0.001 * fs * self.mod_depth;

        let w = std::f32::consts::TAU * self.mod_rate / fs;
        self.lfo_sin_inc = w.sin();
        self.lfo_cos_inc = w.cos();

        // Damping maps logarithmically from wide open down to a dark 1 kHz room.
        let cutoff = 18_000.0 * (1_000.0f32 / 18_000.0).powf(self.damping);
        self.hf_coeff = one_pole_coeff(cutoff.min(fs * 0.45), fs);
        self.lf_coeff = one_pole_coeff(LOW_DAMP_HZ, fs);
        self.lf_cut = self.low_damping * LOW_DAMP_MAX_CUT;

        self.duck_attack = env_coeff(self.duck_attack_ms, fs);
        self.duck_release = env_coeff(self.duck_release_ms, fs);
        self.allpass_g = 0.35 + 0.4 * self.diffusion;
    }
}

impl Effect for OwnReverb {
    fn effect_type(&self) -> EffectType {
        EffectType::OwnReverb
    }

    #[allow(clippy::needless_range_loop)]
    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        self.mix_ramp.begin_block();
        if !self.enabled || channels == 0 {
            return;
        }

        let stride = channels as usize;
        let stereo = stride >= 2;
        let per_ms = self.sample_rate * 0.001;
        let pre_samples = (self.pre_delay_ms * per_ms) as usize;

        let frozen = self.freeze;
        let input_gain = if frozen { 0.0 } else { INPUT_SCALE };
        let (hf_coeff, lf_cut) = if frozen {
            (1.0, 0.0)
        } else {
            (self.hf_coeff, self.lf_cut)
        };
        let lf_coeff = self.lf_coeff;
        let allpass_g = self.allpass_g;
        let mod_samples = self.mod_samples;
        let (sin_inc, cos_inc) = (self.lfo_sin_inc, self.lfo_cos_inc);
        let (duck_attack, duck_release) = (self.duck_attack, self.duck_release);
        let duck_depth = self.duck_depth;
        let width = self.width;
        let early = self.early_level;
        let late = self.late_level;

        // Early reflection tap offsets in samples, resolved once per block.
        let mut er_taps = [[0usize; 8]; 2];
        for ch in 0..2 {
            for (k, &(ms, _)) in ER_TAPS[ch].iter().enumerate() {
                er_taps[ch][k] = pre_samples + (ms * self.size * per_ms) as usize;
            }
        }

        let mut v = [0.0f32; N];

        for frame in buffer.chunks_exact_mut(stride) {
            let mix = self.mix_ramp.advance();
            let in_l = frame[0];
            let in_r = if stereo { frame[1] } else { in_l };
            let mono = 0.5 * (in_l + in_r);

            self.input_line.write(mono);
            let pre = self.input_line.tap(pre_samples);

            let mut early_l = 0.0f32;
            let mut early_r = 0.0f32;
            for k in 0..8 {
                early_l += self.input_line.tap(er_taps[0][k]) * ER_TAPS[0][k].1;
                early_r += self.input_line.tap(er_taps[1][k]) * ER_TAPS[1][k].1;
            }
            self.input_line.advance();

            // Two independent all-pass chains so the tank sees decorrelated sides.
            let mut dif_l = pre;
            let mut dif_r = pre;
            for k in 0..4 {
                dif_l = self.diffusers[0][k].process(dif_l, allpass_g);
                dif_r = self.diffusers[1][k].process(dif_r, allpass_g);
            }

            // Read every line at its modulated tap, then damp and scale it.
            for i in 0..N {
                let (off, len) = (self.offsets[i], self.lens[i]);
                let sweep = self.lfo[i].advance(sin_inc, cos_inc) * mod_samples;
                let delay = (self.delay_samples[i] + sweep).clamp(2.0, (len - 3).max(3) as f32);
                let raw = read_hermite(&self.lines[off..off + len], self.write_pos[i], delay);

                let damped = if frozen {
                    raw
                } else {
                    self.damp[i].process(raw, hf_coeff, lf_coeff, lf_cut)
                };
                v[i] = damped;
            }

            let mut fed = v;
            if !frozen {
                for i in 0..N {
                    fed[i] *= self.gains[i];
                }
            }
            hadamard(&mut fed);

            for i in 0..N {
                let src = if i % 2 == 0 { dif_l } else { dif_r };
                let write = fed[i] + src * INJECT_SIGN[i] * input_gain;
                let (off, len) = (self.offsets[i], self.lens[i]);
                self.lines[off + self.write_pos[i]] = denormal::flush(write);
                self.write_pos[i] += 1;
                if self.write_pos[i] >= len {
                    self.write_pos[i] = 0;
                }
            }

            // Even lines to the left, odd to the right.
            let mut late_l = 0.0f32;
            let mut late_r = 0.0f32;
            for i in 0..N {
                let tapped = v[i] * TAP_SIGN[i];
                if i % 2 == 0 {
                    late_l += tapped;
                } else {
                    late_r += tapped;
                }
            }

            let mut wet_l = late_l * OUTPUT_SCALE * late + early_l * early;
            let mut wet_r = late_r * OUTPUT_SCALE * late + early_r * early;

            // Mid/side widening — pushing side alone keeps mono compatibility.
            let mid = 0.5 * (wet_l + wet_r);
            let side = 0.5 * (wet_l - wet_r) * width;
            wet_l = mid + side;
            wet_r = mid - side;

            if duck_depth > 0.0 {
                let env = self.ducker.follow(mono, duck_attack, duck_release);
                let duck = (1.0 - duck_depth * env).clamp(0.0, 1.0);
                wet_l *= duck;
                wet_r *= duck;
            }

            frame[0] = in_l * (1.0 - mix) + wet_l * mix;
            if stereo {
                frame[1] = in_r * (1.0 - mix) + wet_r * mix;
            }
        }

        for lfo in &mut self.lfo {
            lfo.renormalise();
        }
    }

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED => {
                self.enabled = value >= 0.5;
                return true;
            }
            PARAM_MIX => {
                self.mix = value.clamp(0.0, 1.0);
                self.mix_ramp.set(self.mix);
                return true;
            }
            PARAM_PRE_DELAY => self.pre_delay_ms = value.clamp(0.0, MAX_PRE_DELAY_MS),
            PARAM_DECAY => self.decay_s = value.clamp(0.1, 20.0),
            PARAM_SIZE => self.size = value.clamp(MIN_SIZE, MAX_SIZE),
            PARAM_DAMPING => self.damping = value.clamp(0.0, 1.0),
            PARAM_LOW_DAMPING => self.low_damping = value.clamp(0.0, 1.0),
            PARAM_DIFFUSION => self.diffusion = value.clamp(0.0, 1.0),
            PARAM_MOD_RATE => self.mod_rate = value.clamp(0.05, 5.0),
            PARAM_MOD_DEPTH => self.mod_depth = value.clamp(0.0, 1.0),
            PARAM_WIDTH => self.width = value.clamp(0.0, 2.0),
            PARAM_EARLY_LEVEL => self.early_level = value.clamp(0.0, 1.0),
            PARAM_LATE_LEVEL => self.late_level = value.clamp(0.0, 1.0),
            PARAM_DUCK_DEPTH => self.duck_depth = value.clamp(0.0, 1.0),
            PARAM_DUCK_ATTACK => self.duck_attack_ms = value.clamp(1.0, 200.0),
            PARAM_DUCK_RELEASE => self.duck_release_ms = value.clamp(10.0, 2000.0),
            PARAM_FREEZE => self.freeze = value >= 0.5,
            _ => return false,
        }
        self.update_coefficients();
        true
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        Some(match param_id {
            PARAM_ENABLED => self.enabled as u8 as f32,
            PARAM_MIX => self.mix,
            PARAM_PRE_DELAY => self.pre_delay_ms,
            PARAM_DECAY => self.decay_s,
            PARAM_SIZE => self.size,
            PARAM_DAMPING => self.damping,
            PARAM_LOW_DAMPING => self.low_damping,
            PARAM_DIFFUSION => self.diffusion,
            PARAM_MOD_RATE => self.mod_rate,
            PARAM_MOD_DEPTH => self.mod_depth,
            PARAM_WIDTH => self.width,
            PARAM_EARLY_LEVEL => self.early_level,
            PARAM_LATE_LEVEL => self.late_level,
            PARAM_DUCK_DEPTH => self.duck_depth,
            PARAM_DUCK_ATTACK => self.duck_attack_ms,
            PARAM_DUCK_RELEASE => self.duck_release_ms,
            PARAM_FREEZE => self.freeze as u8 as f32,
            _ => return None,
        })
    }

    fn reset(&mut self) {
        self.lines.iter_mut().for_each(|s| *s = 0.0);
        self.write_pos = [0; N];
        self.damp = [LoopDamp::default(); N];
        for (i, lfo) in self.lfo.iter_mut().enumerate() {
            *lfo = Lfo::at_phase(i as f32 * std::f32::consts::TAU / N as f32);
        }
        self.input_line.clear();
        for chain in &mut self.diffusers {
            for d in chain {
                d.clear();
            }
        }
        self.ducker = Ducker::default();
        self.mix_ramp.reset(self.mix);
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

    fn stereo_sine(frames: usize, hz: f32, amp: f32) -> Vec<f32> {
        let step = std::f32::consts::TAU * hz / FS;
        (0..frames)
            .flat_map(|f| {
                let s = amp * (step * f as f32).sin();
                [s, s]
            })
            .collect()
    }

    fn impulse(frames: usize) -> Vec<f32> {
        let mut v = vec![0.0f32; frames * 2];
        v[0] = 1.0;
        v[1] = 1.0;
        v
    }

    fn rms(block: &[f32]) -> f32 {
        let sum: f32 = block.iter().map(|s| s * s).sum();
        (sum / block.len() as f32).sqrt()
    }

    #[test]
    fn defaults_are_a_medium_hall() {
        let r = OwnReverb::new(FS);
        assert_eq!(r.get_param(PARAM_PRE_DELAY), Some(20.0));
        assert_eq!(r.get_param(PARAM_DECAY), Some(2.5));
        assert_eq!(r.get_param(PARAM_SIZE), Some(1.0));
        assert_eq!(r.get_param(PARAM_MIX), Some(0.3));
        assert_eq!(r.get_param(PARAM_FREEZE), Some(0.0));
        assert!(r.is_enabled());
    }

    #[test]
    fn params_clamp_and_unknown_ids_are_rejected() {
        let mut r = OwnReverb::new(FS);
        r.set_param(PARAM_DECAY, 500.0);
        assert_eq!(r.get_param(PARAM_DECAY), Some(20.0));
        r.set_param(PARAM_SIZE, 0.0);
        assert_eq!(r.get_param(PARAM_SIZE), Some(MIN_SIZE));
        r.set_param(PARAM_WIDTH, 99.0);
        assert_eq!(r.get_param(PARAM_WIDTH), Some(2.0));
        assert!(!r.set_param(999, 1.0));
        assert_eq!(r.get_param(999), None);
    }

    #[test]
    fn hadamard_is_orthogonal() {
        // Energy in equals energy out — that is the property the whole decay model
        // rests on, so it is worth asserting rather than assuming.
        let mut v: [f32; N] = std::array::from_fn(|i| ((i * 37 % 19) as f32) - 9.0);
        let before: f32 = v.iter().map(|x| x * x).sum();
        hadamard(&mut v);
        let after: f32 = v.iter().map(|x| x * x).sum();
        assert!((before - after).abs() < 1e-3, "{before} vs {after}");
    }

    #[test]
    fn hermite_reproduces_integer_taps() {
        let line: Vec<f32> = (0..64).map(|i| (i as f32) * 0.5).collect();
        // w = 0 means tap n reads line[64 - n].
        for n in 2..60usize {
            let got = read_hermite(&line, 0, n as f32);
            assert!((got - line[64 - n]).abs() < 1e-4, "n={n}: {got}");
        }
    }

    #[test]
    fn disabled_effect_is_a_straight_wire() {
        let mut r = OwnReverb::new(FS);
        r.set_enabled(false);
        let input = stereo_sine(256, 440.0, 0.5);
        let mut buf = input.clone();
        r.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn output_stays_finite_and_bounded() {
        let mut r = OwnReverb::new(FS);
        r.set_param(PARAM_MIX, 1.0);
        r.set_param(PARAM_DECAY, 12.0);
        let mut buf = stereo_sine(48_000, 220.0, 0.5);
        r.process(&mut buf, 2);
        assert!(buf.iter().all(|s| s.is_finite()));
        assert!(buf.iter().all(|s| s.abs() < 4.0), "the tank ran away");
    }

    #[test]
    fn mono_and_multichannel_buffers_survive() {
        for channels in [1u16, 2, 4] {
            let mut r = OwnReverb::new(FS);
            let mut buf = vec![0.25f32; 512 * channels as usize];
            r.process(&mut buf, channels);
            assert!(buf.iter().all(|s| s.is_finite()), "{channels} ch");
        }
    }

    #[test]
    fn tail_keeps_ringing_after_the_input_stops() {
        let mut r = OwnReverb::new(FS);
        r.set_param(PARAM_MIX, 1.0);
        let mut buf = impulse(64);
        r.process(&mut buf, 2);

        let mut tail = vec![0.0f32; 2 * 24_000];
        r.process(&mut tail, 2);
        assert!(rms(&tail) > 1e-5, "no tail at all");
    }

    #[test]
    fn longer_decay_means_a_louder_tail() {
        let level_after = |decay: f32| {
            let mut r = OwnReverb::new(FS);
            r.set_param(PARAM_MIX, 1.0);
            r.set_param(PARAM_DECAY, decay);
            let mut buf = impulse(64);
            r.process(&mut buf, 2);
            let mut tail = vec![0.0f32; 2 * 48_000];
            r.process(&mut tail, 2);
            rms(&tail[2 * 36_000..])
        };
        let short = level_after(0.4);
        let long = level_after(8.0);
        assert!(long > short * 10.0, "short={short:e} long={long:e}");
    }

    #[test]
    fn damping_darkens_the_tail() {
        // A bright tail carries more energy at 8 kHz than a damped one; comparing
        // the total tail RMS of a broadband impulse is enough to see it.
        let tail_level = |damping: f32| {
            let mut r = OwnReverb::new(FS);
            r.set_param(PARAM_MIX, 1.0);
            r.set_param(PARAM_DAMPING, damping);
            let mut buf = impulse(64);
            r.process(&mut buf, 2);
            let mut tail = vec![0.0f32; 2 * 24_000];
            r.process(&mut tail, 2);
            rms(&tail)
        };
        assert!(tail_level(0.95) < tail_level(0.05));
    }

    #[test]
    fn ducking_pulls_the_wet_down_while_the_dry_plays() {
        let wet_level = |depth: f32| {
            let mut r = OwnReverb::new(FS);
            r.set_param(PARAM_MIX, 1.0);
            r.set_param(PARAM_DUCK_DEPTH, depth);
            let mut buf = stereo_sine(24_000, 200.0, 0.9);
            r.process(&mut buf, 2);
            rms(&buf[2 * 12_000..])
        };
        assert!(wet_level(1.0) < wet_level(0.0) * 0.9);
    }

    #[test]
    fn freeze_holds_the_tail() {
        let mut r = OwnReverb::new(FS);
        r.set_param(PARAM_MIX, 1.0);
        let mut buf = stereo_sine(12_000, 300.0, 0.8);
        r.process(&mut buf, 2);
        r.set_param(PARAM_FREEZE, 1.0);

        let mut first = vec![0.0f32; 2 * 24_000];
        r.process(&mut first, 2);
        let mut later = vec![0.0f32; 2 * 24_000];
        r.process(&mut later, 2);

        // Frozen the loop is lossless, so half a second on the level should barely
        // move — and it certainly must not decay away.
        assert!(rms(&later) > rms(&first) * 0.5, "the freeze leaked");
    }

    #[test]
    fn width_zero_collapses_the_wet_to_mono() {
        let mut r = OwnReverb::new(FS);
        r.set_param(PARAM_MIX, 1.0);
        r.set_param(PARAM_WIDTH, 0.0);
        let mut buf = stereo_sine(4_096, 500.0, 0.6);
        r.process(&mut buf, 2);
        assert!(buf.chunks_exact(2).all(|f| (f[0] - f[1]).abs() < 1e-5));
    }

    #[test]
    fn reset_restores_a_fresh_instance() {
        let mut r = OwnReverb::new(FS);
        let input = stereo_sine(2_048, 440.0, 0.5);
        let mut first = input.clone();
        r.process(&mut first, 2);
        r.reset();
        let mut second = input.clone();
        r.process(&mut second, 2);
        assert_eq!(first, second);
    }

    #[test]
    fn a_decaying_tail_leaves_no_subnormals_behind() {
        // Same guarantee the Freeverb port carries: nothing that recirculates may
        // sit in the subnormal range once the tail has faded, or the audio thread
        // eats a microcode assist per sample exactly as the track ends.
        let mut r = OwnReverb::new(FS);
        r.set_param(PARAM_MIX, 1.0);
        r.set_param(PARAM_DECAY, 0.3);
        let mut buf = impulse(2);
        r.process(&mut buf, 2);
        let mut silence = vec![0.0f32; 2 * 600_000];
        r.process(&mut silence, 2);

        let normal = |v: f32, what: &str| {
            assert!(
                v == 0.0 || v.abs() >= f32::MIN_POSITIVE,
                "subnormal in {what}: {v:e}"
            );
        };
        for &s in &r.lines {
            normal(s, "fdn line");
        }
        for d in &r.damp {
            normal(d.hf, "damp hf");
            normal(d.lf, "damp lf");
        }
        for chain in &r.diffusers {
            for d in chain {
                for &s in &d.buf {
                    normal(s, "diffuser");
                }
            }
        }
    }
}
