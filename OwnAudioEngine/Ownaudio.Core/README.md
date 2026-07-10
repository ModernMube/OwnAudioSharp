# Ownaudio.Core - Cross-Platform Audio Engine Core

Cross-platform audio engine core library providing unified interfaces, decoders, and zero-allocation infrastructure for real-time audio processing.

## Overview

This package provides the **core foundation** for OwnAudioSharp's audio engine architecture, including:

- **Platform-agnostic interfaces** (`IAudioEngine`, `IAudioDecoder`, `IDeviceEnumerator`)
- **Native Rust/Symphonia decoder** for broad format support (MP3, WAV, FLAC, AAC, OGG, AIFF, …)
- **Lock-free data structures** for real-time audio thread communication
- **Zero-allocation primitives** (object pools, ring buffers, pooled frame types)
- **AOT-compatible factory pattern** for automatic engine and decoder selection

**IMPORTANT**: This is a **core library only** — it does not contain platform-specific implementations. For actual audio I/O, use **[Ownaudio.Native](../Ownaudio.Native/)** — the cross-platform native engine built on **cpal** (Rust), supporting Windows, Linux, macOS, Android, and iOS.

> **Version**: 3.4.0  
> **Target Framework**: `net10.0` (mobile: `net10.0-android`, `net10.0-ios`)

### Key Features

- **Zero-allocation design**: No GC pressure in real-time audio paths
- **Lock-free architecture**: Wait-free SPSC ring buffers for thread-safe communication
- **Pure managed code**: No native dependencies for core infrastructure
- **AOT & trim compatible**: `IsAotCompatible = true`, `IsTrimmable = true`
- **Object pooling**: Reusable `PooledAudioFrame` buffers to minimize allocations
- **Broad format support**: Native Rust (Symphonia) decoder handles MP3, FLAC, WAV, AAC, OGG/Vorbis, AIFF, M4A out of the box

## Architecture

### Core Interfaces

The library defines three primary interfaces that all platform-specific implementations must adhere to:

#### 1. IAudioEngine

Core audio engine interface for playback and recording:

```csharp
public interface IAudioEngine : IDisposable
{
    // Status
    EngineStatus Status { get; }
    int FramesPerBuffer { get; }
    IntPtr GetStream();
    int OwnAudioEngineActivate();
    int OwnAudioEngineStopped();

    // Lifecycle (⚠️ BLOCKING — use Async extensions on UI threads!)
    int Initialize(AudioConfig config);  // Blocks 50–5000 ms
    int Start();
    int Stop();                          // Blocks up to 2000 ms

    // Real-time I/O (⚠️ Send blocks 10–50 ms if buffer full)
    void Send(Span<float> samples);
    int Receives(Span<float> destination); // zero-allocation: caller provides the buffer

    // Device management
    List<AudioDeviceInfo> GetOutputDevices();
    List<AudioDeviceInfo> GetInputDevices();
    int SetOutputDeviceByName(string deviceName);
    int SetOutputDeviceByIndex(int deviceIndex);
    int SetInputDeviceByName(string deviceName);
    int SetInputDeviceByIndex(int deviceIndex);

    // Device events
    event EventHandler<AudioDeviceChangedEventArgs>      OutputDeviceChanged;
    event EventHandler<AudioDeviceChangedEventArgs>      InputDeviceChanged;
    event EventHandler<AudioDeviceStateChangedEventArgs> DeviceStateChanged;
    event EventHandler<AudioDeviceReconnectedEventArgs>  DeviceReconnected;

    // Device monitoring control
    void PauseDeviceMonitoring();
    void ResumeDeviceMonitoring();
}
```

#### 2. IAudioDecoder

Unified interface for audio file decoding (zero-allocation, buffer-based):

```csharp
public interface IAudioDecoder : IDisposable
{
    AudioStreamInfo StreamInfo { get; }

    // Zero-allocation read path: caller provides the byte buffer
    AudioDecoderResult ReadFrames(byte[] buffer);

    bool TrySeek(TimeSpan position, out string error);
}
```

