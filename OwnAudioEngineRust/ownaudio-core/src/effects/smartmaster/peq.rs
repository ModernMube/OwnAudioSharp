//! Input parametric EQ — eight fully swept bands, the stage a PA2 tunes a room
//! with once the graphic EQ has done the coarse work.
//!
//! A band with 0 dB gain is skipped entirely, so an unused PEQ costs nothing.

use super::biquad::{Coeffs, State};

/// Bands in the section, matching the PA2's input PEQ.
pub const BANDS: usize = 8;

/// Channels the per-band state is pre-allocated for.
const PREALLOC_CHANNELS: usize = 2;

const MIN_GAIN_DB: f32 = -20.0;
const MAX_GAIN_DB: f32 = 20.0;

/// Gains smaller than this leave the band out of the chain.
const ACTIVE_EPS_DB: f32 = 0.01;

/// What shape a band takes.
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum BandShape {
    Bell,
    LowShelf,
    HighShelf,
}

impl BandShape {
    /// Wire encoding, so the C# side can push it as a plain float.
    pub fn from_param(v: f32) -> Self {
        match v.round() as i32 {
            1 => Self::LowShelf,
            2 => Self::HighShelf,
            _ => Self::Bell,
        }
    }

    pub fn to_param(self) -> f32 {
        match self {
            Self::Bell => 0.0,
            Self::LowShelf => 1.0,
            Self::HighShelf => 2.0,
        }
    }
}

#[derive(Clone, Copy)]
struct Band {
    shape: BandShape,
    freq: f32,
    q: f32,
    gain_db: f32,
    coeffs: Coeffs,
}

impl Band {
    fn recompute(&mut self, sample_rate: f32) {
        self.coeffs = match self.shape {
            BandShape::Bell => Coeffs::peaking(sample_rate, self.freq, self.q, self.gain_db),
            BandShape::LowShelf => Coeffs::low_shelf(sample_rate, self.freq, self.q, self.gain_db),
            BandShape::HighShelf => {
                Coeffs::high_shelf(sample_rate, self.freq, self.q, self.gain_db)
            }
        };
    }
}

/// Eight-band parametric EQ, one biquad per active band per channel.
pub struct ParametricEq {
    sample_rate: f32,
    bands: [Band; BANDS],
    active: [usize; BANDS],
    active_count: usize,
    /// Flattened `channel * BANDS + band`.
    state: Vec<State>,
    channels: usize,
}

impl ParametricEq {
    /// Flat, with the bands spread over the spectrum so a UI has sane starting
    /// points.
    pub fn new(sample_rate: f32) -> Self {
        let defaults = [
            60.0, 120.0, 250.0, 500.0, 1_000.0, 2_500.0, 6_000.0, 12_000.0,
        ];
        let mut bands = [Band {
            shape: BandShape::Bell,
            freq: 1_000.0,
            q: 1.4,
            gain_db: 0.0,
            coeffs: Coeffs::IDENTITY,
        }; BANDS];

        for (b, f) in bands.iter_mut().zip(defaults) {
            b.freq = f;
            b.recompute(sample_rate);
        }

        Self {
            sample_rate,
            bands,
            active: [0; BANDS],
            active_count: 0,
            state: vec![State::default(); PREALLOC_CHANNELS * BANDS],
            channels: PREALLOC_CHANNELS,
        }
    }

    pub fn set_shape(&mut self, band: usize, shape: BandShape) {
        if let Some(b) = self.bands.get_mut(band) {
            b.shape = shape;
            b.recompute(self.sample_rate);
        }
    }

    pub fn set_frequency(&mut self, band: usize, freq: f32) {
        if let Some(b) = self.bands.get_mut(band) {
            b.freq = freq.clamp(20.0, 20_000.0);
            b.recompute(self.sample_rate);
        }
    }

    pub fn set_q(&mut self, band: usize, q: f32) {
        if let Some(b) = self.bands.get_mut(band) {
            b.q = q.clamp(0.1, 16.0);
            b.recompute(self.sample_rate);
        }
    }

