# Ownaudio.Native - Cross-Platform Native Audio Engine

## Overview

**Ownaudio.Native** is the PRIMARY audio engine for OwnAudioSharp 2.1.0+, providing cross-platform audio playback and recording using **PortAudio** (when available) or **MiniAudio** (bundled fallback).

This engine represents a hybrid approach that prioritizes PortAudio for better audio quality and system integration, while ensuring MiniAudio is always available as a reliable fallback.

## Architecture

### Backend Selection Strategy

The engine uses an intelligent backend selection strategy:

1. **Try PortAudio first** (all platforms):
   - Checks bundled library in `runtimes/{rid}/native/`
   - Checks system-installed PortAudio:
     - **macOS**: Homebrew paths (`/opt/homebrew` for ARM64, `/usr/local` for x64)
     - **Linux**: System library paths (`/usr/lib/{arch}-linux-gnu/`)
     - **Windows**: Bundled `libportaudio.dll` for x64

2. **Fallback to MiniAudio** (always bundled):
   - Used when PortAudio is not found or fails to initialize
   - Bundled for all platforms: Windows, Linux, macOS, Android, iOS

### Key Features

- ✅ **Zero external dependencies** - MiniAudio is always bundled
- ✅ **System integration** - Automatic detection of system-installed PortAudio
- ✅ **Cross-platform** - Works on Windows, Linux, macOS, Android, iOS
- ✅ **Zero-allocation design** - Lock-free ring buffers for RT-safe audio processing
- ✅ **IAudioEngine compliant** - Implements Ownaudio.Core.IAudioEngine interface
- ✅ **Automatic fallback** - Gracefully falls back if PortAudio unavailable

## Project Structure

```
Ownaudio.Native/
├── Utils/
│   └── LibraryLoader.cs          # Cross-platform native library loader
├── PortAudio/
│   ├── PaBinding.cs               # Main P/Invoke wrapper
│   ├── PaBinding.Enums.cs         # PortAudio enumerations
│   ├── PaBinding.Structs.cs       # PortAudio structures
│   └── PaBinding.Delegates.cs     # Function delegates
├── MiniAudio/
│   ├── MaBinding.cs               # Main P/Invoke wrapper
│   ├── MaBinding.Enums.cs         # MiniAudio enumerations
│   ├── MaBinding.Structs.cs       # MiniAudio structures
│   └── MaBinding.Delegates.cs     # Function delegates
├── NativeAudioEngine.cs           # Main engine implementation
├── runtimes/                      # Native libraries
│   ├── win-x64/native/
│   │   ├── libportaudio.dll      # PortAudio for Windows x64
│   │   └── libminiaudio.dll      # MiniAudio for Windows x64
│   ├── linux-x64/native/
│   │   └── libminiaudio.so       # MiniAudio for Linux x64
│   ├── osx-x64/native/
│   │   └── libminiaudio.dylib    # MiniAudio for macOS x64
│   └── ...                        # Other platforms
└── Ownaudio.Native.csproj
```

## LibraryLoader

The `LibraryLoader` class uses .NET's built-in `NativeLibrary` API (introduced in .NET Core 3.0) for reliable cross-platform library loading.

### Search Order

1. Application's `runtimes/{rid}/native/` folder (bundled libraries)
2. Application's output directory
3. System-specific paths (for PortAudio):
   - **macOS ARM64**: `/opt/homebrew/opt/portaudio/lib/`
   - **macOS x64**: `/usr/local/opt/portaudio/lib/`
   - **Linux**: `/usr/lib/{arch}-linux-gnu/`
4. System library path (LD_LIBRARY_PATH / DYLD_LIBRARY_PATH)

### Installing System PortAudio

To use system-installed PortAudio for better performance:

**macOS** (Homebrew):
```bash
brew install portaudio
```

**Linux** (Debian/Ubuntu):
```bash
sudo apt-get install portaudio19-dev libportaudio2
```

**Linux** (Fedora):
```bash
sudo dnf install portaudio portaudio-devel
```

**Linux** (Arch):
```bash
sudo pacman -S portaudio
```

## NativeAudioEngine Implementation

### Current Status

✅ **Completed**:
- Backend detection and selection logic
- PortAudio initialization and device enumeration
- PortAudio playback callback with lock-free ring buffers
- PortAudio input (recording) support
- Device listing (input/output devices)
- Start/Stop control
- Proper resource disposal

