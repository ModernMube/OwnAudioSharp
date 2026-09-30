//! Shared DSP bits for the Own* effects (OwnCompressor, OwnDelay).
//! Hot path stuff never allocates; buffers are sized in the constructors.

use crate::denormal;

/// 20 * log10(2), one log2 step in dB.
pub const DB_PER_LOG2: f32 = 6.020_6;

/// Reciprocal of [`DB_PER_LOG2`].
pub const LOG2_PER_DB: f32 = 1.0 / DB_PER_LOG2;

/// Lowest level we take the log of (-180 dBFS), keeps silence finite.
pub const LEVEL_FLOOR: f32 = 1.0e-9;

/// Fast log2, error under 0.001 dB. Non-positive input counts as the smallest normal.
#[inline]
pub fn fast_log2(x: f32) -> f32 {
    // Poor man's log: the exponent bits give the integer part for free,
    // a tiny polynomial on the mantissa does the rest. Way cheaper than log10.
    let bits = x.max(f32::MIN_POSITIVE).to_bits();
    let exponent = ((bits >> 23) & 0xff) as i32 - 127;
    let t = f32::from_bits((bits & 0x007f_ffff) | 0x3f80_0000) - 1.0;
    let p = t * (1.438_637_7 + t * (-0.677_741_2 + t * (0.321_875_57 + t * -0.082_858_25)));
    exponent as f32 + p
}

/// Fast 2^x, relative error under 4e-6. Always returns a normal positive float.
#[inline]
pub fn fast_exp2(x: f32) -> f32 {
    let x = x.clamp(-126.0, 126.0);
    let mut xi = x as i32;
    if xi as f32 > x {
        xi -= 1;
    }
    let f = x - xi as f32;
    let p = 1.0 + f * (0.693_017_5 + f * (0.241_448_76 + f * (0.051_947_75 + f * 0.013_581_784)));
    p * f32::from_bits(((xi + 127) as u32) << 23)
}

/// Linear magnitude to dB.
#[inline]
pub fn lin_to_db(x: f32) -> f32 {
    fast_log2(x.max(LEVEL_FLOOR)) * DB_PER_LOG2
}

/// dB to linear gain; 0 dB gives exactly 1.0.
#[inline]
pub fn db_to_lin(db: f32) -> f32 {
    fast_exp2(db * LOG2_PER_DB)
}

/// One-pole coefficient covering 10 % to 90 % of a step in `ms` (t90, like the datasheets).
pub fn t90_coeff(ms: f32, sample_rate: f32) -> f32 {
    let samples = (ms as f64 * 0.001 * sample_rate as f64).max(1.0e-3);
    let c = 1.0 - (-(9.0f64.ln()) / samples).exp();
    (c as f32).clamp(f32::MIN_POSITIVE, 1.0)
}

/// One-pole coefficient for a plain time constant (63 %) of `ms`.
pub fn tau_coeff(ms: f32, sample_rate: f32) -> f32 {
    let samples = (ms as f64 * 0.001 * sample_rate as f64).max(1.0e-3);
    let c = 1.0 - (-1.0 / samples).exp();
    (c as f32).clamp(f32::MIN_POSITIVE, 1.0)
}

/// Coefficients of a [`TptSvf`]. One `tan` each, so refresh them at control rate.
#[derive(Clone, Copy, Debug)]
pub struct SvfCoeffs {
    g: f32,
    k: f32,
    a1: f32,
}

impl SvfCoeffs {
    /// Coefficients for cutoff `fc` and quality `q`; the cutoff stays below Nyquist.
    pub fn new(fc: f32, q: f32, sample_rate: f32) -> Self {
        let fc = fc.clamp(1.0, sample_rate * 0.49);
        let g = (std::f64::consts::PI * fc as f64 / sample_rate as f64).tan() as f32;
        let k = 1.0 / q.max(0.1);
        Self {
            g,
            k,
            a1: 1.0 / (1.0 + g * (g + k)),
        }
    }
}

