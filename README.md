<div align="center">
  <img src="ownaudio-logo.svg" width="440" alt="OwnAudio">
</div>

<div align="center">
  <a href="https://www.nuget.org/packages/OwnAudioSharp">
    <img src="https://img.shields.io/badge/NuGet-OwnAudioSharp%204.0.0-blue" alt="OwnAudioSharp NuGet Package">
  </a>
  <a href="https://modernmube.github.io/OwnAudioSharp">
    <img src="https://img.shields.io/badge/Docs-API%20Documentation-darkgreen" alt="Documentation">
  </a>
  <a href="https://github.com/ModernMube/OwnAudioSharp/tree/master/OwnAudio/Examples">
    <img src="https://img.shields.io/badge/Examples-Sample%20Projects-red" alt="Examples">
  </a>
  <a href="https://www.buymeacoffee.com/ModernMube">
    <img src="https://img.shields.io/badge/Support-Buy%20Me%20A%20Coffee-orange" alt="Buy Me a Coffee">
  </a>
</div>

##

## A new kind of audio API for .NET

**OwnAudioSharp** is a professional-grade, cross-platform audio API for C# — with something no other .NET audio library has: **from the first sample to the last byte, your audio is processed by a purpose-built Rust engine.** You write 100% C#; Rust does the heavy lifting.

The entire audio path — decoding, mixing, effects, resampling, playback and capture — runs in a native Rust core that was **written specifically for this package**. On top of it sits a clean, idiomatic C# surface. The result: low CPU usage and a small memory footprint — engineered to industry standards for professional, production-ready use.

- 🦀 **Rust engine, C# surface** — native performance with a managed developer experience.
- 🎯 **Rock-solid real-time path** — no dropouts, no glitches.
- 📦 **Zero external dependencies** — one NuGet package, native code bundled and built for it.
- ⚡ **Low CPU & memory** — real-time headroom on desktop and mobile alike.
- 🌍 **Truly cross-platform** — Windows, macOS, Linux, Android and iOS from a single API.

Nothing like it exists for C# today.

Decoding is pure Rust too — no external codecs, no FFmpeg, no system dependencies. The built-in decoder handles **MP3, FLAC, WAV (PCM/ADPCM), AAC, MP4/M4A, OGG/Vorbis and AIFF** out of the box, on every platform.

---

## What you get

| Category | Capability |
|---|---|
| Playback & Mixing | Multi-track sync, real-time tempo/pitch, per-track volume |
| Recording | Low-latency capture with device selection |
| Effects | 15 real-time DSP effects (reverb, EQ, compressor, limiter, …) |
| Plugins | VST3 effect plugins with cross-platform editor GUI |
| MIDI | Hardware I/O, SMF file read/write, hardware-accurate clock |
| Network | Sample-accurate multi-device sync over LAN |
| Mastering | Reference-based mastering (Audio Matchering) |
| Analysis | Real-time chord detection |
| Calibration | SmartMaster speaker calibration with automatic EQ correction |

**Recommended for:** music players and DAWs, DJ software, music-education tools, broadcast and podcast pipelines, live-performance apps, and low-latency game audio.

---

## Installation

```bash
dotnet add package OwnAudioSharp          # Desktop — full features
dotnet add package OwnAudioSharp.Mobile   # Android / iOS
dotnet add package OwnAudioSharp.Basic    # Lightweight
```

**Requirement:** .NET 10.0 or later. The native Rust engine — including the audio decoder — is bundled in the package. There is nothing else to install and no external codecs to configure.

---

## Features

### Multi-Track Synchronized Playback
Play multiple audio files in perfect sync using a shared central clock. Each track has independent volume, pitch and tempo control — ideal for DAW-style apps or multitrack players.

### Real-Time Tempo & Pitch
Adjust playback speed and pitch independently, in real time, across multiple tracks simultaneously.

### 15 Real-Time DSP Effects
Reverb, equalizer, compressor, limiter, chorus, delay, distortion and more — freely combinable, inserted per-track or on the master bus.

### VST3 Plugin Support
Load VST3 effect plugins and use their native cross-platform editor GUI, integrated into the effect chain like any built-in effect.

> Full guide: [OwnAudio/Source/Effects/VST/README.md](OwnAudio/Source/Effects/VST/README.md)

### Simple Recording & Playback
Straightforward capture from any input device with configurable sample rate, buffer size and channel count.

### SmartMaster — Automatic Speaker Calibration
Measures your speakers with a microphone and corrects the output automatically. Includes speaker profiles (HiFi, Headphone, Studio, Club, Concert), a 31-band EQ, multiband compression and a brick-wall limiter.

> Full guide: [OwnAudio/Source/Effects/SmartMaster/README.md](OwnAudio/Source/Effects/SmartMaster/README.md)

### NetworkSync — Multi-Device Synchronization
Synchronizes playback across devices on the local network with sample-accurate precision (< 5 ms on LAN). Zero-configuration with automatic server discovery.

