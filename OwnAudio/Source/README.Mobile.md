# OwnAudioSharp.Mobile

**Cross-platform audio library for .NET mobile applications**

OwnAudioSharp.Mobile is a professional-grade audio engine providing high-performance audio playback, recording, and processing for Android and iOS with zero external dependencies.

## Key Features

- **Native Rust Audio Engine**: Built on a purpose-built native Rust core for high-performance, low-latency audio on Android and iOS. Device I/O, mixing, and the full effect chain run entirely in native code with a real-time-safe, zero-GC hot path — no PortAudio or MiniAudio dependency.
- **Multi-format Support**: Native pure-Rust decoder (Symphonia) with built-in support for WAV, MP3, FLAC, OGG/Vorbis, AAC/M4A, and AIFF
- **Real-time Processing**: Zero-allocation design with lock-free buffers and native mixing for professional-grade performance
- **Advanced Audio Features**:
  - **Network Synchronization**: Multi-device audio sync across WiFi (< 20ms accuracy)
  - **Master Clock**: Sample-accurate timeline synchronization for multi-track playback
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
// Perfect for: Party mode, car audio sync, wireless speakers
// Server mode (main phone/tablet)
await OwnaudioNet.StartNetworkSyncServerAsync(port: 9876);

// Client mode (other devices on WiFi)
await OwnaudioNet.StartNetworkSyncClientAsync(
    serverAddress: null,  // Auto-discovery on WiFi
    allowOfflinePlayback: true);

// All devices play in perfect sync over WiFi
// Automatic reconnection if WiFi drops

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

- **Android**: Android 7.0 (API 24)+ (ARM64, x64)
- **iOS**: iOS 11.0+ (ARM64, x64 Simulator)

## Mobile-Specific Features

- Optimized for mobile battery life
- Background audio playback support
- Integration with platform media controls
- Low-latency audio processing

## Architecture

OwnAudioSharp.Mobile uses a two-layer architecture:

1. **Native Rust Engine Layer**: Device I/O, multi-track mixing, resampling, and the full effect chain run in a native Rust core. Audio data stays in native memory on a real-time-safe, allocation-free hot path.
2. **Managed API Layer**: High-level thread-safe wrappers that drive the native engine through a lock-free FFI boundary, issuing only control commands so the UI thread is never blocked.

## Documentation

- GitHub: https://github.com/ModernMube/OwnAudioSharp
- Documentation: https://modernmube.github.io/OwnAudioSharp/

## License

MIT License - Copyright (c) 2025 ModernMube
