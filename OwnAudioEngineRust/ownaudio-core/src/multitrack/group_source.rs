//! Group track source: several audio files laid out on one content timeline and summed into a
//! single track, so the whole lot shares one stretch stage, one effect chain, one gain, pan and
//! route — the way a DAW lane holds its clips.
//!
//! A [`GroupTrackSource`] keeps its own content cursor. Every clip sits at a start frame on that
//! timeline; the audio thread sums whichever clips overlap the block it is asked for and pads the
//! rest with silence, so gaps between clips cost nothing and the cursor never drifts off the
//! timeline, even when one of the clip decoders runs dry for a moment.
//!
//! What a clip plays comes from a [`GroupClipData`], loaded once and independent of any track:
//! short files are decoded into a shared buffer (sample exact, no thread), long ones are only
//! probed and later streamed through a [`StreamingTrack`] per placement. A host that takes its
//! tracks off the mixer at every stop and puts them back at every play hands the same data to
//! each new group — nothing is decoded twice.
//!
//! Clips are added and removed through a lock-free command queue and moved by writing their start
//! frame, so all three are safe while the track plays. Removed clips travel back to the control
//! thread for deallocation — nothing is freed on the real-time path.

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

use rtrb::{Consumer, Producer, RingBuffer};

use crate::decoder::backend::create_backend;
use crate::decoder::StreamingTrack;
use crate::error::{AudioError, Result};
use crate::multitrack::track::TrackSource;

/// Most clips a single group holds. The audio-side clip list is allocated at this capacity up
/// front, so adding a clip never allocates on the audio thread.
pub const MAX_GROUP_CLIPS: usize = 256;

/// Prefetch ring of a streamed clip, in frames (about two seconds at 48 kHz).
const STREAM_PREFETCH_FRAMES: usize = 96_000;

/// Frames a streamed clip may fall behind its place on the timeline before the source gives up
/// catching up by skipping and seeks it instead (about a quarter second at 48 kHz).
const MAX_STREAM_LAG_FRAMES: u64 = 12_000;

/// Scratch the streamed clips read through, in frames. Blocks longer than this are simply read
/// in several passes.
const SCRATCH_FRAMES: usize = 4_096;

/// Samples decoded per pass when a clip is loaded into memory.
const MEMORY_DECODE_CHUNK: usize = 16_384;

/// Where a loaded clip's audio lives.
enum ClipContent {
    /// Fully decoded, interleaved. Shared by every group the clip is placed on.
    Memory(Arc<[f32]>),
    /// Probed only; each placement opens its own stream.
    Stream(String),
}

/// A file loaded for use on groups: decoded into memory when short enough, otherwise probed for
/// its length and left on disk. Lives on the control side; placing it on a group is cheap.
pub struct GroupClipData {
    content: ClipContent,
    length_frames: u64,
    sample_rate: u32,
    channels: u32,
}

impl GroupClipData {
    /// Loads `path` decoded to `sample_rate` / `channels`. Files no longer than
    /// `memory_max_frames` — and files that cannot tell their own length — are decoded into
    /// memory right here; the rest are only probed.
    pub fn open(
        path: &str,
        sample_rate: u32,
        channels: u32,
        memory_max_frames: u64,
    ) -> Result<Self> {
        let mut backend = create_backend(path, sample_rate, channels)?;
        let info = backend.stream_info();

        let known_frames = backend.total_output_frames().or(info.total_frames());
        if let Some(frames) = known_frames.filter(|&f| f > memory_max_frames) {
            return Ok(Self {
                content: ClipContent::Stream(path.to_string()),
                length_frames: frames,
                sample_rate: info.sample_rate,
                channels: info.channels,
            });
        }

        let mut samples = Vec::new();
        let mut chunk = vec![0.0f32; MEMORY_DECODE_CHUNK];
        loop {
            let result = backend.read_frames(&mut chunk)?;
            samples.extend_from_slice(&chunk[..result.samples_written]);
            if result.is_eof {
                break;
            }
        }

        let length_frames = (samples.len() / info.channels.max(1) as usize) as u64;
        Ok(Self {
            content: ClipContent::Memory(samples.into()),
            length_frames,
            sample_rate: info.sample_rate,
            channels: info.channels,
        })
    }

