//! Lock-free command channel between the control thread (FFI / C#) and the
//! audio thread that owns the [`MultiTrackMixer`].
//!
//! # Why
//!
//! The mixer's structure — its set of tracks, each track's audio source, and
//! each track's effect chain — must only ever be mutated by the thread that
//! runs [`MultiTrackMixer::mix`].  If the control thread mutated the track
//! vector or an effect chain while the audio thread iterated it, a reallocation
//! would be an immediate data race / use-after-free.
//!
//! # How
//!
//! All structural changes are expressed as [`MixerCommand`]s and pushed onto a
//! single-producer/single-consumer (`rtrb`) ring buffer by the
//! [`MixerController`].  The audio thread drains the queue **allocation-free**
//! at the very start of every render block (see
//! [`MultiTrackMixer::mix`]) and applies the changes itself, so it remains the
//! sole owner of its data.
//!
//! Removing a track, source or effect must not free heap memory on the audio
//! thread (a deallocation can block).  Instead the removed resource is moved —
//! by value, into a pre-allocated ring slot, never boxed on the audio thread —
//! onto the [`Retired`] return queue, and the control thread drops it later via
//! [`MixerController::collect_retired`].
//!
//! Track and effect vectors are pre-allocated (see [`super::MAX_TRACKS`] and
//! [`super::track::MAX_EFFECTS_PER_TRACK`]); a command that would exceed the
//! reserved capacity is refused on the audio thread by retiring the payload
//! rather than reallocating.

use rtrb::{Consumer, Producer, RingBuffer};
use std::sync::Arc;

use crate::effects::{Effect, EffectEntry};

use super::track::{Track, TrackShared, TrackSource};

/// A structural change to be applied by the audio thread at the top of the next
/// render block.  Payloads that own heap memory are carried by value so moving
/// them through the ring buffer never allocates on the audio thread.
pub enum MixerCommand {
    /// Insert a fully-constructed track (built on the control thread).
    AddTrack(Track),
    /// Remove the track with this id; the removed track is retired.
    RemoveTrack(u64),
    /// Replace a track's audio source; any previous source is retired.
    SetTrackSource {
        /// Target track id.
        track_id: u64,
        /// New source, or `None` to silence the track.
        source: Option<Box<dyn TrackSource>>,
    },
    /// Append an effect (with its stable id) to a track's chain.
    AddEffect {
        /// Target track id.
        track_id: u64,
        /// Stable effect id assigned by the controller.
        effect_id: u64,
        /// The effect itself, constructed on the control thread.
        effect: Box<dyn Effect>,
    },
    /// Remove an effect by id; the removed effect is retired.
    RemoveEffect {
        /// Target track id.
        track_id: u64,
        /// Effect id to remove.
        effect_id: u64,
    },
    /// Set a numeric parameter on an effect.
    SetEffectParam {
        /// Target track id.
        track_id: u64,
        /// Target effect id.
        effect_id: u64,
        /// Effect-specific parameter id.
        param_id: u32,
        /// New value (the effect clamps it).
        value: f32,
    },
    /// Enable or bypass an effect.
    SetEffectEnabled {
        /// Target track id.
        track_id: u64,
        /// Target effect id.
        effect_id: u64,
        /// `true` to enable, `false` to bypass.
        enabled: bool,
    },
}

/// A resource removed by the audio thread and handed back to the control thread
/// for deallocation, so the heap free never happens on the real-time path.
pub enum Retired {
    /// A removed (or capacity-refused) track.
    Track(Track),
    /// A removed (or capacity-refused) effect.
    Effect(EffectEntry),
    /// A replaced or orphaned audio source.
    Source(Box<dyn TrackSource>),
}

/// Error returned when a command cannot be enqueued because the queue is full.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CommandError {
    /// The command ring buffer is full; the caller should retry later.
    QueueFull,
}

