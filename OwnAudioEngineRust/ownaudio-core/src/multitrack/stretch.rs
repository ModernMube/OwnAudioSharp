//! Per-track time-stretch / pitch-shift stage.
//!
//! Applies the track's [`TrackShared::tempo_ratio`](super::track::TrackShared::tempo_ratio)
//! and [`TrackShared::pitch_semitones`](super::track::TrackShared::pitch_semitones) to the
//! audio pulled from the track's [`TrackSource`], using the WSOLA
//! [`ownaudio_soundtouch::SoundTouchProcessor`] — the same engine (and the same mandatory
//! settings) the managed `OwnaudioNET.Processing.SoundTouchProcessor` wrapper drove in the
//! legacy chain.
//!
//! ## Rate adaptation
//!
//! SoundTouch is a variable-ratio FIFO: at tempo `r` it consumes roughly `r` input frames per
//! output frame, and it introduces an initial latency before it yields any output. The mixer,
//! however, needs exactly one block of output per render call. This stage bridges the two by
//! pulling input blocks from the source until the processor can emit a full output block, then
//! receiving exactly that block. Only when the source is exhausted (returns zero) is the
//! processor flushed and the remainder silence-padded by the caller.
//!
//! When both tempo and pitch are at unity the stage is a transparent bypass: the caller reads
//! the source directly and [`TrackStretch::deactivate`] clears the processor once so no stale
//! FIFO latency leaks in when stretching resumes.

use ownaudio_soundtouch::{SettingId, SoundTouchProcessor};

use super::track::TrackSource;

/// Below this absolute deviation from `1.0` the tempo ratio is treated as unity.
const TEMPO_EPSILON: f32 = 0.001;

/// Below this absolute semitone amount the pitch shift is treated as none.
const PITCH_EPSILON: f32 = 0.001;

/// Safety cap on the number of source pulls per output block, so a pathological source (one
/// that returns a positive but tiny count every call) can never spin the audio thread. In
/// steady state the loop needs a handful of iterations even at the maximum tempo; the cap is
/// far above that and only bounds the worst case.
const MAX_PULL_ITERS: usize = 128;

/// Per-track time-stretch / pitch-shift stage backed by a lazily-built SoundTouch processor.
pub(crate) struct TrackStretch {
    /// Sample rate the processor is configured for.
    sample_rate: u32,
    /// Channel count the processor is currently configured for (0 = not yet built).
    channels: u16,
    /// The SoundTouch processor, or `None` before the first activation.
    processor: Option<SoundTouchProcessor>,
    /// Reusable interleaved input scratch, pulled from the source while filling the FIFO.
    input: Vec<f32>,
    /// Whether the processor produced stretched output on the previous block, so a transition
    /// back to unity can clear the stale FIFO latency exactly once.
    active: bool,
    /// Last tempo pushed to the processor, to avoid redundant re-configuration each block.
    last_tempo: f32,
    /// Last pitch (semitones) pushed to the processor.
    last_pitch: f32,
    /// Set once the source has reported EOF and the FIFO tail was flushed, so `flush` is not
    /// re-issued every block (which would emit an endless zero-padded tail). Cleared as soon
    /// as the source yields samples again (e.g. after a seek or loop restart).
    eof_flushed: bool,
}

impl TrackStretch {
    /// Creates an idle stage for the given sample rate, pre-sizing the input scratch to the
    /// mixer's maximum block size so the steady-state pull path is allocation-free.
    pub(crate) fn new(sample_rate: f32, max_buffer_size: usize) -> Self {
        Self {
            sample_rate: sample_rate as u32,
            channels: 0,
            processor: None,
            input: vec![0.0f32; max_buffer_size.max(1)],
            active: false,
            last_tempo: 1.0,
            last_pitch: 0.0,
            eof_flushed: false,
        }
    }

    /// Returns whether the given tempo/pitch require stretching (i.e. either deviates from
    /// unity by more than its epsilon).
    #[inline]
    pub(crate) fn is_active(&self, tempo: f32, pitch: f32) -> bool {
        (tempo - 1.0).abs() > TEMPO_EPSILON || pitch.abs() > PITCH_EPSILON
    }

