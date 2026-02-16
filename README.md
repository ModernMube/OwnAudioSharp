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
  <a href="https://modernmube.github.io/OwnAudioSharp/documents/effects.html">
    <img src="https://img.shields.io/badge/Effects%20and%20VST3_plugins-blue" width="230">
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
- **VST3 Plugin Support** - Load and use VST3 audio effect plugins with cross-platform editor GUI

### 🎛️ **SmartMaster** - Intelligent Audio Mastering Chain with Auto-Calibration
Professional audio mastering system with automatic speaker calibration using an external measurement microphone. The system analyzes your speakers' frequency response and automatically optimizes the audio output for optimal sound quality. Includes speaker profiles (HiFi, Headphone, Studio, Club, Concert), 31-band EQ, multiband compression, and brick-wall limiting.

**Use cases:** Automatic room correction, speaker calibration, broadcast preparation, professional mastering

### 🌐 **NetworkSync** - Multi-Device Audio Synchronization
Synchronize audio playback across multiple devices on your local network with sample-accurate precision (<5ms on LAN). Zero-configuration with automatic server discovery, perfect for multi-room audio, live performances, DJ setups, and synchronized installations.

**Use cases:** Multi-room audio systems, live PA setups, museum installations, collaborative production

### 🎵 **Audio Matchering** - AI-Driven Audio Mastering
Reference-based mastering that analyzes your favorite tracks and applies their sonic characteristics to your audio. Automatic EQ matching and spectral processing deliver professional mastering results without expensive plugins.

**Use cases:** Music production, podcast mastering, audio restoration

### 🎤 **Vocal Remover** - AI Vocal Separation with HTDemucs
State-of-the-art vocal and instrumental track separation using ONNX neural networks. Features the advanced **HTDemucs** model for professional-grade 4-stem separation (vocals, drums, bass, other) with margin-trimming technology to eliminate chunk boundary artifacts. Multiple quality models available including `htdemucs` (4-stem), `default`, `best`, and `karaoke` models. Provides professional-grade stem isolation for remixing, karaoke, and audio analysis.

**HTDemucs Features:**
- **4-Stem Separation**: Isolate vocals, drums, bass, and other instruments independently
- **Margin-Trimming**: Advanced processing eliminates volume fluctuations at chunk boundaries
- **High Quality**: Superior separation quality using hybrid transformer architecture
- **Example Code**: See [VocalRemover Example](OwnAudio/Examples/Ownaudio.Example.HTDemucs/) for complete implementation

> [!IMPORTANT]
> **HTDemucs Model Setup**
> 
> **For NuGet Package Users:** The HTDemucs model is **included in the NuGet package** - no manual download required!
> 
> **For Source Code / Building from Source:**
> 1. Download: [htdemucs.onnx](https://huggingface.co/ModernMube/HTDemucs_onnx/resolve/main/htdemucs.onnx) (166 MB)
> 2. Copy to the same directory as other vocal remover models
> 3. The model will be automatically detected by the API

**Use cases:** Karaoke creation, remixing, vocal analysis, instrumental extraction, stem mastering

### 🎸 **Chord Detection** - Advanced Musical Analysis
Real-time and offline chord recognition supporting major, minor, diminished, augmented, and extended chords (7th, 9th, 11th, 13th). Chromagram-based analysis provides accurate recognition from simple to professional chord structures.

**Use cases:** Music transcription, chord chart generation, music education, DJ software

---

**📚 Complete Documentation:** Visit the [OwnAudioSharp website](https://modernmube.github.io/OwnAudioSharp/) for detailed API documentation, tutorials, and usage examples.

**💻 Working Examples:** See the [Examples directory](OwnAudio/Examples/) for complete, runnable projects demonstrating each feature in action.



## ⚠️ Version History

**Version 2.1.0+ (Current)** - Native engine with PortAudio/miniaudio backends

**Version 2.0.0** - Attempted pure managed code (GC issues discovered)

**Pre-2.0.0** - Native libraries (miniaudio, portaudio, ffmpeg)

### Core Engine Features

- **Native C++ Audio Engine (Default)**:
  - GC-free, deterministic real-time audio processing
  - PortAudio backend (if installed) or embedded miniaudio fallback
  - Professional-grade performance on all platforms
  - Zero audio glitches or dropouts

- **Managed C# Engines (Optional)**:
  - Windows (WASAPI), macOS (Core Audio), Linux (PulseAudio), Android (AAudio)
  - Pure C# implementation for development and debugging
  - Available but may experience GC-related glitches

- **Dual API Layers**:
  - Low-level Core API for direct engine control
  - High-level NET API for professional features

- **Audio Processing**:
  - Multi-format support (MP3, WAV, FLAC) with built-in decoders
  - Real-time effects: reverb, compressor, equalizer, pitch shifting, tempo control
  - Multi-source audio mixing with synchronized playback

- **High Performance**:
  - Native engine: Zero GC interference
  - Lock-free ring buffers for thread safety
  - SIMD-optimized audio processing

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
