# OwnAudioRust — Native Layer

The **Native** layer contains the raw `LibraryImport`-based P/Invoke bindings to the
`ownaudio_ffi` shared library compiled from Rust. It sits at the bottom of the managed stack
and is never used directly by application code.

## Role in the Stack

```
HighLevel  ← developer-facing API (players, recorders, device manager)
  Safe     ← handles, error mapping, callback marshallers
 Native    ← THIS LAYER: raw LibraryImport / P/Invoke to ownaudio_ffi
  Rust     ← ownaudio-ffi crate (cpal + Symphonia + native DSP)
```

---

## Project Structure

```
Native/
└── RustAudio/
    ├── Interop/                        # LibraryImport P/Invoke declarations
    │   ├── NativeLibraryLoader.cs      # Cross-platform ownaudio_ffi loader
    │   ├── OwnAudioNative.Abi.cs       # ABI version check
    │   ├── OwnAudioNative.Bpm.cs       # BPM detector calls
    │   ├── OwnAudioNative.Callback.cs  # Audio callback helpers
    │   ├── OwnAudioNative.Decoder.cs   # Streaming decoder calls
    │   ├── OwnAudioNative.Device.cs    # Device enumeration calls
    │   ├── OwnAudioNative.Effects.cs   # DSP effect calls
    │   ├── OwnAudioNative.Engine.cs    # Engine create/destroy calls
    │   ├── OwnAudioNative.ErrorCode.cs # Error code enum
    │   ├── OwnAudioNative.HostApi.cs   # Host API enum (NativeHostApi)
    │   ├── OwnAudioNative.Stream.cs    # Output/input stream calls
    │   └── OwnAudioNative.Track.cs     # Multi-track mixer / track calls
    │
    ├── Enums/
    │   └── EffectType.cs               # Native EffectType enum
    │
    └── Structs/
        ├── NativeAudioStreamInfo.cs    # Decoder stream-info layout
        ├── NativeDeviceInfo.cs         # Device descriptor layout
        ├── NativeStreamConfig.cs       # Stream-open config layout
        └── NativeStreamErrorKind.cs    # Stream error discriminant
```

---

## Native Library (`ownaudio_ffi`)

The managed assembly loads a single shared library:

| Platform | File |
|----------|------|
| Windows x64 | `ownaudio_ffi.dll` |
| Windows x86 | `ownaudio_ffi.dll` |
| Windows ARM64 | `ownaudio_ffi.dll` |
| Linux x64 | `libownaudio_ffi.so` |
| Linux ARM | `libownaudio_ffi.so` |
| Linux ARM64 | `libownaudio_ffi.so` |
| macOS x64 | `libownaudio_ffi.dylib` |
| macOS ARM64 | `libownaudio_ffi.dylib` |
| Android ARM (armeabi-v7a) | `libownaudio_ffi.so` |
| Android ARM64 (arm64-v8a) | `libownaudio_ffi.so` |
| Android x64 (x86_64) | `libownaudio_ffi.so` |

The library is shipped inside the NuGet package under `runtimes/{rid}/native/` and copied
to the application output directory on build. `NativeLibraryLoader` resolves the correct
path at runtime using `NativeLibrary.Load`.

### Backend

The Rust crate uses **cpal** for audio I/O and **Symphonia** for decoding. Both are statically
linked into the shared library — no additional system dependencies are required on any platform.

---

## P/Invoke Surface

All P/Invoke declarations follow the `ownaudio_v1_*` naming convention. They are partitioned
into partial classes for maintainability:

