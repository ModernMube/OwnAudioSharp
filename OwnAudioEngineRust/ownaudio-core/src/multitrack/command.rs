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

use super::track::{Track, TrackShared, TrackSource, TrackState};

/// Upper bound (exclusive) of effect parameter ids probed for their default
/// values when an effect is added, so the control-side shadow can answer
/// [`MixerController::get_effect_param`] while the effect itself lives on the
/// audio thread.  Every effect parameter id in this crate falls well below this
/// bound (the widest, the 30-band equaliser, reaches id 31).
const PARAM_PROBE_MAX: u32 = 64;

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
    /// Set a track's plugin delay compensation, in frames.
    SetTrackDelay {
        /// Target track id.
        track_id: u64,
        /// Compensation delay in frames (`max_chain_latency − this_chain_latency`).
        delay_frames: u32,
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

/// Control-side mirror of one effect's parameter values.
///
/// The effect itself is owned by the audio thread once it has been drained from
/// the command queue, so its parameters can no longer be read directly from the
/// control thread.  This shadow caches every parameter value — seeded with the
/// effect's defaults when it is added and updated on every
/// [`MixerController::set_effect_param`] — so `get_param` can be answered
/// synchronously without ever touching the audio thread.
struct EffectShadow {
    /// Stable effect id this shadow mirrors.
    effect_id: u64,
    /// Id of the track that owns the effect, so the shadow can be dropped when
    /// the whole track is removed.
    track_id: u64,
    /// Cached `(param_id, value)` pairs.  A small linear vector: the parameter
    /// count per effect is tiny (≤ 32) and lookups happen off the audio thread.
    params: Vec<(u32, f32)>,
    /// Processing latency this effect introduces, in frames, cached so the
    /// controller can recompute plugin delay compensation when effects change
    /// without reaching into the audio thread.
    latency: u32,
}

impl EffectShadow {
    /// Returns the cached value for `param_id`, or `None` when unknown.
    fn get(&self, param_id: u32) -> Option<f32> {
        self.params
            .iter()
            .find(|(id, _)| *id == param_id)
            .map(|(_, v)| *v)
    }

    /// Updates the cached value for `param_id`.  Returns `true` when the
    /// parameter is one this effect exposes (i.e. it was seeded at add time).
    fn set(&mut self, param_id: u32, value: f32) -> bool {
        match self.params.iter_mut().find(|(id, _)| *id == param_id) {
            Some(slot) => {
                slot.1 = value;
                true
            }
            None => false,
        }
    }
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
    /// Control-side shadow of each live effect's parameters (see
    /// [`EffectShadow`]), so `get_param` is answerable while the effect lives on
    /// the audio thread.
    effect_shadows: Vec<EffectShadow>,
    /// Last plugin-delay-compensation value (in frames) sent to each track, so a
    /// recompute only enqueues a [`MixerCommand::SetTrackDelay`] when a track's
    /// compensation actually changes — otherwise the command queue would take a
    /// write for every track on every effect edit.
    pdc_sent: Vec<(u64, u32)>,
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
        effect_shadows: Vec::new(),
        pdc_sent: Vec::with_capacity(super::MAX_TRACKS),
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
        // A fresh track has zero effect latency; recompute so it is delayed to
        // match any existing higher-latency track (and seeds its pdc_sent entry).
        self.recompute_pdc();
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
        // Drop the shadows of every effect that lived on the removed track.
        self.effect_shadows.retain(|s| s.track_id != id);
        self.pdc_sent.retain(|(tid, _)| *tid != id);
        // Removing the highest-latency track can lower the max, so re-slacken the
        // others' compensation.
        self.recompute_pdc();
        Ok(true)
    }

    /// Starts every registered track at once by flipping its shared state to
    /// [`TrackState::Playing`] from the control thread.
    ///
    /// Because all the state writes complete before the audio thread reads them
    /// in the next [`MultiTrackMixer::mix`], every track begins on the same
    /// render block — a sample-accurate start — without a per-track round-trip.
    /// Works whether or not the mixer has been moved onto the audio thread,
    /// since it touches only the shared (atomic) blocks the controller holds.
    pub fn play_all(&self) {
        for (_, shared) in &self.track_registry {
            shared.set_state(TrackState::Playing);
        }
    }

    /// Pauses every registered track at once by flipping its shared state to
    /// [`TrackState::Paused`] from the control thread.
    ///
    /// Like [`play_all`](Self::play_all), every state write completes before the
    /// audio thread reads them in the next [`MultiTrackMixer::mix`], so all
    /// tracks pause on the same render block without a per-track round-trip.
    pub fn pause_all(&self) {
        for (_, shared) in &self.track_registry {
            shared.set_state(TrackState::Paused);
        }
    }

    /// Stops every registered track at once by flipping its shared state to
    /// [`TrackState::Stopped`] from the control thread (sample-accurate, no
    /// per-track round-trip).
    pub fn stop_all(&self) {
        for (_, shared) in &self.track_registry {
            shared.set_state(TrackState::Stopped);
        }
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
    ///
    /// Before the effect is handed to the audio thread its default parameter
    /// values are probed (param ids `0..PARAM_PROBE_MAX`) and cached in a
    /// control-side shadow, so [`MixerController::get_effect_param`] can read
    /// them back synchronously afterwards.
    pub fn add_effect(
        &mut self,
        track_id: u64,
        effect: Box<dyn Effect>,
    ) -> Result<u64, CommandError> {
        let effect_id = self.next_effect_id;

        // Snapshot the effect's defaults before it moves to the audio thread.
        let mut params = Vec::new();
        for param_id in 0..PARAM_PROBE_MAX {
            if let Some(value) = effect.get_param(param_id) {
                params.push((param_id, value));
            }
        }
        let latency = effect.latency_samples();

        self.enqueue(MixerCommand::AddEffect {
            track_id,
            effect_id,
            effect,
        })?;
        self.next_effect_id += 1;
        self.effect_shadows.push(EffectShadow {
            effect_id,
            track_id,
            params,
            latency,
        });
        self.recompute_pdc();
        Ok(effect_id)
    }

    /// Enqueues removal of an effect by id and drops its parameter shadow.
    pub fn remove_effect(&mut self, track_id: u64, effect_id: u64) -> Result<(), CommandError> {
        self.enqueue(MixerCommand::RemoveEffect {
            track_id,
            effect_id,
        })?;
        self.effect_shadows.retain(|s| s.effect_id != effect_id);
        self.recompute_pdc();
        Ok(())
    }

    /// Total effect-chain latency for a track, in frames (sum of its effects'
    /// reported latencies). The master chain is not a track, so its latency never
    /// participates — it delays the summed mix uniformly and needs no per-track
    /// compensation.
    fn track_latency(&self, track_id: u64) -> u32 {
        self.effect_shadows
            .iter()
            .filter(|s| s.track_id == track_id)
            .map(|s| s.latency)
            .sum()
    }

    /// Recomputes plugin delay compensation across all registered tracks and
    /// enqueues a [`MixerCommand::SetTrackDelay`] for each track whose
    /// compensation changed. Each track is delayed by `max_latency − its_latency`
    /// so every track lines up sample-accurately with the highest-latency one.
    ///
    /// Best-effort: if the command queue is momentarily full a track's `pdc_sent`
    /// entry is left unchanged, so the next effect edit retries it.
    fn recompute_pdc(&mut self) {
        let max_latency = self
            .track_registry
            .iter()
            .map(|(tid, _)| self.track_latency(*tid))
            .max()
            .unwrap_or(0);

        // Collect first so the immutable registry borrow ends before enqueuing.
        let updates: Vec<(u64, u32)> = self
            .track_registry
            .iter()
            .filter_map(|(tid, _)| {
                let comp = max_latency - self.track_latency(*tid);
                // A track with no recorded entry is already at zero delay, so a
                // zero compensation needs no command (avoids a redundant write for
                // every freshly added track).
                let already = self
                    .pdc_sent
                    .iter()
                    .find(|(t, _)| t == tid)
                    .map(|(_, c)| *c)
                    .unwrap_or(0);
                (already != comp).then_some((*tid, comp))
            })
            .collect();

        for (tid, comp) in updates {
            if self
                .enqueue(MixerCommand::SetTrackDelay {
                    track_id: tid,
                    delay_frames: comp,
                })
                .is_ok()
            {
                match self.pdc_sent.iter_mut().find(|(t, _)| *t == tid) {
                    Some(slot) => slot.1 = comp,
                    None => self.pdc_sent.push((tid, comp)),
                }
            }
        }
    }

    /// Enqueues a parameter change on an effect, updating the control-side
    /// shadow so a subsequent [`MixerController::get_effect_param`] reflects it.
    ///
    /// Returns `Ok(true)` when the effect exposes `param_id` (the change is
    /// queued), `Ok(false)` when the parameter is unknown for this effect (no
    /// command is enqueued), mirroring the effect's own `set_param` contract.
    pub fn set_effect_param(
        &mut self,
        track_id: u64,
        effect_id: u64,
        param_id: u32,
        value: f32,
    ) -> Result<bool, CommandError> {
        let known = match self
            .effect_shadows
            .iter_mut()
            .find(|s| s.effect_id == effect_id)
        {
            Some(shadow) => shadow.set(param_id, value),
            None => false,
        };

        if !known {
            return Ok(false);
        }

        self.enqueue(MixerCommand::SetEffectParam {
            track_id,
            effect_id,
            param_id,
            value,
        })?;
        Ok(true)
    }

    /// Reads back an effect parameter from the control-side shadow.
    ///
    /// Returns `None` when the effect or parameter is unknown.
    pub fn get_effect_param(&self, effect_id: u64, param_id: u32) -> Option<f32> {
        self.effect_shadows
            .iter()
            .find(|s| s.effect_id == effect_id)
            .and_then(|s| s.get(param_id))
    }

    /// Enqueues an enable/bypass change on an effect, keeping the control-side
    /// shadow's [`PARAM_ENABLED`](crate::effects::PARAM_ENABLED) entry in step.
    pub fn set_effect_enabled(
        &mut self,
        track_id: u64,
        effect_id: u64,
        enabled: bool,
    ) -> Result<(), CommandError> {
        if let Some(shadow) = self
            .effect_shadows
            .iter_mut()
            .find(|s| s.effect_id == effect_id)
        {
            shadow.set(crate::effects::PARAM_ENABLED, if enabled { 1.0 } else { 0.0 });
        }
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
    use crate::effects::{EffectType, PARAM_ENABLED, PARAM_MIX};
    use crate::multitrack::{MultiTrackMixer, TrackState, MASTER_EFFECT_TARGET};

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

    /// Test effect that delays its output by `latency` frames (mono) and reports
    /// exactly that latency — a stand-in for a look-ahead effect or a hosted plugin.
    struct LatencyDelayEffect {
        buf: Vec<f32>,
        idx: usize,
        latency: u32,
    }
    impl LatencyDelayEffect {
        fn new(latency: usize) -> Self {
            Self {
                buf: vec![0.0; latency.max(1)],
                idx: 0,
                latency: latency as u32,
            }
        }
    }
    impl Effect for LatencyDelayEffect {
        fn effect_type(&self) -> EffectType {
            EffectType::Distortion
        }
        fn process(&mut self, buffer: &mut [f32], _channels: u16) {
            let len = self.buf.len();
            for s in buffer.iter_mut() {
                let out = self.buf[self.idx];
                self.buf[self.idx] = *s;
                self.idx = (self.idx + 1) % len;
                *s = out;
            }
        }
        fn set_param(&mut self, _param_id: u32, _value: f32) -> bool {
            false
        }
        fn get_param(&self, _param_id: u32) -> Option<f32> {
            None
        }
        fn reset(&mut self) {}
        fn is_enabled(&self) -> bool {
            true
        }
        fn set_enabled(&mut self, _enabled: bool) {}
        fn latency_samples(&self) -> u32 {
            self.latency
        }
    }

    #[test]
    fn pdc_aligns_a_latency_track_with_a_compensated_one() {
        // Mono mixer so one sample equals one frame.
        let mut mixer = MultiTrackMixer::new(48_000.0, 1);
        let (mut ctl, rx) = command_channel(64, mixer.sample_rate(), mixer.max_buffer_size());
        mixer.attach_command_receiver(rx);

        const L: usize = 3;
        let mut impulse = vec![0.0f32; 16];
        impulse[0] = 1.0;

        // Track A: an impulse routed through a latency-L delay effect.
        let (a, sa) = ctl.add_track().unwrap();
        ctl.set_track_source(a, Some(Box::new(VecSource::new(impulse.clone()))))
            .unwrap();
        ctl.add_effect(a, Box::new(LatencyDelayEffect::new(L))).unwrap();

        // Track B: the same impulse with no effect — the mixer must delay it by L
        // (plugin delay compensation) so it lines up with A's delayed output.
        let (b, sb) = ctl.add_track().unwrap();
        ctl.set_track_source(b, Some(Box::new(VecSource::new(impulse))))
            .unwrap();

        sa.set_state(TrackState::Playing);
        sb.set_state(TrackState::Playing);

        // One render drains every command (add track/source/effect + the recomputed
        // SetTrackDelay for B) and then renders with them applied.
        let mut out = vec![0.0f32; 16];
        mixer.mix(&mut out);

        // Both impulses land aligned at frame L (summed), and nothing leaks out at
        // frame 0 — B was not left un-delayed.
        assert_eq!(out[0], 0.0, "no un-compensated impulse at frame 0");
        assert_eq!(out[L], 2.0, "both tracks' impulses align at frame L");
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

    /// Source that endlessly yields a constant sample (survives repeated mixes).
    struct ConstSource(f32);
    impl TrackSource for ConstSource {
        fn read(&mut self, out: &mut [f32]) -> usize {
            out.fill(self.0);
            out.len()
        }
    }

    #[test]
    fn master_effect_processes_the_summed_mix() {
        let (mut ctl, mut mixer) = wired();

        // Two tracks each contributing 1.0 → summed mix is 2.0 before the master.
        for _ in 0..2 {
            let (id, shared) = ctl.add_track().unwrap();
            ctl.set_track_source(id, Some(Box::new(ConstSource(1.0))))
                .unwrap();
            shared.set_state(TrackState::Playing);
        }

        // A master effect scaling by 3 applies once over the whole mix: 2.0 → 6.0.
        let effect_id = ctl
            .add_effect(
                MASTER_EFFECT_TARGET,
                Box::new(ScalarGain {
                    enabled: true,
                    gain: 3.0,
                }),
            )
            .unwrap();

        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);
        assert_eq!(out, [6.0, 6.0, 6.0, 6.0]);
        assert_eq!(mixer.master_effect_count(), 1);

        // Removing the master effect restores the un-processed summed mix (2.0).
        ctl.remove_effect(MASTER_EFFECT_TARGET, effect_id).unwrap();
        let mut out2 = [0.0f32; 4];
        mixer.mix(&mut out2);
        assert_eq!(out2, [2.0, 2.0, 2.0, 2.0]);
        assert_eq!(mixer.master_effect_count(), 0);
    }

    #[test]
    fn master_effect_param_change_takes_effect() {
        let (mut ctl, mut mixer) = wired();
        let (id, shared) = ctl.add_track().unwrap();
        ctl.set_track_source(id, Some(Box::new(VecSource::new(vec![1.0; 4]))))
            .unwrap();
        shared.set_state(TrackState::Playing);

        let effect_id = ctl
            .add_effect(
                MASTER_EFFECT_TARGET,
                Box::new(ScalarGain {
                    enabled: true,
                    gain: 1.0,
                }),
            )
            .unwrap();

        // Re-target the master gain to 5 through the command queue.
        ctl.set_effect_param(MASTER_EFFECT_TARGET, effect_id, PARAM_GAIN, 5.0)
            .unwrap();

        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);
        assert_eq!(out, [5.0, 5.0, 5.0, 5.0]);
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
    fn play_all_via_controller_starts_registered_tracks() {
        // The controller can start every track through the shared blocks alone,
        // so play_all works even though the mixer owns the tracks.
        let (mut ctl, mut mixer) = wired();
        let (a, _) = ctl.add_track().unwrap();
        let (b, _) = ctl.add_track().unwrap();
        ctl.set_track_source(a, Some(Box::new(VecSource::new(vec![1.0; 4]))))
            .unwrap();
        ctl.set_track_source(b, Some(Box::new(VecSource::new(vec![2.0; 4]))))
            .unwrap();

        // Nothing audible before play_all.
        let mut pre = [0.0f32; 4];
        mixer.mix(&mut pre);
        assert_eq!(pre, [0.0, 0.0, 0.0, 0.0]);

        ctl.play_all();
        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);
        for &s in &out {
            assert!((s - 3.0).abs() < 1e-6, "out={out:?}");
        }
    }

    #[test]
    fn effect_shadow_seeds_defaults_and_reflects_writes() {
        let (mut ctl, _mixer) = wired();
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

        // Defaults were probed at add time and are readable synchronously.
        assert_eq!(ctl.get_effect_param(eid, PARAM_GAIN), Some(2.0));
        assert_eq!(ctl.get_effect_param(eid, PARAM_ENABLED), Some(1.0));
        // Unknown parameter / unknown effect → None.
        assert_eq!(ctl.get_effect_param(eid, 999), None);
        assert_eq!(ctl.get_effect_param(12345, PARAM_GAIN), None);

        // A known write returns true and updates the shadow.
        assert_eq!(ctl.set_effect_param(id, eid, PARAM_GAIN, 5.0), Ok(true));
        assert_eq!(ctl.get_effect_param(eid, PARAM_GAIN), Some(5.0));

        // An unknown write returns false and leaves the shadow untouched.
        assert_eq!(ctl.set_effect_param(id, eid, 999, 1.0), Ok(false));
        assert_eq!(ctl.get_effect_param(eid, 999), None);

        // set_effect_enabled keeps the PARAM_ENABLED shadow in step.
        ctl.set_effect_enabled(id, eid, false).unwrap();
        assert_eq!(ctl.get_effect_param(eid, PARAM_ENABLED), Some(0.0));
    }

    #[test]
    fn effect_shadow_seeds_real_effect_defaults() {
        // A real effect's defaults must be captured by the probe, not just the
        // test stub's.  Distortion exposes drive (id 2) defaulting to 2.0.
        use crate::effects::Distortion;
        let (mut ctl, _mixer) = wired();
        let (id, _shared) = ctl.add_track().unwrap();
        let eid = ctl
            .add_effect(id, Box::new(Distortion::new(48_000.0)))
            .unwrap();
        const PARAM_DRIVE: u32 = 2;
        assert_eq!(ctl.get_effect_param(eid, PARAM_DRIVE), Some(2.0));
        assert!(ctl.get_effect_param(eid, PARAM_MIX).is_some());
    }

    #[test]
    fn removing_effect_and_track_drops_their_shadows() {
        let (mut ctl, _mixer) = wired();
        let (id, _shared) = ctl.add_track().unwrap();
        let eid = ctl
            .add_effect(
                id,
                Box::new(ScalarGain {
                    enabled: true,
                    gain: 1.0,
                }),
            )
            .unwrap();
        assert!(ctl.get_effect_param(eid, PARAM_GAIN).is_some());

        ctl.remove_effect(id, eid).unwrap();
        assert_eq!(ctl.get_effect_param(eid, PARAM_GAIN), None);

        // Effect shadows are also dropped when the whole track is removed.
        let eid2 = ctl
            .add_effect(
                id,
                Box::new(ScalarGain {
                    enabled: true,
                    gain: 1.0,
                }),
            )
            .unwrap();
        assert!(ctl.get_effect_param(eid2, PARAM_GAIN).is_some());
        ctl.remove_track(id).unwrap();
        assert_eq!(ctl.get_effect_param(eid2, PARAM_GAIN), None);
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
