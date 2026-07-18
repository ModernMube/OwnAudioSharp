# ownaudio-ffi

C ABI FFI layer for OwnAudioSharp. Wraps [`ownaudio-core`](../ownaudio-core) with a stable `extern "C"` interface designed for consumption from C#'s `LibraryImport` / `DllImport`, but compatible with any language that can call C functions.

## Table of Contents

- [Architecture](#architecture)
- [Building](#building)
  - [Feature Flags](#feature-flags)
  - [ASIO Support](#asio-support)
  - [Output Artifacts](#output-artifacts)
  - [C Header](#c-header)
- [ABI Contract](#abi-contract)
  - [ABI Version](#abi-version)
  - [Error Codes](#error-codes)
  - [Thread-Local Error Messages](#thread-local-error-messages)
  - [Handle Types](#handle-types)
  - [Memory Ownership](#memory-ownership)
- [API Reference](#api-reference)
  - [Version & Diagnostics](#version--diagnostics)
  - [Engine Lifecycle](#engine-lifecycle)
  - [Device Enumeration](#device-enumeration)
  - [Stream Configuration](#stream-configuration)
  - [Output Streams](#output-streams)
  - [Input Streams](#input-streams)
  - [Host API Selection](#host-api-selection)
  - [Mixer Lifecycle](#mixer-lifecycle)
  - [Track Management](#track-management)
  - [Effects](#effects)
- [Callback Signatures](#callback-signatures)
- [C# Integration Example](#c-integration-example)
- [Common Pitfalls](#common-pitfalls)

---

## Architecture

```
C# / C / other consumer
       │  LibraryImport / DllImport
       ▼
ownaudio-ffi  (this crate)
  ├─ extern "C" function wrappers
  ├─ Rust error → i32 error code mapping
  ├─ Box<T> opaque handles
  ├─ catch_unwind on every entry point
  └─ callback trampolines (C fn ptr → Rust closure)
       │
       ▼
ownaudio-core
  ├─ AudioEngine / OutputStream / InputStream
  ├─ MultiTrackMixer / Track / EffectChain
  ├─ Resampler, Mixer, RingBuffer
  └─ cpal backend (WASAPI / CoreAudio / ALSA)
```

Every public symbol in this crate is prefixed with `ownaudio_v1_` — the `v1` infix is the ABI version and will change on any breaking API change.

---

## Building

```bash
# Dynamic library (.dll / .so / .dylib)
cargo build --release -p ownaudio-ffi

# Static library (.lib / .a)
# Both cdylib and staticlib are built in the same cargo invocation.
# The crate-type in Cargo.toml includes: cdylib, staticlib, rlib

# Windows ASIO support (see ASIO section below)
cargo build --release -p ownaudio-ffi --features asio
```

The `build.rs` script runs automatically and:
1. Reads `version.json` from the workspace root and sets `OWNAUDIO_VERSION` and `OWNAUDIO_ABI_VERSION`.
2. Validates `ASIO_SDK_DIR` when `--features asio` is active.
3. Runs `cbindgen` to generate `include/ownaudio_ffi.h`.

### Feature Flags

| Feature | Default | Description |
|---------|---------|-------------|
| `asio` | off | Enable Windows ASIO backend (requires Steinberg ASIO SDK) |

### ASIO Support

ASIO requires the Steinberg ASIO SDK, available under a dual licence (proprietary / GPLv2):

```bash
# Set ASIO_SDK_DIR before building
set ASIO_SDK_DIR=C:\path\to\ASIOSDK
cargo build --release -p ownaudio-ffi --features asio
```

Without `--features asio`, calling `ownaudio_v1_engine_create_with_host` with `OwnHostApi::Asio` returns `HostApiNotAvailable` (10).

### Output Artifacts

After `cargo build --release`:

| Platform | Artifacts |
|----------|-----------|
| Windows | `target/release/ownaudio_ffi.dll`, `ownaudio_ffi.dll.lib`, `ownaudio_ffi.lib` |
| Linux | `target/release/libownaudio_ffi.so`, `libownaudio_ffi.a` |
| macOS | `target/release/libownaudio_ffi.dylib`, `libownaudio_ffi.a` |

### C Header

`cbindgen` generates `include/ownaudio_ffi.h` during the build. This header declares all exported types and functions and is the authoritative source for C/C++ consumers.

---

## ABI Contract

### ABI Version

```c
uint32_t ownaudio_v1_get_abi_version();
```

Returns the integer ABI version (currently `1`). Call this on library load and fail fast if the version does not match what you compiled against.

### Error Codes

All functions that can fail return `int32_t`. Zero means success.

| Code | Name | Meaning |
|------|------|---------|
| 0 | `Success` | Operation succeeded |
| 1 | `DeviceNotFound` | No audio device matching the request |
| 2 | `DeviceEnumerationFailed` | OS failed to enumerate devices |
| 3 | `UnsupportedConfig` | Sample rate, channel count, or format rejected by device |
| 4 | `StreamBuildFailed` | Could not open the audio stream |
| 5 | `StreamControlFailed` | Play/pause operation failed |
| 6 | `NullPointer` | A required pointer argument was null |
| 7 | `InvalidHandle` | Handle is invalid or has already been freed |
| 8 | `InternalPanic` | Rust panic was caught at the FFI boundary |
| 9 | `InternalError` | Unexpected internal error |
| 10 | `HostApiNotAvailable` | Requested audio host API not supported on this platform |
| 11 | `AsioDriverNotFound` | ASIO driver not installed (Windows only) |

### Thread-Local Error Messages

After any non-zero return code, call:

```c
const char* ownaudio_v1_last_error_message();
```

Returns a UTF-8 null-terminated string with a human-readable description of the last error on the **current thread**. The pointer is valid until the next FFI call on the same thread. Do not free it.

### Handle Types

All handles are opaque pointers. Never dereference them directly.

| C Type | Description |
|--------|-------------|
| `OwnAudioEngineHandle*` | Audio engine instance |
| `OwnAudioOutputStreamHandle*` | Output audio stream |
| `OwnAudioInputStreamHandle*` | Input (capture) audio stream |
| `OwnAudioMixerHandle*` | Multi-track mixer |
| `OwnAudioTrackHandle*` | Individual mixer track (non-owning; owned by mixer) |
| `OwnAudioEffectHandle*` | Effect in a track's effect chain (non-owning; owned by mixer) |

**Ownership model:**

- `Engine`, `OutputStream`, `InputStream`, and `Mixer` handles are **owning**. You must call the corresponding `_destroy()` function exactly once.
- `Track` and `Effect` handles are **non-owning views** into the mixer. Call `_remove()` to detach them, then `_destroy()` to free the handle struct itself.
- All `_destroy()` functions are null-safe (no-op on null pointer).

### Memory Ownership

| Allocation | Who frees | How |
|-----------|-----------|-----|
| Device list (`OwnAudioDeviceInfo*`) | Caller | `ownaudio_v1_free_device_list(ptr, count)` |
| `name` field inside `OwnAudioDeviceInfo` | **Rust** — do NOT free individually | Freed by `ownaudio_v1_free_device_list` |
| `ownaudio_v1_last_error_message()` result | **Rust** — do NOT free | Valid until next FFI call on this thread |
| All other returned strings | **Rust** — do NOT free | Statically or thread-locally managed |
| Handle structs | Caller calls `_destroy()` | One call per handle |

---

## API Reference

### Version & Diagnostics

```c
uint32_t    ownaudio_v1_get_abi_version();
const char* ownaudio_v1_get_package_version();   // e.g. "0.1.0"
const char* ownaudio_v1_last_error_message();    // thread-local; do not free
```

### Engine Lifecycle

```c
// Create engine using the platform default audio host
int32_t ownaudio_v1_engine_create(OwnAudioEngineHandle** out_handle);

// Create engine with a specific host API (see OwnHostApi enum below)
int32_t ownaudio_v1_engine_create_with_host(
    OwnHostApi            host_api,
    OwnAudioEngineHandle** out_handle
);

// Destroy engine. Pass null for no-op.
void ownaudio_v1_engine_destroy(OwnAudioEngineHandle* handle);
```

### Device Enumeration

```c
typedef struct {
    const char* name;              // UTF-8; do NOT free individually
    bool        is_default_input;
    bool        is_default_output;
    uint16_t    max_input_channels;
    uint16_t    max_output_channels;
    uint32_t    default_sample_rate;
} OwnAudioDeviceInfo;

int32_t ownaudio_v1_list_output_devices(
    OwnAudioDeviceInfo** out_devices,  // set to allocated array
    size_t*              out_count
);

int32_t ownaudio_v1_list_input_devices(
    OwnAudioDeviceInfo** out_devices,
    size_t*              out_count
);

// Must be called to release the array returned above
void ownaudio_v1_free_device_list(OwnAudioDeviceInfo* devices, size_t count);
```

Pattern:

```c
OwnAudioDeviceInfo* devices;
size_t count;
if (ownaudio_v1_list_output_devices(&devices, &count) != 0) { /* handle error */ }

for (size_t i = 0; i < count; i++) {
    printf("%s | %u Hz\n", devices[i].name, devices[i].default_sample_rate);
}

ownaudio_v1_free_device_list(devices, count);
```

### Stream Configuration

```c
typedef enum {
    OwnAudioSampleFormat_F32 = 0,  // 32-bit IEEE float (preferred)
    OwnAudioSampleFormat_I16 = 1,  // Signed 16-bit integer
    OwnAudioSampleFormat_U16 = 2,  // Unsigned 16-bit integer
} OwnAudioSampleFormat;

typedef struct {
    uint32_t              sample_rate;         // Hz, e.g. 48000
    uint16_t              channels;            // 1 = mono, 2 = stereo
    OwnAudioSampleFormat  sample_format;
    uint32_t              buffer_size_frames;  // 0 = platform default
} OwnAudioStreamConfig;
```

### Output Streams

```c
// Callback type — see Callback Signatures section
typedef void (*OwnAudioOutputCallback)(
    float*   buffer,       // interleaved samples to fill
    size_t   frame_count,  // frames per channel
    uint16_t channels,
    void*    user_data
);

int32_t ownaudio_v1_open_output_stream(
    OwnAudioEngineHandle*        engine,
    const char*                  device_name,  // NULL = system default
    const OwnAudioStreamConfig*  config,
    OwnAudioOutputCallback       callback,
    void*                        user_data,
    OwnAudioOutputStreamHandle** out_stream
);

int32_t ownaudio_v1_output_stream_play(OwnAudioOutputStreamHandle*  stream);
int32_t ownaudio_v1_output_stream_pause(OwnAudioOutputStreamHandle* stream);
void    ownaudio_v1_output_stream_destroy(OwnAudioOutputStreamHandle* stream);
```

### Input Streams

```c
typedef void (*OwnAudioInputCallback)(
    const float* buffer,      // interleaved captured samples
    size_t       frame_count,
    uint16_t     channels,
    void*        user_data
);

int32_t ownaudio_v1_open_input_stream(
    OwnAudioEngineHandle*       engine,
    const char*                 device_name,   // NULL = system default
    const OwnAudioStreamConfig* config,
    OwnAudioInputCallback       callback,
    void*                       user_data,
    OwnAudioInputStreamHandle** out_stream
);

int32_t ownaudio_v1_input_stream_play(OwnAudioInputStreamHandle*  stream);
int32_t ownaudio_v1_input_stream_pause(OwnAudioInputStreamHandle* stream);
void    ownaudio_v1_input_stream_destroy(OwnAudioInputStreamHandle* stream);
```

### Host API Selection

```c
typedef enum {
    OwnHostApi_Wasapi    = 0,  // Windows (default on Windows)
    OwnHostApi_Asio      = 1,  // Windows ASIO (requires --features asio)
    OwnHostApi_CoreAudio = 2,  // macOS (default on macOS)
    OwnHostApi_Alsa      = 3,  // Linux (default on Linux)
} OwnHostApi;
```

Requesting a host API that is not compiled in or not available on the current platform returns error code `10` (`HostApiNotAvailable`).

### Mixer Lifecycle

```c
int32_t ownaudio_v1_mixer_create(
    float             sample_rate,  // Hz, e.g. 48000.0
    uint16_t          channels,     // 1 or 2
    OwnAudioMixerHandle** out_mixer
);

void ownaudio_v1_mixer_destroy(OwnAudioMixerHandle* mixer);
```

The mixer owns all tracks and effects added to it. Destroying the mixer destroys everything it contains.

### Track Management

```c
// Add a track to the mixer; returns a non-owning handle
int32_t ownaudio_v1_track_create(
    OwnAudioMixerHandle*  mixer,
    OwnAudioTrackHandle** out_track
);

// Remove track from mixer (but handle struct is still alive; call _destroy after)
int32_t ownaudio_v1_track_remove(
    OwnAudioMixerHandle* mixer,
    OwnAudioTrackHandle* track
);

// Free the handle struct (call after _remove, or if you never added it)
void ownaudio_v1_track_destroy(OwnAudioTrackHandle* track);

// Transport
int32_t ownaudio_v1_track_play(OwnAudioTrackHandle*  track);
int32_t ownaudio_v1_track_pause(OwnAudioTrackHandle* track);
int32_t ownaudio_v1_track_stop(OwnAudioTrackHandle*  track);
int32_t ownaudio_v1_track_seek(OwnAudioTrackHandle*  track, uint64_t sample_position);

// Parameters
int32_t ownaudio_v1_track_set_gain(  OwnAudioTrackHandle* track, float gain);      // 1.0 = unity
int32_t ownaudio_v1_track_set_tempo( OwnAudioTrackHandle* track, float ratio);     // 0.25–4.0
int32_t ownaudio_v1_track_set_pitch( OwnAudioTrackHandle* track, float semitones); // -24 to +24
int32_t ownaudio_v1_track_set_mute(  OwnAudioTrackHandle* track, float muted);     // 0.0=unmuted, 1.0=muted
```

**Track removal pattern:**

```c
// 1. Detach from mixer
ownaudio_v1_track_remove(mixer, track);

// 2. Free the handle
ownaudio_v1_track_destroy(track);
track = NULL;
```

### Effects

```c
// Add effect to a track
int32_t ownaudio_v1_track_add_effect(
    OwnAudioMixerHandle*   mixer,
    OwnAudioTrackHandle*   track,
    uint32_t               effect_type,   // see EffectType table below
    float                  sample_rate,   // Hz
    OwnAudioEffectHandle** out_effect
);

// Remove effect from track (handle is still alive; call _destroy after)
int32_t ownaudio_v1_effect_remove(
    OwnAudioMixerHandle*  mixer,
    OwnAudioTrackHandle*  track,
    OwnAudioEffectHandle* effect
);

// Free the handle struct
void ownaudio_v1_effect_destroy(OwnAudioEffectHandle* effect);

// Parameter access
int32_t ownaudio_v1_effect_set_param(
    OwnAudioMixerHandle*  mixer,
    OwnAudioEffectHandle* effect,
    uint32_t              param_id,
    float                 value
);

int32_t ownaudio_v1_effect_get_param(
    OwnAudioMixerHandle*  mixer,
    OwnAudioEffectHandle* effect,
    uint32_t              param_id,
    float*                out_value
);
```

**Effect type IDs:**

| Value | Name |
|-------|------|
| 0 | Reverb |
| 1 | Equalizer (10-band ISO) |
| 2 | Compressor |
| 3 | Limiter |
| 4 | Delay |
| 5 | Chorus |
| 6 | Distortion |
| 7 | Overdrive |
| 8 | Flanger |
| 9 | Phaser |
| 10 | Rotary |
| 11 | AutoGain |
| 12 | Enhancer |
| 13 | Gate |
| 14 | PitchShift |
| 15 | DynamicAmp |
| 16 | Equalizer30 (30-band 1/3-octave) |

**Universal parameter IDs (all effects):**

| ID | Meaning | Range |
|----|---------|-------|
| 0 | Enabled | `0.0` = bypass, `1.0` = active |
| 1 | Dry/Wet mix | `0.0`–`1.0` |

Effect-specific parameter IDs start at `2`. See `ownaudio-core` README for per-effect tables.

---

## Callback Signatures

Callbacks are called from the **audio thread** — a high-priority OS thread. The same real-time constraints that apply to `ownaudio-core` apply here:

- **No heap allocation** inside the callback.
- **No blocking locks** (mutex, condvar, critical section).
- **No blocking I/O** (file, socket, etc.).
- **Must complete within the buffer duration** (e.g. ~10 ms for a 512-frame 48 kHz stream).

```c
// Output: fill `frame_count × channels` interleaved float samples into `buffer`
typedef void (*OwnAudioOutputCallback)(
    float*   buffer,
    size_t   frame_count,
    uint16_t channels,
    void*    user_data
);

// Input: read `frame_count × channels` interleaved float samples from `buffer`
typedef void (*OwnAudioInputCallback)(
    const float* buffer,
    size_t       frame_count,
    uint16_t     channels,
    void*        user_data
);
```

`user_data` is the opaque pointer you passed to `open_output_stream` / `open_input_stream`. Its lifetime must extend beyond the stream's lifetime. Thread safety of `user_data` access is the caller's responsibility.

**Interleaving:** Samples are interleaved across channels. For stereo: `[L0, R0, L1, R1, …]`.

---

## C# Integration Example

```csharp
using System.Runtime.InteropServices;

internal static partial class OwnAudioFFI
{
    private const string Lib = "ownaudio_ffi";

    [LibraryImport(Lib, EntryPoint = "ownaudio_v1_engine_create")]
    internal static partial int EngineCreate(out nint handle);

    [LibraryImport(Lib, EntryPoint = "ownaudio_v1_engine_destroy")]
    internal static partial void EngineDestroy(nint handle);

    [LibraryImport(Lib, EntryPoint = "ownaudio_v1_open_output_stream")]
    internal static partial int OpenOutputStream(
        nint                 engine,
        nint                 deviceName,   // IntPtr.Zero for default
        in StreamConfig      config,
        nint                 callback,     // function pointer
        nint                 userData,
        out nint             outStream
    );

    [LibraryImport(Lib, EntryPoint = "ownaudio_v1_output_stream_play")]
    internal static partial int OutputStreamPlay(nint stream);

    [LibraryImport(Lib, EntryPoint = "ownaudio_v1_output_stream_destroy")]
    internal static partial void OutputStreamDestroy(nint stream);

    [LibraryImport(Lib, EntryPoint = "ownaudio_v1_last_error_message")]
    internal static partial nint LastErrorMessage();

    [StructLayout(LayoutKind.Sequential)]
    internal struct StreamConfig
    {
        public uint   SampleRate;
        public ushort Channels;
        public int    SampleFormat;    // 0 = F32, 1 = I16, 2 = U16
        public uint   BufferSizeFrames;
    }
}
```

```csharp
// Usage
var config = new OwnAudioFFI.StreamConfig
{
    SampleRate       = 48_000,
    Channels         = 2,
    SampleFormat     = 0,  // F32
    BufferSizeFrames = 0,  // platform default
};

int result = OwnAudioFFI.EngineCreate(out nint engine);
if (result != 0)
{
    string msg = Marshal.PtrToStringUTF8(OwnAudioFFI.LastErrorMessage()) ?? "unknown error";
    throw new InvalidOperationException($"Engine create failed ({result}): {msg}");
}

// Keep the delegate alive for the stream's lifetime!
AudioOutputCallback callback = (buffer, frameCount, channels, userData) =>
{
    // fill interleaved float samples
};
nint callbackPtr = Marshal.GetFunctionPointerForDelegate(callback);

result = OwnAudioFFI.OpenOutputStream(engine, IntPtr.Zero, in config, callbackPtr, IntPtr.Zero, out nint stream);
if (result != 0) { /* handle */ }

OwnAudioFFI.OutputStreamPlay(stream);
// ...
OwnAudioFFI.OutputStreamDestroy(stream);
OwnAudioFFI.EngineDestroy(engine);
```

---

## Common Pitfalls

**Delegate garbage collection:**
In C#, always hold a strong reference (field or static) to the delegate wrapping your callback. If the GC collects the delegate while the stream is active, the native callback pointer becomes dangling and will crash.

```csharp
// BAD — delegate may be collected
OwnAudioFFI.OpenOutputStream(..., Marshal.GetFunctionPointerForDelegate(
    (OutputCallback)((buf, n, ch, ud) => { })), ...);

// GOOD — field keeps delegate alive
private AudioOutputCallback _callback = (buf, n, ch, ud) => { };
// store _callback on the instance; use Marshal.GetFunctionPointerForDelegate(_callback)
```

**`_destroy()` before `_remove()` for tracks and effects:**
A `Track` handle is non-owning. If you call `ownaudio_v1_track_destroy` without first calling `ownaudio_v1_track_remove`, the track data remains inside the mixer and leaks. Always remove first, then destroy the handle.

**Checking ABI version on load:**
Call `ownaudio_v1_get_abi_version()` immediately after loading the library. The current ABI version is `1`. If the value does not match your compiled-against version, abort — the API layout may be incompatible.

**Null device name = default device:**
Pass a null pointer (or `IntPtr.Zero` in C#) as `device_name` to select the OS default device. Passing an empty string is not equivalent and may fail with `DeviceNotFound`.

**Buffer sizes are in frames, not samples:**
`OwnAudioStreamConfig.buffer_size_frames` is per-channel frame count. Total float values in the callback buffer = `frame_count × channels`.

---

## Development Tools

This project is developed with the following tools:

| | |
|:--:|:--|
| ![Claude Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/claude.svg) | **Anthropic** — Claude Code |
| ![Visual Studio Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/vscode.svg) | **Microsoft** — Visual Studio Code |
| ![Visual Studio 2022](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/visualstudio.svg) | **Microsoft** — Visual Studio 2022 |
| ![Rider](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/rider.svg) | **JetBrains** — Rider |
