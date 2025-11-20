# Ownaudio.Core - Cross-Platform Audio Engine Core

Cross-platform audio engine core library providing unified interfaces, decoders, and zero-allocation infrastructure for real-time audio processing.

## Overview

This package provides the **core foundation** for OwnAudioSharp's audio engine architecture, including:

- **Platform-agnostic interfaces** (`IAudioEngine`, `IAudioDecoder`, `IDeviceEnumerator`)
- **Audio format decoders** (MP3, WAV, FLAC) with zero external dependencies
- **Lock-free data structures** for real-time audio thread communication
- **Zero-allocation primitives** (object pools, SIMD converters, ring buffers)
- **Platform detection and factory** for automatic engine selection

**IMPORTANT**: This is a **core library only** - it does not contain platform-specific implementations. For actual audio I/O, you need one of the platform-specific packages:
- [Ownaudio.Windows](../Ownaudio.Windows/) - WASAPI implementation
- [Ownaudio.Linux](../Ownaudio.Linux/) - PulseAudio implementation
- [Ownaudio.macOS](../Ownaudio.macOS/) - Core Audio implementation
- [Ownaudio.Android](../Ownaudio.Android/) - AAudio implementation
- [Ownaudio.iOS](../Ownaudio.iOS/) - Core Audio implementation

### Key Features

- **Zero-allocation design**: No GC pressure in real-time audio paths
- **Lock-free architecture**: Wait-free SPSC ring buffers for thread-safe communication
- **Pure managed code**: No native dependencies for core functionality
- **SIMD optimization**: Vectorized audio processing using `System.Numerics`
- **Object pooling**: Reusable buffers to minimize allocations
- **Multi-platform support**: Windows, Linux, macOS, Android, iOS

## Architecture

### Core Interfaces

The library defines three primary interfaces that all platform-specific implementations must adhere to:

#### 1. IAudioEngine
Core audio engine interface for playback and recording:
```csharp
public interface IAudioEngine : IDisposable
{
    // Initialization (⚠️ BLOCKING 50-5000ms!)
    int Initialize(AudioConfig config);

    // Control
    int Start();
    int Stop();  // ⚠️ BLOCKING up to 2000ms

    // Real-time I/O (⚠️ May block 1-20ms if buffer full/empty)
    void Send(Span<float> samples);
    int Receives(out float[] samples);

    // Device management
    List<AudioDeviceInfo> GetOutputDevices();
    List<AudioDeviceInfo> GetInputDevices();
    int SetOutputDeviceByName(string deviceName);
    // ... more methods
}
```

#### 2. IAudioDecoder
Unified interface for audio file decoding:
```csharp
public interface IAudioDecoder : IDisposable
{
    AudioStreamInfo StreamInfo { get; }
    AudioDecoderResult DecodeNextFrame();
    bool TrySeek(TimeSpan position, out string error);
    AudioDecoderResult DecodeAllFrames(TimeSpan position);
}
```

#### 3. IDeviceEnumerator
Platform-specific device enumeration:
```csharp
public interface IDeviceEnumerator
{
    List<AudioDeviceInfo> EnumerateOutputDevices();
    List<AudioDeviceInfo> EnumerateInputDevices();
    AudioDeviceInfo GetDefaultOutputDevice();
    AudioDeviceInfo GetDefaultInputDevice();
}
```

### Factory Pattern

The `AudioEngineFactory` automatically detects the platform and loads the appropriate engine:

```csharp
// Automatic platform detection
var engine = AudioEngineFactory.CreateDefault();

// Or with custom configuration
var config = new AudioConfig
{
    SampleRate = 48000,
    Channels = 2,
    BufferSize = 512
};
var engine = AudioEngineFactory.Create(config);
```

**Platform Detection Logic**:
1. Windows → Loads `Ownaudio.Windows.WasapiEngine`
2. macOS → Loads `Ownaudio.macOS.CoreAudioEngine`
3. iOS → Loads `Ownaudio.iOS.CoreAudioIOSEngine`
4. Android → Loads `Ownaudio.Android.AAudioEngine`
5. Linux → Loads `Ownaudio.Linux.PulseAudioEngine`