> **Note (v4.0+):** The previous `DecodeNextFrame()` / `DecodeAllFrames()` API has been replaced by `ReadFrames(byte[] buffer)`. All decoding is now handled by the native Rust (Symphonia) engine — the managed MP3/WAV/FLAC decoders and optional FFmpeg fallback have been removed.

#### 3. IDeviceEnumerator

Platform-specific device enumeration:

```csharp
public interface IDeviceEnumerator
{
    List<AudioDeviceInfo>  EnumerateOutputDevices();
    List<AudioDeviceInfo>  EnumerateInputDevices();
    List<AudioDeviceInfo>  EnumerateAllDevices();
    AudioDeviceInfo?       GetDefaultOutputDevice();
    AudioDeviceInfo?       GetDefaultInputDevice();
    AudioDeviceInfo?       GetDeviceInfo(string deviceId);
}
```

### Factory Pattern

Both factories use a **delegate-based, AOT-safe registration** — no reflection or late assembly loading.

#### AudioEngineFactory

```csharp
// Automatic platform detection (Ownaudio.Native registers itself at module-init time)
var engine = AudioEngineFactory.CreateDefault();

// Or with custom configuration
var config = new AudioConfig
{
    SampleRate  = 48000,
    Channels    = 2,
    BufferSize  = 512
};
var engine = AudioEngineFactory.Create(config);

// Convenience presets
var lowLatency  = AudioEngineFactory.CreateLowLatency();
var highLatency = AudioEngineFactory.CreateHighLatency();

// Diagnostic info
Console.WriteLine(AudioEngineFactory.GetPlatformInfo());
// Platform: macOS
// Implementation: RustAudioEngine (cpal)
```

#### AudioDecoderFactory

```csharp
// Create decoder — format is auto-detected from file content
using var decoder = AudioDecoderFactory.Create("music.mp3", targetSampleRate: 48000, targetChannels: 2);

// Or from a stream (buffered to a temp file internally)
using var decoder = AudioDecoderFactory.Create(stream, AudioFormat.Flac);

// Detect format from stream magic bytes
AudioFormat fmt = AudioDecoderFactory.DetectFormat(stream);

// Register native decoder (called automatically by Ownaudio.Native at module-init)
AudioDecoderFactory.RegisterNativeDecoder((path, rate, ch) => new MyDecoder(path, rate, ch));
```

**Supported formats** (via native Rust/Symphonia engine, loaded by Ownaudio.Native):

| Format | Extensions |
|--------|-----------|
| MP3 | .mp3 |
| FLAC | .flac |
| WAV (PCM / ADPCM) | .wav |
| AAC | .aac |
| MP4 / M4A | .mp4, .m4a |
| OGG / Vorbis | .ogg |
| AIFF | .aif, .aiff |

## Project Structure

```
Ownaudio.Core/
├── IAudioEngine.cs              # Core audio engine interface
├── IAudioDecoder.cs             # Audio decoder interface
├── IDeviceEnumerator.cs         # Device enumeration interface
│
├── AudioConfig.cs               # Engine configuration (with channel selectors)
├── AudioFormat.cs               # Audio format enum (Wav, Mp3, Flac, FFmpeg)
├── AudioStreamInfo.cs           # Stream metadata
├── AudioFrame.cs                # Immutable audio frame (byte[]-based)
├── AudioDecoderResult.cs        # Decoder output (zero-alloc & legacy paths)
├── AudioDeviceInfo.cs           # Device information
├── AudioDeviceEventArgs.cs      # Device event argument types
├── EngineStatus.cs              # Engine state enum
├── EngineHostType.cs            # Host API selector (WASAPI, ASIO, CoreAudio, …)
│
├── AudioEngineFactory.cs        # AOT-safe delegate-based engine factory
├── AudioDecoderFactory.cs       # AOT-safe delegate-based decoder factory
│
├── AudioEngineAsyncExtensions.cs # Async wrappers for blocking engine methods
├── AudioDecoderExtensions.cs    # Helper extensions for IAudioDecoder
│
├── Common/                      # Zero-Allocation Infrastructure
│   ├── LockFreeRingBuffer.cs    # Lock-free SPSC queue (power-of-2)
│   ├── AudioFramePool.cs        # Object pool for PooledAudioFrame (byte[]-based)
│   ├── MutableAudioFrame.cs     # Mutable frame for internal use
│   ├── ObjectPool.cs            # Generic object pool
│   ├── PooledByteBufferWriter.cs # Pooled byte array writer
│   ├── AudioBuffer.cs           # Reusable audio buffer helper
│   └── AudioException.cs        # Base exception with error category & code
│
└── Logging/
    └── Logger.cs                # Internal diagnostic logger
```

