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

## 🎯 NEW in v2.4.0: MasterClock Timeline-Based Synchronization

### Revolutionary Multi-Track Synchronization

OwnAudioSharp v2.4.0 introduces **MasterClock**, a professional timeline-based synchronization system that replaces the legacy GhostTrack architecture. This is the most significant update to the synchronization layer since the project's inception.

### Why MasterClock?

The legacy **GhostTrack/AudioSynchronizer** system worked but had limitations:

❌ **Frame-based tracking** - synchronization tied to sample frames, not physical time
❌ **Complex sync groups** - required manual group management and cascading updates
❌ **No dropout visibility** - buffer underruns happened silently
❌ **Limited flexibility** - couldn't support DAW-style timeline features

### The Solution: Timeline-Based MasterClock

**MasterClock** provides professional-grade synchronization with a clean, modern API:

✅ **Timeline-based** - Physical time in seconds (double precision)
✅ **Sample-accurate** - Long precision sample position tracking
✅ **Automatic drift correction** - 10ms tolerance with automatic resyncing
✅ **Dropout events** - Real-time notifications for buffer underruns
✅ **Global tempo** - Tempo applied to all synchronized tracks (like professional DAWs)
✅ **Start offset support** - DAW-style regions (tracks start at different timeline positions)
✅ **Zero overhead** - Single null check when not using sync features

### Migration from Legacy to v2.4.0+

**IMPORTANT:** The legacy GhostTrack/AudioSynchronizer system is **deprecated** and will be **removed in v3.0.0**.

#### Legacy Code (Deprecated - works until v3.0.0):
```csharp
// OLD: GhostTrack + AudioSynchronizer
var synchronizer = new AudioSynchronizer();  // ⚠️ Deprecated
var syncGroup = synchronizer.CreateSyncGroup("MainTracks", sources);
synchronizer.SetSyncGroupTempo("MainTracks", 1.5f);
synchronizer.StartSyncGroup("MainTracks");

// Get position
double position = mixer.GetSyncGroupPosition("MainTracks");  // ⚠️ Deprecated
```

#### NEW Code (v2.4.0+ - Required in v3.0.0):
```csharp
// NEW: MasterClock direct attachment
foreach (var source in sources) {
    if (source is IMasterClockSource clockSource) {
        // Attach to master clock
        clockSource.AttachToClock(mixer.MasterClock);

        // Optional: Set start offset (DAW-style regions)
        clockSource.StartOffset = 0.0; // Track starts at 0 seconds
    }
    mixer.AddSource(source);
    source.Play();
}

// Get position
double position = mixer.MasterClock.CurrentTimestamp;

// Seek
mixer.MasterClock.SeekTo(30.0); // Jump to 30 seconds
```

### Key Benefits

| Feature | Legacy (GhostTrack) | NEW (MasterClock) |
|---------|---------------------|-------------------|
| **Time base** | Frame count | Physical time (seconds) |
| **Drift tolerance** | 512 samples (~10ms) | Configurable (default 10ms) |
| **Start offset** | ❌ Not supported | ✅ DAW-style regions |
| **Dropout events** | ❌ Silent failures | ✅ Real-time notifications |
| **Performance metrics** | ❌ None | ✅ CPU, buffer fill, dropout history |
| **Tempo control** | ⚠️ Per-track (complex) | ✅ Global tempo (like DAWs) |
| **Rendering modes** | ❌ Realtime only | ✅ Realtime / Offline |
| **API simplicity** | ⚠️ Sync groups + observers | ✅ Direct clock attachment |

**Note on Tempo**: When using MasterClock, tempo is **global** (affects all synchronized tracks equally), similar to professional DAWs like Ableton, Logic, or FL Studio. This ensures perfect synchronization across all tracks while SoundTouch processes the audio in real-time.

### Advanced Features

#### 1. DAW-Style Timeline Regions
```csharp
track1.AttachToClock(mixer.MasterClock);
track1.StartOffset = 0.0;   // Intro starts at 0s

track2.AttachToClock(mixer.MasterClock);
track2.StartOffset = 4.0;   // Drums come in at 4s

track3.AttachToClock(mixer.MasterClock);
track3.StartOffset = 8.0;   // Bass enters at 8s
```

#### 2. Dropout Event Monitoring
```csharp
mixer.TrackDropout += (sender, e) => {
    Console.WriteLine($"Dropout: {e.TrackName}");
    Console.WriteLine($"  Timestamp: {e.MasterTimestamp:F3}s");
    Console.WriteLine($"  Missed frames: {e.MissedFrames}");
    Console.WriteLine($"  Reason: {e.Reason}");
};
```

#### 3. Offline Rendering Mode
```csharp
// Deterministic rendering (no dropouts, blocks until ready)
mixer.RenderingMode = ClockMode.Offline;

mixer.MasterClock.SeekTo(0.0);
foreach (var source in sources) {
    source.Play();
}

// Render loop - waits for all tracks to be ready
// Perfect for exporting to file
```

#### 4. Per-Track Performance Metrics
```csharp
// Access track metrics
var metrics = mixer.GetTrackMetrics(trackId);
Console.WriteLine($"CPU: {metrics.AverageCpuUsage:F2}%");
Console.WriteLine($"Buffer fill: {metrics.BufferFillPercentage:F1}%");
Console.WriteLine($"Dropouts: {metrics.TotalDropoutCount}");
```

### What's Deprecated (Removed in v3.0.0)

The following classes and methods are marked `[Obsolete]` and will be **removed in v3.0.0**:

⚠️ `GhostTrackSource` - Use `MasterClock` instead
⚠️ `IGhostTrackObserver` - Implement `IMasterClockSource` instead
⚠️ `AudioSynchronizer` - Use `AudioMixer.MasterClock` directly
⚠️ `CreateSyncGroup()` / `StartSyncGroup()` / `StopSyncGroup()` - Attach tracks to MasterClock
⚠️ `SetSyncGroupTempo()` - Set tempo directly on each track
⚠️ `GetSyncGroupPosition()` - Use `mixer.MasterClock.CurrentTimestamp`
⚠️ `SeekSyncGroup()` - Use `mixer.MasterClock.SeekTo()`

### Migration Timeline

- **v2.4.0** (Current): Legacy and new systems coexist, legacy shows deprecation warnings
- **v2.5.0 - v2.9.0**: Transition period - update your code
- **v3.0.0**: Legacy system removed, only MasterClock available

**Action Required:** If you're using GhostTrack or AudioSynchronizer, migrate to MasterClock before v3.0.0.

### See It In Action

Check out the updated **MultitrackPlayer** example project to see the new MasterClock system in action:
- [MultitrackPlayer Example](OwnAudio/Examples/Ownaudio.Example.MultitrackPlayer/)
- Real-time dropout monitoring UI
- Timeline-based seek operations
- Per-track tempo control
- Professional synchronization with zero glitches

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
