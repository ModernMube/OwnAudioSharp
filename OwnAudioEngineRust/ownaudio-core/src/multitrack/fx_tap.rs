//! Pre/post effect-chain tap: mirrors a block to the control thread on both
//! sides of the chain, so an analyser can compare what went in with what came
//! out.

use crate::ringbuffer::{ring_buffer_frames, RingBufferReader, RingBufferWriter};

/// Audio-thread half of the tap. Installed through [`MixerCommand::SetFxTap`],
/// retired on [`Retired::FxTap`] so the rings never get freed on the real-time
/// path. Overflow is dropped rather than blocking.
///
/// [`MixerCommand::SetFxTap`]: super::MixerCommand::SetFxTap
/// [`Retired::FxTap`]: super::Retired::FxTap
pub struct FxTap {
    pre: RingBufferWriter,
    post: RingBufferWriter,
    /// Set when both rings had room for this block. A one-sided overflow would
    /// slide the streams a block apart and every later comparison would be
    /// silently wrong, so a pair goes in whole or not at all.
    armed: bool,
}

impl FxTap {
    /// Mirrors the block going into the chain, if the post side can take its twin too.
    #[inline]
    pub(crate) fn write_pre(&mut self, block: &[f32]) {
        self.armed = self.pre.free() >= block.len() && self.post.free() >= block.len();
        if self.armed {
            self.pre.write(block);
        }
    }

    /// The other half. No-op unless the matching pre block went in.
    #[inline]
    pub(crate) fn write_post(&mut self, block: &[f32]) {
        if self.armed {
            self.post.write(block);
            self.armed = false;
        }
    }
}

/// Control-thread half. Both sides always come back the same length, so `pre[i]`
/// and `post[i]` are the same instant of audio.
pub struct FxTapReader {
    pre: RingBufferReader,
    post: RingBufferReader,
}

impl FxTapReader {
    /// Drains one chunk into both destinations, returning how many samples landed
    /// in each. Short reads are normal — a block whose post half is still inside
    /// the chain waits for its twin.
    pub fn read(&mut self, pre_out: &mut [f32], post_out: &mut [f32]) -> usize {
        let wanted = pre_out
            .len()
            .min(post_out.len())
            .min(self.pre.available())
            .min(self.post.available());
        if wanted == 0 {
            return 0;
        }

        let read = self.pre.read(&mut pre_out[..wanted]);
        self.post.read(&mut post_out[..read])
    }

    /// Samples currently readable as complete pre/post pairs.
    pub fn available(&self) -> usize {
        self.pre.available().min(self.post.available())
    }
}

/// Builds a tap and its reader, `capacity_samples` per side, refusing to split a
/// frame so an overflow can never rotate the channels.
pub(crate) fn fx_tap(capacity_samples: usize, channels: u16) -> (FxTap, FxTapReader) {
    let frame = channels.max(1) as usize;
    let capacity = capacity_samples.max(frame);
    let (pre_writer, pre_reader) = ring_buffer_frames(capacity, frame);
    let (post_writer, post_reader) = ring_buffer_frames(capacity, frame);

    (
        FxTap {
            pre: pre_writer,
            post: post_writer,
            armed: false,
        },
        FxTapReader {
            pre: pre_reader,
            post: post_reader,
        },
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pre_and_post_come_back_paired() {
        let (mut tap, mut reader) = fx_tap(64, 2);

        tap.write_pre(&[1.0, 1.0, 2.0, 2.0]);
        tap.write_post(&[3.0, 3.0, 4.0, 4.0]);

        let mut pre = [0.0f32; 4];
        let mut post = [0.0f32; 4];
        assert_eq!(reader.read(&mut pre, &mut post), 4);
        assert_eq!(pre, [1.0, 1.0, 2.0, 2.0]);
        assert_eq!(post, [3.0, 3.0, 4.0, 4.0]);
    }

    #[test]
    fn a_half_written_block_is_held_back() {
        let (mut tap, mut reader) = fx_tap(64, 2);

        tap.write_pre(&[1.0, 1.0]);

        let mut pre = [0.0f32; 2];
        let mut post = [0.0f32; 2];
        assert_eq!(reader.read(&mut pre, &mut post), 0);

        tap.write_post(&[2.0, 2.0]);
        assert_eq!(reader.read(&mut pre, &mut post), 2);
    }

    #[test]
    fn overflow_drops_whole_pairs_not_halves() {
        let (mut tap, mut reader) = fx_tap(4, 2);

        for v in [1.0f32, 2.0, 3.0] {
            tap.write_pre(&[v, v]);
            tap.write_post(&[-v, -v]);
        }

        assert_eq!(reader.available(), 4);

        let mut pre = [0.0f32; 4];
        let mut post = [0.0f32; 4];
        assert_eq!(reader.read(&mut pre, &mut post), 4);
        assert_eq!(pre, [1.0, 1.0, 2.0, 2.0]);
        assert_eq!(post, [-1.0, -1.0, -2.0, -2.0]);
    }

    #[test]
    fn post_without_pre_writes_nothing() {
        let (mut tap, reader) = fx_tap(64, 2);

        tap.write_post(&[9.0, 9.0]);
        assert_eq!(reader.available(), 0);
    }
}