impl Default for SvfCoeffs {
    fn default() -> Self {
        Self::new(1_000.0, std::f32::consts::FRAC_1_SQRT_2, 48_000.0)
    }
}

/// Zavalishin TPT state variable filter, 12 dB/oct. Doesn't blow up when the cutoff jumps.
#[derive(Clone, Copy, Debug, Default)]
pub struct TptSvf {
    s1: f32,
    s2: f32,
}

impl TptSvf {
    /// One sample in, `(high_pass, band_pass, low_pass)` out.
    #[inline]
    pub fn tick(&mut self, x: f32, c: &SvfCoeffs) -> (f32, f32, f32) {
        let hp = (x - (c.k + c.g) * self.s1 - self.s2) * c.a1;
        let v1 = c.g * hp;
        let bp = v1 + self.s1;
        self.s1 = denormal::flush(bp + v1);
        let v2 = c.g * bp;
        let lp = v2 + self.s2;
        self.s2 = denormal::flush(lp + v2);
        (hp, bp, lp)
    }

    /// High-pass output only.
    #[inline]
    pub fn high_pass(&mut self, x: f32, c: &SvfCoeffs) -> f32 {
        self.tick(x, c).0
    }

    /// Low-pass output only.
    #[inline]
    pub fn low_pass(&mut self, x: f32, c: &SvfCoeffs) -> f32 {
        self.tick(x, c).2
    }

    /// Clears the filter memory.
    pub fn reset(&mut self) {
        self.s1 = 0.0;
        self.s2 = 0.0;
    }
}

/// Schroeder all-pass section. With `g == 0` it's just a delay of [`len`](Self::len) samples.
pub struct Diffuser {
    buf: Vec<f32>,
    idx: usize,
}

impl Diffuser {
    /// Section of `ms` milliseconds, at least one sample long.
    pub fn new(ms: f32, sample_rate: f32) -> Self {
        let len = ((ms * 0.001 * sample_rate).round() as usize).max(1);
        Self {
            buf: vec![0.0; len],
            idx: 0,
        }
    }

    /// Delay length in samples.
    pub fn len(&self) -> usize {
        self.buf.len()
    }

    /// Never true, a section holds at least one sample.
    pub fn is_empty(&self) -> bool {
        self.buf.is_empty()
    }

    /// One sample through the all-pass with coefficient `g` (|g| < 1).
    #[inline]
    pub fn process(&mut self, x: f32, g: f32) -> f32 {
        let stored = self.buf[self.idx];
        let v = x + g * stored;
        self.buf[self.idx] = denormal::flush(v);
        self.idx += 1;
        if self.idx >= self.buf.len() {
            self.idx = 0;
        }
        stored - g * v
    }

    /// Clears the section.
    pub fn reset(&mut self) {
        self.buf.fill(0.0);
        self.idx = 0;
    }
}

/// Quadrature sine/cosine LFO, call [`renormalise`](Self::renormalise) once a block.
#[derive(Clone, Copy, Debug)]
pub struct Lfo {
    s: f32,
    c: f32,
    sin_inc: f32,
    cos_inc: f32,
}

impl Lfo {
    /// Oscillator at phase zero running at `hz`.
    pub fn new(hz: f32, sample_rate: f32) -> Self {
        let mut lfo = Self {
            s: 0.0,
            c: 1.0,
            sin_inc: 0.0,
            cos_inc: 1.0,
        };
        lfo.set_rate(hz, sample_rate);
        lfo
    }

    /// New rate, no phase jump.
    pub fn set_rate(&mut self, hz: f32, sample_rate: f32) {
        let w = std::f32::consts::TAU * hz / sample_rate;
        self.sin_inc = w.sin();
        self.cos_inc = w.cos();
    }

