# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**OwnAudioSharp** is a cross-platform C# audio library providing professional-grade audio playback, recording, and processing with zero external dependencies. The library uses pure managed code with native system audio APIs (WASAPI on Windows, PulseAudio on Linux, Core Audio on macOS).

**Current Version:** 2.1.0 (NuGet Package: OwnAudioSharp)

**Important:** Version 2.0.0+ is a major rewrite using fully managed code. Pre-2.0.0 code can be accessed via the `OwnaudioLegacy` namespace for backward compatibility.

## Solution Structure

The solution is organized into four main categories:

### Engine Layer (OwnAudioEngine/)
Platform-specific and core audio engine implementations:

- **Ownaudio.Core**: Cross-platform interfaces, decoders (MP3/WAV/FLAC), lock-free buffers, object pools
- **Ownaudio.Windows**: WASAPI implementation for Windows
- **Ownaudio.Linux**: PulseAudio implementation for Linux
- **Ownaudio.macOS**: Core Audio implementation for macOS

### API Layer (OwnAudio/OwnaudioSource/)
High-level .NET API built on top of the engine:

- **OwnaudioNET.csproj**: Main NuGet package that bundles all platform-specific engines
- Contains high-level features: AudioMixer, ChordDetection, Matchering, VocalRemover, Effects
- Uses lock-free ring buffers to prevent UI thread blocking

### Examples (OwnAudio/OwnaudioExamples/)
Sample applications demonstrating API usage:

- **OwnaudioNETtest**: Basic playback example
- **ChordDetect**: Musical chord recognition
- **Matching**: Audio matchering/mastering
- **VocalRemover**: AI-driven vocal separation using ONNX models
- **OwnaudioInput**: Audio recording example

### Tests (OwnAudioTests/)
Unit and integration tests:

- **Ownaudio.EngineTest**: Engine-level tests (MSTest)
- **Ownaudio.OwnaudioNET**: NET API-level tests

## Common Development Tasks

### Building the Solution

```bash
# Build entire solution
dotnet build Ownaudio.sln

# Build in Release configuration
dotnet build Ownaudio.sln -c Release

# Build specific project
dotnet build OwnAudio/OwnaudioSource/OwnaudioNET.csproj

# Build for specific platform
dotnet build -c Release -r win-x64
dotnet build -c Release -r linux-x64
dotnet build -c Release -r osx-x64
```

### Running Tests

```bash
# Run all tests
dotnet test Ownaudio.sln

# Run tests for a specific project
dotnet test OwnAudioTests/Ownaudio.EngineTest/Ownaudio.EngineTest.csproj

# Run tests with detailed output
dotnet test --logger "console;verbosity=detailed"

# Run tests for specific platform configuration
dotnet test --configuration Debug --platform x64
```

### Building NuGet Package

The main OwnaudioNET project is configured to generate the NuGet package on build.

```bash
# Build and generate NuGet package
dotnet build OwnAudio/OwnaudioSource/OwnaudioNET.csproj -c Release

# Or use the provided build scripts
cd OwnAudio/OwnaudioSource
./build-nuget.sh    # Linux/macOS
# or
build-nuget.bat     # Windows

# Package will be in: OwnAudio/OwnaudioSource/nupkg/
```

**Important:** The NuGet package embeds all platform-specific DLLs (Windows, Linux, macOS) using PrivateAssets and includes them in the package via explicit ItemGroup entries.

### Running Examples

```bash
# Run the basic example
dotnet run --project OwnAudio/OwnaudioExamples/OwnaudioNETtest/OwnaudiNETexample.csproj

# Run chord detection example
dotnet run --project OwnAudio/OwnaudioExamples/ChordDetect/ChordDetect.csproj

# Run vocal remover (requires ONNX model files)
dotnet run --project OwnAudio/OwnaudioExamples/VocalRemover/Vocalremover.csproj
```

## Architecture Principles

### Two-Layer Architecture

1. **Core/Engine Layer** (Low-level):
   - Direct platform API access (WASAPI/PulseAudio/CoreAudio)
   - Interfaces: `IAudioEngine`, `IAudioDecoder`, `IDeviceEnumerator`
   - Factory pattern: `AudioEngineFactory.CreateDefault()` or platform-specific creation
   - Real-time audio thread with lock-free communication

2. **NET API Layer** (High-level):
   - User-friendly wrapper: `OwnaudioNet` static class
   - Thread-safe with lock-free ring buffers via `AudioEngineWrapper`
   - Features: `AudioMixer`, `ChordDetector`, `AudioMatchering`, `VocalRemover`
   - Zero-allocation design for real-time performance

### Thread Architecture

```
UI/Main Thread
  └─> OwnaudioNet.Send() [lock-free, <0.1ms]
       └─> AudioEngineWrapper [ring buffer write]
            └─> Pump Thread [dedicated]
                 └─> engine.Send() [may block 1-20ms]
                      └─> Audio RT Thread [platform-specific]
```

