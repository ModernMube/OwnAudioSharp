<div align="center">
  <img src="ownaudio-logo.svg" width="440" alt="OwnAudio">
</div>

<div align="center">
  <a href="https://www.nuget.org/packages/OwnAudioSharp">
    <img src="https://img.shields.io/badge/NuGet-OwnAudioSharp%204.0.5--preview.1-blue" alt="OwnAudioSharp NuGet Package">
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

Decoding is pure Rust too — no external codecs, no FFmpeg, no system dependencies. The built-in decoder handles **MP3, FLAC, WAV (PCM/ADPCM), AAC, ALAC, MP4/M4A, OGG/Vorbis and AIFF** out of the box, on every platform.

---

## 🆕 The documentation site has been rebuilt

<div align="center">
  <a href="https://modernmube.github.io/OwnAudioSharp">
    <img src="https://img.shields.io/badge/📚%20Read%20the%20docs-modernmube.github.io%2FOwnAudioSharp-2997ff?style=for-the-badge" alt="OwnAudioSharp documentation">
  </a>
</div>

It is no longer a flat list of classes. The site now walks you through the API in the order you
actually meet it, and answers "how do I build this?" as well as "what does this method do?".

| | |
|---|---|
| ⚡ **[Quick Start](https://modernmube.github.io/OwnAudioSharp/documents/quickstart.html)** | Empty folder to playing audio in three steps. |
| 🧭 **[Core Concepts](https://modernmube.github.io/OwnAudioSharp/documents/concepts.html)** | The five pieces — engine, mixer, sources, effects, clock — plus the audio vocabulary. |
| 🍳 **[Recipes](https://modernmube.github.io/OwnAudioSharp/documents/recipes.html)** | Complete, copyable answers: multi-track sync, transport, meters, live effects, recording, generated audio, offline rendering. |
| 📘 **[The API, in order](https://modernmube.github.io/OwnAudioSharp/documents/api-core.html)** | Eight numbered steps from initialization to MIDI, each with a "why and when". |
| 🩺 **[Troubleshooting & FAQ](https://modernmube.github.io/OwnAudioSharp/documents/troubleshooting.html)** | No sound, crackling, drift, plugin problems — what happened, why, and the fix. |

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
| Analysis | Real-time chord detection, MT3 multi-instrument transcription, before/after effect spectrum |
| Calibration | SmartMaster speaker calibration with automatic EQ correction |

**Recommended for:** music players and DAWs, DJ software, music-education tools, broadcast and podcast pipelines, live-performance apps, and low-latency game audio.

---

## Installation

```bash
dotnet add package OwnAudioSharp          # Desktop — everything
dotnet add package OwnAudioSharp.Mobile   # Android / iOS
dotnet add package OwnAudioSharp.Basic    # Desktop — minimal engine
dotnet add package OwnAudioSharp.Mt3      # Optional — MT3 transcription
```

| Package | Platforms | What it is |
|---|---|---|
| `OwnAudioSharp` | Windows, Linux, macOS | The complete edition. Playback, recording, mixing, effects, VST3 and MIDI, plus the analysis features (chord detection, note transcription, matchering) and the waveform display. |
| `OwnAudioSharp.Mobile` | Android, iOS | The same feature set built for mobile, including matchering and the waveform display — minus the ONNX-based analysis (no chord detection, no note transcription). |
| `OwnAudioSharp.Basic` | Windows, Linux, macOS | Audio in and out, and nothing that could be left out. Playback, recording, mixing, effects and VST3 — no analysis features, no ONNX models, no UI dependency. |
| `OwnAudioSharp.Mt3` | Windows, Linux, macOS (ARM) | Optional add-on. Swaps the note transcriber behind chord detection for MT3, which labels every note with its instrument. Separate package because ONNX Runtime costs ~26 MB per platform. |

**Requirement:** .NET 10.0 or later. The native Rust engine — including the audio decoder — is bundled in the package. There is nothing else to install and no external codecs to configure.

### Building a mobile app

If you consume `OwnAudioSharp.Mobile` from NuGet, there is nothing to configure. The package
carries the native engine for every Android ABI and both iOS architectures, and the SDK puts
them into your APK or app bundle on its own — the JNI handshake Android needs for device
enumeration is done by the library at startup.

| | Minimum | Notes |
|---|---|---|
| Android | 7.0 (API 24) | `arm64-v8a`, `armeabi-v7a`, `x86_64`. Audio runs on AAudio. |
| iOS | 12.2 | Device and simulator, arm64 + x64. The engine is linked statically. |

Add `RECORD_AUDIO` to your manifest if you capture audio; playback needs no permission.

**Building this repository from source is different — `-p:BuildMobile=true` is mandatory:**

```bash
dotnet build YourApp.csproj -c Debug -p:BuildMobile=true
dotnet publish YourApp.csproj -c Release -p:BuildMobile=true
```

The engine projects only expose their `net10.0-android` and `net10.0-ios` target frameworks when
that property is set. Leave it off and everything still *builds* — your app quietly links the
desktop `net10.0` assemblies instead, installs onto the phone, and then throws on the first
`Initialize` call:

```
AudioEngineException: Failed to initialize Rust audio engine:
  internal panic in native audio engine: android context was not initialized
```

The flag has to be on the command line, where it reaches restore as well as build; setting it
inside the project file or through `AdditionalProperties` on a `ProjectReference` does not work.
This applies to the samples in `OwnAudio/Examples/` too — see the
[Android](OwnAudio/Examples/Ownaudio.Example.Android/README.md) and
[iOS](OwnAudio/Examples/Ownaudio.Example.iOS/README.md) sample READMEs.

On iOS add a `RuntimeIdentifier` as well (`ios-arm64`, `iossimulator-arm64` or
`iossimulator-x64`): the engine is a static library picked per RID, and no single one covers
device and simulator.

Deploying with `-t:Run` or `-t:Install` needs an emulator or a connected device with USB
debugging enabled, otherwise the Android SDK stops with `XA0010: No available device`. Check
with `adb devices` first.

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

### Effect Analysis — See What an Effect Does
Tap any effect chain and get the signal on both sides of it, block for block, while it plays. `EffectSpectrumAnalyzer` turns that into a live before/after spectrum; effect latency is compensated, so the two sides line up.

> Guide: [Effect analysis](https://modernmube.github.io/OwnAudioSharp/documents/api-effects.html#analysis)

### Simple Recording & Playback
Straightforward capture from any input device with configurable sample rate, buffer size and channel count.

### SmartMaster — Automatic Speaker Calibration
Measures your speakers with a microphone and corrects the output automatically. Includes speaker profiles (HiFi, Headphone, Studio, Club, Concert), a 30-band EQ, multiband compression and a brick-wall limiter.

> Full guide: [OwnAudio/Source/Effects/SmartMaster/README.md](OwnAudio/Source/Effects/SmartMaster/README.md)

### NetworkSync — Multi-Device Synchronization
Synchronizes playback across devices on the local network with sample-accurate precision (< 5 ms on LAN). Zero-configuration with automatic server discovery.

### Audio Matchering — Reference-Based Mastering
Analyzes a reference track and applies its spectral and dynamic characteristics to your audio for professional mastering results.

Besides the offline renderers (file in, file out) it can stop one step earlier and hand back the *settings* instead — the numbers a live mastering chain has to be set to. The reference can be a file or one of the ten built-in playback-system presets, and nothing touches the disk:

```csharp
var analyzer = new AudioAnalyzer();

AudioSpectrum mix = analyzer.AnalyzeAudioBuffer(masterSum, 48000, 2);

MatcheringProfile profile = analyzer.CalculateProfile(mix, PlaybackSystem.ClubPA, 48000);

// The profile is 30 third-octave bands, so it needs the 30-band EQ —
// the same filter bank the offline renderer drives. Its band centres
// already sit on the profile's frequencies, so read them back per band.
var eq = new Equalizer30BandEffect(sampleRate: 48000f);

for (int band = 0; band < 30; band++)
    eq.SetBandGain(band, eq.GetBandFrequency(band), profile.QFactors[band], profile.BandGainsDb[band]);
```

`BandGainsDb` is what the filter bank has to be *set to*, not the curve you asked for — the two differ because a 1/3-octave bell bleeds into its neighbours. Analysis is seconds of work on a full song, so run it off the UI thread.

> Full guide: [OwnAudio/Source/Features/Matchering/README.md](OwnAudio/Source/Features/Matchering/README.md)

### Chord Detection — Real-Time Musical Analysis
Recognizes major, minor, diminished, augmented and extended chords (7th–13th) from audio in real time or offline, using a chromagram-based pipeline.

The notes feeding that pipeline now come from a swappable `INoteTranscriber`. The built-in BasicPitch model stays the default and needs no setup; MT3 is the alternative:

```csharp
var (chords, key, bpm) = ChordDetect.DetectFromFile("song.mp3");   // BasicPitch, as before

using var mt3 = new Mt3Transcriber(Mt3ModelPaths.FromDirectory("/models/mt3"));
var (chords2, key2, bpm2) = ChordDetect.DetectFromFile("song.mp3", mt3, 1.0f,
    p => Console.Write($"\rAnalyzing: {p:P0}"));   // 0..1 over the whole run
```

> Full guide: [OwnAudio/Source/Features/ChorDetect/README.md](OwnAudio/Source/Features/ChorDetect/README.md)

### MT3 — Multi-Instrument Transcription
Where BasicPitch hears *pitches*, MT3 hears *instruments*. It is a sequence-to-sequence transformer that emits MIDI-like events with a program number attached, so a bass line and a piano voicing stay separate instead of collapsing into one smear on the chromagram — which is most of what it buys chord detection. Every note comes back with a `Program` and an `IsDrum` flag.

Inference is entirely native: the ONNX sessions, the autoregressive decode loop with its KV cache, the MT3 event codec and the note state machine all live in Rust. Roughly 4× realtime on CPU, so a full song is about a minute of work — run it offline, not on a UI thread.

**The model weights are not in the package** (~290 MB). Download the four files into one folder and pass that folder's path:

```bash
mkdir -p ~/models/mt3 && cd ~/models/mt3
BASE=https://huggingface.co/ModernMube/HTDemucs_onnx/resolve/main/mt3-onnx
for f in mt3_encoder.onnx mt3_decoder_init.onnx mt3_decoder_step.onnx vocab.json; do
  curl -L -o "$f" "$BASE/$f?download=true"
done
```

> Full guide: [OwnAudio/Source/Mt3/README.md](OwnAudio/Source/Mt3/README.md)

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

MIDI and MT3 follow the same shape in their own workspaces — `ownaudio-midi-ffi` and `ownaudio-mt3-ffi` ship as separate native libraries so the audio engine never has to carry MIDI backends or ONNX Runtime.

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
