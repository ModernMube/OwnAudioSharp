//! Android JNI bootstrap.
//!
//! Everything AAudio does for playback goes through the NDK's C API and needs no Java at all.
//! Listing devices is the exception: cpal asks `AudioManager.getDevices()` over JNI, and it takes
//! the `JavaVM` and the app `Context` from `ndk_context` — a global that panics when nothing has
//! filled it in.
//!
//! Crates that own `main` (ndk-glue, android-activity) fill it in for you. A .NET Android app does
//! not: the runtime loads us with `dlopen` through P/Invoke, so there is no `JNI_OnLoad` and no
//! glue. Handing us the two pointers once, early, is what turns the device list from "Default
//! Device" into the real speakers, headsets and USB interfaces.
//!
//! Skipping the call is fine — [`crate::ffi_device`] then reports the default device only.

use crate::error_code::{set_last_error, OwnAudioErrorCode};

/// Hands the engine the JVM pointer and the app `Context` it needs for device enumeration.
///
/// - `java_vm` — the process `JavaVM*` (`JNIEnv::GetJavaVM`, or `JniEnvironment.Runtime.InvocationPointer` from C#).
/// - `context` — a global reference to the application `Context` (`Android.App.Application.Context.Handle`).
///
/// Call it once, before the first engine or device call. Calling it again replaces the previous
/// pointers. Returns `OwnAudioErrorCode::Success` (0), or `NullPointer` if either argument is null.
///
/// On every platform other than Android this is a no-op that reports success, so the managed side
/// can call it unconditionally.
///
/// # Safety
/// - `java_vm` must be the live process-wide `JavaVM` pointer.
/// - `context` must be a **global** JNI reference (not a local one) that outlives every engine call;
///   a local reference goes stale as soon as the creating JNI frame returns.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_android_init(
    java_vm: *mut std::os::raw::c_void,
    context: *mut std::os::raw::c_void,
) -> i32 {
    if java_vm.is_null() || context.is_null() {
        set_last_error("ownaudio_v1_android_init: java_vm and context must both be non-null");
        return OwnAudioErrorCode::NullPointer as i32;
    }

    #[cfg(target_os = "android")]
    unsafe {
        ndk_context::initialize_android_context(java_vm, context);
    }

    OwnAudioErrorCode::Success as i32
}
