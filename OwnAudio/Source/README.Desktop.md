# OwnAudioSharp

**Complete cross-platform audio library for .NET desktop applications**

OwnAudioSharp is a professional-grade audio engine providing high-performance audio playback, recording, and processing for Windows, Linux, and macOS with zero external dependencies. This is the full edition: everything the library offers, including MIDI, the analysis features (chord detection, note transcription, matchering) and the Avalonia waveform display. For a lean playback/recording-only build see `OwnAudioSharp.Basic`; for Android and iOS see `OwnAudioSharp.Mobile`.

## Key Features

- **Native Rust Audio Engine**: Built on a purpose-built native Rust core for professional-grade, low-latency audio. Device I/O, mixing, and the full effect chain run entirely in native code with a real-time-safe hot path — no PortAudio or MiniAudio dependency.
- **Multi-format Support**: Native pure-Rust decoder (Symphonia) with built-in support for WAV, MP3, FLAC, OGG/Vorbis, AAC/M4A, ALAC/M4A, and AIFF — no external codecs, no FFmpeg, no system dependencies.
- **Real-time Processing**: Zero-allocation design with lock-free buffers and native mixing for professional-grade performance
- **Advanced Audio Features**:
  - **Network Synchronization**: Multi-device audio sync across local network (< 5ms accuracy on LAN)
  - **Master Clock**: Sample-accurate timeline synchronization for multi-track playback
  - **SmartMaster Effect**: Intelligent audio mastering with auto-calibration
  - Audio matchering and mastering
  - Real-time chord detection
  - Built-in effects and DSP routines
  - **Effect Analysis**: tap any effect chain for the signal on both sides of it while it plays, with a ready-made before/after spectrum analyzer

## Quick Start

```csharp
using OwnaudioNET;

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
```

### Vocal separation

ONNX-based vocal/instrumental separation is **not** part of this package. It ships separately as `OwnVocalRemover`, which keeps the original `OwnaudioNET.Features.Vocalremover` namespace, so existing code works once the package is referenced:

```bash
dotnet add package OwnVocalRemover
```

## Audio Decoding

Decoding is handled by the native Rust engine using a pure-Rust Symphonia backend — no managed decoder and no external runtime is required for the common formats:

**Natively supported (built-in):** WAV, MP3, FLAC, OGG/Vorbis, AAC/M4A, ALAC/M4A, AIFF

There is no second decoder behind this one. Versions before 4.0 could fall back to a system FFmpeg for other formats; that path was removed along with the managed engines, so a format outside the list above will not open.

## Platform Support

- **Windows**: Windows 10/11 (x64, ARM64)
- **Linux**: Ubuntu, Debian, Fedora, etc. (x64, ARM64)
- **macOS**: macOS 10.13+ (x64, ARM64)

## Architecture

OwnAudioSharp uses a two-layer architecture:

1. **Native Rust Engine Layer**: Device I/O, multi-track mixing, resampling, and the full effect chain run in a native Rust core. Audio data stays in native memory on a real-time-safe, allocation-free hot path.
2. **Managed API Layer**: High-level thread-safe wrappers that drive the native engine through a lock-free FFI boundary. The managed side only issues control commands, so the UI thread is never blocked and no managed code runs in the audio path.

## Documentation

- GitHub: https://github.com/ModernMube/OwnAudioSharp
- Documentation: https://modernmube.github.io/OwnAudioSharp/

## License

MIT License - Copyright (c) 2025 ModernMube

---

## Development Tools

This project is developed with the following tools:

| | |
|:--:|:--|
| ![Claude Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/claude.svg) | **Anthropic** — Claude Code |
| ![Visual Studio Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/vscode.svg) | **Microsoft** — Visual Studio Code |
| ![Visual Studio 2022](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/visualstudio.svg) | **Microsoft** — Visual Studio 2022 |
| ![Rider](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/rider.svg) | **JetBrains** — Rider |
