# OwnAudioSharp

**Cross-platform audio library for .NET desktop applications**

OwnAudioSharp is a professional-grade audio engine providing high-performance audio playback, recording, and processing for Windows, Linux, and macOS with zero external dependencies.

## Key Features

- **Native Audio Engines**: Built on PortAudio and MiniAudio (all platforms) for professional-grade audio processing
- **Managed Wrappers**: Platform-specific managed engines with WASAPI (Windows), PulseAudio (Linux), and Core Audio (macOS) integration
- **Multi-format Support**: Built-in decoders for MP3, WAV, and FLAC
- **Real-time Processing**: Zero-allocation design with lock-free buffers for professional-grade performance
- **Advanced Audio Features**:
  - AI-powered vocal removal (ONNX-based neural separation)
  - Audio matchering and mastering
  - Real-time chord detection
  - Multi-track mixing with synchronization
  - Built-in effects and DSP routines

## Quick Start

```csharp
using OwnAudioSharp;

// Initialize the audio engine
OwnaudioNet.Initialize(new AudioConfig
{
    SampleRate = 48000,
    Channels = 2,
    BufferSize = 512
});

// Play an audio file
OwnaudioNet.PlayFile("music.mp3");

// Use the audio mixer
var mixer = new AudioMixer(engine);
mixer.AddSource(audioSource1);
mixer.AddSource(audioSource2);
mixer.Start();

// AI vocal removal
var vocalRemover = new VocalRemover();
vocalRemover.ProcessFile("song.mp3", "vocals.wav", "instrumental.wav");
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
