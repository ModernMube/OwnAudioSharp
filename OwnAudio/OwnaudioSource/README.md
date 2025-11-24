# OwnAudioSharp

**OwnAudioSharp** is a cross-platform managed audio engine built entirely in C# with **zero external native dependencies**. It provides professional-grade audio playback, recording, and processing capabilities through pure managed code.

## Features

- **Pure Managed Code**: No native library dependencies - runs anywhere .NET runs
- **Cross-Platform**: Windows (WASAPI), macOS (Core Audio), Linux (PulseAudio), Android (AAudio)
- **Professional Audio Processing**:
  - AI-driven vocal separation using ONNX neural networks
  - Audio mastering and matchering
  - Advanced chord detection
  - Real-time effects: reverb, compressor, equalizer, pitch shifting, tempo control
- **Multi-Format Support**: Built-in decoders for MP3, WAV, FLAC
- **High Performance**: Zero-allocation design, lock-free buffers, SIMD optimization
- **Multi-Source Mixing**: Synchronized playback with per-source volume control

## Installation

```bash
dotnet add package OwnAudioSharp
```

## Quick Start

```csharp
using Ownaudio;

// Initialize the engine
await OwnaudioNet.InitializeAsync();

// Create and play audio
var source = new FileSource("music.mp3");
var mixer = new AudioMixer(OwnaudioNet.Engine);
mixer.AddSource(source);
mixer.Start();
source.Play();

// Apply effects
var reverb = new ReverbEffect { Mix = 0.3f, RoomSize = 0.7f };
var sourceWithEffects = new SourceWithEffects(source, reverb);
```

## Requirements

- .NET 9.0 or later
- No external dependencies

## Documentation

Complete API documentation and examples: [https://modernmube.github.io/OwnAudioSharp/](https://modernmube.github.io/OwnAudioSharp/)

## Repository

GitHub: [https://github.com/ModernMube/OwnAudioSharp](https://github.com/ModernMube/OwnAudioSharp)

## License

MIT License - See LICENSE file for details