> **Note:** The managed `Decoders/` directory (Mp3, Wav, Flac subdirectories) and types such as `SimdAudioConverter`, `AudioResampler`, `AudioChannelConverter`, `AudioFormatConverter`, `OptimizedAudioStream`, `MemoryMappedAudioStream`, `DecodedAudioCache`, and `StreamingAudioCache` **have been removed** as of v4.0. All format conversion and decoding is now delegated to the native Rust engine.

## Audio Decoding

### Decoder Architecture (v4.0+)

All decoding is performed by the **native Rust (Symphonia) engine**, registered by the `Ownaudio.Native` assembly via a `[ModuleInitializer]`. No manual setup is needed.

```csharp
// The factory will throw AudioException if Ownaudio.Native is not loaded
using var decoder = AudioDecoderFactory.Create("audio.flac");
Console.WriteLine($"Duration: {decoder.StreamInfo.Duration}");
```

### Usage Example — Zero-Allocation Decode Loop

```csharp
using Ownaudio.Decoders;

// Create decoder (format auto-detected; optional resampling/downmix)
using var decoder = AudioDecoderFactory.Create(
    "music.mp3",
    targetSampleRate: 48000,
    targetChannels: 2
);

// Allocate a single reusable buffer (pre-allocate outside the loop!)
var buffer = new byte[65536];

// Seek to start
decoder.TrySeek(TimeSpan.Zero, out _);

// Decode frame by frame — zero allocation per iteration
while (true)
{
    var result = decoder.ReadFrames(buffer);

    if (result.IsEOF)
        break;

    if (!result.IsSucceeded)
    {
        Console.WriteLine($"Error: {result.ErrorMessage}");
        break;
    }

    // result.FramesRead = number of audio frames written into buffer
    // result.PresentationTime = PTS in milliseconds
    ProcessAudio(buffer.AsSpan(0, result.FramesRead));
}
```

### Stream Decoding

```csharp
// Stream is buffered to a temp file automatically (deleted on Dispose)
using var fileStream = File.OpenRead("audio.ogg");
using var decoder = AudioDecoderFactory.Create(fileStream, AudioFormat.Unknown);
```

### Format Detection

```csharp
using var stream = File.OpenRead("unknown_file");
AudioFormat format = AudioDecoderFactory.DetectFormat(stream);
// Inspects magic bytes (RIFF/WAVE, ID3/0xFF, fLaC); stream position is restored
```

## Lock-Free Ring Buffer

Core primitive for real-time audio communication between threads:

```csharp
using Ownaudio.Core.Common;

// Capacity is rounded up to the next power of 2 automatically
var ringBuffer = new LockFreeRingBuffer<float>(8192);

// Available space and data counts
int canWrite = ringBuffer.WritableCount;
int canRead  = ringBuffer.Available;      // also: AvailableRead

// Producer thread (e.g., decoder)
float[] samples = new float[512];
// ... fill samples ...
int written = ringBuffer.Write(samples);       // accepts ReadOnlySpan<T>

// Consumer thread (e.g., audio callback)
float[] output = new float[512];
int read = ringBuffer.Read(output);            // accepts Span<T>

// Reset (call only when no concurrent access)
ringBuffer.Clear();
```

### Thread Safety

- ✅ **Safe**: One reader + one writer simultaneously (SPSC)
- ❌ **Unsafe**: Multiple readers or multiple writers
- ✅ **Real-time safe**: No locks, no allocations
- ✅ Uses `Volatile.Read/Write` for correct ARM/x86 memory ordering

