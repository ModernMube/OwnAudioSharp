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
//! receiving exactly that block. Only when the source reports a *real* end-of-stream
//! ([`TrackSource::is_eof`]) is the processor flushed; a merely transient zero read (prefetch
//! underrun or in-flight seek) leaves the FIFO primed and the remainder is silence-padded by the
//! caller.
//!
//! The stage runs unconditionally while a source is attached — even at unity tempo and pitch —
//! mirroring the legacy managed chain, where SoundTouch always processed. Keeping the FIFO
//! continuously primed means a mid-playback tempo or pitch change lands on a warm processor,
//! so it does not click/crackle from the latency refill a cold start would incur. On a seek the
//! owning track clears the FIFO (via [`TrackStretch::clear`]) so no pre-seek audio leaks out.

use ownaudio_soundtouch::{SettingId, SoundTouchProcessor};

use super::track::TrackSource;

/// Safety cap on the number of source pulls per output block, so a pathological source (one
/// that returns a positive but tiny count every call) can never spin the audio thread. In
/// steady state the loop needs a handful of iterations even at the maximum tempo; the cap is
/// far above that and only bounds the worst case.
const MAX_PULL_ITERS: usize = 128;

/// Per-track time-stretch / pitch-shift stage backed by a SoundTouch processor built and warmed
/// up on the control thread at track construction (see [`TrackStretch::new`]).
pub(crate) struct TrackStretch {
    /// Sample rate the processor is configured for.
    sample_rate: u32,
    /// Channel count the processor is currently configured for (0 = not yet built).
    channels: u16,
    /// The SoundTouch processor, or `None` before the first activation.
    processor: Option<SoundTouchProcessor>,
    /// Reusable interleaved input scratch, pulled from the source while filling the FIFO.
    input: Vec<f32>,
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
    /// Creates a stage for the given sample rate and channel count, pre-sizing the input scratch
    /// to the mixer's maximum block size so the steady-state pull path is allocation-free.
    ///
    /// The SoundTouch processor is built and warmed up **here**, on the control thread that
    /// constructs the track (tracks are built off the audio thread and moved into the mixer via
    /// the command queue). This keeps the processor's heap allocations and the anti-alias FIR
    /// computation off the audio thread entirely: the mixer always calls [`TrackStretch::fill`]
    /// with this same `channels`, so [`TrackStretch::ensure_configured`] hits its fast path on the
    /// audio thread and never rebuilds. A `channels` of 0 leaves the stage idle (the processor is
    /// built lazily on first `fill`, as before), which only the degenerate zero-channel path hits.
    pub(crate) fn new(sample_rate: f32, channels: u16, max_buffer_size: usize) -> Self {
        let block = max_buffer_size.max(1);
        let mut stage = Self {
            sample_rate: sample_rate as u32,
            channels: 0,
            processor: None,
            input: vec![0.0f32; block],
            last_tempo: 1.0,
            last_pitch: 0.0,
            eof_flushed: false,
        };
        if channels >= 1 && stage.ensure_configured(channels) {
            stage.prime(channels, block);
        }
        stage
    }

    /// Warms up the freshly-built processor on the control thread by pumping a few unity-tempo
    /// blank blocks through it and draining the output, then clearing. This grows the internal
    /// FIFOs to their steady-state working set before the track ever renders; because
    /// `FifoSampleBuffer::clear` keeps the backing allocation, that capacity survives, so the audio
    /// thread reuses it instead of reallocating mid-render on the first blocks or on a live
    /// tempo/pitch nudge. Runs off the audio thread, so the scratch allocation here is harmless.
    fn prime(&mut self, channels: u16, block: usize) {
        let ch = channels as usize;
        if ch == 0 || block == 0 {
            return;
        }
        let Some(proc) = self.processor.as_mut() else {
            return;
        };
        let mut scratch = vec![0.0f32; block * ch];
        for _ in 0..8 {
            let _ = proc.put_samples(&scratch, block);
            let avail = proc.available_samples();
            if avail > 0 {
                let _ = proc.receive_samples(&mut scratch, avail.min(block));
            }
        }
        proc.clear();
        // The warm-up left no residual audio, but force the first real block to re-push the
        // tempo/pitch so the processor state is unambiguous.
        self.last_tempo = f32::NAN;
        self.last_pitch = f32::NAN;
    }

