<div align="center">
    <img src="Ownaudiologo.png" width="800">
</div>

<div align="center">
  <a href="https://www.nuget.org/packages/OwnAudioSharp">
    <img src="https://img.shields.io/badge/Nuget-OwnAudioSharp%20Nuget%20Package-blue" alt="OwnAudioSharp Package">
  </a>
  <a href="https://www.buymeacoffee.com/ModernMube">
    <img src="https://img.shields.io/badge/Support-Buy%20Me%20A%20Coffee-orange" alt="Buy Me a Coffee">
  </a>
</div>

<div align="center">
  <a href="https://modernmube.github.io/OwnAudioSharp/documents/api-net.html">
    <img src="https://img.shields.io/badge/Ownaudio%20NET%20API-blue" width="175">
  </a>
  <a href="https://modernmube.github.io/OwnAudioSharp/documents/matchering.html">
    <img src="https://img.shields.io/badge/Audio%20Matchering%20API-blue" width="200">
  </a>
  <a href="https://modernmube.github.io/OwnAudioSharp/documents/vocalremover.html">
    <img src="https://img.shields.io/badge/Vocal%20Remover%20API-blue" width="180">
  </a>
  <a href="https://modernmube.github.io/OwnAudioSharp/documents/chorddetect.html">
    <img src="https://img.shields.io/badge/Chord%20Detect%20API-blue" width="165">
  </a>
</div>

<div align="center">
  <a href="https://github.com/ModernMube/OwnAudioSharp/tree/master/OwnAudio/Examples">
    <img src="https://img.shields.io/badge/Examples-Ownaudio%20Example%20Projects-red" width="370">
  </a>
</div>

##

**OwnAudioSharp** is a cross-platform audio framework with advanced features for professional audio applications. It combines native C++ engines for real-time audio processing with fully managed C# implementations, providing efficient and flexible audio solutions across all major platforms.

## 🎯 Platform Support

- **Windows** - Native C++ and managed C# engines
- **macOS** - Native C++ and managed C# engines  
- **Linux** - Native C++ and managed C# engines
- **Android** - Managed C# engine
- **iOS** - Native C++ engine

## 🚀 Why OwnAudioSharp?

OwnAudioSharp simplifies audio application development in C# by providing a comprehensive, easy-to-use API with professional-grade features:

### Core Features

- **Synchronized Multi-Track Playback** - Play multiple audio files in perfect sync using a central time clock, ideal for multitrack applications
- **Real-Time Tempo & Pitch Control** - Adjust playback speed and pitch in real-time, even across multiple tracks simultaneously
- **15 Professional Real-Time Effects** - Freely combine effects on inputs and outputs including reverb, EQ, compression, and more
- **Network-Synchronized Playback** - Synchronize audio across multiple devices using server-client architecture
- **Simple Recording & Playback** - Straightforward API for audio capture and playback
- **SmartMaster Auto-Calibration** - Measure speakers with a microphone and automatically correct audio output for optimal sound quality
- **Chord Detection** - Detect chords from audio files for music analysis and transcription
- **Automatic Mastering** - Master audio based on reference tracks or built-in presets
- **Vocal & Music Separation** - Separate audio into stems: vocals + music, or drums + bass + music + vocals

**💻 Try it yourself!** Working demo code for all features is available in the [Examples directory](https://github.com/ModernMube/OwnAudioSharp/tree/master/OwnAudio/Examples). Each example project demonstrates how to use these features in real applications.

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
- **Optional:** PortAudio library for best performance (automatically falls back to embedded miniaudio)

## 📚 Documentation

**Complete API documentation with examples is available on the official website:**

<div align="center">
  <a href="https://modernmube.github.io/OwnAudioSharp/">
    <img src="https://img.shields.io/badge/📖_Full_API_Documentation-OwnAudioSharp_Website-blue?style=for-the-badge" alt="Documentation" width="400">
  </a>
</div>

The website includes:
- Complete API reference for all classes and methods
- Step-by-step tutorials and usage examples
- Architecture and design documentation
- Best practices and performance tips
- Professional feature guides (vocal removal, mastering, chord detection)

## 💡 Support

**OwnAudioSharp is completely free and open-source**, providing professional-grade audio features without licensing costs. If you find this library useful, especially for commercial purposes, consider supporting its continued development:

<div align="center">
  <a href="https://www.buymeacoffee.com/ModernMube" target="_blank">
    <img src="https://cdn.buymeacoffee.com/buttons/v2/arial-yellow.png" alt="Buy Me A Coffee" style="height: 60px !important;width: 217px !important;">
  </a>
</div>

**Why support?**
- Enables continued development and new features
- Ensures timely bug fixes and updates
- Improves documentation and examples
- Saves you thousands in commercial audio library licensing costs

Your support helps keep professional audio technology accessible to everyone!

## 📄 License

See the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgements

Special thanks to the creators of:
- [DryWetMidi](https://github.com/melanchall/drywetmidi) - .NET library to work with MIDI data and MIDI devices
- [soundtouch.net](https://github.com/owoudenberg/soundtouch.net) - .NET wrapper for SoundTouch
- [Avalonia](https://github.com/AvaloniaUI/Avalonia) - Cross-platform .NET UI framework