## Object Pooling

### AudioFramePool — Byte-Buffer–Based Pool

```csharp
using Ownaudio.Core.Common;

// Pool of byte buffers for zero-allocation decoding
var pool = new AudioFramePool(
    bufferSize:      65536,   // bytes per frame buffer
    initialPoolSize: 4,
    maxPoolSize:     16
);

// Rent a pooled frame
PooledAudioFrame frame = pool.Rent(presentationTime: 0.0, dataLength: 4096);

// Access data
Span<byte> data = frame.DataSpan;   // active data region
Span<byte> buf  = frame.BufferSpan; // full buffer for writing

// Convert to immutable AudioFrame if needed (allocates)
AudioFrame immutable = frame.ToAudioFrame();

// Return to pool
pool.Return(frame);
```

### ObjectPool\<T\> — Generic Pool

```csharp
var pool = new ObjectPool<MyObject>(() => new MyObject(), initialSize: 8);
var obj = pool.Rent();
// ... use obj ...
pool.Return(obj);
```

### PooledByteBufferWriter

```csharp
var writer = new PooledByteBufferWriter(initialCapacity: 4096);
// Write bytes into pooled buffer
writer.Dispose(); // returns buffer to pool
```

## Configuration

### AudioConfig

```csharp
public sealed class AudioConfig
{
    public int    SampleRate   { get; set; } = 48000;  // Hz
    public int    Channels     { get; set; } = 2;       // 1=Mono, 2=Stereo
    public int    BufferSize   { get; set; } = 512;     // Frames (~10.6 ms @ 48 kHz)
    public bool   EnableInput  { get; set; } = false;   // Recording
    public bool   EnableOutput { get; set; } = true;    // Playback
    public string? OutputDeviceId { get; set; } = null; // null = system default
    public string? InputDeviceId  { get; set; } = null; // null = system default

    // Host API selector — only used with PortAudio backend; MiniAudio ignores it
    public EngineHostType HostType { get; set; } = EngineHostType.None;

    // Channel routing — null = sequential (0, 1, 2, …)
    // Length must equal Channels when non-null
    public int[]? InputChannelSelectors  { get; set; } = null;
    public int[]? OutputChannelSelectors { get; set; } = null;

    // Device disconnect behaviour
    public bool FallbackToDefaultOnDisconnect { get; set; } = true;
}
```

**Channel selectors example** — route ASIO inputs 2 & 3 to logical channels 0 & 1:

```csharp
var config = new AudioConfig
{
    Channels = 2,
    HostType = EngineHostType.ASIO,
    InputChannelSelectors = new[] { 2, 3 }
};
```

**`FallbackToDefaultOnDisconnect`**:
- `true` (default): engine automatically switches to the system default device on disconnect and switches back when the original reconnects — no interruption.
- `false`: engine enters `EngineStatus.DeviceDisconnected` and waits for the original device to reappear.

**Presets**:

```csharp
AudioConfig.Default     // 48 kHz, Stereo, 512 frames (~10.6 ms)
AudioConfig.LowLatency  // 48 kHz, Stereo, 128 frames (~2.7 ms)
AudioConfig.HighLatency // 48 kHz, Stereo, 2048 frames (~42.7 ms)
```

### EngineHostType

Selects the host audio API (only when using the PortAudio backend):

| Value | Platform | Description |
|-------|----------|-------------|
| `None` | All | Use platform default |
| `WASAPI` | Windows | Low-latency modern API (Vista+) |
| `ASIO` | Windows | Ultra-low latency, requires ASIO drivers |
| `WDMKS` | Windows | WDM Kernel Streaming |
| `COREAUDIO` | macOS | Core Audio (low latency) |
| `ALSA` | Linux | Advanced Linux Sound Architecture |
| `JACK` | Linux / macOS | Professional real-time audio server |
| `AAUDIO` | Android 8.0+ | High-performance AAudio API |
| `OPENSL` | Android | OpenSL ES (legacy) |
| `WEBAUDIO` | Web | Web Audio API |