/// The audio-thread end of the command channel: the command queue to drain and
/// the retirement queue to push removed resources onto.
///
/// Held by the [`MultiTrackMixer`] (see
/// [`MultiTrackMixer::attach_command_receiver`]).  Both ends are touched only
/// from the audio thread, so neither blocks nor allocates.
pub struct CommandReceiver {
    pub(crate) commands: Consumer<MixerCommand>,
    pub(crate) retire: Producer<Retired>,
}

/// The control-thread end of the command channel.
///
/// Builds tracks and effects (allocating on the control thread), assigns their
/// stable ids, enqueues the corresponding [`MixerCommand`]s, and keeps a
/// registry of each live track's [`TrackShared`] so parameter handles can be
/// returned synchronously without ever touching the audio thread's data.
pub struct MixerController {
    commands: Producer<MixerCommand>,
    retired: Consumer<Retired>,
    next_track_id: u64,
    next_effect_id: u64,
    sample_rate: f32,
    max_buffer_size: usize,
    /// Live tracks' shared parameter blocks, kept so the control thread can
    /// answer parameter queries without reaching into the mixer.
    track_registry: Vec<(u64, Arc<TrackShared>)>,
}

/// Creates a paired [`MixerController`] / [`CommandReceiver`].
///
/// `capacity` is the maximum number of in-flight commands (and the size of the
/// retirement queue, so retirement can never overflow while the control thread
/// keeps up).  `sample_rate` and `max_buffer_size` are used to build new tracks
/// on the control thread.
pub fn command_channel(
    capacity: usize,
    sample_rate: f32,
    max_buffer_size: usize,
) -> (MixerController, CommandReceiver) {
    let capacity = capacity.max(1);
    let (cmd_tx, cmd_rx) = RingBuffer::<MixerCommand>::new(capacity);
    let (retire_tx, retire_rx) = RingBuffer::<Retired>::new(capacity);

    let controller = MixerController {
        commands: cmd_tx,
        retired: retire_rx,
        next_track_id: 0,
        next_effect_id: 0,
        sample_rate,
        max_buffer_size: max_buffer_size.max(1),
        track_registry: Vec::with_capacity(super::MAX_TRACKS),
    };
    let receiver = CommandReceiver {
        commands: cmd_rx,
        retire: retire_tx,
    };
    (controller, receiver)
}

impl MixerController {
    /// Pushes a command, first draining the retirement queue so removed
    /// resources are freed promptly on this (control) thread.
    ///
    /// Keeping the retirement queue drained on every enqueue means the audio
    /// thread never finds it full and is never forced to drop a removed
    /// resource itself (a heap free on the real-time path).  Callers therefore
    /// do not need to remember to call [`MixerController::collect_retired`].
    fn enqueue(&mut self, command: MixerCommand) -> Result<(), CommandError> {
        self.collect_retired();
        self.commands
            .push(command)
            .map_err(|_| CommandError::QueueFull)
    }

    /// Builds a new track on the control thread, enqueues its insertion, and
    /// returns its stable id and a clone of its shared parameter block.
    ///
    /// The shared block is valid immediately (parameters can be set before the
    /// audio thread has even drained the insert command).
    pub fn add_track(&mut self) -> Result<(u64, Arc<TrackShared>), CommandError> {
        let id = self.next_track_id;
        let track = Track::new(id, self.sample_rate, self.max_buffer_size);
        let shared = track.shared.clone();

        self.enqueue(MixerCommand::AddTrack(track))?;

        self.next_track_id += 1;
        self.track_registry.push((id, shared.clone()));
        Ok((id, shared))
    }

    /// Enqueues removal of the track with the given id and forgets its shared
    /// block.  Returns `false` if no such track is registered.
    pub fn remove_track(&mut self, id: u64) -> Result<bool, CommandError> {
        if !self.track_registry.iter().any(|(tid, _)| *tid == id) {
            return Ok(false);
        }
        self.enqueue(MixerCommand::RemoveTrack(id))?;
        self.track_registry.retain(|(tid, _)| *tid != id);
        Ok(true)
    }

