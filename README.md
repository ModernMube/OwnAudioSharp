<div align="center">
  <img src="Ownaudiologo.png" alt="Logo" width="600"/>
</div>

<a href="https://www.buymeacoffee.com/ModernMube">
  <img src="https://img.shields.io/badge/Support-Buy%20Me%20A%20Coffee-orange" alt="Buy Me a Coffee">
</a>

<a href="https://www.nuget.org/packages/OwnAudioSharp">
  <img src="https://img.shields.io/badge/Nuget-OwnAudioSharp%20Nuget%20Package-blue" alt="OwnAudioSharp Package">
</a>

##

**OwnAudioSharp** is a cross-platform C# audio library providing professional-grade audio playback, recording, and processing. Built with pure managed code using native system audio APIs - no external dependencies required.

## ⚠️ Important Notice

**Version 2.0.0 introduces major improvements!**

Pre-2.0.0 versions relied on native libraries (miniaudio, portaudio, ffmpeg) and were less optimized. Starting from version 2.0.0, OwnAudioSharp operates with **zero external dependencies** using a fully managed audio engine.

**Key changes:**
- ✅ Fully managed audio engine across all platforms
- ✅ ~90% backward compatibility with previous API
- ✅ Significant performance improvements
- ⚠️ Legacy APIs marked as `[Obsolete]` - will be removed in future versions

**Using OwnaudioSharp version 2 with older code:**
If you need pre-2.0.0 functionality because you wrote your code for an older version of OwnaudioSharp, replace the **Ownaudio** namespaces in your code with the **OwnaudioLegacy** namespace.
for example: **Ownaudio.Source** => **OwnaudioLegacy.Source**

**Migration recommendation:** Use version 2.0.0 or later for all new projects. The new managed engine offers superior performance and maintainability.

## ✨ Key Features

- **Cross-platform**: Windows (WASAPI), macOS (Core Audio), Linux (PulseAudio), Android (Aaudio) iOS (in progress)
- **Dual API layers**: Core API (low-level control) and NET API (high-level features)
- **Audio playback**: Support for MP3, WAV, FLAC
- **Real-time processing**: Pitch shifting, tempo control, effects
- **Audio mixing**: Multi-source mixing with synchronized playback
- **Professional mastering**: AI-driven audio matchering and EQ analysis
- **Chord detection**: Automatic musical chord recognition
- **Vocal remover**: Advanced AI-driven audio separation technology
- **Zero-allocation**: Optimized performance for real-time usage

## 📦 Installation

### NuGet Package Manager
```powershell
Install-Package OwnAudioSharp
```

### .NET CLI
```bash
dotnet add package OwnAudioSharp
```

### Requirements
- .NET 9.0 or later
- No external dependencies

## 📚 Documentation

Complete documentation is available on the official website:

<a href="https://modernmube.github.io/OwnAudioSharp/">
  <img src="https://img.shields.io/badge/Documentation-OwnAudioSharp%20Website-blue" alt="Documentation" width="350">
</a>

### 🔧 Engine Architecture Documentation

OwnAudioSharp's audio engine is built on a modular architecture with platform-specific implementations. Detailed documentation is available for each component:

#### Core Components
- **[Ownaudio.Core](OwnAudioEngine/Ownaudio.Core/README.md)** - Cross-platform interfaces, audio decoders (MP3/WAV/FLAC), lock-free buffers, and zero-allocation primitives

#### Platform-Specific Implementations
- **[Ownaudio.Windows](OwnAudioEngine/Ownaudio.Windows/README.md)** - WASAPI implementation for Windows (10+)
- **[Ownaudio.Linux](OwnAudioEngine/Ownaudio.Linux/README.md)** - PulseAudio implementation for Linux
- **[Ownaudio.macOS](OwnAudioEngine/Ownaudio.macOS/README.md)** - Core Audio implementation for macOS
- **[Ownaudio.Android](OwnAudioEngine/Ownaudio.Android/README.md)** - AAudio implementation for Android (API 26+)

Each platform implementation includes:
- Architecture overview and native API details
- Performance characteristics and latency information
- Platform-specific requirements and dependencies
- Usage examples and best practices
- Troubleshooting guides

For low-level engine development or platform-specific optimization, refer to the individual platform documentation.

## 🚀 Quick Start Example

```csharp
// Initialize OwnaudioNET (async for UI apps!)
await OwnaudioNet.InitializeAsync();

// Create file source
var source = new FileSource("music.mp3");

// Create mixer and add source
var mixer = new AudioMixer(OwnaudioNet.Engine);
mixer.AddSource(source);
mixer.Start();

// Play the source
source.Play();

// Apply professional effects
var reverb = new ReverbEffect { Mix = 0.3f, RoomSize = 0.7f };
var compressor = new CompressorEffect(threshold: 0.5f, ratio: 4.0f, sampleRate: 48000f);
var sourceWithEffects = new SourceWithEffects(source, reverb, compressor);

// Control playback
source.Volume = 0.8f;
source.Seek(30.0); // seconds
```

## 💡 Support

If you find this library useful or use it for commercial purposes, consider supporting the development:

<a href="https://www.buymeacoffee.com/ModernMube" target="_blank">
  <img src="https://cdn.buymeacoffee.com/buttons/v2/arial-yellow.png" alt="Buy Me A Coffee" style="height: 60px !important;width: 217px !important;">
</a>

## 📄 License

See the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgements

Special thanks to the creators of:
- [DryWetMidi](https://github.com/melanchall/drywetmidi) - .NET library to work with MIDI data and MIDI devices
- [soundtouch.net](https://github.com/owoudenberg/soundtouch.net) - .NET wrapper for SoundTouch
- [Avalonia](https://github.com/AvaloniaUI/Avalonia) - Cross-platform .NET UI framework