### EngineStatus

```csharp
public enum EngineStatus
{
    Idle               = 0,   // Initialized, not yet started
    Running            = 1,   // Actively processing audio
    DeviceDisconnected = 2,   // USB/Bluetooth device unplugged; monitoring for reconnection
    Error              = -1   // Fatal error
}
```

### AudioFormat (file format enum)

```csharp
public enum AudioFormat
{
    Unknown = 0,
    Wav     = 1,   // PCM, IEEE Float, ADPCM
    Mp3     = 2,   // MPEG-1/2 Layer III
    Flac    = 3,   // Free Lossless Audio Codec
    FFmpeg  = 4    // Formats decoded by FFmpeg (OGG, Opus, AAC, M4A, WMA, AIFF, …)
}
```

> `AudioFormat` is used as an **extension hint** for stream decoding (temp-file suffix). The native decoder auto-detects the real format from the file content regardless of this value.

## Async Extensions

Wrapper methods for blocking `IAudioEngine` calls — always use these on UI threads:

```csharp
using Ownaudio.Core;

var engine = AudioEngineFactory.CreateDefault();

// Non-blocking — runs Initialize on a thread-pool thread
await engine.InitializeAsync(config);

// Non-blocking stop
await engine.StopAsync();

// Device listing
List<AudioDeviceInfo> outputs = await engine.GetOutputDevicesAsync();
List<AudioDeviceInfo> inputs  = await engine.GetInputDevicesAsync();

// Device switching
await engine.SetOutputDeviceByNameAsync("Speakers (Realtek)");
await engine.SetInputDeviceByNameAsync("Microphone (USB)");

// BAD — Blocks UI thread!
// engine.Initialize(config);
// engine.Stop();
```

All async extensions accept an optional `CancellationToken`.

> On **Windows**, `Initialize` always runs on a dedicated MTA thread to satisfy WASAPI COM requirements, regardless of whether you call the sync or async overload.

## Error Handling

All errors surface as `AudioException` (namespace `Ownaudio.Core.Common`):

```csharp
using Ownaudio.Core.Common;

try
{
    var engine = AudioEngineFactory.Create(config);
    await engine.InitializeAsync(config);
}
catch (AudioException ex)
{
    Console.WriteLine($"Category  : {ex.Category}");   // AudioErrorCategory enum
    Console.WriteLine($"Error code: {ex.ErrorCode}");  // platform-specific int
    Console.WriteLine($"File path : {ex.FilePath}");
    Console.WriteLine($"Stream pos: {ex.StreamPosition}");
}
catch (PlatformNotSupportedException ex)
{
    Console.WriteLine($"Platform not supported: {ex.Message}");
}
```

### AudioErrorCategory

| Value | Meaning |
|-------|---------|
| `Unknown` | Unspecified error |
| `FileFormat` | Invalid or unsupported file format |
| `IO` | Read/write/seek failure |
| `Decoding` | Audio decoding failure |
| `Seeking` | Seek operation failed |
| `PlatformAPI` | Native API call failed |
| `OutOfMemory` | Buffer or allocation failure |
| `Device` | Audio device operation failed |
| `Configuration` | Invalid configuration parameters |

### Common Error Codes

| Code | Meaning |
|------|---------|
| `0` | Success |
| `-1` | Generic / unknown error |
| `-2` | Invalid configuration |
| `-3` | Device not found |
| `-4` | Device disconnected |
| `-5` | Buffer overflow / underrun |

## Threading Constraints

**CRITICAL**: Never call blocking `IAudioEngine` operations from UI threads.

| Method | Typical Blocking Time | Worst Case |
|--------|-----------------------|------------|
| `Initialize()` | 50–500 ms | 5000 ms (Linux PulseAudio) |
| `Stop()` | 10–100 ms | 2000 ms (thread join timeout) |
| `Send()` | 10–50 ms | depends on buffer size |
| `Receives()` | < 0.1 ms | 1 ms (ring buffer read, zero-allocation) |

**Solutions:**

1. **Use async extensions** (recommended):

