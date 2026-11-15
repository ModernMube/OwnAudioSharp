//! Gain ramps the control thread asks for and the audio thread runs frame by frame: a fade
//! in, a fade out to silence, a dip and back. A separate multiplier from the track or master
//! gain, so the gain the user set stays where it is while the fade moves.
//!
//! The ramp is a straight line in dB (an exponential in gain), the way the ear hears an even
//! move. Silence sits at [`FADE_FLOOR_DB`]: a fade to zero runs down to the floor and then
//! lands on true zero, and a fade up from zero starts there.

use std::sync::atomic::{AtomicU32, Ordering};

/// Where a fade calls it silence.
pub const FADE_FLOOR_DB: f32 = -60.0;

fn floor_gain() -> f32 {
    10.0f32.powf(FADE_FLOOR_DB / 20.0)
}

/// The control-thread side of one ramp: what was asked for, and how far the audio thread got.
///
/// A request writes the target and the length first and bumps `requested` last (Release); the
/// audio thread reads `requested` first (Acquire), so it never starts a ramp from half a request.
/// `reached` is the last request the audio thread has finished.
pub struct FadeShared {
    target_bits: AtomicU32,
    frames: AtomicU32,
    requested: AtomicU32,
    reached: AtomicU32,
    level_bits: AtomicU32,
}

impl FadeShared {
    /// Resting at unity, nothing asked for.
    pub fn new() -> Self {
        Self {
            target_bits: AtomicU32::new(1.0f32.to_bits()),
            frames: AtomicU32::new(0),
            requested: AtomicU32::new(0),
            reached: AtomicU32::new(0),
            level_bits: AtomicU32::new(1.0f32.to_bits()),
        }
    }

    /// Ramps from wherever the fade stands to `target` (linear, 0 = silence, 1 = unity) over
    /// `frames`; 0 frames jumps. A request made while another runs takes over from the level
    /// reached so far. Returns the request's number, for [`FadeShared::reached`].
    pub fn request(&self, target: f32, frames: u32) -> u32 {
        let target = if target.is_finite() {
            target.max(0.0)
        } else {
            0.0
        };
        self.target_bits.store(target.to_bits(), Ordering::Relaxed);
        self.frames.store(frames, Ordering::Relaxed);
        self.requested
            .fetch_add(1, Ordering::AcqRel)
            .wrapping_add(1)
    }

    /// The last request the audio thread has finished.
    pub fn reached(&self) -> u32 {
        self.reached.load(Ordering::Acquire)
    }

    /// True once the latest request has landed — the block that ended it is rendered.
    pub fn is_done(&self) -> bool {
        self.reached() == self.requested.load(Ordering::Acquire)
    }

    /// The fade's level at the end of the last rendered block.
    pub fn level(&self) -> f32 {
        f32::from_bits(self.level_bits.load(Ordering::Relaxed))
    }
}

/// The audio-thread side: the ramp in flight. Owned by whoever renders, never shared.
pub struct FadeStage {
    seen: u32,
    level: f32,
    target: f32,
    ratio: f32,
    remaining: u32,
}

impl FadeStage {
    /// At unity, where a fade that was never asked for stays.
    pub fn new() -> Self {
        Self {
            seen: 0,
            level: 1.0,
            target: 1.0,
            ratio: 1.0,
            remaining: 0,
        }
    }

    fn start(&mut self, target: f32, frames: u32) {
        self.target = target;

        if frames == 0 {
            self.level = target;
            self.remaining = 0;
            return;
        }

        let floor = floor_gain();
        let from = self.level.max(floor);
        let to = target.max(floor);

        self.level = from;
        self.ratio = (to / from).powf(1.0 / frames as f32);
        self.remaining = frames;
    }