    /// Enqueues a replacement of the track's audio source.
    pub fn set_track_source(
        &mut self,
        track_id: u64,
        source: Option<Box<dyn TrackSource>>,
    ) -> Result<(), CommandError> {
        self.enqueue(MixerCommand::SetTrackSource { track_id, source })
    }

    /// Builds an effect insertion command, returning the new effect's stable id.
    pub fn add_effect(
        &mut self,
        track_id: u64,
        effect: Box<dyn Effect>,
    ) -> Result<u64, CommandError> {
        let effect_id = self.next_effect_id;
        self.enqueue(MixerCommand::AddEffect {
            track_id,
            effect_id,
            effect,
        })?;
        self.next_effect_id += 1;
        Ok(effect_id)
    }

    /// Enqueues removal of an effect by id.
    pub fn remove_effect(&mut self, track_id: u64, effect_id: u64) -> Result<(), CommandError> {
        self.enqueue(MixerCommand::RemoveEffect {
            track_id,
            effect_id,
        })
    }

    /// Enqueues a parameter change on an effect.
    pub fn set_effect_param(
        &mut self,
        track_id: u64,
        effect_id: u64,
        param_id: u32,
        value: f32,
    ) -> Result<(), CommandError> {
        self.enqueue(MixerCommand::SetEffectParam {
            track_id,
            effect_id,
            param_id,
            value,
        })
    }

    /// Enqueues an enable/bypass change on an effect.
    pub fn set_effect_enabled(
        &mut self,
        track_id: u64,
        effect_id: u64,
        enabled: bool,
    ) -> Result<(), CommandError> {
        self.enqueue(MixerCommand::SetEffectEnabled {
            track_id,
            effect_id,
            enabled,
        })
    }

    /// Returns a clone of the shared parameter block for a registered track.
    pub fn track_shared(&self, id: u64) -> Option<Arc<TrackShared>> {
        self.track_registry
            .iter()
            .find(|(tid, _)| *tid == id)
            .map(|(_, shared)| shared.clone())
    }

    /// Number of tracks currently registered on the control side.
    pub fn track_count(&self) -> usize {
        self.track_registry.len()
    }

    /// Drains and drops every resource the audio thread has retired, freeing
    /// their heap memory on the control thread.  Returns how many were freed.
    ///
    /// Call periodically (e.g. after sending a batch of commands) so the
    /// retirement queue never fills.
    pub fn collect_retired(&mut self) -> usize {
        let mut count = 0;
        while self.retired.pop().is_ok() {
            count += 1;
        }
        count
    }
}