⏳ **In Progress**:
- MiniAudio backend implementation (callback and initialization)
- Device switching (SetOutputDeviceByName/Index)

### Example Usage

```csharp
using Ownaudio.Core;
using Ownaudio.Native;

// Create and initialize engine
var engine = new NativeAudioEngine();
var config = AudioConfig.Default; // 48kHz, 2 channels, 512 frames/buffer

int result = engine.Initialize(config);
if (result != 0)
{
    Console.WriteLine($"Failed to initialize: {result}");
    return;
}

// Start playback
engine.Start();

// Send audio data (non-blocking)
float[] audioSamples = new float[config.FramesPerBuffer * config.Channels];
// ... fill audioSamples ...
engine.Send(audioSamples);

// Stop and cleanup
engine.Stop();
engine.Dispose();
```

## Thread Safety & Performance

The engine follows OwnAudioSharp's zero-allocation and real-time safety principles:

- **Lock-free ring buffers** (`LockFreeRingBuffer<float>`) for cross-thread communication
- **Zero allocations** in audio callback path
- **Pre-allocated buffers** for audio processing
- **Non-blocking Send()** - writes to ring buffer without waiting
- **Real-time safe** - suitable for professional audio applications

### Threading Model

```
User Thread                     Audio RT Thread
    │                                 │
    ├─ Send(samples) ──────►  Ring Buffer ──────► Callback reads
    │  (non-blocking)               │               │
    │                               │               ├─ Processes audio
    │                               │               └─ Outputs to device
    │                               │
    └─ Receives() ◄────────── Ring Buffer ◄────── Callback writes
       (non-blocking)                              (input mode)
```

## Integration with AudioEngineFactory

Once fully implemented, update `Ownaudio.Core/AudioEngineFactory.cs`:

```csharp
private static IAudioEngine CreateNativeEngine()
{
    try
    {
        var assembly = Assembly.Load("Ownaudio.Native");
        var type = assembly.GetType("Ownaudio.Native.NativeAudioEngine");
        return (IAudioEngine)Activator.CreateInstance(type);
    }
    catch
    {
        // Fallback to platform-specific engines
        return null;
    }
}
```

And prioritize it in the `Create()` method:

```csharp
public static IAudioEngine Create(AudioConfig config)
{
    // Try native engine first
    var nativeEngine = CreateNativeEngine();
    if (nativeEngine != null)
    {
        if (nativeEngine.Initialize(config) == 0)
            return nativeEngine;
        nativeEngine.Dispose();
    }

    // Fallback to platform-specific engines
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        return CreateWindowsEngine();
    // ... etc
}
```

## Decoder Integration

The engine will use MiniAudio's decoder for all audio formats:

- **MP3**: Frame-based decoding with layer 1/2/3 support
- **WAV**: PCM and IEEE float formats
- **FLAC**: Lossless compression

Decoder implementation location: `Ownaudio.Core/Decoders/MiniAudio/`

## Build Requirements

- **.NET 8.0+ SDK** (project currently targets .NET 8.0)
- **Platform-specific notes**:
  - Windows: No additional dependencies
  - Linux: Optional `libportaudio2` for system PortAudio
  - macOS: Optional `brew install portaudio` for system PortAudio

## Testing

Test project location: `OwnAudioTests/Ownaudio.EngineTest/`

Recommended tests:
1. Backend selection logic (PortAudio vs MiniAudio)
2. Device enumeration
3. Playback functionality
4. Recording functionality
5. Ring buffer performance
6. Thread safety

## Contributing

When contributing to the native engine:

1. Maintain zero-allocation design in audio callback paths
2. Use lock-free structures for cross-thread communication
3. Test on all supported platforms
4. Follow OwnAudioSharp coding standards
5. Update this README with any architectural changes

## License

Copyright © 2025 Ownaudio Team
Part of the OwnAudioSharp project

## See Also

- [OwnAudioSharp Documentation](https://modernmube.github.io/OwnAudioSharp/)
- [CLAUDE.md](../../CLAUDE.md) - Project development guidelines
- [THREAD_BLOCKING_ANALYSIS.md](../../THREAD_BLOCKING_ANALYSIS.md) - Threading constraints
