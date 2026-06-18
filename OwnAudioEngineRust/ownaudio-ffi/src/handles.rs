use ownaudio_core::{AudioEngine, InputStream, MultiTrackMixer, OutputStream};

/// Opaque handle to an [`AudioEngine`] instance.
///
/// The C# side holds this as `IntPtr`.  Create with
/// `ownaudio_v1_engine_create`; release with `ownaudio_v1_engine_destroy`.
#[repr(C)]
pub struct OwnAudioEngineHandle {
    _private: [u8; 0],
}

/// Opaque handle to an output audio stream.
///
/// Create with `ownaudio_v1_open_output_stream`; release with
/// `ownaudio_v1_output_stream_destroy`.
#[repr(C)]
pub struct OwnAudioOutputStreamHandle {
    _private: [u8; 0],
}

/// Opaque handle to an input audio stream.
///
/// Create with `ownaudio_v1_open_input_stream`; release with
/// `ownaudio_v1_input_stream_destroy`.
#[repr(C)]
pub struct OwnAudioInputStreamHandle {
    _private: [u8; 0],
}

// ---------------------------------------------------------------------------
// Internal wrapper types — never exposed across the FFI boundary
// ---------------------------------------------------------------------------

pub(crate) struct EngineWrapper {
    pub inner: AudioEngine,
}

pub(crate) struct OutputStreamWrapper {
    pub inner: OutputStream,
}

pub(crate) struct InputStreamWrapper {
    pub inner: InputStream,
}

// SAFETY: cpal::Stream is not Send on all platforms (e.g. macOS AudioQueue).
// The FFI contract places thread-safety responsibility on the caller: handle
// pointers must not be used concurrently from multiple threads without
// external synchronization.
unsafe impl Send for OutputStreamWrapper {}
unsafe impl Sync for OutputStreamWrapper {}
unsafe impl Send for InputStreamWrapper {}
unsafe impl Sync for InputStreamWrapper {}

// ---------------------------------------------------------------------------
// Helper: safely dereference an opaque handle pointer
// ---------------------------------------------------------------------------

/// Casts a raw `*mut OwnAudioEngineHandle` back to `&mut EngineWrapper`.
///
/// Returns `None` if the pointer is null.
///
/// # Safety
/// The caller must guarantee that `ptr` was obtained from
/// `ownaudio_v1_engine_create` and has not been destroyed yet.
pub(crate) unsafe fn engine_from_ptr<'a>(
    ptr: *mut OwnAudioEngineHandle,
) -> Option<&'a mut EngineWrapper> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut EngineWrapper))
    }
}

/// Casts a raw `*mut OwnAudioOutputStreamHandle` back to `&mut OutputStreamWrapper`.
pub(crate) unsafe fn output_stream_from_ptr<'a>(
    ptr: *mut OwnAudioOutputStreamHandle,
) -> Option<&'a mut OutputStreamWrapper> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut OutputStreamWrapper))
    }
}

/// Casts a raw `*mut OwnAudioInputStreamHandle` back to `&mut InputStreamWrapper`.
pub(crate) unsafe fn input_stream_from_ptr<'a>(
    ptr: *mut OwnAudioInputStreamHandle,
) -> Option<&'a mut InputStreamWrapper> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut InputStreamWrapper))
    }
}

// ---------------------------------------------------------------------------
// Mixer / track / effect opaque handles
// ---------------------------------------------------------------------------

/// Opaque handle to a [`MultiTrackMixer`] instance.
///
/// Create with `ownaudio_v1_mixer_create`; release with `ownaudio_v1_mixer_destroy`.
#[repr(C)]
pub struct OwnAudioMixerHandle {
    _private: [u8; 0],
}

/// Opaque handle to a single audio [`Track`] inside a mixer.
///
/// Create with `ownaudio_v1_track_create`; release with `ownaudio_v1_track_destroy`.
#[repr(C)]
pub struct OwnAudioTrackHandle {
    _private: [u8; 0],
}

/// Opaque handle to an audio effect inside a track's effect chain.
///
/// Create with `ownaudio_v1_track_add_effect`; release with `ownaudio_v1_effect_destroy`.
#[repr(C)]
pub struct OwnAudioEffectHandle {
    _private: [u8; 0],
}

// ---------------------------------------------------------------------------
// Internal wrappers
// ---------------------------------------------------------------------------

pub(crate) struct MixerWrapper {
    pub inner: MultiTrackMixer,
}

/// Borrows a track inside a mixer by index.
///
/// The `*mut MixerWrapper` is non-owning; the mixer must outlive all track handles.
pub(crate) struct TrackWrapper {
    /// Back-pointer to the owning mixer (non-owning).
    pub mixer: *mut MixerWrapper,
    /// Zero-based track index within the mixer.
    pub track_index: usize,
}

/// References a single effect inside a track's effect chain.
///
/// All pointer fields are non-owning; the mixer must outlive all effect handles.
pub(crate) struct EffectWrapper {
    /// Back-pointer to the owning mixer (non-owning).
    /// Reserved for future use when effect-level mixer queries are added.
    #[allow(dead_code)]
    pub mixer: *mut MixerWrapper,
    /// Index of the containing track.
    pub track_index: usize,
    /// Index of this effect within the track's chain.
    pub effect_index: usize,
}

unsafe impl Send for MixerWrapper {}
unsafe impl Sync for MixerWrapper {}
unsafe impl Send for TrackWrapper {}
unsafe impl Send for EffectWrapper {}

// ---------------------------------------------------------------------------
// Helper functions
// ---------------------------------------------------------------------------

/// Casts a raw `*mut OwnAudioMixerHandle` back to `&mut MixerWrapper`.
pub(crate) unsafe fn mixer_from_ptr<'a>(
    ptr: *mut OwnAudioMixerHandle,
) -> Option<&'a mut MixerWrapper> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut MixerWrapper))
    }
}

/// Casts a raw `*mut OwnAudioTrackHandle` back to `&mut TrackWrapper`.
pub(crate) unsafe fn track_from_ptr<'a>(
    ptr: *mut OwnAudioTrackHandle,
) -> Option<&'a mut TrackWrapper> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut TrackWrapper))
    }
}

/// Casts a raw `*mut OwnAudioEffectHandle` back to `&mut EffectWrapper`.
pub(crate) unsafe fn effect_from_ptr<'a>(
    ptr: *mut OwnAudioEffectHandle,
) -> Option<&'a mut EffectWrapper> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut EffectWrapper))
    }
}