impl Drop for MixerController {
    fn drop(&mut self) {
        // Free anything still sitting in the retirement queue on this (control)
        // thread rather than leaking it.
        self.collect_retired();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::effects::{EffectType, PARAM_ENABLED};
    use crate::multitrack::{MultiTrackMixer, TrackState};

    /// In-memory source yielding a fixed buffer once, then silence.
    struct VecSource {
        data: Vec<f32>,
        pos: usize,
    }
    impl VecSource {
        fn new(data: Vec<f32>) -> Self {
            Self { data, pos: 0 }
        }
    }
    impl TrackSource for VecSource {
        fn read(&mut self, out: &mut [f32]) -> usize {
            let n = out.len().min(self.data.len() - self.pos);
            out[..n].copy_from_slice(&self.data[self.pos..self.pos + n]);
            self.pos += n;
            n
        }
    }

    /// Test effect that scales the buffer by a settable gain (param id 2).
    struct ScalarGain {
        enabled: bool,
        gain: f32,
    }
    const PARAM_GAIN: u32 = 2;
    impl Effect for ScalarGain {
        fn effect_type(&self) -> EffectType {
            EffectType::Distortion
        }
        fn process(&mut self, buffer: &mut [f32], _channels: u16) {
            for s in buffer.iter_mut() {
                *s *= self.gain;
            }
        }
        fn set_param(&mut self, param_id: u32, value: f32) -> bool {
            match param_id {
                PARAM_ENABLED => {
                    self.enabled = value >= 0.5;
                    true
                }
                PARAM_GAIN => {
                    self.gain = value;
                    true
                }
                _ => false,
            }
        }
        fn get_param(&self, param_id: u32) -> Option<f32> {
            match param_id {
                PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
                PARAM_GAIN => Some(self.gain),
                _ => None,
            }
        }
        fn reset(&mut self) {}
        fn is_enabled(&self) -> bool {
            self.enabled
        }
        fn set_enabled(&mut self, enabled: bool) {
            self.enabled = enabled;
        }
    }

    /// Builds a mono mixer wired to a fresh command channel.
    fn wired() -> (MixerController, MultiTrackMixer) {
        let mixer = MultiTrackMixer::new(48_000.0, 1);
        let (ctl, rx) = command_channel(64, mixer.sample_rate(), mixer.max_buffer_size());
        let mut mixer = mixer;
        mixer.attach_command_receiver(rx);
        (ctl, mixer)
    }

    #[test]
    fn commands_are_deferred_until_the_next_mix() {
        let (mut ctl, mut mixer) = wired();
        ctl.add_track().unwrap();
        // Not applied yet: the audio thread has not rendered a block.
        assert_eq!(mixer.track_count(), 0);

        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);
        assert_eq!(mixer.track_count(), 1);
    }