**Critical:** Never call `IAudioEngine.Send()` directly from UI thread. Always use `AudioEngineWrapper` or higher-level APIs.

### Threading Constraints (IMPORTANT)

The engine layer has blocking operations that must NOT be called from UI threads:

- `Initialize()`: Blocks 50-5000ms (especially Linux PulseAudio)
- `Stop()`: Blocks up to 2000ms waiting for audio thread to join
- `Send()`/`Receive()`: May block 1-20ms when buffers are full/empty

**Solution:** Use async wrappers or dedicated threads:
```csharp
// BAD - blocks UI thread
engine.Initialize(config);

// GOOD - async initialization
await Task.Run(() => engine.Initialize(config));

// BETTER - use high-level API which handles threading
OwnaudioNet.Initialize(config);  // Uses AudioEngineWrapper internally
```

See `THREAD_BLOCKING_ANALYSIS.md` for detailed analysis and recommendations.

### Zero-Allocation Design

The engine is designed for real-time audio with minimal GC pressure:

- **Object Pools**: `AudioFramePool`, `ObjectPool<T>` for buffer reuse
- **Lock-Free Structures**: `LockFreeRingBuffer` for cross-thread communication
- **Span<T>**: Extensive use of `Span<float>` and `ReadOnlySpan<float>` for stack allocation
- **SIMD Optimization**: `SimdAudioConverter` for vectorized operations

## Key Subsystems

### Audio Decoders

Location: `OwnAudioEngine/Ownaudio.Core/Decoders/`

Supported formats:
- **MP3**: Frame-based decoder with layer 1/2/3 support
- **WAV**: PCM, IEEE float, with automatic format conversion
- **FLAC**: Lossless compression support

Usage:
```csharp
using var decoder = AudioDecoderFactory.Create(
    "music.mp3",
    targetSampleRate: 48000,
    targetChannels: 2
);

while (true) {
    var result = decoder.DecodeNextFrame();
    if (result.IsEOF) break;
    // Use result.Frame.Samples
}
```

### Lock-Free Ring Buffer

Location: `OwnAudioEngine/Ownaudio.Core/Common/LockFreeRingBuffer.cs`

Core primitive for cross-thread audio data transfer:
- Wait-free reads and writes
- Single-producer, single-consumer (SPSC) design
- Memory barrier guarantees for ARM/x86
- Used by `AudioEngineWrapper` to decouple UI from audio threads

### Platform-Specific Engines

Each platform implementation follows the `IAudioEngine` interface:

**Windows (WASAPI)**:
- Uses COM interop for MMDevice API
- Supports shared and exclusive modes
- Event-driven and timer-driven callback modes
- Location: `OwnAudioEngine/Ownaudio.Windows/WasapiEngine.cs`

**Linux (PulseAudio)**:
- P/Invoke to libpulse
- Async API with threaded mainloop
- Longer initialization times (up to 5s timeout)
- Location: `OwnAudioEngine/Ownaudio.Linux/PulseAudioEngine.cs`

**macOS (Core Audio)**:
- AudioQueue API via P/Invoke
- Callback-based processing
- Location: `OwnAudioEngine/Ownaudio.macOS/CoreAudioEngine.cs`

### Audio Mixer

Location: `OwnAudio/OwnaudioSource/Mixing/`

Multi-source audio mixing with synchronization:
- Add/remove sources dynamically
- Per-source volume control
- Synchronized playback start
- Uses dedicated pump thread to avoid blocking
- Outputs to `IAudioEngine` via `AudioEngineWrapper`

```csharp
var mixer = new AudioMixer(engine, bufferSizeInFrames: 512);
mixer.AddSource(audioSource1);
mixer.AddSource(audioSource2);
mixer.Start();
```

### Advanced Features

**ChordDetection** (`Features/ChordDetect/`):
- Chromagram-based chord recognition
- Real-time and offline analysis
- Supports major, minor, and extended chords

**Matchering** (`Features/Matchering/`):
- AI-driven audio mastering
- EQ matching to reference tracks
- Spectral analysis and processing

**VocalRemover** (`Features/VocalRemover/`):
- ONNX-based neural network separation
- Multiple quality models: `nmp.onnx`, `best.onnx`, `default.onnx`, `karaoke.onnx`
- STFT processing with noise reduction
- Models are embedded resources in OwnaudioNET.csproj

## Platform Considerations

### Conditional Compilation

The core engine uses platform-specific defines:
- `WINDOWS`: Windows platform
- `LINUX`: Linux platform
- `MACOS`: macOS platform
- `ANDROID`: Android platform (future)
- `IOS`: iOS platform (future)

These are set automatically via MSBuild conditions in `Ownaudio.Core.csproj`.

### Runtime Identifiers

The NuGet package supports multiple RIDs:
- `win-x64`, `win-arm64`
- `linux-x64`, `linux-arm64`
- `osx-x64`, `osx-arm64`

