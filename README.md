<div align="center">
  <img src="Ownaudiologo.png" alt="Logo" width="600"/>
</div>

<a href="https://www.buymeacoffee.com/ModernMube">
  <img src="https://img.shields.io/badge/Support-Buy%20Me%20A%20Coffee-orange" alt="Buy Me a Coffee">
</a>

<a href="https://www.nuget.org/packages/OwnAudioSharp">
  <img src="https://img.shields.io/badge/Nuget-OwnAudioSharp%20Nuget%20Package-blue" alt="OwnAudioSharp Package">
</a>

<a href="https://github.com/ModernMube/OwnAudioSharpDemo">
  <img src="https://img.shields.io/badge/Sample-OwnAudioSharp%20Demo%20Application-darkgreen" alt="OwnAudioSharp Demo">
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

- **Cross-platform**: Windows (WASAPI), macOS (Core Audio), Linux (PulseAudio), iOS & Android (in progress)
- **Dual API layers**: Core API (low-level control) and NET API (high-level features)
- **Audio playback**: Support for MP3, WAV, FLAC and other formats
- **Real-time processing**: Pitch shifting, tempo control, effects
- **Audio mixing**: Multi-source mixing with synchronized playback
- **Professional mastering**: AI-driven audio matchering and EQ analysis
- **Chord detection**: Automatic musical chord recognition
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
- .NET 8.0 or later
- No external dependencies

## 📚 Documentation

Complete documentation is available on the official website:

<a href="https://modernmube.github.io/OwnAudioSharp/">
  <img src="https://img.shields.io/badge/Documentation-OwnAudioSharp%20Website-blue" alt="Documentation" width="350">
</a>

## 🚀 Quick Start Example

```csharp
using Ownaudio.Core;
using Ownaudio.Decoders;

// Create audio engine with default settings
using var engine = AudioEngineFactory.CreateDefault();
engine.Initialize(AudioConfig.Default);
engine.Start();

// Create decoder for audio file
using var decoder = AudioDecoderFactory.Create(
    "music.mp3",
    targetSampleRate: 48000,
    targetChannels: 2
);

// Decode and play frames
while (true)
{
    var result = decoder.DecodeNextFrame();
    if (result.IsEOF) break;
    
    engine.Send(result.Frame.Samples);
}

engine.Stop();
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
- [Bufdio](https://github.com/luthfiampas/Bufdio) - Audio playback library for .NET
- [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) - FFmpeg bindings for C#/.NET
- [soundtouch.net](https://github.com/owoudenberg/soundtouch.net) - .NET wrapper for SoundTouch
- [Avalonia](https://github.com/AvaloniaUI/Avalonia) - Cross-platform .NET UI framework
- [SoundFlow](https://github.com/LSXPrime/SoundFlow) - Cross-platform .NET audio engine