    /// Clears the processor's FIFO, dropping any buffered (pre-seek) audio and the EOF latch.
    /// Called by the owning track on a seek or source swap so the stage restarts cleanly from
    /// the new position. No-op before the processor has been built.
    #[inline]
    pub(crate) fn clear(&mut self) {
        if let Some(proc) = self.processor.as_mut() {
            proc.clear();
        }
        self.eof_flushed = false;
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
                // A zero read is ambiguous: it happens at a real end-of-stream, but also on a
                // transient prefetch underrun and while a seek is in flight. Only a *true* EOF
                // should flush the FIFO tail — flushing on a momentary dry spell pumps blank
                // frames into the middle of the stream (audible garbage/silence tail) and, worse,
                // does heavy WSOLA work on the audio thread exactly when it is already starved.
                // On a transient zero we simply stop pulling and let the caller silence-pad; the
                // FIFO stays primed for when audio resumes.
                let at_eof = source.as_ref().map(|s| s.is_eof()).unwrap_or(false);
                if at_eof && !self.eof_flushed {
                    // Flush the FIFO tail out exactly once. Re-flushing every block would pad an
                    // endless zero tail, so guard it and just drain thereafter.
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

        fn is_eof(&self) -> bool {
            self.remaining_frames == 0
        }
    }

    /// Source that returns zero for a stretch of reads *without* reporting EOF, then resumes —
    /// modelling a transient prefetch underrun or an in-flight seek. The stretch stage must not
    /// flush the FIFO on these, only on a genuine EOF.
    struct UnderrunSource {
        channels: usize,
        /// Number of leading reads that return zero (underrun) before audio resumes.
        underruns_left: usize,
    }

    impl TrackSource for UnderrunSource {
        fn read(&mut self, out: &mut [f32]) -> usize {
            if self.underruns_left > 0 {
                self.underruns_left -= 1;
                return 0;
            }
            let ch = self.channels;
            let frames = out.len() / ch;
            for s in out[..frames * ch].iter_mut() {
                *s = 0.2;
            }
            frames * ch
        }
        // is_eof() stays the default `false`: an underrun is not end-of-stream.
    }

    #[test]
    fn unity_still_produces_full_block() {
        // The stage runs even at unity tempo/pitch; after warm-up it returns a full block.
        let mut s = TrackStretch::new(48000.0, 2, 4096);
        let mut source: Option<Box<dyn TrackSource>> =
            Some(Box::new(SineSource { phase: 0.0, channels: 2 }));
        let mut out = vec![0.0f32; 1024];

        let mut last = 0usize;
        for _ in 0..8 {
            last = s.fill(&mut source, &mut out, 2, 1.0, 0.0);
            assert!(out.iter().all(|v| v.is_finite()));
        }
        assert_eq!(last, out.len(), "unity steady-state block must be fully produced");
    }

    #[test]
    fn faster_tempo_fills_full_block_and_is_finite() {
        let mut s = TrackStretch::new(48000.0, 2, 4096);
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
        let mut s = TrackStretch::new(44100.0, 2, 4096);
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
        let mut s = TrackStretch::new(48000.0, 2, 4096);
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
    fn transient_underrun_does_not_flush_and_resumes() {
        // A source that underruns (zero read, not EOF) for several blocks then resumes must
        // keep producing finite audio afterwards, with the FIFO left primed (no flush, no spin).
        let mut s = TrackStretch::new(48000.0, 2, 4096);
        let mut source: Option<Box<dyn TrackSource>> = Some(Box::new(UnderrunSource {
            channels: 2,
            underruns_left: 4,
        }));
        let mut out = vec![0.0f32; 1024];

        // Drive through the underrun window and past it.
        let mut produced_energy = 0.0f64;
        for _ in 0..24 {
            s.fill(&mut source, &mut out, 2, 1.25, 0.0);
            assert!(out.iter().all(|v| v.is_finite()));
            produced_energy += out.iter().map(|&v| (v as f64) * (v as f64)).sum::<f64>();
        }
        // The stage must not have latched EOF on the transient zeros.
        assert!(!s.eof_flushed, "transient underrun must not flush/latch EOF");
        assert!(
            produced_energy > 1.0,
            "stage must resume producing audio after a transient underrun"
        );
    }

    #[test]
    fn clear_is_safe_before_and_after_use() {
        let mut s = TrackStretch::new(48000.0, 2, 2048);
        // Clearing before the processor is built must not panic.
        s.clear();

        let mut source: Option<Box<dyn TrackSource>> =
            Some(Box::new(SineSource { phase: 0.0, channels: 2 }));
        let mut out = vec![0.0f32; 512];
        s.fill(&mut source, &mut out, 2, 1.3, 0.0);

        // Clearing after use drops the FIFO tail; the stage keeps producing afterwards.
        s.clear();
        let n = s.fill(&mut source, &mut out, 2, 1.3, 0.0);
        assert!(n <= out.len());
        assert!(out.iter().all(|v| v.is_finite()));
    }
}