    pub fn set_gain_db(&mut self, band: usize, gain_db: f32) {
        if let Some(b) = self.bands.get_mut(band) {
            b.gain_db = gain_db.clamp(MIN_GAIN_DB, MAX_GAIN_DB);
            b.recompute(self.sample_rate);
            self.rebuild_active();
        }
    }

    pub fn shape(&self, band: usize) -> Option<BandShape> {
        self.bands.get(band).map(|b| b.shape)
    }

    pub fn frequency(&self, band: usize) -> Option<f32> {
        self.bands.get(band).map(|b| b.freq)
    }

    pub fn q(&self, band: usize) -> Option<f32> {
        self.bands.get(band).map(|b| b.q)
    }

    pub fn gain_db(&self, band: usize) -> Option<f32> {
        self.bands.get(band).map(|b| b.gain_db)
    }

    fn rebuild_active(&mut self) {
        self.active_count = 0;
        for (i, b) in self.bands.iter().enumerate() {
            if b.gain_db.abs() > ACTIVE_EPS_DB {
                self.active[self.active_count] = i;
                self.active_count += 1;
            }
        }
    }

    /// Runs the active bands over an interleaved block, in place.
    pub fn process(&mut self, buffer: &mut [f32], channels: u16) {
        if self.active_count == 0 || channels == 0 {
            return;
        }
        let ch = channels as usize;
        if ch > self.channels {
            self.state.resize(ch * BANDS, State::default());
            self.channels = ch;
        }

        for frame in buffer.chunks_mut(ch) {
            for (c, sample) in frame.iter_mut().enumerate() {
                let mut x = *sample;
                for &b in &self.active[..self.active_count] {
                    x = self.state[c * BANDS + b].tick(&self.bands[b].coeffs, x);
                }
                *sample = x;
            }
        }
    }

    pub fn reset(&mut self) {
        for s in self.state.iter_mut() {
            s.clear();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SR: f32 = 48_000.0;

    fn rms_at(eq: &mut ParametricEq, freq: f32) -> f32 {
        let n = 24_000;
        let mut buf: Vec<f32> = (0..n)
            .map(|i| (2.0 * std::f32::consts::PI * freq * i as f32 / SR).sin())
            .collect();
        eq.process(&mut buf, 1);
        let tail = &buf[n / 2..];
        (tail.iter().map(|x| x * x).sum::<f32>() / tail.len() as f32).sqrt()
            * std::f32::consts::SQRT_2
    }

    #[test]
    fn flat_eq_is_bit_transparent() {
        let mut eq = ParametricEq::new(SR);
        let input: Vec<f32> = (0..512).map(|i| (i as f32 * 0.01).sin()).collect();
        let mut buf = input.clone();
        eq.process(&mut buf, 1);
        assert_eq!(buf, input);
    }

    #[test]
    fn bell_hits_its_gain_at_centre() {
        let mut eq = ParametricEq::new(SR);
        eq.set_frequency(0, 1_000.0);
        eq.set_q(0, 2.0);
        eq.set_gain_db(0, -8.0);

        let g = 20.0 * rms_at(&mut eq, 1_000.0).log10();
        assert!((g + 8.0).abs() < 0.4, "got {g} dB");
    }

    #[test]
    fn high_shelf_lifts_the_top_and_leaves_the_bottom() {
        let mut eq = ParametricEq::new(SR);
        eq.set_shape(1, BandShape::HighShelf);
        eq.set_frequency(1, 4_000.0);
        eq.set_q(1, 0.707);
        eq.set_gain_db(1, 6.0);

        let top = 20.0 * rms_at(&mut eq, 12_000.0).log10();
        eq.reset();
        let bottom = 20.0 * rms_at(&mut eq, 100.0).log10();

        assert!((top - 6.0).abs() < 0.5, "shelf top {top} dB");
        assert!(bottom.abs() < 0.5, "shelf bottom {bottom} dB");
    }

    #[test]
    fn shape_param_round_trips() {
        assert!(BandShape::from_param(0.0) == BandShape::Bell);
        assert!(BandShape::from_param(1.0) == BandShape::LowShelf);
        assert!(BandShape::from_param(2.0) == BandShape::HighShelf);
        assert_eq!(BandShape::HighShelf.to_param(), 2.0);
    }
}