    /// Length in frames at [`GroupClipData::sample_rate`].
    pub fn length_frames(&self) -> u64 {
        self.length_frames
    }

    /// `true` when the audio sits decoded in memory, `false` when it streams from disk.
    pub fn in_memory(&self) -> bool {
        matches!(self.content, ClipContent::Memory(_))
    }

    /// Rate the clip decodes to.
    pub fn sample_rate(&self) -> u32 {
        self.sample_rate
    }

    /// Interleaved width the clip decodes to.
    pub fn channels(&self) -> u32 {
        self.channels
    }
}

/// What the control side hands the audio thread.
enum GroupCommand {
    Add(Box<GroupClip>),
    Remove(u64),
}

/// The part of a clip the control side keeps a hold of once the clip itself belongs to the
/// audio thread: where it sits, which is all there is to change about a clip in place.
struct ClipPlacement {
    start_frame: AtomicU64,
}

/// Where a placed clip's audio comes from.
enum ClipBody {
    Memory(Arc<[f32]>),
    Streaming(StreamingClip),
}

/// Bookkeeping for a streamed clip, owned by the audio thread.
struct StreamingClip {
    track: StreamingTrack,
    /// Clip-local frame the timeline expects the decoder to deliver next.
    expected: u64,
    /// Frames the decoder owes the timeline: a dry read left silence where they belonged, so
    /// they are skipped once the decoder catches up.
    lag: u64,
}

/// One clip placed on the group's timeline.
struct GroupClip {
    id: u64,
    placement: Arc<ClipPlacement>,
    length_frames: u64,
    body: ClipBody,
}

/// Control-thread side of a group: the queues into and out of the audio thread and the
/// placement of every clip it still holds.
struct GroupController {
    commands: Producer<GroupCommand>,
    retired: Consumer<Box<GroupClip>>,
    clips: Vec<(u64, Arc<ClipPlacement>, u64)>,
    next_id: u64,
}

/// Shared between the control thread (FFI / C#) and a [`GroupTrackSource`] on the audio thread.
///
/// Seeking and the finished latch are atomics, clip edits go through a mutex that only the
/// control side ever takes.
pub struct GroupSourceControl {
    sample_rate: u32,
    channels: u32,
    seek_frame: AtomicU64,
    seek_pending: AtomicBool,
    finished: AtomicBool,
    /// Content frame the cursor stood at after the last block.
    position: AtomicU64,
    controller: Mutex<GroupController>,
}

impl GroupSourceControl {
    /// Places `data` on the timeline at `start_frame` and returns the id to move and remove it
    /// by. A memory clip only shares its buffer; a streamed one opens its file here.
    pub fn add_clip(&self, data: &GroupClipData, start_frame: u64) -> Result<u64> {
        if data.sample_rate != self.sample_rate || data.channels != self.channels {
            return Err(AudioError::UnsupportedConfig(format!(
                "clip decodes to {} Hz / {} ch, the group runs at {} Hz / {} ch",
                data.sample_rate, data.channels, self.sample_rate, self.channels
            )));
        }

        let mut ctl = self.lock();
        Self::drain_retired(&mut ctl);

        if ctl.clips.len() >= MAX_GROUP_CLIPS {
            return Err(AudioError::UnsupportedConfig(format!(
                "a group holds at most {MAX_GROUP_CLIPS} clips"
            )));
        }

        let body = match &data.content {
            ClipContent::Memory(samples) => ClipBody::Memory(Arc::clone(samples)),
            ClipContent::Stream(path) => ClipBody::Streaming(StreamingClip {
                track: StreamingTrack::open(
                    path,
                    self.sample_rate,
                    self.channels,
                    STREAM_PREFETCH_FRAMES,
                )?,
                expected: 0,
                lag: 0,
            }),
        };

        let id = ctl.next_id;
        let placement = Arc::new(ClipPlacement {
            start_frame: AtomicU64::new(start_frame),
        });
        let clip = Box::new(GroupClip {
            id,
            placement: Arc::clone(&placement),
            length_frames: data.length_frames,
            body,
        });

        if ctl.commands.push(GroupCommand::Add(clip)).is_err() {
            return Err(AudioError::UnsupportedConfig(
                "group command queue is full".to_string(),
            ));
        }

        ctl.next_id += 1;
        ctl.clips.push((id, placement, data.length_frames));
        Ok(id)
    }

