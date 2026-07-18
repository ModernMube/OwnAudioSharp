# OwnAudioSharp.Basic

**Lightweight cross-platform audio library for .NET desktop and mobile applications**

OwnAudioSharp.Basic is a professional-grade audio engine providing high-performance audio playback, recording, and processing for Windows, Linux, macOS, Android, and iOS — without AI/ML dependencies, making it significantly smaller and faster to install than the full `OwnAudioSharp` package.

## Why Basic?

| Feature | OwnAudioSharp | OwnAudioSharp.Basic |
|---|---|---|
| Audio playback & recording | ✅ | ✅ |
| Multi-track mixing | ✅ | ✅ |
| Effects & SmartMaster | ✅ | ✅ |
| Network synchronization | ✅ | ✅ |
| VST3 plugin support | ✅ | ✅ |
| Pitch shift / time stretch | ✅ | ✅ |
| AI vocal removal (ONNX) | ✅ | ❌ |
| Chord detection | ✅ | ❌ |
| Audio matchering | ✅ | ❌ |
| Wave visualization control | ✅ | ❌ |
| Package size (models) | ~290 MB | < 5 MB |

Use `OwnAudioSharp.Basic` when you need a lean audio engine without the overhead of large ONNX model files and AI inference dependencies.

## Key Features

- **Native Rust Audio Engine**: Built on a purpose-built native Rust core for professional-grade, low-latency audio. Device I/O, mixing, and the full effect chain run entirely in native code with a real-time-safe hot path — no PortAudio or MiniAudio dependency.
- **Multi-format Support**: Native pure-Rust decoder (Symphonia) with built-in support for WAV, MP3, FLAC, OGG/Vorbis, AAC/M4A, and AIFF. For any other format, FFmpeg is used automatically as a fallback when installed — no code changes required.
- **Real-time Processing**: Zero-allocation design with lock-free buffers and native mixing for professional-grade performance
- **Advanced Audio Features**:
  - **Network Synchronization**: Multi-device audio sync across local network (< 5ms accuracy on LAN)
  - **Master Clock**: Sample-accurate timeline synchronization for multi-track playback
  - **SmartMaster Effect**: Intelligent audio mastering with auto-calibration
  - **VST3 Plugin Support**: Load and host VST3 audio effect plugins
  - Built-in effects and DSP routines (EQ, compressor, reverb, etc.)
  - Native pitch shifting and time stretching (Rust SoundTouch)

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

// Apply effects
var source = new FileSource("music.mp3");
source.Effects.Add(new ReverbEffect { RoomSize = 0.5f });
source.Effects.Add(new EqualizerEffect());
mixer.AddSource(source);

// Per-source controls (built into every source)
var pitchedSource = new FileSource("music.mp3");
pitchedSource.Volume = 0.8f;    // 80% volume
pitchedSource.Pan = -0.3f;      // stereo pan: -1 left … 0 center … +1 right
pitchedSource.PitchShift = 2;   // Shift up 2 semitones
pitchedSource.Tempo = 1.1f;     // 10% faster
mixer.AddSource(pitchedSource);
```

## Platform Support

### Desktop
- **Windows**: Windows 10/11 (x64, x86, ARM64)
- **Linux**: Ubuntu, Debian, Fedora, etc. (x64, ARM, ARM64)
- **macOS**: macOS 10.13+ (x64, ARM64)

### Mobile
- **Android**: Android 7.0 (API 24)+ (ARM, ARM64, x64)
- **iOS**: iOS 12.2+ (ARM64)

## What Is NOT Included

The following features from the full `OwnAudioSharp` package are **not available** in the Basic edition:

- **VocalRemover** — AI-powered source separation using ONNX neural networks (`nmp.onnx`, `best.onnx`, `default.onnx`, `karaoke.onnx`, `htdemucs.onnx`)
- **ChordDetector** — Real-time and offline chord recognition
- **Matchering** — Reference-based audio mastering
- **WaveDisplayControl** — Avalonia-based waveform visualization UI control

If you need any of these features, use the full [`OwnAudioSharp`](https://www.nuget.org/packages/OwnAudioSharp) package instead.

## Audio Decoding

Decoding is handled by the native Rust engine using a pure-Rust Symphonia backend — no managed decoder and no external runtime is required for the common formats:

**Natively supported (built-in):** WAV, MP3, FLAC, OGG/Vorbis, AAC/M4A, AIFF

For any format the native backend cannot handle, OwnAudioSharp transparently falls back to FFmpeg when it is installed on the system. This is **not part of the public API** — the decoder layer selects the best backend automatically, with no code changes required.

**Decoder priority:** native Rust (Symphonia) → FFmpeg (optional fallback)

## Architecture

OwnAudioSharp.Basic uses a two-layer architecture:

1. **Native Rust Engine Layer**: Device I/O, multi-track mixing, resampling, and the full effect chain run in a native Rust core. Audio data stays in native memory on a real-time-safe, allocation-free hot path.
2. **Managed API Layer**: High-level thread-safe wrappers that drive the native engine through a lock-free FFI boundary, issuing only control commands so the UI thread is never blocked.

```
UI/Main Thread
  └─> Managed control API (Play/Seek/effect params) [lock-free, <0.1ms]
       └─> FFI command queue [lock-free, allocation-free]
            └─> Native Rust engine (mix + effects + device I/O)
                 └─> OS audio callback [real-time thread]
```

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