```csharp
await engine.InitializeAsync(config);
await engine.StopAsync();
```

2. **High-level API** (`OwnaudioNET`) handles threading internally.

## Platform Requirements

| Requirement | Value |
|-------------|-------|
| **.NET** | 10.0 or later |
| **Default target** | `net10.0` |
| **Mobile targets** | `net10.0-android` (API 24+), `net10.0-ios` (12.2+) |
| **Architecture** | x64, ARM64 |
| **Windows** | 10+ |
| **Linux** | Any modern distro |
| **macOS** | 10.14+ |
| **Android** | API 24+ (Android 7.0+) |
| **iOS** | 12.2+ |

Mobile builds are only produced when `BuildingForMobile=true` is set:

```bash
dotnet build -p:BuildingForMobile=true -f net10.0-android
dotnet build -p:BuildingForMobile=true -f net10.0-ios
```

## Performance Characteristics

### Zero-Allocation Guarantees

The following operations produce **zero allocations** after warmup:

- ✅ `Send(Span<float>)` — audio output
- ✅ `Receives(Span<float>)` — audio input (caller provides pre-allocated buffer)
- ✅ `LockFreeRingBuffer<T>.Write/Read` — thread communication
- ✅ `AudioFramePool.Rent/Return` — object pooling
- ✅ `decoder.ReadFrames(byte[])` — decode into caller-provided buffer

### CPU & Memory

| Component | Footprint |
|-----------|-----------|
| `AudioConfig` | ~80 bytes |
| `LockFreeRingBuffer<float>` (8192 elements) | ~32 KB |
| `AudioFramePool` (16 × 64 KB) | ~1 MB |
| `PooledAudioFrame` (64 KB buffer) | ~64 KB |

## Usage Examples

### Basic Engine Usage

```csharp
using Ownaudio.Core;

using var engine = AudioEngineFactory.CreateDefault();
await engine.InitializeAsync(AudioConfig.Default);
engine.Start();

// Send audio samples (interleaved Float32)
float[] samples = GenerateAudioSamples(512 * 2); // 512 frames × 2 ch
engine.Send(samples);

Console.WriteLine(engine.Status);          // Running
Console.WriteLine(engine.FramesPerBuffer); // actual negotiated buffer size

await engine.StopAsync();
```

### Decoding and Playing an Audio File

```csharp
using Ownaudio.Core;
using Ownaudio.Decoders;

using var decoder = AudioDecoderFactory.Create("music.flac");
using var engine  = AudioEngineFactory.Create(new AudioConfig
{
    SampleRate = decoder.StreamInfo.SampleRate,
    Channels   = decoder.StreamInfo.Channels
});

await engine.InitializeAsync(AudioConfig.Default);
engine.Start();

var buffer = new byte[65536];
decoder.TrySeek(TimeSpan.Zero, out _);

while (true)
{
    var result = decoder.ReadFrames(buffer);
    if (result.IsEOF) break;
    // Send decoded PCM bytes reinterpreted as float — or convert as needed
}

await engine.StopAsync();
```

### Device Selection with Channel Routing

```csharp
var config = new AudioConfig
{
    SampleRate             = 48000,
    Channels               = 2,
    HostType               = EngineHostType.ASIO,
    OutputChannelSelectors = new[] { 4, 5 }  // use ASIO outputs 4 & 5
};
using var engine = AudioEngineFactory.Create(config);
```

### Cross-Thread Ring Buffer

```csharp
using Ownaudio.Core.Common;

var ringBuffer = new LockFreeRingBuffer<float>(8192);

// Producer (decoder thread)
Task.Run(() =>
{
    float[] samples = new float[512];
    while (decoding)
    {
        FillSamples(samples);
        ringBuffer.Write(samples);   // returns count actually written
    }
});

// Consumer (audio callback — real-time thread)
void AudioCallback(Span<float> output)
{
    int read = ringBuffer.Read(output);
    if (read < output.Length)
        output.Slice(read).Clear(); // silence for underrun
}
```

### Device Monitoring