Uses reflection to avoid hard dependencies, allowing per-platform deployment.

## Project Structure

```
Ownaudio.Core/
├── Interfaces/
│   ├── IAudioEngine.cs              # Core audio engine interface
│   ├── IAudioDecoder.cs             # Audio decoder interface
│   └── IDeviceEnumerator.cs         # Device enumeration interface
│
├── Configuration/
│   ├── AudioConfig.cs               # Engine configuration
│   ├── AudioFormat.cs               # Audio format definitions
│   └── AudioStreamInfo.cs           # Stream metadata
│
├── Data Types/
│   ├── AudioFrame.cs                # Immutable audio frame
│   ├── MutableAudioFrame.cs         # Mutable audio frame (pooled)
│   ├── AudioDeviceInfo.cs           # Device information
│   ├── AudioDeviceEventArgs.cs      # Event arguments
│   └── AudioDecoderResult.cs        # Decoder output
│
├── Common/ (Zero-Allocation Infrastructure)
│   ├── LockFreeRingBuffer.cs        # Lock-free SPSC queue
│   ├── AudioFramePool.cs            # Object pool for frames
│   ├── ObjectPool.cs                # Generic object pool
│   ├── SimdAudioConverter.cs        # SIMD audio conversion
│   ├── AudioResampler.cs            # Sample rate converter
│   ├── AudioChannelConverter.cs     # Channel layout converter
│   ├── AudioFormatConverter.cs      # Format conversion
│   ├── AudioBuffer.cs               # Reusable audio buffer
│   ├── OptimizedAudioStream.cs      # Memory-efficient streaming
│   ├── MemoryMappedAudioStream.cs   # Large file streaming
│   ├── DecodedAudioCache.cs         # Decoded audio cache
│   ├── StreamingAudioCache.cs       # Streaming cache
│   ├── PooledByteBufferWriter.cs    # Pooled buffer writer
│   └── AudioException.cs            # Base exception type
│
├── Decoders/
│   ├── BaseStreamDecoder.cs         # Base decoder class
│   ├── Mp3/
│   │   ├── Mp3Decoder.cs            # MPEG-1/2/2.5 Layer 1/2/3
│   │   └── IPlatformMp3Decoder.cs   # Platform-specific backend
│   ├── Wav/
│   │   ├── WavDecoder.cs            # RIFF WAVE decoder
│   │   └── WavFormat.cs             # WAV format structures
│   └── Flac/
│       ├── FlacDecoder.cs           # FLAC decoder
│       ├── FlacBitReader.cs         # Bitstream reader
│       ├── FlacCrc.cs               # CRC validation
│       └── FlacStructs.cs           # FLAC structures
│
├── Factory/
│   ├── AudioEngineFactory.cs        # Platform-specific engine factory
│   └── AudioDecoderFactory.cs       # Format-based decoder factory
│
└── Extensions/
    └── AudioEngineAsyncExtensions.cs # Async wrappers for blocking methods
```

## Audio Decoders

Ownaudio.Core includes **pure managed decoders** for common audio formats:

### Supported Formats

| Format | Extension | Codec | Compression | Performance |
|--------|-----------|-------|-------------|-------------|
| **MP3** | .mp3 | MPEG-1/2/2.5 Layer III | Lossy | High |
| **WAV** | .wav | PCM, IEEE Float | Uncompressed | Very High |
| **FLAC** | .flac | FLAC | Lossless | Medium |

### Usage Example

```csharp
using Ownaudio.Core;
using Ownaudio.Decoders;

// Create decoder for any supported format
using var decoder = AudioDecoderFactory.Create(
    "music.mp3",
    targetSampleRate: 48000,
    targetChannels: 2
);

// Get stream information
var info = decoder.StreamInfo;
Console.WriteLine($"Duration: {info.Duration}");
Console.WriteLine($"Sample Rate: {info.SampleRate} Hz");
Console.WriteLine($"Channels: {info.Channels}");

// Decode frame by frame
while (true)
{
    var result = decoder.DecodeNextFrame();
    if (result.IsEOF)
        break;

    // Process result.Frame.Samples (float[])
    ProcessAudio(result.Frame.Samples);
}

// Or decode all at once
decoder.TrySeek(TimeSpan.Zero, out _);
var allFrames = decoder.DecodeAllFrames(TimeSpan.Zero);
```

