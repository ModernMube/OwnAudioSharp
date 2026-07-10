# OwnAudioRust — Safe Layer

The **Safe** layer is the first managed C# abstraction over the native Rust FFI. It sits directly
on top of the raw `OwnAudioNative` P/Invoke bindings and converts them into a safe, typed,
and resource-managed API.

## Role in the Stack

```
HighLevel  ← developer-facing API (players, recorders, device manager)
  Safe     ← THIS LAYER: handles, error mapping, callback marshallers
 Native    ← raw LibraryImport / P/Invoke to ownaudio_ffi
  Rust     ← ownaudio-ffi + ownaudio-core (cpal + Symphonia)
```

The Safe layer does four things and nothing more:

1. **Manages native handle lifetimes** via `SafeHandle` subclasses so the GC can clean up
   even if `Dispose()` is never explicitly called.
2. **Maps integer error codes to typed exceptions** (`OwnAudioException` hierarchy) so callers
   receive meaningful, structured errors.
3. **Marshals audio callbacks** between managed delegates and native C function pointers
   without per-call heap allocation.
4. **Validates parameters** at the managed/native boundary so the native layer never receives
   invalid inputs.

**When should you use the Safe layer directly?**

- You are building custom playback or capture logic and need full control over the callback.
- The HighLevel layer does not fit your architecture.
- You only need the raw audio stream callback without a high-level state machine.
- You need low-level streaming decoding, BPM detection, or direct stream management.

Most developers should start with the HighLevel layer. If you are unsure, start there.

---

## Table of Contents