```csharp
engine.DeviceStateChanged  += (_, e) => Console.WriteLine($"Device state: {e.DeviceName}");
engine.OutputDeviceChanged += (_, e) => Console.WriteLine($"Output changed: {e.DeviceName}");
engine.DeviceReconnected   += (_, e) => Console.WriteLine($"Reconnected: {e.DeviceName}");

// Suppress monitoring during sensitive UI operations (e.g., opening a plugin editor)
engine.PauseDeviceMonitoring();
OpenPluginEditor();
engine.ResumeDeviceMonitoring();
```

## Best Practices

### 1. Always Use Async Wrappers on UI Threads

```csharp
// GOOD
await engine.InitializeAsync(config);
await engine.StopAsync();

// BAD — Blocks UI thread!
engine.Initialize(config);
engine.Stop();
```

### 2. Pre-Allocate Decode Buffers Outside the Loop

```csharp
// GOOD — single allocation before the loop
var buffer = new byte[65536];
while (true)
{
    var result = decoder.ReadFrames(buffer);
    if (result.IsEOF) break;
}

// BAD — allocates on every iteration
while (true)
{
    var result = decoder.ReadFrames(new byte[65536]);
}
```

### 3. Dispose Resources Properly

```csharp
// GOOD
using var engine  = AudioEngineFactory.CreateDefault();
using var decoder = AudioDecoderFactory.Create("music.mp3");

// BAD — memory/resource leak
var engine = AudioEngineFactory.CreateDefault();
// ... never disposed
```

### 4. Return Pooled Objects

```csharp
// GOOD
var frame = pool.Rent(presentationTime: 0.0, dataLength: 4096);
ProcessAudio(frame.DataSpan);
pool.Return(frame);

// BAD — pool exhaustion
var frame = pool.Rent(presentationTime: 0.0, dataLength: 4096);
// ... never returned
```

### 5. Use Span\<T\> for Zero-Copy Paths

```csharp
// GOOD — no allocation
Span<float> samples = stackalloc float[512];
engine.Send(samples);

// BAD — heap allocation
engine.Send(new float[512]);
```

## Building from Source

```bash
# Build Core library (default: net10.0)
dotnet build OwnAudioEngine/Ownaudio.Core/Ownaudio.Core.csproj -c Release

# Build for mobile (requires BuildingForMobile flag)
dotnet build OwnAudioEngine/Ownaudio.Core/Ownaudio.Core.csproj \
    -p:BuildingForMobile=true -f net10.0-android
dotnet build OwnAudioEngine/Ownaudio.Core/Ownaudio.Core.csproj \
    -p:BuildingForMobile=true -f net10.0-ios
```

## Testing

```bash
# Run Core library tests
dotnet test OwnAudioTests/Ownaudio.EngineTest/Ownaudio.EngineTest.csproj \
    --filter "TestCategory=Core"

# Run specific component tests
dotnet test --filter "FullyQualifiedName~LockFreeRingBuffer"
dotnet test --filter "FullyQualifiedName~AudioDecoder"
dotnet test --filter "FullyQualifiedName~AudioFramePool"
```

## Known Limitations

1. **Platform-specific implementations required**: Core library alone cannot play audio — load `Ownaudio.Native`.
2. **Float32 only**: All audio I/O uses interleaved Float32 internally.
3. **SPSC only**: `LockFreeRingBuffer<T>` supports a single producer and a single consumer.
4. **Power-of-2 buffers**: Ring buffer capacity is automatically rounded up to the next power of 2.
5. **Native decoder required**: `AudioDecoderFactory.Create()` throws if `Ownaudio.Native` has not been loaded.
6. **No built-in DSP**: Signal processing (EQ, reverb, …) is out of scope for this library.

## Related Documentation

- [OwnAudioSharp Documentation](https://modernmube.github.io/OwnAudioSharp/)
- [Native Engine (Ownaudio.Native)](../Ownaudio.Native/)
- [Threading Analysis](../../../THREAD_BLOCKING_ANALYSIS.md)

## License

Copyright © 2025 Ownaudio Team

Part of the OwnAudioSharp project.
