# OwnAudioSharp

**Cross-platform audio library for .NET desktop applications**

OwnAudioSharp is a professional-grade audio engine providing high-performance audio playback, recording, and processing for Windows, Linux, and macOS with zero external dependencies.

## Key Features

- **Native Audio Engines**: Built on PortAudio and MiniAudio (all platforms) for professional-grade audio processing
- **Managed Wrappers**: Platform-specific managed engines with WASAPI (Windows), PulseAudio (Linux), and Core Audio (macOS) integration
- **Multi-format Support**: Built-in decoders for MP3, WAV, and FLAC
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