    /// Scales an interleaved block of `ch` channels by the ramp. Picks up a new request at the
    /// top of the block, says when one is done. At rest on unity the block is left alone, so a
    /// fade nobody asked for costs nothing and changes nothing.
    pub fn apply(&mut self, shared: &FadeShared, buf: &mut [f32], ch: usize) {
        let asked = shared.requested.load(Ordering::Acquire);
        if asked != self.seen {
            self.seen = asked;
            self.start(
                f32::from_bits(shared.target_bits.load(Ordering::Relaxed)),
                shared.frames.load(Ordering::Relaxed),
            );
        }

        if self.remaining == 0 {
            if self.level == 0.0 {
                buf.fill(0.0);
            } else if self.level != 1.0 {
                for s in buf.iter_mut() {
                    *s *= self.level;
                }
            }
        } else {
            for frame in buf.chunks_mut(ch.max(1)) {
                if self.remaining > 0 {
                    self.remaining -= 1;
                    self.level = if self.remaining == 0 {
                        self.target
                    } else {
                        self.level * self.ratio
                    };
                }

                for s in frame.iter_mut() {
                    *s *= self.level;
                }
            }
        }

        shared
            .level_bits
            .store(self.level.to_bits(), Ordering::Relaxed);

        if self.remaining == 0 && shared.reached.load(Ordering::Relaxed) != self.seen {
            shared.reached.store(self.seen, Ordering::Release);
        }
    }

    /// Where the fade stands right now.
    pub fn level(&self) -> f32 {
        self.level
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn db(gain: f32) -> f32 {
        20.0 * gain.log10()
    }

    #[test]
    fn an_unasked_fade_leaves_the_audio_alone() {
        let shared = FadeShared::new();
        let mut stage = FadeStage::new();
        let mut buf = [0.3f32, -0.7, 0.1, 0.9];

        stage.apply(&shared, &mut buf, 2);

        assert_eq!(buf, [0.3, -0.7, 0.1, 0.9]);
        assert!(shared.is_done());
    }

    #[test]
    fn a_fade_out_lands_on_silence_and_says_so() {
        let shared = FadeShared::new();
        let mut stage = FadeStage::new();
        let seq = shared.request(0.0, 16);

        let mut buf = [1.0f32; 16];
        stage.apply(&shared, &mut buf, 2);
        assert!(!shared.is_done());
        assert!(buf[14] < buf[0]);

        let mut buf = [1.0f32; 16];
        stage.apply(&shared, &mut buf, 2);
        assert_eq!(buf[15], 0.0);
        assert_eq!(shared.reached(), seq);
        assert!(shared.is_done());
        assert_eq!(shared.level(), 0.0);

        let mut buf = [1.0f32; 16];
        stage.apply(&shared, &mut buf, 2);
        assert!(buf.iter().all(|&s| s == 0.0));
    }

    #[test]
    fn the_ramp_is_a_straight_line_in_db() {
        let shared = FadeShared::new();
        let mut stage = FadeStage::new();
        shared.request(0.1, 4);

        let mut buf = [1.0f32; 4];
        stage.apply(&shared, &mut buf, 1);

        let steps: Vec<f32> = buf.windows(2).map(|w| db(w[1]) - db(w[0])).collect();
        for step in &steps {
            assert!((step - steps[0]).abs() < 1e-3, "steps {steps:?}");
        }
        assert!((buf[3] - 0.1).abs() < 1e-6);
    }

    #[test]
    fn a_fade_in_from_silence_starts_at_the_floor() {
        let shared = FadeShared::new();
        let mut stage = FadeStage::new();
        shared.request(0.0, 0);
        stage.apply(&shared, &mut [1.0f32; 2], 1);

        shared.request(1.0, 100);
        let mut buf = [1.0f32; 100];
        stage.apply(&shared, &mut buf, 1);

        assert!(db(buf[0]) < FADE_FLOOR_DB + 1.0);
        assert_eq!(buf[99], 1.0);
        assert!(shared.is_done());
    }

    #[test]
    fn a_new_request_takes_over_from_where_the_old_one_stood() {
        let shared = FadeShared::new();
        let mut stage = FadeStage::new();
        shared.request(0.0, 100);
        stage.apply(&shared, &mut [1.0f32; 50], 1);
        let halfway = stage.level();

        shared.request(1.0, 100);
        let mut buf = [1.0f32; 1];
        stage.apply(&shared, &mut buf, 1);

        assert!(buf[0] > halfway * 0.99 && buf[0] < 1.0);
    }

    #[test]
    fn both_channels_of_a_frame_get_the_same_gain() {
        let shared = FadeShared::new();
        let mut stage = FadeStage::new();
        shared.request(0.5, 3);

        let mut buf = [1.0f32; 6];
        stage.apply(&shared, &mut buf, 2);

        for frame in buf.chunks(2) {
            assert_eq!(frame[0], frame[1]);
        }
    }
}
