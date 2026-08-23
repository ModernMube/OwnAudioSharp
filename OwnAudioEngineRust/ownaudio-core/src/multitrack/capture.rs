//! One capture stream, many input tracks.
//!
//! The device is opened once, at its own physical width, and every input track hangs a
//! *tap* off that single stream: a channel map (`map[track_channel] = capture_channel`)
//! plus the write side of its own ring. The capture callback de-interleaves each tap out
//! of the device buffer and pushes it into that ring, whose reader is installed as the
//! track's source — the same lock-free bridge a per-track input stream used, minus the
//! extra streams.
//!
//! That matters most on ASIO, where every registered callback walks the driver's channel
//! buffers and a driver is a single-client affair: N input tracks used to mean N streams
//! on one device, which is somewhere between wasteful and refused. Here it is always one.
//!
//! Taps are attached and detached through a [`CaptureController`] over an `rtrb` queue the
//! capture callback drains at the top of each block, so the callback stays the sole owner
//! of its tap list and nothing is ever allocated or freed on the capture thread — a
//! detached tap goes back to the control thread on the retirement queue, exactly like the
//! mixer's retired tracks.

use rtrb::{Consumer, Producer, RingBuffer};

use crate::ringbuffer::RingBufferWriter;

use super::track::MAX_ROUTE_CHANNELS;

/// How many input tracks may feed off one capture stream at the same time. Well past what
/// a live rig needs, and the list is pre-allocated to it so attaching never reallocates on
/// the capture thread.
pub const MAX_CAPTURE_TAPS: usize = 32;

/// One input track's share of the capture stream: which device channels it takes, and where
/// the samples go.
pub struct CaptureTap {
    /// Track this tap feeds, for detaching later.
    pub track_id: u64,
    /// `map[track_channel] = capture_channel`. Out-of-range entries capture silence.
    map: [usize; MAX_ROUTE_CHANNELS],
    /// Track-side channel count — how much of `map` is live.
    channels: usize,
    /// Write side of the track's ring; its reader is the track's source.
    writer: RingBufferWriter,
}

impl CaptureTap {
    /// Builds a tap taking `map` (track channel → capture channel) into `writer`.
    /// Entries past [`MAX_ROUTE_CHANNELS`] are dropped.
    pub fn new(track_id: u64, map: &[u32], writer: RingBufferWriter) -> Self {
        let channels = map.len().min(MAX_ROUTE_CHANNELS);
        let mut slots = [0usize; MAX_ROUTE_CHANNELS];
        for (slot, &src) in slots.iter_mut().zip(map).take(channels) {
            *slot = src as usize;
        }
        Self {
            track_id,
            map: slots,
            channels,
            writer,
        }
    }

    /// Track-side width this tap produces.
    #[inline]
    pub fn channels(&self) -> usize {
        self.channels
    }
}

/// Attach / detach requests handed to the capture callback.
pub enum CaptureCommand {
    /// Start feeding this tap.
    Attach(CaptureTap),
    /// Stop feeding the tap belonging to this track; it is retired for the control thread.
    Detach(u64),
}

/// Control-thread end of the capture bridge.
pub struct CaptureController {
    commands: Producer<CaptureCommand>,
    retired: Consumer<CaptureTap>,
}

/// The queue is full and the capture callback has not drained it yet.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CaptureQueueFull;

impl std::fmt::Display for CaptureQueueFull {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("capture command queue is full")
    }
}

impl std::error::Error for CaptureQueueFull {}

impl CaptureController {
    /// Hands a tap to the capture callback. It starts feeding on the next captured block.
    pub fn attach(&mut self, tap: CaptureTap) -> Result<(), CaptureQueueFull> {
        self.collect_retired();
        self.commands
            .push(CaptureCommand::Attach(tap))
            .map_err(|_| CaptureQueueFull)
    }

    /// Stops feeding `track_id`. Its ring writer comes back on the retirement queue and is
    /// dropped here, never on the capture thread.
    pub fn detach(&mut self, track_id: u64) -> Result<(), CaptureQueueFull> {
        self.collect_retired();
        self.commands
            .push(CaptureCommand::Detach(track_id))
            .map_err(|_| CaptureQueueFull)
    }

    /// Drops whatever the capture callback has handed back. Called for you on every
    /// attach/detach, so the queue never fills up unattended.
    pub fn collect_retired(&mut self) {
        while self.retired.pop().is_ok() {}
    }
}

impl Drop for CaptureController {
    fn drop(&mut self) {
        self.collect_retired();
    }
}

/// Capture-thread end of the bridge: owns the taps and fans one device buffer out to them.
pub struct CaptureHub {
    taps: Vec<CaptureTap>,
    commands: Consumer<CaptureCommand>,
    retire: Producer<CaptureTap>,
    /// De-interleave scratch, sized on the fly to the largest block × tap width seen.
    scratch: Vec<f32>,
    /// Physical width of the capture stream.
    channels: usize,
}

impl CaptureHub {
    /// Fans one captured block out to every attached tap, after applying any queued
    /// attach/detach. `data` is interleaved at the capture stream's own width.
    pub fn on_capture(&mut self, data: &[f32]) {
        while let Ok(cmd) = self.commands.pop() {
            self.apply(cmd);
        }

        let frames = data.len() / self.channels.max(1);
        if frames == 0 {
            return;
        }

        let widest = self
            .taps
            .iter()
            .map(CaptureTap::channels)
            .max()
            .unwrap_or(0);
        if widest == 0 {
            return;
        }
        // One-time growth, the way the render scratch grows: the device settles on a block
        // size within the first few callbacks and never touches this again.
        if self.scratch.len() < frames * widest {
            self.scratch.resize(frames * widest, 0.0);
        }

        let capture_ch = self.channels.max(1);
        for tap in &mut self.taps {
            let width = tap.channels;
            let out = &mut self.scratch[..frames * width];
            for (f, frame) in out.chunks_mut(width).enumerate() {
                for (slot, &src) in frame.iter_mut().zip(&tap.map[..width]) {
                    *slot = data.get(f * capture_ch + src).copied().unwrap_or(0.0);
                }
            }
            tap.writer.write(out);
        }
    }

