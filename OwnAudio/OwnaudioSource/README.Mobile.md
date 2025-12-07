# OwnAudioSharp.Mobile

**Cross-platform audio library for .NET mobile applications**

OwnAudioSharp.Mobile is a professional-grade audio engine providing high-performance audio playback, recording, and processing for Android and iOS with zero external dependencies.

## Key Features

- **Native Audio Engine**: Built on MiniAudio for high-performance, low-latency audio processing
- **Managed Wrappers**: Platform-specific managed engines with Android AudioTrack/AudioRecord and iOS Audio Queue Services integration
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

- **Android**: Android 7.0 (API 24)+ (ARM64, x64)
- **iOS**: iOS 11.0+ (ARM64, x64 Simulator)

## Mobile-Specific Features

- Optimized for mobile battery life
- Background audio playback support
- Integration with platform media controls
- Low-latency audio processing

## Architecture

OwnAudioSharp.Mobile uses a two-layer architecture:

1. **Engine Layer**: Low-level platform-specific audio processing with real-time thread management
2. **API Layer**: High-level thread-safe wrappers with lock-free ring buffers to prevent UI blocking

## Documentation

- GitHub: https://github.com/ModernMube/OwnAudioSharp
- Documentation: https://modernmube.github.io/OwnAudioSharp/

## License

MIT License - Copyright (c) 2025 ModernMube