### MP3 Decoder Features

- **Layers**: MPEG-1/2/2.5 Layer I, II, III
- **Bit rates**: 8-320 kbps + VBR
- **Sample rates**: 8000-48000 Hz
- **Channels**: Mono, Stereo, Joint Stereo, Dual Channel
- **ID3 tags**: v1, v2.2, v2.3, v2.4 parsing
- **Frame sync**: Robust error recovery
- **Zero allocation**: Reuses buffers via object pool

### WAV Decoder Features

- **Formats**: PCM (8/16/24/32-bit), IEEE Float (32/64-bit)
- **Extensible format**: Support for WAVE_FORMAT_EXTENSIBLE
- **Automatic conversion**: Converts all formats to Float32
- **Chunk parsing**: Handles RIFF, fmt, data, fact chunks
- **Large files**: Supports files >4GB (RF64)

### FLAC Decoder Features

- **Bit depths**: 4-32 bits per sample
- **Sample rates**: 1-655350 Hz
- **Channels**: 1-8 channels
- **Compression**: All prediction orders (0-32)
- **CRC validation**: Frame and stream integrity checks
- **Metadata**: STREAMINFO, VORBIS_COMMENT, PICTURE tags

## Lock-Free Ring Buffer

Core primitive for real-time audio communication between threads:

```csharp
using Ownaudio.Core.Common;

// Create power-of-2 sized buffer
var ringBuffer = new LockFreeRingBuffer<float>(8192);

// Producer thread (e.g., decoder)
float[] samples = new float[512];
// ... fill samples ...
ringBuffer.Write(samples);

// Consumer thread (e.g., audio callback)
float[] output = new float[512];
int read = ringBuffer.Read(output);
```

### Features

- **Wait-free**: SPSC (Single Producer, Single Consumer) design
- **Memory barriers**: Proper volatile semantics for ARM/x86
- **Zero allocation**: Pre-allocated buffer
- **Power-of-2 optimization**: Fast modulo via bitmask
- **Span<T> support**: Modern zero-copy API

### Thread Safety

- ✅ **Safe**: One reader + one writer simultaneously
- ❌ **Unsafe**: Multiple readers or multiple writers
- ✅ **Real-time safe**: No locks, no allocations

## Object Pooling

Minimize GC pressure with reusable audio buffers:

```csharp
using Ownaudio.Core.Common;

// Create pool for audio frames
var framePool = new AudioFramePool(capacity: 128);

// Rent from pool
var frame = framePool.Rent(sampleCount: 1024);

// Use frame...
ProcessAudio(frame.Samples);

// Return to pool
framePool.Return(frame);
```

### Available Pools

- `AudioFramePool` - Reusable `MutableAudioFrame` objects
- `ObjectPool<T>` - Generic object pool for any type
- `PooledByteBufferWriter` - Pooled byte array writer

## SIMD Audio Processing

Hardware-accelerated audio conversion using `System.Numerics`:

```csharp
using Ownaudio.Core.Common;

// Convert Int16 PCM to Float32 (vectorized)
short[] pcmSamples = new short[1024];
float[] floatSamples = new float[1024];

SimdAudioConverter.ConvertInt16ToFloat32(
    pcmSamples,
    floatSamples
);

// Volume scaling (vectorized)
SimdAudioConverter.MultiplyInPlace(floatSamples, volume: 0.5f);
```

### Supported Operations

- `ConvertInt16ToFloat32` - PCM int16 → float32
- `ConvertInt32ToFloat32` - PCM int32 → float32
- `ConvertFloat32ToInt16` - float32 → PCM int16
- `MultiplyInPlace` - Volume/gain adjustment
- `MixInPlace` - Mixing multiple audio streams

Uses `Vector<T>` for automatic SIMD utilization (SSE/AVX on x64, NEON on ARM).