    /// Takes a clip off the timeline. `false` when the group holds no such clip.
    pub fn remove_clip(&self, id: u64) -> bool {
        let mut ctl = self.lock();
        Self::drain_retired(&mut ctl);

        let Some(index) = ctl.clips.iter().position(|c| c.0 == id) else {
            return false;
        };

        if ctl.commands.push(GroupCommand::Remove(id)).is_err() {
            return false;
        }

        ctl.clips.swap_remove(index);
        true
    }

    /// Moves a clip. Lands on the next block, playing or not. `false` for an unknown id.
    pub fn set_clip_start(&self, id: u64, start_frame: u64) -> bool {
        let ctl = self.lock();
        match ctl.clips.iter().find(|c| c.0 == id) {
            Some(c) => {
                c.1.start_frame.store(start_frame, Ordering::Release);
                true
            }
            None => false,
        }
    }

    /// Frame just past the last clip end, zero for an empty group.
    pub fn end_frame(&self) -> u64 {
        let ctl = self.lock();
        ctl.clips
            .iter()
            .map(|c| c.1.start_frame.load(Ordering::Acquire) + c.2)
            .max()
            .unwrap_or(0)
    }

    /// Clips currently on the timeline.
    pub fn clip_count(&self) -> usize {
        self.lock().clips.len()
    }

    /// Requests a jump of the content cursor. Non-blocking, applied on the next read.
    pub fn seek_frames(&self, frame: u64) {
        self.seek_frame.store(frame, Ordering::Relaxed);
        self.seek_pending.store(true, Ordering::Release);
    }

    /// `true` once the cursor ran past the last clip end. Cleared by a seek.
    pub fn is_finished(&self) -> bool {
        self.finished.load(Ordering::Relaxed)
    }