1. [Quick Start — Playback](#1-quick-start--playback)
2. [Quick Start — Recording](#2-quick-start--recording)
3. [AudioEngine](#3-audioengine)
4. [AudioStreamConfig](#4-audiostreamconfig)
5. [SampleFormat](#5-sampleformat)
6. [AudioDevice](#6-audiodevice)
7. [AudioOutputStream](#7-audiooutputstream)
8. [AudioInputStream](#8-audioinputstream)
9. [Callbacks and Callback Arguments](#9-callbacks-and-callback-arguments)
10. [StreamingAudioDecoder](#10-streamingaudiodecoder)
11. [BpmDetect](#11-bpmdetect)
12. [Handles](#12-handles)
13. [Error Handling and Exceptions](#13-error-handling-and-exceptions)
14. [Threading and Real-Time Rules](#14-threading-and-real-time-rules)
15. [Resource Management](#15-resource-management)
16. [ABI Version Check](#16-abi-version-check)

---

## 1. Quick Start — Playback

The example below plays a 440 Hz sine wave for 2 seconds through the default output device.

```csharp
using Ownaudio.Safe;
using Ownaudio.Safe.Callbacks;

// 1. Create the engine — also verifies the ABI version
using var engine = AudioEngine.Create();

// 2. Stream configuration (44 100 Hz, stereo, float32)
var config = new AudioStreamConfig(
    sampleRate: 44_100,
    channels: 2,
    sampleFormat: SampleFormat.F32,
    bufferSizeFrames: 0);   // 0 = let the platform choose

// 3. Phase accumulator for the sine wave (state preserved across callbacks)
double phase = 0.0;
const double frequency = 440.0;   // A4
const double amplitude = 0.3;

// 4. Open the output stream — starts in the paused state
using var stream = engine.OpenOutputStream(
    device: null,   // null = system default output device
    config: config,
    callback: (in AudioOutputCallbackArgs args) =>
    {
        // This method runs on the real-time audio thread!
        // DO NOT: allocate with new, lock, Console.Write, file I/O.
        double step = 2.0 * Math.PI * frequency / config.SampleRate;

        for (int frame = 0; frame < args.FrameCount; frame++)
        {
            float sample = (float)(Math.Sin(phase) * amplitude);
            phase += step;

            // Write the same sample to every channel (stereo)
            for (int ch = 0; ch < args.Channels; ch++)
            {
                args.Buffer[frame * args.Channels + ch] = sample;
            }
        }
    });

// 5. Subscribe to callback errors (optional but recommended)
stream.CallbackError += (_, ex) =>
    Console.WriteLine($"Audio callback error: {ex.Message}");

// 6. Start playback — callbacks begin firing
stream.Play();

Console.WriteLine("Playing for 2 seconds...");
Thread.Sleep(2000);

// 7. Pause (the stream remains alive and can be restarted with Play())
stream.Pause();

// 8. The `using` statement calls Dispose() automatically
```

---

## 2. Quick Start — Recording

```csharp
using Ownaudio.Safe;
using Ownaudio.Safe.Callbacks;
using System.Threading.Channels;

using var engine = AudioEngine.Create();

var config = new AudioStreamConfig(
    sampleRate: 44_100,
    channels: 1,    // mono capture
    sampleFormat: SampleFormat.F32);

// Channel<T> transfers data lock-free from the audio thread to a processing thread
var channel = Channel.CreateBounded<float[]>(capacity: 64);

using var stream = engine.OpenInputStream(
    device: null,
    config: config,
    callback: (in AudioInputCallbackArgs args) =>
    {
        // Running on the real-time audio thread — lock-free operations only!
        // args.Buffer is only valid during this callback, so copy it immediately.
        var copy = args.Buffer.ToArray();
        channel.Writer.TryWrite(copy);   // lock-free, never blocks
    });

stream.Play();
Console.WriteLine("Recording for 3 seconds...");

// Processing thread — unlimited operations allowed here
var processor = Task.Run(async () =>
{
    await foreach (float[] buf in channel.Reader.ReadAllAsync())
    {
        // Write to WAV, run FFT, compute RMS, etc.
        Console.WriteLine($"  Buffer size: {buf.Length} samples");
    }
});

Thread.Sleep(3000);
stream.Pause();
channel.Writer.Complete();
await processor;
```

---

## 3. AudioEngine

**Namespace:** `Ownaudio.Safe`

`AudioEngine` is the single entry point of the Safe layer. It manages native engine
initialisation, device enumeration, and stream creation.

### Creating an Engine

```csharp
// Platform default backend (WASAPI / CoreAudio / ALSA)
using var engine = AudioEngine.Create();

// Specific host API
using var engine = AudioEngine.Create(hostApi: HostApi.Wasapi);
```

> `AudioEngine.Create()` verifies the ABI version as its very first step.
> If the loaded native binary does not match the managed code, it throws
> `AbiVersionMismatchException` immediately — before any audio subsystem is touched.

### Available HostApi Values

| Value | Platform | Notes |
|---|---|---|
| `null` (default) | All | Platform picks the best backend automatically |
| `HostApi.Wasapi` | Windows | Recommended Windows backend |
| `HostApi.Asio` | Windows | Low-latency; requires an ASIO driver |
| `HostApi.CoreAudio` | macOS | Default macOS backend |
| `HostApi.Alsa` | Linux | Default Linux backend |

> **ASIO note:** Only available when the native binary was compiled with ASIO support
> and an ASIO driver is installed (e.g. ASIO4ALL, RME, Focusrite).
> Without a driver, the engine throws `AsioDriverNotFoundException`.

### Device Enumeration

```csharp
IReadOnlyList<AudioDevice> outputs = engine.EnumerateOutputDevices();
IReadOnlyList<AudioDevice> inputs  = engine.EnumerateInputDevices();

foreach (AudioDevice dev in outputs)
{
    Console.WriteLine(dev);
    // Example: "Realtek HD Audio (in=0 out=2 @48000 Hz)"
}

// Find the system default
AudioDevice? defaultOut = outputs.FirstOrDefault(d => d.IsDefaultOutput);

// Find a specific device by name
AudioDevice? focusrite = outputs.FirstOrDefault(d => d.Name.Contains("Focusrite"));
```

### Opening Streams

```csharp
// Output stream (playback) with managed callback
AudioOutputStream outStream = engine.OpenOutputStream(device, config, outputCallback);

// Output stream driven by a native mixer (no managed callback — HighLevel path)
AudioOutputStream mixerStream = engine.OpenMixerOutputStream(mixerHandle, device, config);

// Input stream (capture) with managed callback
AudioInputStream inStream = engine.OpenInputStream(device, config, inputCallback);
```

Streams are always created in the **paused state** — call `.Play()` to start them.

### Disposal

```csharp
engine.Dispose();
```

> **Important:** Dispose all streams before disposing the engine. If the engine is destroyed
> first, a pending callback could access freed native memory.

---

## 4. AudioStreamConfig

**Namespace:** `Ownaudio.Safe`

Holds the parameters for opening an audio stream. Immutable — all values are validated in the
constructor, so an invalid configuration can never reach the native layer.

```csharp
var config = new AudioStreamConfig(
    sampleRate: 48_000,              // Hz; valid range: 8 000 – 192 000
    channels: 2,                      // channel count; valid range: 1 – 32
    sampleFormat: SampleFormat.F32,   // default when omitted
    bufferSizeFrames: 512);           // frames; 0 = platform default; non-zero: 16 – 8 192
```

### Properties

| Property | Type | Description |
|---|---|---|
| `SampleRate` | `int` | Sample rate in Hz |
| `Channels` | `int` | Number of channels (1 = mono, 2 = stereo) |
| `SampleFormat` | `SampleFormat` | Sample data format |
| `BufferSizeFrames` | `int` | Buffer size in frames (0 = automatic) |

### Choosing a Buffer Size

| `BufferSizeFrames` | Effect |
|---|---|
| `0` | Platform chooses the optimal value (recommended starting point) |
| `64 – 256` | Low latency (live synthesis, ASIO) — higher CPU load |
| `512 – 1024` | Balance between latency and CPU usage |
| `2048 – 8192` | High latency; stable on weak hardware |

### Common Configurations

```csharp
// CD quality, stereo
var cdStereo = new AudioStreamConfig(44_100, 2);

// Professional studio, low latency
var studio = new AudioStreamConfig(48_000, 2, SampleFormat.F32, 256);

// Mono microphone capture
var monoCapture = new AudioStreamConfig(44_100, 1);

// High-resolution stereo
var hiRes = new AudioStreamConfig(96_000, 2);
```

---

## 5. SampleFormat

**Namespace:** `Ownaudio.Safe`

```csharp
public enum SampleFormat
{
    F32 = 0,   // 32-bit IEEE 754 float [-1.0, +1.0] — RECOMMENDED
    I16 = 1,   // signed 16-bit integer [-32768, +32767]
    U16 = 2,   // unsigned 16-bit integer [0, 65535]
}
```

**Use `F32` unless you have a specific reason not to.** It covers the full `[-1.0, +1.0]`
range, is accurate enough for all DSP work, and all effects operate on float internally.

---

## 6. AudioDevice

**Namespace:** `Ownaudio.Safe`

An immutable device descriptor returned by `EnumerateOutputDevices()` /
`EnumerateInputDevices()`. All string data is copied from the native buffer during
construction, so the object remains valid after the native list is freed.

### Properties

| Property | Type | Description |
|---|---|---|
| `Name` | `string` | OS-provided device name — pass this to `OpenOutputStream` / `OpenInputStream` |
| `IsDefaultInput` | `bool` | True when this is the system default input device |
| `IsDefaultOutput` | `bool` | True when this is the system default output device |
| `MaxInputChannels` | `int` | Maximum number of input channels supported |
| `MaxOutputChannels` | `int` | Maximum number of output channels supported |
| `DefaultSampleRate` | `int` | Device preferred sample rate (e.g. 44 100 or 48 000) |

```csharp
// Select a specific device by name
AudioDevice? myDevice = engine.EnumerateOutputDevices()
    .FirstOrDefault(d => d.Name.Contains("Focusrite"));

// Open a stream on that device
using var stream = engine.OpenOutputStream(myDevice, config, callback);

// ToString() output: "Realtek HD Audio (in=0 out=2 @48000 Hz)"
Console.WriteLine(myDevice);
```

---

## 7. AudioOutputStream

**Namespace:** `Ownaudio.Safe`

A playback audio stream. Created exclusively by `AudioEngine.OpenOutputStream()` or
`AudioEngine.OpenMixerOutputStream()`.

### Methods

| Method | Description |
|---|---|
| `Play()` | Starts or resumes audio output. Callbacks begin firing on the audio thread. |
| `Pause()` | Pauses output without destroying the stream. Restartable with `Play()`. |
| `Dispose()` | Destroys the native stream and releases the callback pin. Safe to call multiple times. |

### Event

| Event | Description |
|---|---|
| `CallbackError` | Raised on a `ThreadPool` thread when the user callback throws an unhandled exception. The stream continues operating. |

```csharp
using var stream = engine.OpenOutputStream(null, config, MyCallback);

stream.CallbackError += (_, ex) =>
{
    // The exception was swallowed at the FFI boundary — log it here
    logger.LogError(ex, "Audio callback error");
};

stream.Play();
// ... audio plays ...
stream.Pause();
// ... can be restarted at any time:
stream.Play();
```

### Lifecycle Diagram

```
OpenOutputStream()
       │
       ▼
   [Paused] ─── Play() ──► [Playing]
      ▲                        │
      └──────── Pause() ───────┘
                    │
                 Dispose()
```

---

## 8. AudioInputStream

**Namespace:** `Ownaudio.Safe`

A capture audio stream. Created exclusively by `AudioEngine.OpenInputStream()`.
The interface is identical to `AudioOutputStream`.

### Methods

| Method | Description |
|---|---|
| `Play()` | Starts or resumes capture. Callbacks begin firing. |
| `Pause()` | Pauses capture without destroying the stream. |
| `Dispose()` | Destroys the native stream and releases the callback pin. |

### Event

| Event | Description |
|---|---|
| `CallbackError` | Raised when the capture callback throws an unhandled exception. |

---

## 9. Callbacks and Callback Arguments

### AudioOutputCallbackHandler

```csharp
public delegate void AudioOutputCallbackHandler(in AudioOutputCallbackArgs args);
```

Pass this delegate to `OpenOutputStream()`. The native audio thread calls it for
every buffer cycle — this is where you generate or copy audio data into the output buffer.

### AudioOutputCallbackArgs

| Property | Type | Description |
|---|---|---|
| `Buffer` | `Span<float>` | Output samples to fill. Pre-zeroed before each call. |
| `FrameCount` | `int` | Number of audio frames in this callback cycle |
| `Channels` | `int` | Number of interleaved channels per frame |

**Buffer size:** `Buffer.Length == FrameCount * Channels`

**Stereo buffer layout:**

```
Buffer[0] = frame 0, left channel
Buffer[1] = frame 0, right channel
Buffer[2] = frame 1, left channel
Buffer[3] = frame 1, right channel
...
Buffer[frame * Channels + channelIndex] = sample
```

```csharp
// Example: 440 Hz sine wave on stereo output
double phase = 0.0;
engine.OpenOutputStream(null, config, (in AudioOutputCallbackArgs args) =>
{
    double step = 2.0 * Math.PI * 440.0 / 44_100;
    for (int frame = 0; frame < args.FrameCount; frame++)
    {
        float s = (float)(Math.Sin(phase) * 0.3);
        phase += step;
        args.Buffer[frame * 2 + 0] = s;   // left
        args.Buffer[frame * 2 + 1] = s;   // right
    }
});
```

> **Important:** `Buffer` is a `Span<float>` that is only valid during the callback.
> Do not store or pass the `args` value outside the callback scope.

### AudioInputCallbackHandler

```csharp
public delegate void AudioInputCallbackHandler(in AudioInputCallbackArgs args);
```

### AudioInputCallbackArgs

| Property | Type | Description |
|---|---|---|
| `Buffer` | `ReadOnlySpan<float>` | Captured samples for this cycle (read-only) |
| `FrameCount` | `int` | Number of audio frames |
| `Channels` | `int` | Number of channels per frame |

```csharp
// Correct pattern: copy immediately, hand off via lock-free channel
engine.OpenInputStream(null, config, (in AudioInputCallbackArgs args) =>
{
    // The span expires after this callback returns — copy it first!
    float[] copy = args.Buffer.ToArray();

    // Pass to processing thread via lock-free channel
    myChannel.Writer.TryWrite(copy);

    // FORBIDDEN inside the callback: blocking I/O, lock, Console.Write, Task.Wait
});
```

---

## 10. StreamingAudioDecoder

**Namespace:** `Ownaudio.Safe`

Memory-efficient streaming audio file decoder backed by the native Rust (Symphonia) engine.
A dedicated native prefetch thread decodes the file incrementally into a lock-free ring buffer,
bounding memory usage to the prefetch size rather than the file size.

**Supported formats:** WAV, MP3, FLAC, OGG/Vorbis, AAC/M4A, AIFF (via Symphonia — no external deps).

```csharp
using var decoder = new StreamingAudioDecoder(
    filePath: "music.flac",
    targetSampleRate: 48_000,   // 0 = source rate
    targetChannels: 2,           // 0 = source channels
    prefetchSeconds: 2.0f);

// Stream metadata (after resampling/downmix if requested)
AudioStreamInfo info = decoder.StreamInfo;
Console.WriteLine($"Channels:  {info.Channels}");
Console.WriteLine($"Rate:      {info.SampleRate} Hz");
Console.WriteLine($"Duration:  {info.Duration}");   // TimeSpan.Zero when unknown
Console.WriteLine($"Bit depth: {info.BitDepth}");   // 0 for float/compressed

// Pre-allocate a reusable buffer (single allocation outside the loop)
float[] buffer = new float[4096];

while (!decoder.IsEndOfStream)
{
    int samplesRead = decoder.Read(buffer, offset: 0, count: buffer.Length);
    // or: int samplesRead = decoder.Read(buffer.AsSpan());
    ProcessAudio(buffer.AsSpan(0, samplesRead));
}

// Seek (non-blocking — prefetch thread performs the actual seek)
decoder.Seek(TimeSpan.FromSeconds(30));
decoder.Seek(framePosition: 0);   // seek by frame index
```

### Properties

| Property | Type | Description |
|---|---|---|
| `StreamInfo` | `AudioStreamInfo` | Decoded output metadata (channels, rate, duration, bit depth) |
| `IsEndOfStream` | `bool` | `true` once file is fully decoded and prefetch buffer is drained |

### Methods

| Method | Description |
|---|---|
| `Read(float[], int, int)` | Read decoded interleaved float samples into a buffer slice |
| `Read(Span<float>)` | Zero-allocation Span overload |
| `Seek(long framePosition)` | Non-blocking seek to output frame position |
| `Seek(TimeSpan position)` | Non-blocking seek to time position |
| `Dispose()` | Stops prefetch thread and releases native decoder |

---

## 11. BpmDetect

**Namespace:** `Ownaudio.Safe`

Offline BPM (tempo) estimator backed by the native Rust engine. Replaces the former
managed `SoundTouch.BpmDetect`. Feed audio frames and query the estimated BPM.

```csharp
using var bpmDetector = new BpmDetect(channels: 2, sampleRate: 44_100);

// Feed audio (e.g. from StreamingAudioDecoder)
float[] audio = new float[4096];
while (!decoder.IsEndOfStream)
{
    int n = decoder.Read(audio, 0, audio.Length);
    int frames = n / 2; // stereo: frames = samples / channels
    bpmDetector.InputSamples(audio.AsSpan(0, n), frames);
}

float bpm = bpmDetector.GetBpm();
Console.WriteLine($"Estimated BPM: {bpm:F1}");
// Returns 0 when not enough data has been fed yet
```

### Methods

| Method | Description |
|---|---|
| `InputSamples(ReadOnlySpan<float>, int frames)` | Feed interleaved audio frames |
| `GetBpm()` | Return estimated BPM, or `0` if not enough data |
| `Dispose()` | Release native detector |

---

## 12. Handles

Handles manage the lifetimes of native objects. As `SafeHandle` subclasses, the GC can
invoke the finalizer even when `Dispose()` is never called, preventing native resource leaks.

| Handle Class | Wraps | Releases via |
|---|---|---|
| `AudioEngineHandle` | Native engine context | `ownaudio_v1_engine_destroy` |
| `AudioOutputStreamHandle` | Output stream | `ownaudio_v1_output_stream_destroy` |
| `AudioInputStreamHandle` | Input stream | `ownaudio_v1_input_stream_destroy` |
| `MixerHandle` | Multi-track mixer | `ownaudio_v1_mixer_destroy` |
| `TrackHandle` | Mixer track (non-owning) | — |
| `TrackSourceHandle` | Ring-buffer source | `ownaudio_v1_track_source_destroy` |
| `FileSourceHandle` | File source | `ownaudio_v1_track_file_source_destroy` |
| `MemorySourceHandle` | Memory source | `ownaudio_v1_track_memory_source_destroy` |
| `InputSourceHandle` | Input-capture source | `ownaudio_v1_track_input_source_destroy` |
| `EffectHandle` | DSP effect (non-owning) | — |
| `StreamingDecoderHandle` | Streaming decoder | `ownaudio_v1_decoder_destroy` |
| `BpmDetectHandle` | BPM detector | `ownaudio_v1_bpm_destroy` |

> Handles are `internal` types — you do not work with them directly.
> `AudioEngine`, `AudioOutputStream`, `AudioInputStream`, `StreamingAudioDecoder`,
> `BpmDetect`, and the HighLevel session types manage them on your behalf.

---

## 13. Error Handling and Exceptions

Every native call result passes through `ErrorCodeMapper.ThrowIfError()`, which maps
raw integer codes to typed exceptions and appends the native error message string
(obtained from `ownaudio_v1_last_error_message`) to the exception message.

### Exception Hierarchy

```
OwnAudioException               ← base for all OwnAudio errors
 │  .ErrorCode : AudioEngineErrorCode
 ├─ DeviceException             ← device errors (codes 1, 2)
 ├─ StreamException             ← stream errors (codes 3, 4, 5)
 ├─ DecoderException            ← decoder errors (code 20+)
 ├─ HostApiNotAvailableException   ← requested host API unavailable (code 10)
 ├─ AsioDriverNotFoundException    ← ASIO driver not installed (code 11)
 └─ AbiVersionMismatchException    ← native binary version mismatch (code 12)
```

### AudioEngineErrorCode Values

| Code | Value | When it occurs |
|---|---|---|
| `Success` | 0 | Operation succeeded |
| `DeviceNotFound` | 1 | Requested device not found |
| `DeviceEnumerationFailed` | 2 | OS audio subsystem enumeration failed |
| `UnsupportedConfig` | 3 | Stream configuration not supported by the device |
| `StreamBuildFailed` | 4 | Stream could not be opened |
| `StreamControlFailed` | 5 | Play / Pause call failed |
| `NullPointer` | 6 | Unexpected null pointer on the native side |
| `InvalidHandle` | 7 | Handle is invalid or already destroyed |
| `InternalPanic` | 8 | Rust panic caught at the FFI boundary |
| `InternalError` | 9 | Other internal error |
| `HostApiNotAvailable` | 10 | Host API not available on this platform |
| `AsioDriverNotFound` | 11 | No ASIO driver installed |
| `AbiVersionMismatch` | 12 | Native binary ABI version does not match |

### Recommended Error Handling Pattern

```csharp
try
{
    using var engine = AudioEngine.Create();
    using var stream = engine.OpenOutputStream(null, config, callback);
    stream.Play();
    // ...
}
catch (AbiVersionMismatchException ex)
{
    // Critical: installed ownaudio_ffi is incompatible
    Console.Error.WriteLine($"Version mismatch: {ex.Message}");
    Console.Error.WriteLine("Reinstall the OwnAudioSharp NuGet package.");
}
catch (HostApiNotAvailableException ex)
{
    Console.Error.WriteLine($"Host API unavailable: {ex.Message}");
}
catch (AsioDriverNotFoundException)
{
    Console.Error.WriteLine("No ASIO driver found. Install ASIO4ALL or a vendor driver.");
}
catch (DeviceException ex)
{
    Console.Error.WriteLine($"Device error [{ex.ErrorCode}]: {ex.Message}");
}
catch (StreamException ex)
{
    Console.Error.WriteLine($"Stream error [{ex.ErrorCode}]: {ex.Message}");
}
catch (DecoderException ex)
{
    Console.Error.WriteLine($"Decoder error [{ex.ErrorCode}]: {ex.Message}");
}
catch (OwnAudioException ex)
{
    Console.Error.WriteLine($"Audio error [{ex.ErrorCode}]: {ex.Message}");
}
```

---

## 14. Threading and Real-Time Rules

### The Audio Callback Runs on a Real-Time Thread

Both the output and input callbacks run on the native OS audio thread, which is high-priority
and subject to strict timing deadlines. Any blocking or slow operation inside the callback
causes clicks, pops, or dropouts in the audio.

**FORBIDDEN inside the callback:**

```csharp
// Heap allocation
var list = new List<float>();
float[] arr = new float[1024];

// Locking
lock (myLock) { ... }
Monitor.Enter(myLock);

// Blocking I/O
File.WriteAllBytes("rec.wav", data);
Console.WriteLine("something");

// Task blocking
myTask.Wait();
var result = myTask.Result;

// Thread sleep
Thread.Sleep(10);
```

**Allowed inside the callback:**

```csharp
// Stack-allocated temporary buffer
Span<float> local = stackalloc float[32];

// Lock-free data transfer to a processing thread
myChannel.Writer.TryWrite(copy);   // System.Threading.Channels

// Math and synthesis
float sample = MathF.Sin(phase);
phase += step;

// Span indexing (zero allocation)
args.Buffer[i] = sample;

// Reading a pre-allocated buffer (e.g. WAV playback)
sourceSamples.AsSpan(position, count).CopyTo(args.Buffer);
```

### Thread Safety Summary

| Operation | Thread Safety |
|---|---|
| `AudioEngine.Create()` and `Dispose()` | Safe to call from any thread |
| `OpenOutputStream()` / `OpenInputStream()` | Do not call concurrently on the same instance |
| `AudioOutputStream.Play()` / `Pause()` / `Dispose()` | Do not call concurrently on the same instance |
| `AudioInputStream.Play()` / `Pause()` / `Dispose()` | Do not call concurrently on the same instance |
| `StreamingAudioDecoder.Read()` | Real-time safe (zero-allocation) |
| `StreamingAudioDecoder.Seek()` | Non-blocking; safe from any thread |
| Audio callback body | Lock-free and allocation-free operations only |
| `stream.CallbackError` event | Fires on a `ThreadPool` thread |

---

## 15. Resource Management

Every resource-owning type in the Safe layer implements `IDisposable`.
Always create instances with `using` declarations.

### Correct Pattern with `using`

```csharp
using var engine    = AudioEngine.Create();
using var outStream = engine.OpenOutputStream(null, config, outputCallback);
using var inStream  = engine.OpenInputStream(null, config, inputCallback);

outStream.Play();
inStream.Play();

// ... application runs ...

// At scope exit: streams are disposed in reverse declaration order,
// then the engine — this is the correct order.
```

### Manual Disposal (when `using` is not possible)

```csharp
// 1. ALWAYS dispose streams first
outStream.Dispose();
inStream.Dispose();

// 2. Then dispose the engine
engine.Dispose();
```

> **Why does order matter?** If the engine is destroyed first, a pending callback could
> access freed native memory, causing a crash or silent data corruption.

---

## 16. ABI Version Check

As its very first step, `AudioEngine.Create()` reads the ABI version from the loaded
native binary and compares it with the constant baked into the managed assembly:

```csharp
public const uint ExpectedAbiVersion = 1u;
```

If the values differ, `AbiVersionMismatchException` is thrown immediately — before any
audio subsystem is initialised. This prevents silent failures caused by loading an
incompatible native binary.

**Resolving an ABI mismatch:** Reinstall the OwnAudioSharp NuGet package and verify that
the native binaries in the `runtimes/` folder match the package version.

---

## Related Documentation

- [OwnAudioSharp Documentation](https://modernmube.github.io/OwnAudioSharp/)
- [Native Layer](../Native/README.md)
- [HighLevel Layer](../HighLevel/README.md)

## License

Copyright © 2025 Ownaudio Team  
Part of the OwnAudioSharp project.