| File | Prefix | Coverage |
|------|--------|----------|
| `OwnAudioNative.Abi.cs` | `ownaudio_v1_get_abi_version` | ABI compatibility check |
| `OwnAudioNative.Engine.cs` | `ownaudio_v1_engine_*` | Engine create / destroy / host API |
| `OwnAudioNative.Device.cs` | `ownaudio_v1_list_*` | Device enumeration and list free |
| `OwnAudioNative.Stream.cs` | `ownaudio_v1_*_stream_*` | Output/input stream open / play / pause / destroy |
| `OwnAudioNative.Callback.cs` | `ownaudio_v1_*_callback` | Callback function-pointer registration |
| `OwnAudioNative.Decoder.cs` | `ownaudio_v1_decoder_*` | Streaming decoder open / read / seek / EOF / info |
| `OwnAudioNative.Track.cs` | `ownaudio_v1_track_*`, `ownaudio_v1_mixer_*` | Multi-track mixer and track management |
| `OwnAudioNative.Effects.cs` | `ownaudio_v1_effect_*`, `ownaudio_v1_master_effect_*` | DSP effect create / set / get / destroy |
| `OwnAudioNative.Bpm.cs` | `ownaudio_v1_bpm_*` | BPM detector create / feed / query / destroy |
| `OwnAudioNative.ErrorCode.cs` | — | `NativeErrorCode` integer enum |
| `OwnAudioNative.HostApi.cs` | — | `NativeHostApi` integer enum |

---

## Native Structs

Blittable structs mapped directly to their C-compatible Rust counterparts via `[StructLayout(LayoutKind.Sequential)]`:

| Struct | Fields | Use |
|--------|--------|-----|
| `NativeDeviceInfo` | `Name`, `IsDefaultInput`, `IsDefaultOutput`, `MaxInputChannels`, `MaxOutputChannels`, `DefaultSampleRate` | Returned by device enumeration |
| `NativeStreamConfig` | `SampleRate`, `Channels`, `Format`, `BufferSizeFrames` | Passed when opening a stream |
| `NativeAudioStreamInfo` | `Channels`, `SampleRate`, `DurationMs`, `BitDepth` | Returned by decoder info query |
| `NativeStreamErrorKind` | discriminant enum | Stream error reporting |

---

## Key API Groups

### Engine Lifecycle

```csharp
// Create engine (platform default host)
OwnAudioNative.ownaudio_v1_engine_create(out IntPtr handle);

// Create engine with explicit host API
OwnAudioNative.ownaudio_v1_engine_create_with_host(NativeHostApi api, out IntPtr handle);

// Destroy engine
OwnAudioNative.ownaudio_v1_engine_destroy(IntPtr handle);
```

### ABI Check

```csharp
// Returns the uint ABI version baked into the native binary
uint version = OwnAudioNative.ownaudio_v1_get_abi_version();
```

The managed constant `AudioEngine.ExpectedAbiVersion = 1u` must match this value.
A mismatch results in `AbiVersionMismatchException` thrown before any audio subsystem
is touched.

### Device Enumeration

```csharp
OwnAudioNative.ownaudio_v1_list_output_devices(out IntPtr ptr, out nuint count);
OwnAudioNative.ownaudio_v1_list_input_devices(out IntPtr ptr, out nuint count);
OwnAudioNative.ownaudio_v1_free_device_list(IntPtr ptr, nuint count);
```

The returned pointer points to a contiguous array of `NativeDeviceInfo` structs.
The Safe layer marshals them into `AudioDevice` objects and then calls `free_device_list`.

### Stream Management

```csharp
// Output stream with managed callback
OwnAudioNative.ownaudio_v1_output_stream_open(..., out IntPtr stream);

// Output stream driven by native mixer (no managed callback)
OwnAudioNative.ownaudio_v1_output_stream_open_mixer(..., out IntPtr stream);

// Input stream with managed callback
OwnAudioNative.ownaudio_v1_input_stream_open(..., out IntPtr stream);

OwnAudioNative.ownaudio_v1_output_stream_play(IntPtr stream);
OwnAudioNative.ownaudio_v1_output_stream_pause(IntPtr stream);
OwnAudioNative.ownaudio_v1_output_stream_destroy(IntPtr stream);
// (input stream mirrors these calls)
```

### Streaming Decoder

```csharp
OwnAudioNative.ownaudio_v1_decoder_open(
    string filePath, uint targetRate, uint targetChannels,
    float prefetchSeconds, out IntPtr handle);

OwnAudioNative.ownaudio_v1_decoder_read(
    IntPtr handle, ref float destination, nuint count, out nuint written);

OwnAudioNative.ownaudio_v1_decoder_seek(IntPtr handle, ulong framePosition);
OwnAudioNative.ownaudio_v1_decoder_is_eof(IntPtr handle, out bool eof);
OwnAudioNative.ownaudio_v1_decoder_get_stream_info(IntPtr handle, out NativeAudioStreamInfo info);
OwnAudioNative.ownaudio_v1_decoder_destroy(IntPtr handle);
```