    /// Physical channel count of the capture stream behind this hub.
    #[inline]
    pub fn channels(&self) -> usize {
        self.channels
    }

    fn apply(&mut self, cmd: CaptureCommand) {
        match cmd {
            CaptureCommand::Attach(tap) => {
                // Replacing an existing tap for the same track keeps re-routing cheap: the
                // control side just attaches again with a new map.
                if let Some(pos) = self.taps.iter().position(|t| t.track_id == tap.track_id) {
                    let _ = self.retire.push(self.taps.swap_remove(pos));
                }
                if self.taps.len() < self.taps.capacity() {
                    self.taps.push(tap);
                } else {
                    let _ = self.retire.push(tap);
                }
            }
            CaptureCommand::Detach(track_id) => {
                if let Some(pos) = self.taps.iter().position(|t| t.track_id == track_id) {
                    let _ = self.retire.push(self.taps.swap_remove(pos));
                }
            }
        }
    }
}

/// Wires a capture bridge for a stream of `channels` physical inputs.
///
/// `capacity` sizes the attach/detach queue; a handful is plenty since taps change only
/// when a track is added or re-routed.
pub fn capture_channel(capacity: usize, channels: u16) -> (CaptureController, CaptureHub) {
    let capacity = capacity.max(1);
    let (cmd_tx, cmd_rx) = RingBuffer::<CaptureCommand>::new(capacity);
    let (retire_tx, retire_rx) = RingBuffer::<CaptureTap>::new(capacity + MAX_CAPTURE_TAPS);

    let controller = CaptureController {
        commands: cmd_tx,
        retired: retire_rx,
    };
    let hub = CaptureHub {
        taps: Vec::with_capacity(MAX_CAPTURE_TAPS),
        commands: cmd_rx,
        retire: retire_tx,
        scratch: Vec::new(),
        channels: channels.max(1) as usize,
    };
    (controller, hub)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ringbuffer::ring_buffer_frames;

    /// Interleaved 4-channel block where every sample encodes its channel: `ch + frame/10`.
    fn quad_block(frames: usize) -> Vec<f32> {
        (0..frames * 4)
            .map(|i| (i % 4) as f32 + (i / 4) as f32 / 10.0)
            .collect()
    }

    #[test]
    fn two_taps_take_different_capture_channels() {
        let (mut ctl, mut hub) = capture_channel(8, 4);

        let (w_a, mut r_a) = ring_buffer_frames(256, 1);
        let (w_b, mut r_b) = ring_buffer_frames(256, 2);
        ctl.attach(CaptureTap::new(1, &[2], w_a)).unwrap();
        ctl.attach(CaptureTap::new(2, &[0, 3], w_b)).unwrap();

        hub.on_capture(&quad_block(4));

        let mut a = [0.0f32; 4];
        assert_eq!(r_a.read(&mut a), 4);
        // Track 1 is mono off capture channel 2.
        assert_eq!(a, [2.0, 2.1, 2.2, 2.3]);

        let mut b = [0.0f32; 8];
        assert_eq!(r_b.read(&mut b), 8);
        // Track 2 is stereo off capture channels 0 and 3.
        assert_eq!(b, [0.0, 3.0, 0.1, 3.1, 0.2, 3.2, 0.3, 3.3]);
    }

    #[test]
    fn detached_tap_stops_receiving_and_comes_back() {
        let (mut ctl, mut hub) = capture_channel(8, 4);
        let (w, mut r) = ring_buffer_frames(256, 1);
        ctl.attach(CaptureTap::new(7, &[1], w)).unwrap();
        hub.on_capture(&quad_block(2));

        let mut buf = [0.0f32; 2];
        assert_eq!(r.read(&mut buf), 2);

        ctl.detach(7).unwrap();
        hub.on_capture(&quad_block(2));
        assert_eq!(r.read(&mut buf), 0, "a detached tap must go quiet");
    }

    #[test]
    fn re_attaching_a_track_replaces_its_map() {
        let (mut ctl, mut hub) = capture_channel(8, 4);
        let (w1, _r1) = ring_buffer_frames(256, 1);
        let (w2, mut r2) = ring_buffer_frames(256, 1);
        ctl.attach(CaptureTap::new(3, &[0], w1)).unwrap();
        ctl.attach(CaptureTap::new(3, &[2], w2)).unwrap();

        hub.on_capture(&quad_block(2));

        let mut buf = [0.0f32; 2];
        assert_eq!(r2.read(&mut buf), 2);
        assert_eq!(buf, [2.0, 2.1], "the newest attach wins");
    }

    #[test]
    fn out_of_range_capture_channel_is_silence() {
        let (mut ctl, mut hub) = capture_channel(8, 2);
        let (w, mut r) = ring_buffer_frames(256, 2);
        ctl.attach(CaptureTap::new(1, &[0, 9], w)).unwrap();

        let stereo: Vec<f32> = vec![0.5, -0.5, 0.5, -0.5];
        hub.on_capture(&stereo);

        let mut buf = [9.0f32; 4];
        assert_eq!(r.read(&mut buf), 4);
        assert_eq!(buf, [0.5, 0.0, 0.5, 0.0]);
    }
}
