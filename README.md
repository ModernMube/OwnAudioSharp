<div align="center">
    <img src="Ownaudiologo.png">
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

**OwnAudioSharp** is a professional cross-platform audio framework featuring a **native C++ audio engine** for glitch-free, real-time audio processing. After extensive development with pure managed code, we discovered that the .NET Garbage Collector is insurmountable for professional real-time audio - the native engine is now the default, with managed C# implementations available for development and testing.

### Why OwnAudioSharp?

**Native Engine (Default)**: GC-free C++ audio core eliminates glitches and provides deterministic real-time performance
**Managed Engines (Optional)**: Pure C# implementations available for development, testing, and debugging
**Professional Audio Features**: AI-driven vocal separation, audio mastering, advanced chord detection
**Commercial Quality, Free**: Professional tools without licensing costs
**Truly Cross-Platform**: Windows, macOS, Linux, Android, iOS

## 🎯 Advanced API Features

OwnAudioSharp includes professional-grade features typically found only in commercial software. These APIs provide powerful capabilities for advanced audio processing, synchronization, and analysis:

### 🎚️ **MasterClock** - Sample-Accurate Multi-Track Synchronization
Perfect timeline synchronization for multi-track audio with automatic drift correction and DAW-style regions. Essential for professional music production, video editing, and any application requiring precise timing across multiple audio sources.

**Use cases:** Multi-track recording, audio/video sync, live performance systems

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
- .NET 8.0 or later
- **Optional:** PortAudio library for best performance (automatically falls back to embedded miniaudio if not available)

### Native Engine Dependencies (Automatic)
The native engine includes:
- **PortAudio** (preferred) - Install separately for best performance
- **miniaudio** (fallback) - Embedded as resource, always available
- No installation required - works out of the box with miniaudio

## 📚 Documentation & API Reference

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