    /// Clears the processor's FIFO once when the stage transitions from stretching back to
    /// unity, so no old-tempo audio leaks out the next time stretching resumes. No-op while
    /// already inactive.
    #[inline]
    pub(crate) fn deactivate(&mut self) {
        if self.active {
            if let Some(proc) = self.processor.as_mut() {
                proc.clear();
            }
            self.active = false;
            self.eof_flushed = false;
        }
    }

    /// Lazily (re)builds the processor for `channels`, applying the mandatory settings that
    /// match the managed wrapper (quick-seek off, anti-alias filter on). Returns `false` when
    /// the sample rate or channel count is rejected, in which case the caller passes through.
    fn ensure_configured(&mut self, channels: u16) -> bool {
        if self.processor.is_some() && self.channels == channels {
            return true;
        }

        let mut proc = SoundTouchProcessor::new();
        if proc.set_sample_rate(self.sample_rate).is_err() {
            self.processor = None;
            return false;
        }
        if proc.set_channels(channels as usize).is_err() {
            self.processor = None;
            return false;
        }
        proc.set_setting(SettingId::UseQuickSeek, 0);
        proc.set_setting(SettingId::UseAntiAliasFilter, 1);

        self.channels = channels;
        self.processor = Some(proc);
        // Force the tempo/pitch to be re-pushed onto the fresh processor.
        self.last_tempo = f32::NAN;
        self.last_pitch = f32::NAN;
        true
    }

    /// Pushes `tempo`/`pitch` onto the processor only when they changed, so a slider held
    /// steady does not re-touch SoundTouch every block.
    fn apply_params(&mut self, tempo: f32, pitch: f32) {
        if let Some(proc) = self.processor.as_mut() {
            if !(tempo == self.last_tempo) {
                proc.set_tempo(tempo as f64);
                self.last_tempo = tempo;
            }
            if !(pitch == self.last_pitch) {
                proc.set_pitch_semitones(pitch as f64);
                self.last_pitch = pitch;
            }
        }
    }

