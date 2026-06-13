# OwnAudioSharp

**Cross-platform audio library for .NET desktop applications**

OwnAudioSharp is a professional-grade audio engine providing high-performance audio playback, recording, and processing for Windows, Linux, and macOS with zero external dependencies.

## Key Features

- **Native Audio Engine**: Built on PortAudio and MiniAudio for professional-grade, low-latency audio processing across all platforms
- **Multi-format Support**: Built-in decoders for MP3, WAV, and FLAC. If FFmpeg 7/8 is installed on the system, it is used automatically as the primary decoder, adding support for AAC, OGG, Opus, WMA, AIFF, and virtually any other format — no code changes required.
- **Real-time Processing**: Zero-allocation design with lock-free buffers for professional-grade performance
- **Advanced Audio Features**:
  - **Network Synchronization**: Multi-device audio sync across local network (< 5ms accuracy on LAN)
  - **Master Clock**: Sample-accurate timeline synchronization for multi-track playback
  - **SmartMaster Effect**: Intelligent audio mastering with auto-calibration
  - AI-powered vocal removal (ONNX-based neural separation)
  - Audio matchering and mastering
  - Real-time chord detection
  - Built-in effects and DSP routines

## Quick Start

```csharp
using OwnaudioNET;
using OwnaudioNET.Features.Vocalremover;

// Initialize the audio engine
OwnaudioNet.Initialize();
OwnaudioNet.Start();

// Create the audio mixer using the underlying engine
var mixer = new AudioMixer(OwnaudioNet.Engine.UnderlyingEngine);
mixer.Start();

// Play an audio file
var music = new FileSource("music.mp3");
mixer.AddSource(music);

// Synchronized Multi-track Playback (Master Clock)
var vocals = new FileSource("vocals.wav");
var backing = new FileSource("backing.mp3");

mixer.AddSource(vocals);
mixer.AddSource(backing);

// Attach sources to the Master Clock for sample-accurate sync
vocals.AttachToClock(mixer.MasterClock);
backing.AttachToClock(mixer.MasterClock);

// Start sources individually
vocals.Play();
backing.Play();

// Network Synchronization - Multi-Device Audio
// Server mode (control device)
await OwnaudioNet.StartNetworkSyncServerAsync(port: 9876);

// Client mode (follower devices)
await OwnaudioNet.StartNetworkSyncClientAsync(
    serverAddress: "192.168.1.100",  // Or null for auto-discovery
    allowOfflinePlayback: true);

// All clients automatically follow server commands
// Perfect for multi-room audio, DJ setups, installations

// AI Vocal Removal
var options = new SimpleSeparationOptions 
{ 
    Model = InternalModel.Best, 
    OutputDirectory = "output" 
};

using var separator = new SimpleAudioSeparationService(options);
separator.Initialize();
var result = separator.Separate("song.mp3");
// result.VocalsPath and result.InstrumentalPath contain the output files
```

## FFmpeg Integration (Optional)

OwnAudioSharp automatically detects FFmpeg dynamic libraries on startup. This is **not part of the public API** — it happens transparently in the decoder layer.

**Decoder priority:** FFmpeg → MiniAudio (native) → built-in managed decoder

```csharp
using Ownaudio.Core;

// Optional: set a custom path before first use (default: empty = system paths)
FFmpegConfig.CustomLibraryPath = @"C:\ffmpeg\bin"; // Windows example

// Check whether FFmpeg was detected successfully
if (FFmpegConfig.IsAvailable)
    Console.WriteLine("FFmpeg decoder active — extended format support enabled.");
else
    Console.WriteLine("FFmpeg not found — using built-in decoders (MP3/WAV/FLAC).");

// No API changes needed — AudioDecoderFactory selects the best decoder automatically
var decoder = AudioDecoderFactory.Create("audio.aac", targetSampleRate: 48000, targetChannels: 2);
```

**Installation per platform:**
- **Windows:** Place `avcodec-61.dll`, `avformat-61.dll`, `avutil-59.dll`, `swresample-5.dll` next to the executable, or anywhere on `PATH`.
- **macOS:** `brew install ffmpeg`
- **Linux:** `sudo apt install libavcodec-dev libavformat-dev` (or equivalent)

## Platform Support

- **Windows**: Windows 10/11 (x64, ARM64)
- **Linux**: Ubuntu, Debian, Fedora, etc. (x64, ARM64)
- **macOS**: macOS 10.13+ (x64, ARM64)

## Architecture

OwnAudioSharp uses a two-layer architecture:

1. **Engine Layer**: Low-level platform-specific audio processing with real-time thread management
2. **API Layer**: High-level thread-safe wrappers with lock-free ring buffers to prevent UI blocking

## Documentation

- GitHub: https://github.com/ModernMube/OwnAudioSharp
- Documentation: https://modernmube.github.io/OwnAudioSharp/

## License

MIT License - Copyright (c) 2025 ModernMube