## Audio Format Conversion

### Sample Rate Conversion

```csharp
using Ownaudio.Core.Common;

var resampler = new AudioResampler(
    inputRate: 44100,
    outputRate: 48000,
    channels: 2,
    quality: ResamplerQuality.High
);

float[] input = new float[882]; // 10ms @ 44.1kHz
float[] output = resampler.Resample(input);
```

### Channel Conversion

```csharp
using Ownaudio.Core.Common;

var converter = new AudioChannelConverter();

// Mono → Stereo
float[] mono = new float[512];
float[] stereo = converter.MonoToStereo(mono);

// Stereo → Mono (mix down)
float[] monoMixed = converter.StereoToMono(stereo);

// 5.1 → Stereo (downmix)
float[] surround = new float[512 * 6];
float[] stereoDownmix = converter.DownmixToStereo(surround, channels: 6);
```

## Configuration

### AudioConfig

```csharp
public class AudioConfig
{
    public int SampleRate { get; set; } = 48000;       // Hz
    public int Channels { get; set; } = 2;             // 1=Mono, 2=Stereo
    public int BufferSize { get; set; } = 512;         // Frames
    public bool EnableInput { get; set; } = false;     // Recording
    public bool EnableOutput { get; set; } = true;     // Playback
    public string? OutputDeviceId { get; set; } = null;
    public string? InputDeviceId { get; set; } = null;
}
```

**Presets**:
```csharp
AudioConfig.Default      // 48kHz, Stereo, 512 frames (~10ms)
AudioConfig.LowLatency   // 48kHz, Stereo, 128 frames (~2.7ms)
AudioConfig.HighLatency  // 48kHz, Stereo, 2048 frames (~42ms)
```

### AudioFormat

```csharp
public enum AudioFormat
{
    Unknown = 0,
    Float32 = 1,     // IEEE 754 float32 (native)
    Int16 = 2,       // PCM signed 16-bit
    Int24 = 3,       // PCM signed 24-bit
    Int32 = 4,       // PCM signed 32-bit
    UInt8 = 5        // PCM unsigned 8-bit
}
```

## Async Extensions

Wrapper methods for blocking `IAudioEngine` calls:

```csharp
using Ownaudio.Core;

var engine = AudioEngineFactory.CreateDefault();

// ✅ GOOD - Non-blocking initialization
await engine.InitializeAsync(config);

// ✅ GOOD - Non-blocking stop
await engine.StopAsync();

// ❌ BAD - Blocks UI thread!
engine.Initialize(config);  // Blocks 50-5000ms!
engine.Stop();              // Blocks up to 2000ms!
```

## Threading Constraints

**CRITICAL**: The `IAudioEngine` interface has blocking operations that must NOT be called from UI threads:

### Blocking Methods

| Method | Typical Blocking Time | Worst Case |
|--------|----------------------|------------|
| `Initialize()` | 50-500ms | 5000ms (Linux PulseAudio) |
| `Stop()` | 10-100ms | 2000ms (thread join timeout) |
| `Send()` | 0-5ms | 20ms (buffer full) |
| `Receives()` | 0-5ms | 20ms (buffer empty) |

### Solutions

1. **Use async extensions** (recommended):
```csharp
await engine.InitializeAsync(config);
await engine.StopAsync();
```

2. **Use AudioEngineWrapper** (for Send/Receive):
```csharp
var wrapper = new AudioEngineWrapper(engine, bufferSize: 8192);
wrapper.Send(samples);  // Non-blocking, uses ring buffer
```

3. **Use high-level API** (OwnaudioNET):
```csharp
OwnaudioNet.Initialize(config);  // Handles threading internally
```

See [THREAD_BLOCKING_ANALYSIS.md](../../../THREAD_BLOCKING_ANALYSIS.md) for detailed analysis.

## Platform Requirements

### Minimum Requirements

- **.NET**: 9.0 or later
- **Architecture**: x64, ARM64, x86 (platform-dependent)
- **OS**: Windows 10+, Linux (any modern distro), macOS 10.14+, Android 5.0+, iOS 11.0+

### Target Frameworks