### Multi-Track Mixer

```csharp
// Mixer
OwnAudioNative.ownaudio_v1_mixer_create(float sampleRate, ushort channels, out IntPtr mixer);
OwnAudioNative.ownaudio_v1_mixer_play_all(IntPtr mixer);
OwnAudioNative.ownaudio_v1_mixer_pause_all(IntPtr mixer);
OwnAudioNative.ownaudio_v1_mixer_stop_all(IntPtr mixer);
OwnAudioNative.ownaudio_v1_mixer_set_master_gain(IntPtr mixer, float gain);
OwnAudioNative.ownaudio_v1_mixer_get_master_peaks(IntPtr mixer, out float left, out float right);
OwnAudioNative.ownaudio_v1_mixer_destroy(IntPtr mixer);

// Tracks
OwnAudioNative.ownaudio_v1_track_create(IntPtr mixer, out IntPtr track);
OwnAudioNative.ownaudio_v1_track_remove(IntPtr mixer, IntPtr track);
OwnAudioNative.ownaudio_v1_track_set_ring_source(IntPtr mixer, IntPtr track, nuint capacity, out IntPtr source);
OwnAudioNative.ownaudio_v1_track_open_file(IntPtr mixer, IntPtr track, string path, ...);
OwnAudioNative.ownaudio_v1_track_open_memory(IntPtr mixer, IntPtr track, in float samples, ...);
OwnAudioNative.ownaudio_v1_track_open_input(IntPtr engine, IntPtr mixer, IntPtr track, ...);
```

### DSP Effects

```csharp
// Per-track effect
OwnAudioNative.ownaudio_v1_effect_add(IntPtr mixer, IntPtr track, uint effectType, float sampleRate, out IntPtr effect);
OwnAudioNative.ownaudio_v1_effect_set_param(IntPtr effect, uint paramId, float value);
OwnAudioNative.ownaudio_v1_effect_get_param(IntPtr effect, uint paramId, out float value);
OwnAudioNative.ownaudio_v1_effect_remove(IntPtr mixer, IntPtr track, IntPtr effect);

// Master bus effect
OwnAudioNative.ownaudio_v1_mixer_add_master_effect(IntPtr mixer, uint effectType, float sampleRate, out IntPtr effect);
OwnAudioNative.ownaudio_v1_mixer_remove_master_effect(IntPtr mixer, IntPtr effect);
```

### BPM Detector

```csharp
OwnAudioNative.ownaudio_v1_bpm_create(uint channels, uint sampleRate, out IntPtr handle);
OwnAudioNative.ownaudio_v1_bpm_input_samples(IntPtr handle, ref float samples, nuint frames, nuint count);
OwnAudioNative.ownaudio_v1_bpm_get_bpm(IntPtr handle, out float bpm);
OwnAudioNative.ownaudio_v1_bpm_destroy(IntPtr handle);
```

---

## Error Convention

Every native function returns an `int` error code mapped by the `NativeErrorCode` enum.
A non-zero return value means failure; the Safe layer converts it to a typed exception via
`ErrorCodeMapper.ThrowIfError()`. The native binary also provides:

```csharp
// Retrieves the last error message string (UTF-8)
OwnAudioNative.ownaudio_v1_last_error_message();
```

---

## Build Requirements

- **.NET 10.0+** SDK
- **Native binary**: the `ownaudio_ffi` shared library for the target platform
  (shipped in `runtimes/{rid}/native/` inside the NuGet package)

Mobile builds require `BuildMobile=true`:

```bash
dotnet build -p:BuildMobile=true -f net10.0-android
dotnet build -p:BuildMobile=true -f net10.0-ios
```

---

## Related Documentation

- [OwnAudioSharp Documentation](https://modernmube.github.io/OwnAudioSharp/)
- [Safe Layer](../Safe/README.md)
- [HighLevel Layer](../HighLevel/README.md)

## License

Copyright © 2025 Ownaudio Team  
Part of the OwnAudioSharp project.