    /// Content frame the cursor stood at after the last rendered block.
    pub fn position_frames(&self) -> u64 {
        self.position.load(Ordering::Relaxed)
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, GroupController> {
        self.controller.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Drops whatever the audio thread handed back, here on the control thread.
    fn drain_retired(ctl: &mut GroupController) {
        while ctl.retired.pop().is_ok() {}
    }
}

/// A [`TrackSource`] that sums its clips along a content timeline.
pub struct GroupTrackSource {
    control: Arc<GroupSourceControl>,
    commands: Consumer<GroupCommand>,
    retired: Producer<Box<GroupClip>>,
    clips: Vec<Box<GroupClip>>,
    channels: usize,
    cursor: u64,
    scratch: Vec<f32>,
}

impl GroupTrackSource {
    /// An empty group at `sample_rate` / `channels`, with the control handle to fill it through.
    /// Every clip placed on it has to decode to the same rate and width.
    pub fn new(sample_rate: u32, channels: u32) -> (Self, Arc<GroupSourceControl>) {
        let channels = channels.max(1);
        let (command_tx, command_rx) = RingBuffer::new(MAX_GROUP_CLIPS * 2);
        let (retire_tx, retire_rx) = RingBuffer::new(MAX_GROUP_CLIPS * 2);

        let control = Arc::new(GroupSourceControl {
            sample_rate,
            channels,
            seek_frame: AtomicU64::new(0),
            seek_pending: AtomicBool::new(false),
            finished: AtomicBool::new(false),
            position: AtomicU64::new(0),
            controller: Mutex::new(GroupController {
                commands: command_tx,
                retired: retire_rx,
                clips: Vec::with_capacity(MAX_GROUP_CLIPS),
                next_id: 1,
            }),
        });

        let source = Self {
            control: Arc::clone(&control),
            commands: command_rx,
            retired: retire_tx,
            clips: Vec::with_capacity(MAX_GROUP_CLIPS),
            channels: channels as usize,
            cursor: 0,
            scratch: vec![0.0f32; SCRATCH_FRAMES * channels as usize],
        };

        (source, control)
    }

    /// Takes on every queued add and remove.
    fn apply_commands(&mut self) {
        while let Ok(cmd) = self.commands.pop() {
            match cmd {
                GroupCommand::Add(clip) => {
                    if self.clips.len() < self.clips.capacity() {
                        self.clips.push(clip);
                    } else {
                        let _ = self.retired.push(clip);
                    }
                }
                GroupCommand::Remove(id) => {
                    if let Some(i) = self.clips.iter().position(|c| c.id == id) {
                        let clip = self.clips.swap_remove(i);
                        let _ = self.retired.push(clip);
                    }
                }
            }
        }
    }

    fn end_frame(&self) -> u64 {
        self.clips
            .iter()
            .map(|c| c.placement.start_frame.load(Ordering::Acquire) + c.length_frames)
            .max()
            .unwrap_or(0)
    }
}

impl StreamingClip {
    /// Keeps the decoder where the timeline wants it. `wanted` is the clip-local frame the next
    /// block starts at, or zero while the clip still lies ahead — that is the pre-roll. A jump
    /// (seek, clip moved) or a lag too big to skip away means a real seek.
    fn align(&mut self, wanted: u64, length: u64) {
        if wanted >= length {
            return;
        }
        if wanted != self.expected || self.lag > MAX_STREAM_LAG_FRAMES {
            self.track.seek(wanted);
            self.expected = wanted;
            self.lag = 0;
        }
    }

    /// Sums `frames` frames of the clip into `dst`. Whatever the decoder cannot deliver yet
    /// stays silent and is owed as lag, skipped once it catches up.
    fn mix_into(&mut self, dst: &mut [f32], frames: usize, channels: usize, scratch: &mut [f32]) {
        let chunk_frames = scratch.len() / channels;

        while self.lag > 0 {
            let want = (self.lag as usize).min(chunk_frames);
            let got = self.track.read(&mut scratch[..want * channels]) / channels;
            self.lag -= got as u64;
            if got < want {
                break;
            }
        }

        let mut done = 0usize;
        if self.lag == 0 {
            while done < frames {
                let want = (frames - done).min(chunk_frames);
                let got = self.track.read(&mut scratch[..want * channels]) / channels;
                let out = &mut dst[done * channels..(done + got) * channels];
                for (o, &s) in out.iter_mut().zip(scratch.iter()) {
                    *o += s;
                }
                done += got;
                if got < want {
                    break;
                }
            }
        }

        if done < frames && !self.track.is_eof() {
            self.lag += (frames - done) as u64;
        }
        self.expected += frames as u64;
    }
}

impl TrackSource for GroupTrackSource {
    fn read(&mut self, out: &mut [f32]) -> usize {
        self.apply_commands();

        if self.control.seek_pending.swap(false, Ordering::Acquire) {
            self.cursor = self.control.seek_frame.load(Ordering::Relaxed);
            self.control.finished.store(false, Ordering::Relaxed);
        }

        let ch = self.channels;
        let frames = out.len() / ch;
        let end = self.end_frame();

        if self.cursor >= end {
            self.control.finished.store(true, Ordering::Relaxed);
            return 0;
        }
        if frames == 0 {
            return 0;
        }

        let block_start = self.cursor;
        let block_end = block_start + frames as u64;
        out[..frames * ch].fill(0.0);

        for clip in self.clips.iter_mut() {
            let start = clip.placement.start_frame.load(Ordering::Acquire);
            let clip_end = start + clip.length_frames;

            if let ClipBody::Streaming(s) = &mut clip.body {
                s.align(block_start.saturating_sub(start), clip.length_frames);
            }

            let from = block_start.max(start);
            let to = block_end.min(clip_end);
            if from >= to {
                continue;
            }

            let local = (from - start) as usize;
            let n = (to - from) as usize;
            let offset = (from - block_start) as usize * ch;
            let dst = &mut out[offset..offset + n * ch];

            match &mut clip.body {
                ClipBody::Memory(samples) => {
                    let begin = (local * ch).min(samples.len());
                    let stop = ((local + n) * ch).min(samples.len());
                    for (o, &s) in dst.iter_mut().zip(samples[begin..stop].iter()) {
                        *o += s;
                    }
                }
                ClipBody::Streaming(s) => s.mix_into(dst, n, ch, &mut self.scratch),
            }
        }

        let produced = (end.min(block_end) - block_start) as usize;
        self.cursor += produced as u64;
        self.control.position.store(self.cursor, Ordering::Relaxed);

        produced * ch
    }

    fn is_eof(&self) -> bool {
        self.control.finished.load(Ordering::Relaxed)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::decoder::test_support::TempWav;

    const RATE: u32 = 44_100;

    /// A stereo WAV whose every sample holds `level`, so a clip is recognisable in the mix.
    fn level_wav(frames: usize, level: i16) -> TempWav {
        TempWav::write(2, RATE, &vec![level; frames * 2])
    }

    fn scaled(level: i16) -> f32 {
        level as f32 / 32_768.0
    }

    fn memory_clip(wav: &TempWav) -> GroupClipData {
        GroupClipData::open(wav.path_str(), RATE, 2, u64::MAX).unwrap()
    }

    fn streamed_clip(wav: &TempWav) -> GroupClipData {
        GroupClipData::open(wav.path_str(), RATE, 2, 1_000).unwrap()
    }

    /// One read of `frames` frames, trimmed to what the source produced.
    fn read_frames(src: &mut GroupTrackSource, frames: usize) -> Vec<f32> {
        let mut out = vec![0.0f32; frames * 2];
        let n = src.read(&mut out);
        out.truncate(n);
        out
    }

    fn frame_at(buf: &[f32], frame: usize) -> f32 {
        buf[frame * 2]
    }

    #[test]
    fn loads_short_files_into_memory_and_probes_long_ones() {
        let short = level_wav(100, 1_000);
        let long = level_wav(5_000, 1_000);

        let a = memory_clip(&short);
        assert!(a.in_memory());
        assert_eq!(a.length_frames(), 100);

        let b = streamed_clip(&long);
        assert!(!b.in_memory());
        assert_eq!(b.length_frames(), 5_000);
    }

    #[test]
    fn places_clips_on_the_timeline_with_silence_between() {
        let a = memory_clip(&level_wav(100, 1_000));
        let b = memory_clip(&level_wav(100, 2_000));
        let (mut src, ctl) = GroupTrackSource::new(RATE, 2);

        ctl.add_clip(&a, 0).unwrap();
        ctl.add_clip(&b, 300).unwrap();
        assert_eq!(ctl.end_frame(), 400);

        let out = read_frames(&mut src, 512);
        assert_eq!(out.len(), 400 * 2, "stops at the last clip end");
        assert!((frame_at(&out, 50) - scaled(1_000)).abs() < 1e-4);
        assert_eq!(frame_at(&out, 200), 0.0, "the gap is silent");
        assert!((frame_at(&out, 350) - scaled(2_000)).abs() < 1e-4);

        assert_eq!(src.read(&mut [0.0f32; 64]), 0);
        assert!(ctl.is_finished());
        assert!(src.is_eof());
    }

    #[test]
    fn one_load_serves_many_groups() {
        let a = memory_clip(&level_wav(100, 1_000));
        let (mut first, first_ctl) = GroupTrackSource::new(RATE, 2);
        let (mut second, second_ctl) = GroupTrackSource::new(RATE, 2);

        first_ctl.add_clip(&a, 0).unwrap();
        second_ctl.add_clip(&a, 50).unwrap();

        assert!((frame_at(&read_frames(&mut first, 10), 5) - scaled(1_000)).abs() < 1e-4);
        assert!((frame_at(&read_frames(&mut second, 60), 55) - scaled(1_000)).abs() < 1e-4);
    }

    #[test]
    fn overlapping_clips_sum() {
        let a = memory_clip(&level_wav(200, 1_000));
        let b = memory_clip(&level_wav(200, 2_000));
        let (mut src, ctl) = GroupTrackSource::new(RATE, 2);
        ctl.add_clip(&a, 0).unwrap();
        ctl.add_clip(&b, 100).unwrap();

        let out = read_frames(&mut src, 300);
        assert!((frame_at(&out, 150) - scaled(3_000)).abs() < 1e-4);
    }

    #[test]
    fn moving_a_clip_lands_on_the_next_block() {
        let a = memory_clip(&level_wav(100, 1_000));
        let (mut src, ctl) = GroupTrackSource::new(RATE, 2);
        let id = ctl.add_clip(&a, 0).unwrap();

        assert!(ctl.set_clip_start(id, 500));
        assert!(!ctl.set_clip_start(id + 99, 0));
        assert_eq!(ctl.end_frame(), 600);

        let out = read_frames(&mut src, 600);
        assert_eq!(frame_at(&out, 50), 0.0);
        assert!((frame_at(&out, 550) - scaled(1_000)).abs() < 1e-4);
    }

    #[test]
    fn seek_jumps_the_cursor_and_clears_finished() {
        let a = memory_clip(&level_wav(100, 1_000));
        let (mut src, ctl) = GroupTrackSource::new(RATE, 2);
        ctl.add_clip(&a, 0).unwrap();

        read_frames(&mut src, 200);
        src.read(&mut [0.0f32; 8]);
        assert!(ctl.is_finished());

        ctl.seek_frames(90);
        let out = read_frames(&mut src, 20);
        assert_eq!(out.len(), 10 * 2, "ten frames left from frame 90");
        assert!(!ctl.is_finished());
        assert_eq!(ctl.position_frames(), 100);
    }

    #[test]
    fn removed_clips_go_silent() {
        let a = memory_clip(&level_wav(100, 1_000));
        let b = memory_clip(&level_wav(100, 2_000));
        let (mut src, ctl) = GroupTrackSource::new(RATE, 2);
        let ia = ctl.add_clip(&a, 0).unwrap();
        ctl.add_clip(&b, 0).unwrap();

        assert!(ctl.remove_clip(ia));
        assert!(!ctl.remove_clip(ia), "gone already");
        assert_eq!(ctl.clip_count(), 1);

        let out = read_frames(&mut src, 50);
        assert!((frame_at(&out, 10) - scaled(2_000)).abs() < 1e-4);
    }

    #[test]
    fn empty_group_is_finished_straight_away() {
        let (mut src, ctl) = GroupTrackSource::new(RATE, 2);
        assert_eq!(src.read(&mut [0.0f32; 16]), 0);
        assert!(ctl.is_finished());
    }

    #[test]
    fn a_clip_of_another_width_is_refused() {
        let mono = TempWav::write(1, RATE, &vec![1_000i16; 100]);
        let data = GroupClipData::open(mono.path_str(), RATE, 1, u64::MAX).unwrap();
        let (_src, ctl) = GroupTrackSource::new(RATE, 2);
        assert!(ctl.add_clip(&data, 0).is_err());
    }

    #[test]
    fn long_clips_stream_and_stay_on_the_timeline() {
        let wav = level_wav(20_000, 1_000);
        let a = streamed_clip(&wav);
        let (mut src, ctl) = GroupTrackSource::new(RATE, 2);
        ctl.add_clip(&a, 5_000).unwrap();

        //The gap in front is the pre-roll: by the time the clip enters it is buffered
        let gap = read_frames(&mut src, 4_096);
        assert!(gap.iter().all(|&s| s == 0.0));
        std::thread::sleep(std::time::Duration::from_millis(100));

        let mut heard = Vec::new();
        while heard.len() < 4_000 * 2 {
            heard.extend(read_frames(&mut src, 512));
        }
        let entry = 5_000 - 4_096;
        assert_eq!(frame_at(&heard, entry - 1), 0.0);
        assert!((frame_at(&heard, entry + 10) - scaled(1_000)).abs() < 1e-4);
        assert_eq!(ctl.position_frames(), (4_096 + heard.len() / 2) as u64);
    }

    #[test]
    fn a_dry_stream_keeps_the_cursor_moving() {
        let wav = level_wav(20_000, 1_000);
        let a = streamed_clip(&wav);
        let (mut src, ctl) = GroupTrackSource::new(RATE, 2);
        ctl.add_clip(&a, 0).unwrap();

        //Straight after a seek the decoder has nothing yet, the timeline must not wait for it
        ctl.seek_frames(10_000);
        let out = read_frames(&mut src, 256);
        assert_eq!(out.len(), 256 * 2);
        assert_eq!(ctl.position_frames(), 10_256);
    }
}