    /// Fills `out` with one block of stretched audio pulled from `source`, applying `tempo`
    /// and `pitch`. Returns the number of interleaved samples written (equal to `out.len()`
    /// in steady state; fewer only while the source is draining at EOF, the caller
    /// silence-pads the tail).
    pub(crate) fn fill(
        &mut self,
        source: &mut Option<Box<dyn TrackSource>>,
        out: &mut [f32],
        channels: u16,
        tempo: f32,
        pitch: f32,
    ) -> usize {
        if channels == 0 || out.is_empty() {
            return 0;
        }
        if !self.ensure_configured(channels) {
            // Configuration failed — fall back to a straight source read so the track is not
            // silenced by an unsupported rate/channel combination.
            return match source.as_mut() {
                Some(src) => src.read(out),
                None => 0,
            };
        }

        self.apply_params(tempo, pitch);
        self.active = true;

        let ch = channels as usize;
        let want_frames = out.len() / ch;
        if want_frames == 0 {
            return 0;
        }
        if self.input.len() < out.len() {
            self.input.resize(out.len(), 0.0);
        }

        let mut iters = 0usize;
        while self
            .processor
            .as_ref()
            .map(|p| p.available_samples())
            .unwrap_or(0)
            < want_frames
        {
            let read = match source.as_mut() {
                Some(src) => src.read(&mut self.input[..out.len()]),
                None => 0,
            };

            if read == 0 {
                // Source exhausted: flush the FIFO tail out exactly once. Re-flushing every
                // block would pad an endless zero tail, so guard it and just drain thereafter.
                if !self.eof_flushed {
                    if let Some(proc) = self.processor.as_mut() {
                        proc.flush();
                    }
                    self.eof_flushed = true;
                }
                break;
            }

            self.eof_flushed = false;
            let in_frames = read / ch;
            if in_frames == 0 {
                break;
            }
            if let Some(proc) = self.processor.as_mut() {
                let _ = proc.put_samples(&self.input[..in_frames * ch], in_frames);
            }

            iters += 1;
            if iters >= MAX_PULL_ITERS {
                break;
            }
        }

        let got = match self.processor.as_mut() {
            Some(proc) => proc.receive_samples(out, want_frames),
            None => 0,
        };
        got * ch
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Source that yields a sine sweep so stretched output is non-trivially structured.
    struct SineSource {
        phase: f32,
        channels: usize,
    }

    impl TrackSource for SineSource {
        fn read(&mut self, out: &mut [f32]) -> usize {
            let frames = out.len() / self.channels;
            for f in 0..frames {
                let s = self.phase.sin() * 0.5;
                self.phase += 0.05;
                for c in 0..self.channels {
                    out[f * self.channels + c] = s;
                }
            }
            frames * self.channels
        }
    }

    /// Source that produces a fixed number of frames, then reports EOF (zero).
    struct FiniteSource {
        remaining_frames: usize,
        channels: usize,
    }

    impl TrackSource for FiniteSource {
        fn read(&mut self, out: &mut [f32]) -> usize {
            let ch = self.channels;
            let want = out.len() / ch;
            let give = want.min(self.remaining_frames);
            for s in out[..give * ch].iter_mut() {
                *s = 0.25;
            }
            self.remaining_frames -= give;
            give * ch
        }
    }

    #[test]
    fn unity_is_reported_inactive() {
        let s = TrackStretch::new(48000.0, 2048);
        assert!(!s.is_active(1.0, 0.0));
        assert!(s.is_active(1.2, 0.0));
        assert!(s.is_active(1.0, 3.0));
    }

    #[test]
    fn faster_tempo_fills_full_block_and_is_finite() {
        let mut s = TrackStretch::new(48000.0, 4096);
        let mut source: Option<Box<dyn TrackSource>> =
            Some(Box::new(SineSource { phase: 0.0, channels: 2 }));
        let mut out = vec![0.0f32; 1024];

        // Warm-up plus a few steady-state blocks at 1.5x tempo.
        let mut last = 0usize;
        for _ in 0..8 {
            last = s.fill(&mut source, &mut out, 2, 1.5, 0.0);
            assert!(out.iter().all(|v| v.is_finite()));
        }
        assert_eq!(last, out.len(), "steady-state block must be fully produced");
    }

    #[test]
    fn pitch_only_produces_output() {
        let mut s = TrackStretch::new(44100.0, 4096);
        let mut source: Option<Box<dyn TrackSource>> =
            Some(Box::new(SineSource { phase: 0.0, channels: 2 }));
        let mut out = vec![0.0f32; 1024];

        let mut produced_energy = 0.0f64;
        for _ in 0..8 {
            s.fill(&mut source, &mut out, 2, 1.0, 4.0);
            produced_energy += out.iter().map(|&v| (v as f64) * (v as f64)).sum::<f64>();
        }
        assert!(produced_energy > 1.0, "pitch-shifted output unexpectedly silent");
    }

    #[test]
    fn eof_drains_without_spinning() {
        let mut s = TrackStretch::new(48000.0, 4096);
        let mut source: Option<Box<dyn TrackSource>> = Some(Box::new(FiniteSource {
            remaining_frames: 500,
            channels: 2,
        }));
        let mut out = vec![0.0f32; 1024];

        // Drive well past the source's content; must terminate and eventually report 0.
        let mut saw_zero = false;
        for _ in 0..64 {
            let n = s.fill(&mut source, &mut out, 2, 1.25, 0.0);
            assert!(n <= out.len());
            if n == 0 {
                saw_zero = true;
                break;
            }
        }
        assert!(saw_zero, "stretch must report EOF after the finite source drains");
    }

    #[test]
    fn deactivate_clears_only_once() {
        let mut s = TrackStretch::new(48000.0, 2048);
        let mut source: Option<Box<dyn TrackSource>> =
            Some(Box::new(SineSource { phase: 0.0, channels: 2 }));
        let mut out = vec![0.0f32; 512];

        s.fill(&mut source, &mut out, 2, 1.3, 0.0);
        assert!(s.active);
        s.deactivate();
        assert!(!s.active);
        // Second deactivate is a no-op and must not panic.
        s.deactivate();
        assert!(!s.active);
    }
}
