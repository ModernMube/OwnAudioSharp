<div align="center">
  <img src="Ownaudiologo.png" alt="Logo" width="600"/>
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

## 🎯 NEW in v2.5.0: Professional Audio Features

### MasterClock Timeline-Based Synchronization

OwnAudioSharp v2.4.0 introduces **MasterClock**, a professional timeline-based synchronization system replacing the legacy GhostTrack architecture. MasterClock provides sample-accurate synchronization with automatic drift correction, dropout event monitoring, DAW-style timeline regions, and global tempo control. The legacy GhostTrack/AudioSynchronizer system is deprecated and will be removed in v3.0.0. See the [MultitrackPlayer example](OwnAudio/Examples/Ownaudio.Example.MultitrackPlayer/) for a complete implementation with real-time dropout monitoring and professional synchronization.

### 🎚️ SmartMaster Effect - Intelligent Audio Mastering

OwnAudioSharp v2.5.0 introduces **SmartMaster** is a comprehensive master processing chain that combines professional-grade effects with intelligent auto-calibration for optimal sound across different speaker systems.

#### Key Features

✅ **Factory Presets** - Pre-configured settings for different playback systems:
- **Default** - Transparent passthrough (no processing)
- **HiFi** - Bookshelf/tower speakers with gentle bass enhancement
- **Headphone** - Balanced response for studio/consumer headphones
- **Studio** - Professional monitors with flat, accurate response
- **Club** - DJ/club systems with heavy bass and presence boost
- **Concert** - Medium PA systems with compensated frequency response

✅ **Auto-Calibration Wizard** - Intelligent measurement system:
- **Requires measurement microphone** (external hardware needed for calibration)
- Automatic speaker detection and analysis
- Frequency response measurement with FFT analysis
- Phase alignment detection
- Time-of-arrival (TOA) measurement
- Automatic EQ correction based on measurements
- **Note:** Without a measurement microphone, only factory presets are available

✅ **Professional Processing Chain**:
1. **31-Band Graphic EQ** - Precise frequency shaping
2. **Subharmonic Synthesizer** - Enhanced low-frequency extension
3. **Multiband Compressor** - Dynamic range control
4. **Crossover Filter** - Linkwitz-Riley 4th order (configurable frequency)
5. **Phase & Time Alignment** - Delay compensation for multi-way systems
6. **Brick-Wall Limiter** - Peak protection and loudness maximization

✅ **Custom Preset Management**:
- Save your calibrated settings
- Load and share custom presets
- Reset to factory defaults anytime

#### Try It Now!

**SmartMaster is fully integrated into the MultitrackPlayer example application!**

```bash
# Run the MultitrackPlayer example
cd OwnAudio/Examples/Ownaudio.Example.MultitrackPlayer
dotnet run
```

The MultitrackPlayer UI includes:
- **Enable/Disable Toggle** - Turn SmartMaster on/off in real-time
- **Factory Preset Selection** - Choose from 6 speaker-specific presets
- **Auto Calibration** - Run the measurement wizard with progress tracking
- **Custom Presets** - Save and load your own configurations

#### Quick Start - Code Example

```csharp
using OwnaudioNET.Effects.SmartMaster;

// Initialize SmartMaster
var smartMaster = new SmartMasterEffect();
smartMaster.Initialize(engine.Config);

// Option 1: Use factory preset
smartMaster.LoadSpeakerPreset(SpeakerType.HiFi);
smartMaster.Enabled = true;

// Option 2: Run auto-calibration
await smartMaster.StartMeasurementAsync();
// Measurement wizard guides through speaker detection and analysis

// Option 3: Load custom preset
smartMaster.Load("my_custom_setup");

// Apply to mixer output
mixer.AddMasterEffect(smartMaster);
```

#### Factory Preset Characteristics

