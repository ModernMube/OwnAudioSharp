# OwnAudioRust — Safe Layer

The **Safe** layer is the first managed C# abstraction over the native Rust FFI. It sits directly on top of the raw `OwnAudioNative` P/Invoke bindings and converts them into a safe, typed, and resource-managed API.

## Role in the Stack

```
HighLevel  ← developer-facing API (players, recorders, device manager)
  Safe     ← this layer: handles, error mapping, callback marshalling
 Native    ← raw LibraryImport / P/Invoke to ownaudio_ffi.dll
  Rust     ← ownaudio-ffi + ownaudio-core
```

The Safe layer does four things and nothing more:

1. **Manages native handle lifetimes** via `SafeHandle` subclasses, so the GC can clean up even if `Dispose()` is never called.
2. **Maps integer error codes to typed exceptions** (`OwnAudioException` hierarchy) so callers get meaningful errors.
3. **Marshals audio callbacks** between managed delegates and native C function pointers without per-call allocation.
4. **Validates parameters** at the boundary so the native layer never receives invalid inputs.

## Handles

Every native object is wrapped in a `SafeHandle` subclass:

| Handle | Wraps |
|--------|-------|
| `AudioEngineHandle` | Native audio engine context |
| `AudioOutputStreamHandle` | Output audio stream |
| `AudioInputStreamHandle` | Input / capture stream |
| `MixerHandle` | Multi-track mixer |
| `TrackHandle` | Individual mixer track (non-owning) |
| `EffectHandle` | DSP effect inside a track (non-owning) |

`SafeHandle.ReleaseHandle()` calls the corresponding `ownaudio_v1_*_destroy` function. Handles are ref-counted and disposed correctly even under exceptional unwind.

## Key Types

**`AudioEngine`** — Entry point. Call `AudioEngine.Create()` (or `Create(hostApi)` for ASIO / CoreAudio). Checks ABI version at construction and throws `AbiVersionMismatchException` immediately if the native binary doesn't match. Provides `OpenOutputStream` and `OpenInputStream` methods that return the stream objects below.

**`AudioOutputStream` / `AudioInputStream`** — Low-level stream objects. Streams are created in a **paused state**; call `Play()` to start. `Pause()` suspends callbacks without destroying the stream.

**`AudioDevice`** — Immutable snapshot of a device returned by enumeration (name, channel counts, default sample rate, default flags). Use the `Name` string when opening a specific device.

**`AudioStreamConfig`** — Validated stream configuration: sample rate (8 000–192 000 Hz), channels (1–32), `SampleFormat` (F32 / I16 / U16), buffer size in frames (0 = platform default, otherwise 16–8 192). Constructor throws `ArgumentOutOfRangeException` on bad values.

## Error Handling

All native calls go through `ErrorCodeMapper.ThrowIfError()`. The mapper reads the thread-local error message from the native side (`ownaudio_v1_last_error_message`) and includes it in the exception message.

Exception hierarchy:

```
OwnAudioException               (base; carries ErrorCode : AudioEngineErrorCode)
 ├─ DeviceException             code 1, 2
 ├─ StreamException             code 3, 4, 5
 ├─ HostApiNotAvailableException  code 10
 ├─ AsioDriverNotFoundException   code 11
 └─ AbiVersionMismatchException   code 12
```

## Callback Marshalling

`AudioOutputCallbackMarshaller` and `AudioInputCallbackMarshaller` bridge managed delegates to unmanaged C function pointers.

- The delegate is pinned with `GCHandle.Alloc(…, GCHandleType.Normal)` for the entire stream lifetime so the GC never relocates it.
- The marshaller receives the raw `float*` pointer and wraps it in a `Span<float>` (output) or `ReadOnlySpan<float>` (input) before calling the managed delegate.
- If the delegate throws, the buffer is silenced and the exception is re-raised on a `ThreadPool` thread via the stream's `CallbackError` event. **The real-time audio thread itself never faults.**

The callback args structs (`AudioOutputCallbackArgs`, `AudioInputCallbackArgs`) are `readonly ref struct` types — stack-only, zero allocation. **Do not capture them beyond the callback scope.**

## Threading Notes

- `AudioEngine.Create()` and `Dispose()` are thread-safe.
- Stream `Play()` / `Pause()` / `Dispose()` must not be called concurrently on the same instance.
- The audio callback runs on a high-priority native OS thread. **No heap allocation, no locking, no I/O inside the callback.**
- `TrackHandle` and `EffectHandle` are non-owning. Call `ownaudio_v1_track_remove` / `effect_remove` before calling `Dispose()` on the handle.