The library targets multiple frameworks for maximum compatibility:

```xml
<TargetFrameworks>net9.0;net9.0-android35.0;net9.0-ios18.0</TargetFrameworks>
```

### Platform-Specific Dependencies

Ownaudio.Core itself has **no external dependencies**, but platform-specific implementations require:

- **Windows**: COM interop for WASAPI
- **Linux**: libpulse.so.0 (system library)
- **macOS**: Core Audio (system framework)
- **Android**: AAudio (API 26+)
- **iOS**: Audio Unit (system framework)

## Performance Characteristics

### Zero-Allocation Guarantees

The following operations are guaranteed to produce **zero allocations** after warmup:

- ✅ `Send(Span<float>)` - Audio output
- ✅ `LockFreeRingBuffer<T>.Write/Read` - Thread communication
- ✅ `AudioFramePool.Rent/Return` - Object pooling
- ✅ `SimdAudioConverter.*` - Format conversion
- ✅ Decoder frame iteration (when using pooled buffers)

### CPU Usage

- **Lock-free buffers**: No mutex contention
- **SIMD operations**: 4-8x faster than scalar code
- **Object pooling**: Eliminates GC pressure
- **Span<T>**: Zero-copy operations

### Memory Usage

| Component | Memory Footprint |
|-----------|------------------|
| AudioConfig | ~80 bytes |
| AudioFrame | ~32 bytes + sample array |
| LockFreeRingBuffer (8192 floats) | ~32 KB |
| AudioFramePool (128 frames) | ~4 MB (depending on frame size) |
| MP3 Decoder | ~100 KB |
| FLAC Decoder | ~200 KB |
| WAV Decoder | ~50 KB |

## Error Handling

All errors are reported via exceptions derived from `AudioException`:

```csharp
using Ownaudio.Core.Common;

try
{
    var engine = AudioEngineFactory.Create(config);
    await engine.InitializeAsync(config);
}
catch (PlatformNotSupportedException ex)
{
    // Platform not supported
    Console.WriteLine($"Platform error: {ex.Message}");
}
catch (AudioException ex)
{
    // Audio-specific error
    Console.WriteLine($"Audio error: {ex.Message}");
    Console.WriteLine($"Error code: {ex.ErrorCode}");
}
```

### Common Error Codes

Platform-specific implementations return error codes:

- `0` - Success
- `-1` - Generic error
- `-2` - Invalid configuration
- `-3` - Device not found
- `-4` - Device disconnected
- `-5` - Buffer overflow/underrun

## API Reference

### Key Types

- `IAudioEngine` - Core audio engine interface
- `IAudioDecoder` - Audio decoder interface
- `IDeviceEnumerator` - Device enumeration interface
- `AudioConfig` - Engine configuration
- `AudioFrame` - Immutable audio frame
- `AudioDeviceInfo` - Device information
- `AudioStreamInfo` - Stream metadata
- `LockFreeRingBuffer<T>` - Lock-free queue
- `AudioFramePool` - Object pool for frames

### Factory Classes

- `AudioEngineFactory` - Platform-specific engine creation
- `AudioDecoderFactory` - Format-based decoder creation

### Extension Methods

- `InitializeAsync` - Async initialization wrapper
- `StopAsync` - Async stop wrapper

## Building from Source

```bash
# Build Core library
dotnet build OwnAudioEngine/Ownaudio.Core/Ownaudio.Core.csproj -c Release

# Build for specific target framework
dotnet build -f net9.0
dotnet build -f net9.0-android35.0
dotnet build -f net9.0-ios18.0

# Output: Ownaudio.Core.dll
```

## Testing

```bash
# Run Core library tests
dotnet test OwnAudioTests/Ownaudio.EngineTest/Ownaudio.EngineTest.csproj --filter "TestCategory=Core"

# Test specific components
dotnet test --filter "FullyQualifiedName~LockFreeRingBuffer"
dotnet test --filter "FullyQualifiedName~AudioDecoder"
```

## Usage Examples

### Basic Engine Usage

