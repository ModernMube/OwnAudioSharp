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
| SoundTouch (pitch/tempo) | ✅ | ✅ |
| AI vocal removal (ONNX) | ✅ | ❌ |
| Chord detection | ✅ | ❌ |
| Audio matchering | ✅ | ❌ |
| Wave visualization control | ✅ | ❌ |
| Package size (models) | ~290 MB | < 5 MB |

Use `OwnAudioSharp.Basic` when you need a lean audio engine without the overhead of large ONNX model files and AI inference dependencies.

## Key Features

- **Native Audio Engines**: Built on PortAudio and MiniAudio (all platforms) for professional-grade audio processing
- **Managed Wrappers**: Platform-specific managed engines with WASAPI (Windows), PulseAudio (Linux), and Core Audio (macOS) integration
- **Multi-format Support**: Built-in decoders for MP3, WAV, and FLAC
- **Real-time Processing**: Zero-allocation design with lock-free buffers for professional-grade performance
- **Advanced Audio Features**:
  - **Network Synchronization**: Multi-device audio sync across local network (< 5ms accuracy on LAN)
  - **Master Clock**: Sample-accurate timeline synchronization for multi-track playback
  - **SmartMaster Effect**: Intelligent audio mastering with auto-calibration
  - **VST3 Plugin Support**: Load and host VST3 audio effect plugins
  - Built-in effects and DSP routines (EQ, compressor, reverb, etc.)
  - Pitch shifting and time stretching via SoundTouch

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

// Pitch shifting / time stretching
var pitchedSource = new SoundTouchSource("music.mp3");
pitchedSource.SetPitchSemitones(2);   // Shift up 2 semitones
pitchedSource.SetTempo(1.1f);         // 10% faster
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

## Architecture

OwnAudioSharp.Basic uses a two-layer architecture:

1. **Engine Layer**: Low-level platform-specific audio processing with real-time thread management
2. **API Layer**: High-level thread-safe wrappers with lock-free ring buffers to prevent UI blocking

```
UI/Main Thread
  └─> OwnaudioNet.Send() [lock-free, <0.1ms]
       └─> AudioEngineWrapper [ring buffer write]
            └─> Pump Thread [dedicated]
                 └─> Audio RT Thread [platform-specific]
```

## Documentation

- GitHub: https://github.com/ModernMube/OwnAudioSharp
- Documentation: https://modernmube.github.io/OwnAudioSharp/

## License

MIT License - Copyright (c) 2025 ModernMube
