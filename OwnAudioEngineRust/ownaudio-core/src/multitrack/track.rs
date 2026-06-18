//! Single audio track with a ring buffer, gain, and an effect chain.

use crate::effects::EffectChain;

/// Playback state of a single track.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TrackState {
    /// Track is stopped.
    Stopped,
    /// Track is playing.
    Playing,
    /// Track is paused.
    Paused,
}

/// A single audio track within a [`super::MultiTrackMixer`].
///
/// Audio data is fed from the C# side via [`write`]; the audio thread consumes
/// samples from an internal ring buffer, applies the effect chain, and mixes
/// the result into the output buffer.
pub struct Track {
    /// Gain applied to the track output (linear, 1.0 = unity).
    pub gain: f32,
    /// Mute flag — when `true` the track output is zeroed.
    pub muted: bool,
    /// Solo flag — when any track is soloed, non-soloed tracks are muted.
    pub soloed: bool,
    /// Tempo ratio (1.0 = normal speed).  Applied via time-stretch.
    pub tempo_ratio: f32,
    /// Pitch shift in semitones.  Applied via the PitchShift effect.
    pub pitch_semitones: f32,
    /// Current playback state.
    pub state: TrackState,
    /// Ordered list of effects applied to this track.
    pub effects: EffectChain,
}

impl Track {
    /// Creates a new idle track.
    pub fn new() -> Self {
        Self {
            gain: 1.0,
            muted: false,
            soloed: false,
            tempo_ratio: 1.0,
            pitch_semitones: 0.0,
            state: TrackState::Stopped,
            effects: EffectChain::new(),
        }
    }

    /// Returns `true` when this track contributes audio to the mix.
    #[inline]
    pub fn is_active(&self) -> bool {
        self.state == TrackState::Playing && !self.muted
    }
}

impl Default for Track {
    fn default() -> Self {
        Self::new()
    }
}