```csharp
using Ownaudio.Core;

// Create engine with factory
var engine = AudioEngineFactory.CreateDefault();

// Initialize asynchronously
await engine.InitializeAsync(AudioConfig.Default);

// Start playback
engine.Start();

// Send audio samples
float[] samples = GenerateAudioSamples(512 * 2); // 512 frames * 2 channels
engine.Send(samples);

// Stop and cleanup
await engine.StopAsync();
engine.Dispose();
```

### Decoding Audio Files

```csharp
using Ownaudio.Core;
using Ownaudio.Decoders;

// Decode MP3 file
using var decoder = AudioDecoderFactory.Create("music.mp3");

var engine = AudioEngineFactory.Create(new AudioConfig
{
    SampleRate = decoder.StreamInfo.SampleRate,
    Channels = decoder.StreamInfo.Channels
});

await engine.InitializeAsync(config);
engine.Start();

// Stream decoded audio to engine
while (true)
{
    var result = decoder.DecodeNextFrame();
    if (result.IsEOF) break;

    engine.Send(result.Frame.Samples);
}

await engine.StopAsync();
engine.Dispose();
```

### Using Lock-Free Ring Buffer

```csharp
using Ownaudio.Core.Common;

// Create buffer for cross-thread communication
var ringBuffer = new LockFreeRingBuffer<float>(8192);

// Producer thread (decoder)
Task.Run(() =>
{
    float[] samples = new float[512];
    while (decoding)
    {
        DecodeAudio(samples);
        ringBuffer.Write(samples);
    }
});

// Consumer thread (audio callback)
void AudioCallback(float[] output)
{
    int read = ringBuffer.Read(output);
    if (read < output.Length)
    {
        // Fill remaining with silence
        Array.Clear(output, read, output.Length - read);
    }
}
```

## Best Practices

### 1. Always Use Async Wrappers

```csharp
// ✅ GOOD
await engine.InitializeAsync(config);
await engine.StopAsync();

// ❌ BAD - Blocks UI thread!
engine.Initialize(config);
engine.Stop();
```

### 2. Use Lock-Free Wrappers for UI Applications

```csharp
// ✅ GOOD - Non-blocking
var wrapper = new AudioEngineWrapper(engine, bufferSize: 8192);
wrapper.Send(samples);  // Returns immediately

// ❌ BAD - May block up to 20ms
engine.Send(samples);
```

### 3. Dispose Resources Properly

```csharp
// ✅ GOOD
using var engine = AudioEngineFactory.CreateDefault();
using var decoder = AudioDecoderFactory.Create("music.mp3");

// ❌ BAD - Memory leak
var engine = AudioEngineFactory.CreateDefault();
// ... never disposed
```

### 4. Return Pooled Objects

```csharp
// ✅ GOOD
var frame = framePool.Rent(1024);
ProcessAudio(frame);
framePool.Return(frame);

// ❌ BAD - Pool exhaustion
var frame = framePool.Rent(1024);
// ... never returned
```

### 5. Use Span<T> for Zero-Copy

```csharp
// ✅ GOOD - Zero allocation
Span<float> samples = stackalloc float[512];
engine.Send(samples);

// ❌ BAD - Allocates array
float[] samples = new float[512];
engine.Send(samples);
```

## Known Limitations

1. **Platform-specific implementations required**: Core library alone cannot play audio
2. **Float32 only**: All audio processing uses Float32 format internally
3. **SPSC only**: Lock-free ring buffer supports single producer/consumer only
4. **Power-of-2 buffers**: Ring buffer capacity must be power of 2
5. **No DSP effects**: Core library focuses on I/O, not signal processing

## Related Documentation

- [OwnAudioSharp Documentation](https://modernmube.github.io/OwnAudioSharp/)
- [Windows WASAPI Implementation](../Ownaudio.Windows/README.md)
- [Linux PulseAudio Implementation](../Ownaudio.Linux/README.md)
- [macOS Core Audio Implementation](../Ownaudio.macOS/README.md)
- [Threading Analysis](../../../THREAD_BLOCKING_ANALYSIS.md)

## License

Copyright © 2025 Ownaudio Team

Part of the OwnAudioSharp project.
