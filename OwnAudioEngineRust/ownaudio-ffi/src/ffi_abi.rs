/// The ABI version of this native binary.
///
/// This constant is incremented whenever a **breaking** change is made to the FFI
/// surface: adding, removing, or reordering `extern "C"` exports; changing struct
/// layouts; or renaming enum variants. Consumers (the C# managed layer) call
/// [`ownaudio_v1_get_abi_version`] at startup and reject the binary when the
/// returned value does not match the version they were compiled against.
///
/// **Rules for incrementing:**
/// - Additive-only changes (new exports, new enum variants at the end) → no bump needed.
/// - Any breaking change → bump by 1 and update `AudioEngine.ExpectedAbiVersion` in C#.
pub const ABI_VERSION: u32 = 1;

/// Returns the ABI version of this native binary.
///
/// The C# wrapper calls this function immediately after loading the library and
/// compares the result to its compile-time constant. If the versions differ it
/// raises `AbiVersionMismatchException` so the user gets a clear error instead of
/// a cryptic access violation.
///
/// Returns [`ABI_VERSION`] — always `1` for the initial v1 ABI surface.
#[no_mangle]
pub extern "C" fn ownaudio_v1_get_abi_version() -> u32 {
    ABI_VERSION
}

/// Returns a null-terminated UTF-8 string describing the package version baked
/// into this binary at compile time (e.g. `"1.0.0"` or `"1.2.0-ci.42"`).
///
/// The pointer is valid for the lifetime of the process.  The caller must **not**
/// free it.  The string is derived from `version.json` at build time via `build.rs`.
#[no_mangle]
pub extern "C" fn ownaudio_v1_get_package_version() -> *const std::os::raw::c_char {
    // SAFETY: the literal is a valid null-terminated UTF-8 string embedded in
    // the binary's read-only data section; it lives for the entire process lifetime.
    concat!(env!("OWNAUDIO_VERSION"), "\0").as_ptr().cast()
}
