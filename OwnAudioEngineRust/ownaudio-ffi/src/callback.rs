/// Function pointer the C# side provides for output streams.
///
/// Called on the audio real-time thread for every buffer.
/// - `buffer` — interleaved f32 samples; the callback must fill all
///   `frame_count * channels` elements.
/// - `frame_count` — number of audio frames in this buffer.
/// - `channels` — number of interleaved channels.
/// - `user_data` — the opaque pointer supplied at stream creation, passed
///   back unchanged on every call.
///
/// # Safety
/// The callback must be real-time safe: no heap allocation, no blocking I/O,
/// no non-RT-safe locks.  `user_data` must remain valid for the entire
/// lifetime of the stream and must be safe to access from the audio thread.
pub type OwnAudioOutputCallback = Option<
    unsafe extern "C" fn(
        buffer: *mut f32,
        frame_count: usize,
        channels: u16,
        user_data: *mut std::os::raw::c_void,
    ),
>;

/// Function pointer the C# side provides for input streams.
///
/// Called on the audio real-time thread for every captured buffer.
/// - `buffer` — read-only interleaved f32 samples.
///
/// Same lifetime and real-time constraints as [`OwnAudioOutputCallback`].
pub type OwnAudioInputCallback = Option<
    unsafe extern "C" fn(
        buffer: *const f32,
        frame_count: usize,
        channels: u16,
        user_data: *mut std::os::raw::c_void,
    ),
>;

// ---------------------------------------------------------------------------
// Trampoline state structs
// ---------------------------------------------------------------------------
//
// Rust 2021 RFC 2229 "precise closure capture" would capture the individual
// fields of a struct if the closure body accesses them directly (e.g. `ud.0`),
// which would expose `*mut c_void` as a captured type and break the `Send`
// bound.  Wrapping state in a struct and invoking it through a method forces
// the closure to capture the whole struct, so the `unsafe impl Send` applies.

struct OutputTrampolineState {
    callback: unsafe extern "C" fn(*mut f32, usize, u16, *mut std::os::raw::c_void),
    user_data: *mut std::os::raw::c_void,
    channels: u16,
}

// SAFETY: The caller guarantees that `user_data` is valid for the lifetime of
// the stream and that the callback may be invoked from the audio thread.
unsafe impl Send for OutputTrampolineState {}

impl OutputTrampolineState {
    #[inline(always)]
    unsafe fn call(&self, buf: &mut [f32]) {
        let frame_count = if self.channels > 0 {
            buf.len() / self.channels as usize
        } else {
            0
        };
        // Defense in depth: the cpal-boundary guard in `ownaudio-core`'s engine
        // already catches any panic from this trampoline, and the foreign C#
        // callback aborts at its own `extern "C"` ABI boundary if it were to
        // panic.  We still wrap the invocation here so a panic originating in
        // this trampoline's own Rust logic can never unwind, and so the buffer
        // is left silent rather than partially written on failure.
        let callback = self.callback;
        let user_data = self.user_data;
        let channels = self.channels;
        let ptr = buf.as_mut_ptr();
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            callback(ptr, frame_count, channels, user_data);
        }));
        if result.is_err() {
            buf.fill(0.0);
        }
    }
}

struct InputTrampolineState {
    callback: unsafe extern "C" fn(*const f32, usize, u16, *mut std::os::raw::c_void),
    user_data: *mut std::os::raw::c_void,
    channels: u16,
}

// SAFETY: same guarantees as OutputTrampolineState.
unsafe impl Send for InputTrampolineState {}

impl InputTrampolineState {
    #[inline(always)]
    unsafe fn call(&self, buf: &[f32]) {
        let frame_count = if self.channels > 0 {
            buf.len() / self.channels as usize
        } else {
            0
        };
        // See `OutputTrampolineState::call` for the layering rationale.  Input
        // has no buffer to sanitise, so a caught panic is simply swallowed.
        let callback = self.callback;
        let user_data = self.user_data;
        let channels = self.channels;
        let ptr = buf.as_ptr();
        let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            callback(ptr, frame_count, channels, user_data);
        }));
    }
}

// ---------------------------------------------------------------------------
// Trampoline constructors
// ---------------------------------------------------------------------------

/// Builds the closure that `ownaudio_core` expects for output streams.
///
/// The closure calls `callback` on every audio callback, passing `user_data`
/// back unchanged.
///
/// # Safety
/// `user_data` must remain valid and accessible from the audio thread for the
/// entire lifetime of the stream.
pub(crate) fn make_output_trampoline(
    callback: unsafe extern "C" fn(*mut f32, usize, u16, *mut std::os::raw::c_void),
    user_data: *mut std::os::raw::c_void,
    channels: u16,
) -> impl FnMut(&mut [f32]) + Send + 'static {
    let state = OutputTrampolineState {
        callback,
        user_data,
        channels,
    };
    // The closure captures `state` as a whole (method call, not field access),
    // so `OutputTrampolineState: Send` makes the closure `Send` too.
    move |buf: &mut [f32]| unsafe { state.call(buf) }
}

/// Builds the closure that `ownaudio_core` expects for input streams.
pub(crate) fn make_input_trampoline(
    callback: unsafe extern "C" fn(*const f32, usize, u16, *mut std::os::raw::c_void),
    user_data: *mut std::os::raw::c_void,
    channels: u16,
) -> impl FnMut(&[f32]) + Send + 'static {
    let state = InputTrampolineState {
        callback,
        user_data,
        channels,
    };
    move |buf: &[f32]| unsafe { state.call(buf) }
}
