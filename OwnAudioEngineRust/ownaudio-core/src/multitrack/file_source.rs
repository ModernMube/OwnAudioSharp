//! File-backed track source: owns a streaming decoder and its prefetch thread,
//! and feeds the decoded samples straight into a mixer track on the audio
//! thread — with no managed (C#) pump in the loop.
//!
//! A [`FileTrackSource`] wraps a [`StreamingTrack`] and implements
//! [`TrackSource`], so it can be installed as a track's source through the mixer
//! command queue. Loop and end-of-stream handling live here on the audio thread;
//! the control side observes them through a shared [`FileSourceControl`].
//!
//! An armed jump gets a second lane: on the first [`FileSourceControl::arm_jump`] the control
//! side opens the file again, the spare stream is seeked to the jump's target and prefetches there,
//! and when the cursor reaches the jump's frame the audio thread swaps the two — the old stream's
//! next few milliseconds crossfaded into the new. Nobody ever seeks the stream that is playing, so
//! a decoder that cannot seek to the exact frame cannot put a click into it either. The lanes keep
//! swapping roles from there on; neither is opened or dropped on the audio thread.

use std::sync::atomic::{fence, AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

use rtrb::{Consumer, Producer, RingBuffer};

use crate::decoder::streaming_track::SeekState;
use crate::decoder::{AudioStreamInfo, StreamingTrack};
use crate::error::Result;
use crate::multitrack::track::TrackSource;

/// How long a jump crossfades what would have followed into where it lands, and a stop fades out.
const JUMP_FADE_MS: u32 = 5;

/// The jump target that is none: at the frame the source fades out and stays silent until a seek.
pub const STOP: u64 = u64::MAX;

/// What it takes to open the file again for the spare lane.
struct Opening {
    path: String,
    sample_rate: u32,
    channels: u32,
    prefetch_frames: usize,
}

/// Control-side bookkeeping: each lane's seek handle, and the spare's way to the audio thread.
struct Lanes {
    seeks: [Option<Arc<SeekState>>; 2],
    spare_tx: Producer<StreamingTrack>,
}

/// Control-side state shared between the control thread (FFI / C#) and a
/// [`FileTrackSource`] running on the audio thread.
///
/// Once the source is installed on a track it is owned by the audio thread, so
/// every parameter the control side still needs to touch — loop toggle, the
/// finished latch, seeking, armed jumps — lives here behind atomics.
pub struct FileSourceControl {
    /// When `true`, reaching end-of-stream rewinds to the start and keeps
    /// playing instead of finishing.
    loop_enabled: AtomicBool,
    /// Latched `true` once the source reaches end-of-stream without looping;
    /// polled by the control side to raise its completion event. Cleared as soon
    /// as audio flows again (after a seek).
    finished: AtomicBool,
    /// The lane playing, published by the audio thread when a jump swaps them.
    active: AtomicUsize,
    /// Bumped by every seek, so the audio thread takes `seek_frame` as its cursor.
    seek_epoch: AtomicU64,
    seek_frame: AtomicU64,
    /// The armed jump, `jump_id` 0 for none; a seqlock, as on the group source.
    jump_id: AtomicU64,
    jump_at: AtomicU64,
    jump_to: AtomicU64,
    /// The last jump carried out, and the last one whose frame came before its target was ready.
    jump_taken: AtomicU64,
    jump_missed: AtomicU64,
    opening: Opening,
    lanes: Mutex<Lanes>,
}

impl FileSourceControl {
    /// Enables or disables seamless looping.
    #[inline]
    pub fn set_loop(&self, enabled: bool) {
        self.loop_enabled.store(enabled, Ordering::Relaxed);
    }

    /// Returns whether looping is currently enabled.
    #[inline]
    pub fn loop_enabled(&self) -> bool {
        self.loop_enabled.load(Ordering::Relaxed)
    }

    /// Returns whether the source has reached end-of-stream without looping.
    #[inline]
    pub fn is_finished(&self) -> bool {
        self.finished.load(Ordering::Relaxed)
    }

    /// Requests a seek of the lane playing to `frame_position` (output frames). Non-blocking —
    /// the decoder's prefetch thread performs the reposition on its next iteration, and the
    /// finished latch clears once audio flows from the new position.
    pub fn seek_frames(&self, frame_position: u64) {
        let lanes = self.lock();
        if let Some(seek) = &lanes.seeks[self.active.load(Ordering::Acquire)] {
            seek.request(frame_position);
        }
        self.seek_frame.store(frame_position, Ordering::Relaxed);
        self.seek_epoch.fetch_add(1, Ordering::Release);
    }

    /// When the cursor reaches content frame `at`, the source goes on from `to` — inside the
    /// block, the first few milliseconds crossfaded. The spare stream is opened here on the first
    /// call (cold path) and seeked to `to` on every one, so arm it well ahead: if the frame comes
    /// before the spare is ready, the source does not jump and says so in
    /// [`FileSourceControl::jump_missed`]. One at a time, a new one replaces it; `id` is new each
    /// time, 0 arms nothing. A `to` of [`STOP`] is [`FileSourceControl::arm_stop`].
    pub fn arm_jump(&self, id: u64, at: u64, to: u64) -> Result<()> {
        if to == STOP {
            self.arm_stop(id, at);
            return Ok(());
        }

        let mut lanes = self.lock();
        let spare = 1 - self.active.load(Ordering::Acquire);

        if lanes.seeks[spare].is_none() {
            let o = &self.opening;
            let track =
                StreamingTrack::open(&o.path, o.sample_rate, o.channels, o.prefetch_frames)?;
            lanes.seeks[spare] = Some(track.seek_state());
            if lanes.spare_tx.push(track).is_err() {
                lanes.seeks[spare] = None;
                return Ok(());
            }
        }
        if let Some(seek) = &lanes.seeks[spare] {
            seek.request(to);
        }

        self.publish(id, at, to);
        Ok(())
    }

    /// When the cursor reaches content frame `at`, the source fades out over a few milliseconds
    /// and stays silent until the next seek. Takes the armed jump's place and answers in
    /// [`FileSourceControl::jump_taken`] the same way; no second stream is needed for it.
    pub fn arm_stop(&self, id: u64, at: u64) {
        self.publish(id, at, STOP);
    }

    fn publish(&self, id: u64, at: u64, to: u64) {
        self.jump_id.store(0, Ordering::Relaxed);
        fence(Ordering::Release);
        self.jump_at.store(at, Ordering::Relaxed);
        self.jump_to.store(to, Ordering::Relaxed);
        self.jump_id.store(id, Ordering::Release);
    }

    pub fn disarm_jump(&self) {
        self.jump_id.store(0, Ordering::Release);
    }

    /// Id of the last jump carried out, 0 before the first.
    pub fn jump_taken(&self) -> u64 {
        self.jump_taken.load(Ordering::Acquire)
    }

    /// Id of the last jump that was due before its target was ready, so it did not happen.
    pub fn jump_missed(&self) -> u64 {
        self.jump_missed.load(Ordering::Acquire)
    }

    fn armed_jump(&self) -> Option<(u64, u64, u64)> {
        let id = self.jump_id.load(Ordering::Acquire);
        if id == 0 {
            return None;
        }
        let at = self.jump_at.load(Ordering::Relaxed);
        let to = self.jump_to.load(Ordering::Relaxed);
        fence(Ordering::Acquire);
        (self.jump_id.load(Ordering::Relaxed) == id).then_some((id, at, to))
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Lanes> {
        self.lanes.lock().unwrap_or_else(|e| e.into_inner())
    }
}

/// A [`TrackSource`] that decodes an audio file on its own prefetch thread.
///
/// The heavy lifting (decoding, resampling, prefetching ahead into a lock-free
/// ring) is done by the wrapped [`StreamingTrack`]; this type only adds the
/// audio-thread loop/EOF policy, the armed jumps, and routes them through the
/// shared [`FileSourceControl`].
pub struct FileTrackSource {
    lanes: [Option<StreamingTrack>; 2],
    active: usize,
    spare_rx: Consumer<StreamingTrack>,
    control: Arc<FileSourceControl>,
    channels: usize,
    /// Content frame of the next frame the playing lane hands out.
    cursor: u64,
    seen_epoch: u64,
    taken: u64,
    missed: u64,
    jumped: i64,
    fade: Vec<f32>,
    fade_frames: usize,
    /// Frames of a stop's fade done so far, `None` while no stop came. Past `fade_frames` the
    /// source is silent; a seek lets it play again.
    stop_ramp: Option<usize>,
}

impl FileTrackSource {
    /// Opens `path`, spawns its prefetch thread, and returns the source together
    /// with the shared control handle the caller retains after installing the
    /// source on a track.
    ///
    /// See [`open_streaming`](crate::decoder::open_streaming) for the parameter
    /// semantics.
    pub fn open(
        path: &str,
        target_sample_rate: u32,
        target_channels: u32,
        prefetch_frames: usize,
    ) -> Result<(Self, Arc<FileSourceControl>)> {
        let streaming =
            StreamingTrack::open(path, target_sample_rate, target_channels, prefetch_frames)?;
        let info = streaming.stream_info();
        let (spare_tx, spare_rx) = RingBuffer::new(1);

        let control = Arc::new(FileSourceControl {
            loop_enabled: AtomicBool::new(false),
            finished: AtomicBool::new(false),
            active: AtomicUsize::new(0),
            seek_epoch: AtomicU64::new(0),
            seek_frame: AtomicU64::new(0),
            jump_id: AtomicU64::new(0),
            jump_at: AtomicU64::new(0),
            jump_to: AtomicU64::new(0),
            jump_taken: AtomicU64::new(0),
            jump_missed: AtomicU64::new(0),
            opening: Opening {
                path: path.to_string(),
                sample_rate: target_sample_rate,
                channels: target_channels,
                prefetch_frames,
            },
            lanes: Mutex::new(Lanes {
                seeks: [Some(streaming.seek_state()), None],
                spare_tx,
            }),
        });

        let channels = info.channels.max(1) as usize;
        let fade_frames = (info.sample_rate * JUMP_FADE_MS / 1000).max(1) as usize;
        let source = Self {
            lanes: [Some(streaming), None],
            active: 0,
            spare_rx,
            control: Arc::clone(&control),
            channels,
            cursor: 0,
            seen_epoch: 0,
            taken: 0,
            missed: 0,
            jumped: 0,
            fade: vec![0.0f32; fade_frames * channels],
            fade_frames,
            stop_ramp: None,
        };

        Ok((source, control))
    }

    /// Decoded output format metadata (channels, sample rate, duration).
    pub fn stream_info(&self) -> AudioStreamInfo {
        self.playing().stream_info()
    }

    fn playing(&self) -> &StreamingTrack {
        self.lanes[self.active]
            .as_ref()
            .expect("the playing lane is opened with the source")
    }

    fn playing_mut(&mut self) -> &mut StreamingTrack {
        self.lanes[self.active]
            .as_mut()
            .expect("the playing lane is opened with the source")
    }

    /// The armed jump this block has to carry out, if its frame falls inside it.
    fn due_jump(&self, frames: usize) -> Option<(u64, u64, u64)> {
        self.control.armed_jump().filter(|&(id, at, _)| {
            id != self.taken
                && id != self.missed
                && at >= self.cursor
                && at < self.cursor + frames as u64
        })
    }

    /// The first `at - cursor` frames from the playing lane, the rest from the spare, which takes
    /// over; the old stream's next few milliseconds crossfaded (equal power) into the new.
    fn splice(&mut self, out: &mut [f32], id: u64, at: u64, to: u64) -> usize {
        let ch = self.channels;
        let k = (at - self.cursor) as usize;
        let (before, after) = out.split_at_mut(k * ch);

        let got = self.playing_mut().read(before);
        before[got..].fill(0.0);

        let fade = self.fade_frames.min(after.len() / ch);
        let mut old = std::mem::take(&mut self.fade);
        let got_old = self.playing_mut().read(&mut old[..fade * ch]);
        old[got_old..fade * ch].fill(0.0);

        self.active ^= 1;
        self.control.active.store(self.active, Ordering::Release);

        let got_new = self.playing_mut().read(after);
        after[got_new..].fill(0.0);

        let frames = after
            .chunks_exact_mut(ch)
            .zip(old.chunks_exact(ch))
            .take(fade);
        for (f, (new, was)) in frames.enumerate() {
            let t = (f as f32 + 0.5) / fade as f32 * std::f32::consts::FRAC_PI_2;
            let (fade_in, fade_out) = (t.sin(), t.cos());
            for (n, &w) in new.iter_mut().zip(was) {
                *n = *n * fade_in + w * fade_out;
            }
        }
        self.fade = old;

        self.cursor = to + (got_new / ch) as u64;
        self.jumped += to as i64 - at as i64;
        self.taken = id;
        self.control.jump_taken.store(id, Ordering::Release);
        self.control.finished.store(false, Ordering::Relaxed);
        out.len()
    }

    /// The first `k` frames as they are, then the stop's fade on from where it got to (it may
    /// have begun a block ago), silence once it is down.
    fn ramp_down(&mut self, out: &mut [f32], k: usize) -> usize {
        let ch = self.channels;
        let (before, after) = out.split_at_mut(k * ch);
        let got = self.playing_mut().read(before);
        before[got..].fill(0.0);

        let done = self.stop_ramp.unwrap_or(0);
        let left = self.fade_frames.saturating_sub(done).min(after.len() / ch);
        let got = self.playing_mut().read(&mut after[..left * ch]);
        after[got..].fill(0.0);

        for (f, frame) in after.chunks_exact_mut(ch).take(left).enumerate() {
            let t = ((done + f) as f32 + 0.5) / self.fade_frames as f32;
            let gain = (t * std::f32::consts::FRAC_PI_2).cos();
            frame.iter_mut().for_each(|s| *s *= gain);
        }

        self.stop_ramp = Some(done + left);
        self.cursor += (k + left) as u64;
        out.len()
    }
}

impl TrackSource for FileTrackSource {
    fn read(&mut self, out: &mut [f32]) -> usize {
        if self.lanes[1 - self.active].is_none() {
            if let Ok(spare) = self.spare_rx.pop() {
                self.lanes[1 - self.active] = Some(spare);
            }
        }

        let epoch = self.control.seek_epoch.load(Ordering::Acquire);
        if epoch != self.seen_epoch {
            self.seen_epoch = epoch;
            self.stop_ramp = None;
            self.cursor = self.control.seek_frame.load(Ordering::Relaxed);
            // a seek that crossed a jump's swap went to the lane that stopped playing
            if self.playing().seek_target() != self.cursor {
                self.playing().seek(self.cursor);
            }
        }

        if self.stop_ramp.is_some() {
            return self.ramp_down(out, 0);
        }

        if let Some((id, at, to)) = self.due_jump(out.len() / self.channels) {
            if to == STOP {
                self.taken = id;
                self.control.jump_taken.store(id, Ordering::Release);
                return self.ramp_down(out, (at - self.cursor) as usize);
            }
            let spare = &mut self.lanes[1 - self.active];
            let ready = spare
                .as_mut()
                .is_some_and(|s| s.seek_target() == to && s.primed());
            if ready {
                return self.splice(out, id, at, to);
            }
            self.missed = id;
            self.control.jump_missed.store(id, Ordering::Release);
        } else if self.control.armed_jump().is_some() {
            // the spare's prefetch can only refill once the stale ring its seek left is dropped
            if let Some(spare) = self.lanes[1 - self.active].as_mut() {
                spare.primed();
            }
        }

        let read = self.playing_mut().read(out);
        if read > 0 {
            // Audio is flowing (start, mid-stream, or resumed after a seek), so
            // the source is not finished.
            self.cursor += (read / self.channels) as u64;
            self.control.finished.store(false, Ordering::Relaxed);
            return read;
        }

        // No samples: either a transient prefetch underrun or true end-of-stream.
        if self.playing().is_eof() {
            if self.control.loop_enabled.load(Ordering::Relaxed) {
                // Seamless loop: rewind and keep feeding. The reposition is
                // applied by the prefetch thread; reads return silence for the
                // brief refill window.
                self.playing().seek(0);
                self.cursor = 0;
            } else {
                self.control.finished.store(true, Ordering::Relaxed);
            }
        }

        read
    }

    #[inline]
    fn is_eof(&self) -> bool {
        // Real end-of-stream only: the decoder has finished and the prefetch ring
        // is drained. False during a transient underrun or an in-flight seek, so
        // the stretch stage does not flush its FIFO tail on those.
        self.playing().is_eof()
    }

    #[inline]
    fn ready(&mut self) -> bool {
        self.playing_mut().primed()
    }

    fn take_jump(&mut self) -> i64 {
        std::mem::take(&mut self.jumped)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::decoder::test_support::TempWav;

    /// Builds a short stereo WAV (constant non-silent samples) for feed tests.
    fn stereo_wav(frames: usize) -> TempWav {
        let interleaved = vec![1_000i16; frames * 2];
        TempWav::write(2, 44_100, &interleaved)
    }

    /// Reads the whole source to end-of-stream, returning the total samples read.
    fn drain_to_eof(source: &mut FileTrackSource, control: &FileSourceControl) -> usize {
        let mut buf = vec![0.0f32; 4096];
        let mut total = 0usize;
        for _ in 0..10_000 {
            let n = source.read(&mut buf);
            total += n;
            if control.is_finished() {
                break;
            }
            if n == 0 {
                std::thread::sleep(std::time::Duration::from_millis(1));
            }
        }
        total
    }

    #[test]
    fn finishes_at_end_of_stream() {
        let wav = stereo_wav(4_000);
        let (mut source, control) =
            FileTrackSource::open(wav.path_str(), 44_100, 2, 8_192).unwrap();

        assert!(!control.is_finished());
        let total = drain_to_eof(&mut source, &control);
        assert!(control.is_finished(), "source should latch finished at EOF");
        assert!(total > 0, "expected to read some audio, got {total}");
    }

    #[test]
    fn loop_never_finishes() {
        let wav = stereo_wav(4_000);
        let (mut source, control) =
            FileTrackSource::open(wav.path_str(), 44_100, 2, 8_192).unwrap();
        control.set_loop(true);

        let mut buf = vec![0.0f32; 4096];
        // Read well past the file length; looping must keep the finished latch clear.
        for _ in 0..2_000 {
            let n = source.read(&mut buf);
            assert!(!control.is_finished(), "looping source must never finish");
            if n == 0 {
                std::thread::sleep(std::time::Duration::from_millis(1));
            }
        }
    }

    #[test]
    fn seek_clears_finished_latch() {
        let wav = stereo_wav(4_000);
        let (mut source, control) =
            FileTrackSource::open(wav.path_str(), 44_100, 2, 8_192).unwrap();

        drain_to_eof(&mut source, &control);
        assert!(control.is_finished());

        // Seek back to the start; once audio flows again the latch must clear.
        control.seek_frames(0);
        let mut buf = vec![0.0f32; 4096];
        let mut cleared = false;
        for _ in 0..2_000 {
            let n = source.read(&mut buf);
            if n > 0 {
                cleared = !control.is_finished();
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(1));
        }
        assert!(
            cleared,
            "seek should let the source play again and clear finished"
        );
    }
    /// A stereo ramp: frame `f` holds `f / 32768` on both sides.
    fn ramp_wav(frames: usize) -> TempWav {
        let samples: Vec<i16> = (0..frames as i16).flat_map(|f| [f, f]).collect();
        TempWav::write(2, 44_100, &samples)
    }

    fn ramp(frame: u64) -> f32 {
        frame as f32 / 32_768.0
    }

    /// Waits until the source, and the spare while a jump is armed, have something to give — as
    /// a real track does by arming a section ahead. The empty read hands the spare over first.
    fn settle(source: &mut FileTrackSource) {
        for _ in 0..500 {
            source.read(&mut []);
            let armed = source
                .control
                .armed_jump()
                .is_some_and(|(_, _, to)| to != STOP);
            let spare = &mut source.lanes[1 - source.active];
            let spare_ok = !armed || spare.as_mut().is_some_and(|s| s.primed());
            if source.ready() && spare_ok {
                return;
            }
            std::thread::sleep(std::time::Duration::from_millis(2));
        }
        panic!("the source never got ready");
    }

    /// Reads `blocks` blocks of 512 frames and hands back every frame's left sample.
    fn play(source: &mut FileTrackSource, blocks: usize) -> Vec<f32> {
        let mut heard = Vec::with_capacity(blocks * 512);
        let mut buf = vec![0.0f32; 512 * 2];
        while heard.len() < blocks * 512 {
            let n = source.read(&mut buf);
            heard.extend(buf[..n].iter().step_by(2));
            if n < buf.len() {
                std::thread::sleep(std::time::Duration::from_millis(1));
            }
        }
        heard
    }

    fn open_ramp() -> (FileTrackSource, Arc<FileSourceControl>, TempWav) {
        let wav = ramp_wav(30_000);
        let (mut source, control) =
            FileTrackSource::open(wav.path_str(), 44_100, 2, 8_192).unwrap();
        settle(&mut source);
        (source, control, wav)
    }

    #[test]
    fn an_armed_jump_swaps_to_the_spare_on_its_frame() {
        let (mut source, control, _wav) = open_ramp();

        control.arm_jump(5, 10_000, 2_000).unwrap();
        settle(&mut source);
        let heard = play(&mut source, 28);
        let fade = (44_100 * JUMP_FADE_MS / 1000) as usize;

        assert_eq!(heard[9_999], ramp(9_999), "up to the frame as it was");
        for f in fade..4_000 {
            assert_eq!(
                heard[10_000 + f],
                ramp(2_000 + f as u64),
                "on from the target, frame {f}"
            );
        }
        assert_eq!(control.jump_taken(), 5);
        assert_eq!(control.jump_missed(), 0);
        assert_eq!(source.take_jump(), -8_000);
    }

    /// A loop goes round twice: the second jump swaps back to the first lane, seeked again.
    #[test]
    fn the_lanes_swap_back_on_the_next_jump() {
        let (mut source, control, _wav) = open_ramp();

        control.arm_jump(1, 6_000, 2_000).unwrap();
        settle(&mut source);
        play(&mut source, 14);
        assert_eq!(control.jump_taken(), 1);
        let back = source.cursor;
        assert_eq!(back, 2_000 + 14 * 512 - 6_000);

        control.arm_jump(2, 6_000, 2_000).unwrap();
        settle(&mut source);
        let heard = play(&mut source, 8);
        let at = (6_000 - back) as usize;

        assert_eq!(control.jump_taken(), 2);
        assert_eq!(heard[at - 1], ramp(5_999));
        assert_eq!(heard[at + 500], ramp(2_500), "round again from the target");
    }

    /// A seek after a jump moves the lane that is playing, not the one that stopped.
    #[test]
    fn a_seek_after_a_jump_moves_the_playing_lane() {
        let (mut source, control, _wav) = open_ramp();
        control.arm_jump(1, 4_000, 1_000).unwrap();
        settle(&mut source);
        play(&mut source, 10);
        assert_eq!(control.jump_taken(), 1);

        control.seek_frames(20_000);
        settle(&mut source);
        let heard = play(&mut source, 1);

        let landed = heard[0] * 32_768.0;
        assert!((landed - 20_000.0).abs() < 64.0, "landed at {landed}");
    }

    /// No second stream for a stop: the fade runs on the playing one, across the block boundary.
    #[test]
    fn an_armed_stop_fades_out_and_stays_silent_until_a_seek() {
        let (mut source, control, _wav) = open_ramp();
        let at = 10 * 512 - 20;
        control.arm_stop(6, at);

        let heard = play(&mut source, 20);
        let fade = (44_100 * JUMP_FADE_MS / 1000) as usize;

        assert!(source.lanes[1].is_none(), "no spare opened");
        assert_eq!(heard[at as usize - 1], ramp(at - 1));
        for f in 0..fade {
            let t = (f as f32 + 0.5) / fade as f32 * std::f32::consts::FRAC_PI_2;
            let want = ramp(at + f as u64) * t.cos();
            assert!(
                (heard[at as usize + f] - want).abs() < 1e-6,
                "frame {f} of the fade"
            );
        }
        assert!(
            heard[at as usize + fade..].iter().all(|&s| s == 0.0),
            "silent after it"
        );
        assert_eq!(control.jump_taken(), 6);
        assert!(!control.is_finished());

        control.seek_frames(2_000);
        settle(&mut source);
        let again = play(&mut source, 1);
        assert!(
            (again[0] * 32_768.0 - 2_000.0).abs() < 64.0,
            "a seek plays again"
        );
    }

    #[test]
    fn a_jump_whose_frame_is_behind_the_cursor_never_happens() {
        let (mut source, control, _wav) = open_ramp();
        play(&mut source, 10);

        control.arm_jump(3, 1_000, 0).unwrap();
        settle(&mut source);
        let heard = play(&mut source, 2);

        assert_eq!(control.jump_taken(), 0);
        assert_eq!(heard[0], ramp(10 * 512));
    }
    /// On a stretched track (90 %), where the pinned SoundTouch stage pulls the source ahead of
    /// the output: the jump leaves no hole — the stage is not flushed, it just gets the spliced
    /// stream — and the project position moves by the jump at the tempo's rate.
    #[test]
    fn a_jump_on_a_stretched_track_leaves_no_hole() {
        use crate::multitrack::{MultiTrackMixer, TrackState};

        let (mut source, control, _wav) = open_ramp();
        control.arm_jump(4, 12_000, 2_000).unwrap();
        settle(&mut source);

        let mut mixer = MultiTrackMixer::new(44_100.0, 2);
        let (id, shared) = mixer.add_track();
        shared.set_stretch_pinned(true);
        shared.set_tempo_ratio(0.9);
        mixer.set_track_source(id, Some(Box::new(source)));
        shared.set_state(TrackState::Playing);

        let mut out = vec![0.0f32; 512 * 2];
        let mut silent_run = 0usize;
        let mut worst_run = 0usize;
        for block in 0..60 {
            mixer.mix(&mut out);
            if block < 4 {
                continue;
            }
            for frame in out.chunks_exact(2) {
                silent_run = if frame[0].abs() < 1e-6 {
                    silent_run + 1
                } else {
                    0
                };
                worst_run = worst_run.max(silent_run);
            }
            std::thread::sleep(std::time::Duration::from_millis(1));
        }

        assert_eq!(control.jump_taken(), 4);
        assert!(worst_run < 16, "a hole of {worst_run} frames");
        assert_eq!(
            shared.jump_shift_frames(),
            (-10_000.0f64 / 0.9).round() as i64
        );
    }
}
