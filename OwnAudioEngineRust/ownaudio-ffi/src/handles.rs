use std::sync::Arc;

use ownaudio_core::multitrack::MixerController;
use ownaudio_core::{
    AudioEngine, FileSourceControl, InputStream, MultiTrackMixer, OutputStream, RingBufferWriter,
    StreamingTrack, TrackShared,
};

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

/// Opaque handle to a streaming audio file decoder.
///
/// Create with `ownaudio_v1_decoder_open`; release with
/// `ownaudio_v1_decoder_destroy`.
#[repr(C)]
pub struct OwnAudioDecoderHandle {
    _private: [u8; 0],
}

// ---------------------------------------------------------------------------
// Internal wrapper types — never exposed across the FFI boundary
// ---------------------------------------------------------------------------

pub(crate) struct EngineWrapper {
    pub inner: AudioEngine,
}

pub(crate) struct DecoderWrapper {
    pub inner: StreamingTrack,
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

/// Casts a raw `*mut OwnAudioDecoderHandle` back to `&mut DecoderWrapper`.
///
/// Returns `None` if the pointer is null.
///
/// # Safety
/// The caller must guarantee that `ptr` was obtained from
/// `ownaudio_v1_decoder_open` and has not been destroyed yet.
pub(crate) unsafe fn decoder_from_ptr<'a>(
    ptr: *mut OwnAudioDecoderHandle,
) -> Option<&'a mut DecoderWrapper> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut DecoderWrapper))
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

/// Opaque handle to the write side of a track's audio-feed ring buffer.
///
/// Create with `ownaudio_v1_track_set_ring_source`; release with
/// `ownaudio_v1_track_source_destroy`.  The matching read side is owned by the
/// track on the audio thread, so the control thread pushes decoded samples
/// through this handle without ever touching the mixer.
#[repr(C)]
pub struct OwnAudioTrackSourceHandle {
    _private: [u8; 0],
}

/// Opaque handle to the control side of a file-backed track source.
///
/// Create with `ownaudio_v1_track_open_file`; release with
/// `ownaudio_v1_file_source_destroy`.  The decoded audio is produced entirely on
/// the native prefetch thread, so — unlike [`OwnAudioTrackSourceHandle`] — the
/// control thread never pushes samples; it only toggles looping, polls the
/// end-of-stream latch and requests seeks.
#[repr(C)]
pub struct OwnAudioFileSourceHandle {
    _private: [u8; 0],
}

// ---------------------------------------------------------------------------
// Internal wrappers
// ---------------------------------------------------------------------------

/// Owns one multi-track mixer's control- and audio-side state.
///
/// The [`MultiTrackMixer`] is the audio thread's exclusive data; the control
/// thread (this FFI layer) only ever touches it through `controller`, the
/// lock-free command queue.  Structural changes (tracks, sources, effects,
/// effect parameters) are enqueued on `controller` and drained by the mixer at
/// the top of each render block, so they never race the audio thread.
///
/// `mixer` holds the [`MultiTrackMixer`] until an output stream takes ownership
/// of it (see `ownaudio_v1_mixer_open_output_stream`), at which point it becomes
/// `None` and the mixer renders on the cpal audio thread.  Parameter reads and
/// structural writes keep working through `controller` regardless.
pub(crate) struct MixerWrapper {
    /// Control-thread command queue / parameter shadow for the mixer.
    pub controller: MixerController,
    /// The mixer itself, until an output stream moves it onto the audio thread.
    pub mixer: Option<MultiTrackMixer>,
    /// Output sample rate the mixer was created with (Hz).
    pub sample_rate: f32,
    /// Output channel count the mixer was created with.
    pub channels: u16,
}

/// References a track inside a mixer by its stable id.
///
/// The `*mut MixerWrapper` is non-owning; the mixer must outlive all track
/// handles.  The cloned [`TrackShared`] lets parameter setters mutate the track
/// lock-free without dereferencing the mixer pointer, and stays valid even if
/// the track is removed from the mixer.
pub(crate) struct TrackWrapper {
    /// Back-pointer to the owning mixer (non-owning).
    /// Reserved for the structural lock-free command-queue (TODO 2.6), which
    /// will route add/remove through this pointer; parameter setters reach the
    /// track via `shared` instead.
    #[allow(dead_code)]
    pub mixer: *mut MixerWrapper,
    /// Stable id of the track within the mixer.
    pub id: u64,
    /// Shared atomic parameter block for the track.
    pub shared: Arc<TrackShared>,
}

/// References a single effect inside a track's effect chain.
///
/// All pointer fields are non-owning; the mixer must outlive all effect handles.
pub(crate) struct EffectWrapper {
    /// Back-pointer to the owning mixer (non-owning).
    /// Reserved for future use when effect-level mixer queries are added.
    #[allow(dead_code)]
    pub mixer: *mut MixerWrapper,
    /// Stable id of the containing track.
    pub track_id: u64,
    /// Stable id of this effect within the track's chain.  Unlike a positional
    /// index, this stays valid when sibling effects are removed.
    pub effect_id: u64,
}

/// Owns the write side of a track's audio-feed ring buffer.
///
/// The control thread (FFI / C#) pushes decoded interleaved samples through
/// `writer`; the matching [`RingBufferReader`] was installed as the track's
/// [`TrackSource`] on the audio thread.  Both ends are lock-free and
/// independently owned, so dropping this handle never disturbs the audio thread.
///
/// [`RingBufferReader`]: ownaudio_core::RingBufferReader
/// [`TrackSource`]: ownaudio_core::TrackSource
pub(crate) struct TrackSourceWrapper {
    /// Lock-free producer feeding the track's source.
    pub writer: RingBufferWriter,
}

/// Owns the control side of a file-backed track source.
///
/// The matching [`FileTrackSource`] was installed as the track's source on the
/// audio thread, where it decodes on its own prefetch thread. This handle keeps
/// the shared [`FileSourceControl`] so the control thread can toggle looping,
/// poll the finished latch and request seeks without touching the audio thread.
///
/// [`FileTrackSource`]: ownaudio_core::FileTrackSource
pub(crate) struct FileSourceWrapper {
    /// Shared control block for the audio-thread file source.
    pub control: Arc<FileSourceControl>,
}

unsafe impl Send for MixerWrapper {}
unsafe impl Sync for MixerWrapper {}
unsafe impl Send for TrackWrapper {}
unsafe impl Send for EffectWrapper {}
unsafe impl Send for TrackSourceWrapper {}
unsafe impl Send for FileSourceWrapper {}
unsafe impl Sync for FileSourceWrapper {}

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

/// Casts a raw `*mut OwnAudioTrackSourceHandle` back to `&mut TrackSourceWrapper`.
pub(crate) unsafe fn track_source_from_ptr<'a>(
    ptr: *mut OwnAudioTrackSourceHandle,
) -> Option<&'a mut TrackSourceWrapper> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut TrackSourceWrapper))
    }
}

/// Casts a raw `*mut OwnAudioFileSourceHandle` back to `&mut FileSourceWrapper`.
pub(crate) unsafe fn file_source_from_ptr<'a>(
    ptr: *mut OwnAudioFileSourceHandle,
) -> Option<&'a mut FileSourceWrapper> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut FileSourceWrapper))
    }
}