### Audio Matchering — Reference-Based Mastering
Analyzes a reference track and applies its spectral and dynamic characteristics to your audio for professional mastering results.

> Full guide: [OwnAudio/Source/Features/Matchering/README.md](OwnAudio/Source/Features/Matchering/README.md)

### Chord Detection — Real-Time Musical Analysis
Recognizes major, minor, diminished, augmented and extended chords (7th–13th) from audio in real time or offline, using a chromagram-based pipeline.

> Full guide: [OwnAudio/Source/Features/ChorDetect/README.md](OwnAudio/Source/Features/ChorDetect/README.md)

### MIDI — Hardware I/O, Files, and Clock

> Full API reference: [OwnAudio/Midi/README.md](OwnAudio/Midi/README.md)

AOT-compatible, reflection-free MIDI on Windows (WinMM), macOS (CoreMIDI) and Linux (ALSA rawmidi): real-time input/output, Standard MIDI File (format 0/1) read/write/edit, and a hardware-accurate 24 PPQN clock.

---

## The road to 4.0

Getting to a truly dependable real-time audio API took four generations — each one solved a problem the last one exposed:

| Version | Approach | What it taught us |
|---|---|---|
| **1.0** | MiniAudio + PortAudio + FFmpeg, no optimization | Proved the core idea and shaped the API — but left performance on the table. |
| **2.0** | Fully managed, cross-platform engine, zero native dependencies | Clean and portable, but managed code alone could not deliver the consistency the real-time audio path demands. |
| **3.0** | Optimized native engines (MiniAudio + PortAudio + FFmpeg); mixing, effects and sync in managed code | Fast — but the managed processing stage could still be stalled by the *host* application under load. |
| **4.0** | The **entire** audio chain runs in native Rust; the whole API is wrapped in a thin managed layer | A C# audio API whose real-time path is completely independent of the host application — professional, industry-standard behavior for real-world .NET audio apps. |

**4.0 is the payoff:** no matter what the surrounding C# code does, the audio never stutters — because not a single sample is processed in managed code.

---

## Architecture

OwnAudioSharp is a thin C# surface over a native Rust engine:

```
Application
  └─ OwnaudioNet (high-level C# API)
       └─ AudioEngineWrapper (lock-free, non-blocking)
            └─ Native Rust engine (ownaudio-ffi + ownaudio-core)
                 └─ Audio hardware
```

- **[Ownaudio.Core](OwnAudioEngine/Ownaudio.Core/README.md)** — platform-agnostic interfaces, lock-free ring buffers, SIMD converters and object pools.
- **OwnAudioRust** — the C# binding stack over the Rust core, in three layers: **[HighLevel](OwnAudioEngine/OwnAudioRust/HighLevel/README.md)** → **[Safe](OwnAudioEngine/OwnAudioRust/Safe/README.md)** → **[Native](OwnAudioEngine/OwnAudioRust/Native/README.md)**.
- **Native Rust engine** — **[ownaudio-ffi](OwnAudioEngineRust/ownaudio-ffi/README.md)** (the C ABI boundary) wrapping **[ownaudio-core](OwnAudioEngineRust/ownaudio-core/README.md)** (decoding, mixing, effects, resampling, playback and capture).

All blocking engine methods (`Initialize`, `Stop`, `Send`) must be called off the UI thread. The high-level `OwnaudioNet` API handles threading internally.

---

## Documentation

Complete API reference, tutorials and guides are on the official website:

<div align="center">
  <a href="https://modernmube.github.io/OwnAudioSharp/">
    <img src="https://img.shields.io/badge/📖_Full_API_Documentation-OwnAudioSharp_Website-blue?style=for-the-badge" alt="Documentation" width="400">
  </a>
</div>

Working example projects live in [OwnAudio/Examples/](OwnAudio/Examples/).

---

## Support

**OwnAudioSharp is free and open-source.** If it saves you time or ships in your product, consider supporting its development:

<div align="center">
  <a href="https://www.buymeacoffee.com/ModernMube" target="_blank">
    <img src="https://cdn.buymeacoffee.com/buttons/v2/arial-yellow.png" alt="Buy Me A Coffee" style="height: 60px !important;width: 217px !important;">
  </a>
</div>

Issues and feature requests: [GitHub Issues](https://github.com/modernmube/OwnAudioSharp/issues)

---

## License

See the [LICENSE](LICENSE) file for details.

---

## Development Tools

This project is developed with the following tools:

| | |
|:--:|:--|
| ![Claude Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/claude.svg) | **Anthropic** — Claude Code |
| ![Visual Studio Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/vscode.svg) | **Microsoft** — Visual Studio Code |
| ![Visual Studio 2022](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/visualstudio.svg) | **Microsoft** — Visual Studio 2022 |
| ![Rider](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/rider.svg) | **JetBrains** — Rider |