    #[test]
    fn added_track_with_source_is_audible_after_mix() {
        let (mut ctl, mut mixer) = wired();
        let (id, shared) = ctl.add_track().unwrap();
        ctl.set_track_source(id, Some(Box::new(VecSource::new(vec![1.0; 4]))))
            .unwrap();
        shared.set_state(TrackState::Playing);

        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);
        assert_eq!(out, [1.0, 1.0, 1.0, 1.0]);
    }

    #[test]
    fn add_effect_processes_in_the_chain() {
        let (mut ctl, mut mixer) = wired();
        let (id, shared) = ctl.add_track().unwrap();
        ctl.set_track_source(id, Some(Box::new(VecSource::new(vec![1.0; 4]))))
            .unwrap();
        ctl.add_effect(
            id,
            Box::new(ScalarGain {
                enabled: true,
                gain: 3.0,
            }),
        )
        .unwrap();
        shared.set_state(TrackState::Playing);

        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);
        assert_eq!(out, [3.0, 3.0, 3.0, 3.0]);
    }

    #[test]
    fn set_effect_param_takes_effect_on_next_block() {
        let (mut ctl, mut mixer) = wired();
        let (id, shared) = ctl.add_track().unwrap();
        ctl.set_track_source(id, Some(Box::new(VecSource::new(vec![1.0; 8]))))
            .unwrap();
        let eid = ctl
            .add_effect(
                id,
                Box::new(ScalarGain {
                    enabled: true,
                    gain: 1.0,
                }),
            )
            .unwrap();
        shared.set_state(TrackState::Playing);

        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);
        assert_eq!(out, [1.0, 1.0, 1.0, 1.0]);

        ctl.set_effect_param(id, eid, PARAM_GAIN, 5.0).unwrap();
        let mut out2 = [0.0f32; 4];
        mixer.mix(&mut out2);
        assert_eq!(out2, [5.0, 5.0, 5.0, 5.0]);
    }

    #[test]
    fn set_effect_enabled_bypasses_the_effect() {
        let (mut ctl, mut mixer) = wired();
        let (id, shared) = ctl.add_track().unwrap();
        ctl.set_track_source(id, Some(Box::new(VecSource::new(vec![1.0; 8]))))
            .unwrap();
        let eid = ctl
            .add_effect(
                id,
                Box::new(ScalarGain {
                    enabled: true,
                    gain: 4.0,
                }),
            )
            .unwrap();
        shared.set_state(TrackState::Playing);
        ctl.set_effect_enabled(id, eid, false).unwrap();

        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);
        assert_eq!(out, [1.0, 1.0, 1.0, 1.0]);
    }

    #[test]
    fn removed_track_is_gone_and_retired_for_control_thread_drop() {
        let (mut ctl, mut mixer) = wired();
        let (id, shared) = ctl.add_track().unwrap();
        ctl.set_track_source(id, Some(Box::new(VecSource::new(vec![1.0; 4]))))
            .unwrap();
        shared.set_state(TrackState::Playing);
        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);
        assert_eq!(mixer.track_count(), 1);

        assert!(ctl.remove_track(id).unwrap());
        mixer.mix(&mut out);
        assert_eq!(mixer.track_count(), 0);

        // The removed track (and its source) were handed back, not freed on the
        // audio thread.
        assert!(ctl.collect_retired() >= 1);
    }

    #[test]
    fn removed_effect_is_retired() {
        let (mut ctl, mut mixer) = wired();
        let (id, _shared) = ctl.add_track().unwrap();
        let eid = ctl
            .add_effect(
                id,
                Box::new(ScalarGain {
                    enabled: true,
                    gain: 2.0,
                }),
            )
            .unwrap();
        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);

        ctl.remove_effect(id, eid).unwrap();
        mixer.mix(&mut out);
        assert_eq!(ctl.collect_retired(), 1);
    }

    #[test]
    fn replacing_source_retires_the_previous_one() {
        let (mut ctl, mut mixer) = wired();
        let (id, _shared) = ctl.add_track().unwrap();
        ctl.set_track_source(id, Some(Box::new(VecSource::new(vec![1.0; 4]))))
            .unwrap();
        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);
        ctl.collect_retired();

        ctl.set_track_source(id, Some(Box::new(VecSource::new(vec![2.0; 4]))))
            .unwrap();
        mixer.mix(&mut out);
        // The first source was replaced and handed back.
        assert_eq!(ctl.collect_retired(), 1);
    }

    #[test]
    fn effect_for_unknown_track_is_retired_not_dropped_on_audio_thread() {
        let (mut ctl, mut mixer) = wired();
        // No track with id 999 exists.
        ctl.add_effect(
            999,
            Box::new(ScalarGain {
                enabled: true,
                gain: 2.0,
            }),
        )
        .unwrap();
        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);
        assert_eq!(ctl.collect_retired(), 1);
    }

    #[test]
    fn enqueue_auto_drains_the_retirement_queue() {
        let (mut ctl, mut mixer) = wired();
        let (id, shared) = ctl.add_track().unwrap();
        ctl.set_track_source(id, Some(Box::new(VecSource::new(vec![1.0; 4]))))
            .unwrap();
        shared.set_state(TrackState::Playing);
        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);

        // Remove the track; the next mix retires it on the audio thread.
        ctl.remove_track(id).unwrap();
        mixer.mix(&mut out);

        // The very next enqueue drains the retirement queue automatically, so an
        // explicit collect afterwards finds nothing left — the caller never has
        // to remember to drain, and the audio thread never sees a full queue.
        ctl.add_track().unwrap();
        assert_eq!(ctl.collect_retired(), 0);
    }

    #[test]
    fn controller_tracks_shared_registry() {
        let (mut ctl, _mixer) = wired();
        let (id, shared) = ctl.add_track().unwrap();
        assert_eq!(ctl.track_count(), 1);
        // The registry hands back the same shared block.
        let again = ctl.track_shared(id).unwrap();
        again.set_gain(0.25);
        assert_eq!(shared.gain(), 0.25);

        ctl.remove_track(id).unwrap();
        assert_eq!(ctl.track_count(), 0);
        assert!(ctl.track_shared(id).is_none());
    }
}