| Preset | Use Case | Bass | Mids | Highs | Crossover | Compression |
|--------|----------|------|------|-------|-----------|-------------|
| **Default** | Reference | Flat | Flat | Flat | None | Off |
| **HiFi** | Home stereo | +1.5dB | Flat | +1.0dB | 40Hz | Light |
| **Headphone** | Personal listening | +0.5dB | Flat | -1.0dB | None | Gentle |
| **Studio** | Production | +0.3dB | Flat | Flat | 35Hz | Minimal |
| **Club** | DJ/Dance | +4.0dB | Flat | +2.5dB | 100Hz | Moderate |
| **Concert** | Live PA | +3.0dB | -0.8dB | +2.0dB | 80Hz | Moderate |

#### Advanced Features

**Subharmonic Synthesis**: Generates harmonically-related low frequencies below the speaker's natural cutoff, enhancing bass response on smaller systems.

**Phase Alignment**: Automatically compensates for time-of-arrival differences between drivers in multi-way speaker systems, ensuring coherent sound.

**Adaptive Crossover**: Linkwitz-Riley 4th order filters provide perfect reconstruction when summed, maintaining phase coherence across the crossover region.

**Measurement Engine**: Uses white noise test signals and FFT analysis to measure actual speaker response, then calculates optimal EQ correction automatically.

See the **[SmartMaster in MultitrackPlayer](OwnAudio/Examples/Ownaudio.Example.MultitrackPlayer/)** for a complete working example with full UI integration!

> **📖 Detailed Documentation:** For comprehensive information about SmartMaster including all parameters, technical details, and advanced usage examples, see the **[Effects Documentation](documents/effects.html#master)**.

## ⚠️ Version History

**Version 2.1.0+ (Current)** - Native engine with PortAudio/miniaudio backends

**Version 2.0.0** - Attempted pure managed code (GC issues discovered)

**Pre-2.0.0** - Native libraries (miniaudio, portaudio, ffmpeg)

## ✨ Key Features

### Professional Audio Features (Free!)

Features you typically only find in commercial software:

- **AI Vocal Separation**: State-of-the-art vocal and instrumental track separation using ONNX neural networks
  - Multiple quality models: `default`, `best`, `karaoke`
  - Professional-grade stem isolation

- **Audio Mastering**: AI-driven matchering - master your tracks to match reference audio
  - Automatic EQ matching and spectral analysis
  - Professional mastering without expensive plugins

- **Advanced Chord Detection**: Musical chord recognition from simple to professional
  - Real-time and offline analysis
  - Major, minor, diminished, augmented, extended chords (7th, 9th, 11th, 13th)
  - Chromagram-based recognition

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

For low-level engine development or platform-specific optimization, check out the individual platform documentation.

## 🚀 Quick Start Examples

### Using the Native Engine (Recommended)

```csharp
using Ownaudio.Core;

// Create NATIVE audio engine (DEFAULT - GC-free!)
// Automatically uses PortAudio if available, otherwise miniaudio
using var engine = AudioEngineFactory.CreateDefault();

// Configure and start the engine
var config = new AudioConfig
{
    SampleRate = 48000,
    Channels = 2,
    BufferSize = 512
};
engine.Initialize(config);
engine.Start();

// Create decoder and play audio
using var decoder = AudioDecoderFactory.Create("music.mp3",
    targetSampleRate: 48000,
    targetChannels: 2);

while (true)
{
    var result = decoder.DecodeNextFrame();
    if (result.IsEOF) break;

    // Native engine - no GC glitches!
    engine.Send(result.Frame.Samples);
}

engine.Stop();
```

### Using OwnaudioNET (High-Level API)

```csharp
// Initialize OwnaudioNET (uses native engine by default)
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

### Using Managed Engines (Optional)

```csharp
// Create MANAGED audio engine (pure C#)
// Note: May experience GC-related audio glitches
using var engine = AudioEngineFactory.CreateManaged();

// Or platform-specific:
#if WINDOWS
    using var engine = new WasapiEngine();
#elif LINUX
    using var engine = new PulseAudioEngine();
#elif MACOS
    using var engine = new CoreAudioEngine();
#endif

engine.Initialize(AudioConfig.Default);
engine.Start();
// ... rest of your code
```

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
