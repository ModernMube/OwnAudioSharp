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

## 🎯 NEW in v2.5.3: Network Synchronization

### 🌐 NetworkSync - Multi-Device Audio Synchronization

**Synchronize audio playback across multiple devices on your local network** - perfect for multi-room audio, live performances, DJ setups, and synchronized installations.

#### Key Features

✅ **Zero-Configuration** - Automatic server discovery on local network (no internet required)
✅ **Sample-Accurate Sync** - < 5ms accuracy on LAN, < 20ms on WiFi
✅ **Graceful Degradation** - Automatic fallback to standalone mode if connection lost
✅ **Zero-GC Performance** - No impact on audio quality (< 3% CPU overhead)
✅ **Full Transport Control** - Play, Pause, Stop, Seek, Tempo synchronized across all clients
✅ **Offline Operation** - Works entirely on local network, no internet needed

#### Quick Start - Server Mode

```csharp
using OwnaudioNET;

// Initialize audio system
await OwnaudioNet.InitializeAsync();
OwnaudioNet.Start();

// Start as network sync server
await OwnaudioNet.StartNetworkSyncServerAsync(
    port: 9876,
    useLocalTimeOnly: true);  // No internet required!

// Add tracks and control playback
var mixer = OwnaudioNet.GetAudioMixer();
var track = new FileSource("song.mp3");
track.AttachToClock(mixer.MasterClock);
mixer.AddSource(track);

// All commands are automatically broadcast to clients
track.Play();   // All clients start playing
track.Pause();  // All clients pause
track.Seek(30); // All clients seek to 30 seconds
```

#### Quick Start - Client Mode

```csharp
// Initialize audio system
await OwnaudioNet.InitializeAsync();
OwnaudioNet.Start();

// Connect to server (auto-discovery or manual IP)
await OwnaudioNet.StartNetworkSyncClientAsync(
    serverAddress: "192.168.1.100",  // Or null for auto-discovery
    port: 9876,
    allowOfflinePlayback: true);     // Continue if disconnected

// Add SAME tracks as server
var mixer = OwnaudioNet.GetAudioMixer();
var track = new FileSource("song.mp3");  // Same file as server!
track.AttachToClock(mixer.MasterClock);
mixer.AddSource(track);

// Client automatically follows server commands
// Monitor connection state
OwnaudioNet.NetworkSyncConnectionChanged += (sender, e) =>
{
    Console.WriteLine($"Connection: {e.NewState}");
};
```

#### Real-World Use Cases

🎵 **Multi-Room Audio System**
```csharp
// Living room (Server)
await OwnaudioNet.StartNetworkSyncServerAsync();
// Kitchen, Bedroom, Bathroom (Clients)
await OwnaudioNet.StartNetworkSyncClientAsync();
// Perfect synchronization throughout your home!
```

🎤 **Live Performance / DJ Setup**
```csharp
// Main mixer (Server) controls everything
// Monitor speakers (Clients) follow perfectly
// Backup system (Client) ready for instant failover
```

🎬 **Installation Art / Museums**
```csharp
// Central control (Server)
// Multiple displays/speakers (Clients)
// Synchronized audio-visual experiences
```

🎧 **Collaborative Music Production**
```csharp
// Producer's workstation (Server)
// Musicians' headphones (Clients)
// Everyone hears the same thing at the same time
```

#### Advanced Features

**Connection Monitoring:**
```csharp
var status = OwnaudioNet.GetNetworkSyncStatus();
Console.WriteLine($"Clients: {status.ClientCount}");
Console.WriteLine($"Latency: {status.AverageLatency:F2}ms");
Console.WriteLine($"State: {status.ConnectionState}");
```

**Graceful Fallback:**
```csharp
// Client automatically continues playback if server disconnects
// Reconnects automatically when server comes back online
// No manual intervention needed!
```

**Performance Guarantees:**
- **Zero-GC**: No heap allocations in hot path
- **Low CPU**: < 3% overhead (audio threads unchanged)
- **Low Bandwidth**: < 15 KB/s per client
- **Thread Isolation**: Network never blocks audio

> **📖 Detailed Documentation:** See [NetworkSync API Documentation](https://modernmube.github.io/OwnAudioSharp/documents/networksync.html) for complete technical details.

---

### 🎚️ MasterClock & SmartMaster

**MasterClock** provides sample-accurate timeline synchronization for multi-track audio with automatic drift correction and DAW-style regions. See the [MultitrackPlayer example](OwnAudio/Examples/Ownaudio.Example.MultitrackPlayer/) for implementation details.

**SmartMaster** is an intelligent audio mastering chain with auto-calibration for different speaker systems (HiFi, Headphone, Studio, Club, Concert). Includes 31-band EQ, multiband compression, and brick-wall limiting. See [Effects Documentation](https://modernmube.github.io/OwnAudioSharp/documents/effects.html#master) for details.



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