    /// Steps one sample, returns `(sin, cos)`.
    #[inline]
    pub fn advance(&mut self) -> (f32, f32) {
        let s = self.s * self.cos_inc + self.c * self.sin_inc;
        self.c = self.c * self.cos_inc - self.s * self.sin_inc;
        self.s = s;
        (self.s, self.c)
    }

    /// Pulls the phasor back onto the unit circle.
    pub fn renormalise(&mut self) {
        // The rotation slowly drifts off the circle in f32, one Newton step
        // per block nudges it back before anyone can hear the wobble.
        let k = 1.5 - 0.5 * (self.s * self.s + self.c * self.c);
        self.s *= k;
        self.c *= k;
    }

    /// Back to phase zero.
    pub fn reset(&mut self) {
        self.s = 0.0;
        self.c = 1.0;
    }
}

/// Running minimum over the last `window` pushes, O(1) amortised (monotonic deque).
pub struct SlidingMin {
    pos: Vec<u32>,
    val: Vec<f32>,
    mask: usize,
    head: usize,
    tail: usize,
}

impl SlidingMin {
    /// Deque big enough for windows up to `max_window` samples.
    pub fn new(max_window: usize) -> Self {
        let cap = (max_window + 1).next_power_of_two();
        Self {
            pos: vec![0; cap],
            val: vec![0.0; cap],
            mask: cap - 1,
            head: 0,
            tail: 0,
        }
    }

    /// Pushes `v` stamped `now`, returns the minimum of the last `window` stamps.
    #[inline]
    pub fn push(&mut self, now: u32, v: f32, window: u32) -> f32 {
        // Anything bigger than the new value can never be the minimum again,
        // so kick it out the back. The front just ages out of the window.
        while self.tail != self.head && self.val[self.tail.wrapping_sub(1) & self.mask] >= v {
            self.tail = self.tail.wrapping_sub(1);
        }
        let slot = self.tail & self.mask;
        self.pos[slot] = now;
        self.val[slot] = v;
        self.tail = self.tail.wrapping_add(1);
        while now.wrapping_sub(self.pos[self.head & self.mask]) >= window.max(1) {
            self.head = self.head.wrapping_add(1);
        }
        self.val[self.head & self.mask]
    }

    /// Empties the deque.
    pub fn reset(&mut self) {
        self.head = 0;
        self.tail = 0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fast_log2_is_accurate() {
        let mut x = 1.0e-6f32;
        while x < 1.0e3 {
            let err = (fast_log2(x) - x.log2()).abs();
            assert!(err < 2.0e-4, "x={x} err={err}");
            x *= 1.013;
        }
    }

    #[test]
    fn fast_exp2_is_accurate() {
        let mut x = -40.0f32;
        while x < 40.0 {
            let rel = (fast_exp2(x) / x.exp2() - 1.0).abs();
            assert!(rel < 1.0e-5, "x={x} rel={rel}");
            x += 0.0137;
        }
    }

    #[test]
    fn unity_gain_is_exact() {
        assert_eq!(db_to_lin(0.0), 1.0);
    }

    #[test]
    fn sliding_min_tracks_window() {
        let mut m = SlidingMin::new(8);
        let data = [0.0, -3.0, 0.0, 0.0, 0.0, -1.0, 0.0, 0.0, 0.0, 0.0];
        let mut out = Vec::new();
        for (i, &v) in data.iter().enumerate() {
            out.push(m.push(i as u32, v, 3));
        }
        assert_eq!(
            out,
            vec![0.0, -3.0, -3.0, -3.0, 0.0, -1.0, -1.0, -1.0, 0.0, 0.0]
        );
    }

    #[test]
    fn svf_low_pass_passes_dc() {
        let c = SvfCoeffs::new(1_000.0, 0.707, 48_000.0);
        let mut f = TptSvf::default();
        let mut y = 0.0;
        for _ in 0..48_000 {
            y = f.low_pass(1.0, &c);
        }
        assert!((y - 1.0).abs() < 1.0e-4);
    }
}