Platform selection happens at runtime via `AudioEngineFactory`:
```csharp
public static IAudioEngine CreateDefault()
{
    #if WINDOWS
        return new WasapiEngine();
    #elif LINUX
        return new PulseAudioEngine();
    #elif MACOS
        return new CoreAudioEngine();
    #else
        throw new PlatformNotSupportedException();
    #endif
}
```

## Dependencies

**Core Engine**: No external dependencies (pure managed code)

**NET API Layer** (OwnaudioNET.csproj):
- `Avalonia` (11.3.7): Cross-platform UI framework (used in examples)
- `MathNet.Numerics` (5.0.0): Mathematical operations for DSP
- `Melanchall.DryWetMidi` (8.0.2): MIDI processing for chord detection
- `Microsoft.ML.OnnxRuntime` (1.23.1): Neural network inference for vocal removal
- `SoundTouch.Net` (2.3.2): Pitch shifting and tempo control
- `System.Numerics.Tensors` (9.0.10): Tensor operations for ML

**Test Projects**:
- `Microsoft.NET.Test.Sdk` (17.11.0)
- `MSTest.TestAdapter` (3.6.0)
- `MSTest.TestFramework` (3.6.0)
- `coverlet.collector` (6.0.0)

## Code Style and Patterns

### Naming Conventions
- Interfaces: `IAudioEngine`, `IAudioDecoder`
- Factories: `AudioEngineFactory`, `AudioDecoderFactory`
- Exceptions: `AudioException`, `AudioEngineException`
- Events: `AudioDeviceEventArgs`, `StopCompletedEventArgs`

### Error Handling
- Use specific exceptions: `AudioException` for audio errors, `AudioEngineException` for engine-level errors
- Include inner exceptions for context
- Validate parameters with `ArgumentNullException`, `ArgumentOutOfRangeException`

### Resource Management
- Implement `IDisposable` for all resource-owning types
- Use `using` statements or `using var` declarations
- Stop background threads before disposing native resources
- Return pooled objects explicitly (e.g., `ReturnInputBuffer()`)

### Unsafe Code
- Allowed via `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`
- Used for platform interop (P/Invoke, COM)
- Used for SIMD operations and buffer manipulation
- Always validate pointer bounds

## Testing Approach

### Engine Tests
Location: `OwnAudioTests/Ownaudio.EngineTest/`

Focus on low-level engine functionality:
- Platform-specific audio output/input
- Decoder correctness
- Buffer management
- Thread safety of lock-free structures

Test assets: `OwnAudioTests/Ownaudio.EngineTest/TestAssets/`

### NET API Tests
Location: `OwnAudioTests/Ownaudio.OwnaudioNET/`

Focus on high-level API:
- Wrapper functionality
- Mixer behavior
- Feature integration
- Thread blocking analysis

## Important Files

- `Ownaudio.sln`: Main solution file
- `OwnAudio/OwnaudioSource/OwnaudioNET.csproj`: NuGet package project
- `OwnAudio/OwnaudioSource/OwnaudioNet.cs`: Static API entry point
- `OwnAudioEngine/Ownaudio.Core/IAudioEngine.cs`: Core engine interface
- `OwnAudioEngine/Ownaudio.Core/AudioEngineFactory.cs`: Platform-specific engine creation
- `THREAD_BLOCKING_ANALYSIS.md`: Critical threading constraints documentation
- `README.md`: User-facing documentation
- `.editorconfig`: Code style settings

## Development Workflow

1. **Make changes** in the appropriate layer (Engine or NET API)
2. **Run tests** to verify functionality: `dotnet test`
3. **Build solution** to check for errors: `dotnet build`
4. **Test with examples** to verify real-world usage
5. **Update version** in `OwnaudioNET.csproj` if making a release
6. **Build NuGet package** if updating the public API

## Critical Development Rules

1. **Never call blocking engine methods from UI threads** - Use async wrappers or `AudioEngineWrapper`
2. **Always return pooled buffers** - Call `ReturnInputBuffer()` after `Receive()`
3. **Use Span<T> for audio data** - Avoid array allocations in hot paths
4. **Dispose audio resources** - Always use `using` or call `Dispose()` explicitly
5. **Test on all platforms** - Audio behavior varies significantly across Windows/Linux/macOS
6. **Check THREAD_BLOCKING_ANALYSIS.md** - Before modifying threading-related code
7. **Maintain zero-allocation in RT paths** - No `new` allocations in audio processing callbacks
8. **Use lock-free structures** - Avoid locks in audio processing paths

## Branch Strategy

- **master**: Stable releases, production-ready code
- **develope/mobile**: Current development branch for mobile platform support
- Feature branches should merge to development branch first

## Additional Resources

- Documentation: https://modernmube.github.io/OwnAudioSharp/
- NuGet Package: https://www.nuget.org/packages/OwnAudioSharp
- Issues/Feedback: https://github.com/modernmube/OwnAudioSharp/issues
