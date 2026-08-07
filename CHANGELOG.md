# Changelog

All notable changes to OwnAudioSharp are documented here.
Releases before 4.0.0 are documented on the [GitHub Releases](https://github.com/ModernMube/OwnAudioSharp/releases) page.

## 4.0.3 — 2026-08-07

### API

- `ChordDetect.DetectFromFile` and `DetectFromFiles` now take an optional
  `Action<double>? progress` callback that reports 0..1 over the whole job — decoding, note
  transcription, tempo detection and chord analysis, weighted by phase. With the MT3
  transcriber an analysis is minutes of work on a full song and there was no way to show that
  to the user. The parameter is last on both overloads, so existing calls are unaffected, and
  without a callback the transcription percentage still goes to the log as before.

## 4.0.2 — 2026-08-04

### Fixed

- BPM detection was inaccurate in several ways at once. The spectral flux was computed in the
  power domain instead of magnitude, which gave an accented beat a squared weight and locked the
  autocorrelation onto the accent period — tempi from about 112 BPM up came back halved on
  ordinary kick/snare material. On top of that the log-Gaussian tempo prior is symmetric, so above
  `PREFERRED_BPM * sqrt(2)` (169.7 BPM with the old centre of 120) the half tempo won on prior
  weight alone and everything fast was reported at half speed.
- The estimate only ever looked at the last 8 seconds of input. Since `ChordDetect` feeds a whole
  track and asks once at the end, the outro decided the tempo: a fade, a reverb tail or trailing
  silence produced a confident wrong answer (silence came back as 190 BPM). Every analysis window
  now folds into an accumulated autocorrelation, so the whole track has a say.
- No confidence check at all, and the final `clamp` made failures look like plausible readings —
  white noise reported 152 BPM, digital silence 190. Below a minimum peak correlation the detector
  now returns 0, and the "enough data" threshold went from 1.7 to 4 seconds, which was also the
  point below which slow tempi could not be represented at all and came back doubled.
- Onset frames are twice as dense (64-sample hop instead of 128) for finer lag resolution near
  `MAX_BPM`, and a candidate is compared against its twice-as-fast reading, so a pattern accented
  on every other beat no longer reads as half tempo. `get_bpm` is now allocation-free as well.

## 4.0.1 — 2026-08-03

### Diagnostics

- Every `catch` block in the audio package now reports through `Ownaudio.Core`'s `Log` instead of
  discarding the exception, and the silent failure returns beside them (`-1` / `false` / `null` on
  the device and stream paths) report the same way.
- Initialization, teardown and parameter changes are logged across the engine, mixer, sources,
  network sync and VST3 hosting.
- Real-time code stays off the logger. Audio-rate handlers count their failures and emit one line
  on the first hit plus a total on reset or dispose; control-rate and network loops report the
  first occurrence and every Nth after it, so a stuck fault stays visible without flooding.

### API

- `Log.LoggerLevel` now starts at `Disabled` instead of `Info`, so the library is silent unless
  asked otherwise.
- `OwnaudioNet.Initialize` and `InitializeAsync` take an optional `logLevel` argument that turns
  the console logger on. It sits last on every overload, so existing positional calls keep
  compiling; `Log.LoggerLevel` can still be changed at any time while the API is running.

## 4.0.0 — 2026-07-27

The whole audio engine was rewritten in **Rust**. Everything below the public C# API — device
I/O, decoding, resampling, mixing, effects and the real-time thread — is now native code reached
through a versioned C ABI. The managed platform engines (WASAPI / PulseAudio / CoreAudio written
in C#) are gone, and so is the managed mix thread: audio data never travels through the GC heap
any more.

The public C# API was kept as close to 3.x as possible, but this is a major version and there are
breaking changes — see [Breaking changes](#breaking-changes).

### Engine

- New Rust workspace: `ownaudio-core` (engine, mixer, decoders, resampler, effects, ring buffer,
  RT guards), `ownaudio-ffi` (the C ABI surface) and `ownaudio-soundtouch` (time-stretch / pitch).
- Native-only real-time path — the managed mix thread and `OnSamplesRead` pipeline were removed;
  the Rust mixer is the only mixing path.
- Host API selection: **WASAPI**, **ASIO**, **CoreAudio**, **ALSA** and **AAudio** (Android).
- Symphonia-backed streaming decoder with prefetch is the primary decoder; the managed decoders
  and managed DSP were removed and `AudioDecoderFactory` is native-only.
- Resampler switched from `SincFixedIn` to `FftFixedIn` for SIMD acceleration, and made
  allocation-free on the RT path.
- cpal audio callbacks and every FFI `destroy` function are panic-guarded, so a Rust panic can
  never unwind across the real-time or FFI boundary.
- ABI versioning (`ownaudio_v1_get_abi_version`) with two-sided struct-size parity tests, so a
  mismatched native binary fails loudly instead of corrupting memory.
- Library version is read from the assembly instead of a hardcoded constant.

### Mixing and sources

- Native per-track chain: volume, pan, tempo, pitch, effects and plugin delay compensation all run
  in Rust.
- Equal-power stereo pan for both sources and master.
- Native master volume and peak metering.
- Native per-track start offset (silence pre-roll plus content seek).
- `SampleSource`, `InputSource` and `FileSource` were ported fully to the native chain;
  `FileSource.ReadSamples` decodes synchronously and the managed source threads are gone.
- New `StreamingSource` with callback-driven native playback.
- Sample-accurate `play_all`, additive multitrack mixing, atomic parameter updates and
  anti-zipper gain ramps.
- Lock-free mixer command queue with a self-draining controller, so the retire queue can't back up.

### Effects

- All built-in effects ported to Rust with denormal protection: Equalizer, Compressor, Limiter,
  Gate, Delay, Reverb, Overdrive, Distortion, Chorus, Flanger, Phaser, Rotary, AutoGain,
  DynamicAmp, Enhancer and PitchShift.
- Stereo-linked, frame-based dynamics with RT-safe preallocation.
- Per-effect wet/dry ramping to remove zipper noise, block-rate EQ band-gain smoothing,
  look-ahead attack ramp on the limiter, and threshold hysteresis plus detector smoothing on
  the gate.
- Pitch shift reports a constant latency and aligns the dry path accordingly.
- **SmartMaster** runs as a native composite effect in the Rust chain.
- **VST3 plugins are hosted natively** inside the Rust effect chain; hosted plugins stay warm
  (soft bypass) so toggling them doesn't stall the stream.
- Default and preset parameters were retuned for real-world material.
- SoundTouch's WSOLA engine was ported to Rust; the managed SoundTouch implementation was removed.

### MIDI

- `OwnAudioSharp.Midi` is now Rust-backed, with native packaging and its own CI pipeline.
- Hot-plug port monitoring, hybrid clock timing and a capacity-aware parser.
- iOS support plus Android file and clock handling.
- FFI crossings are batched and the clock backlog handling was hardened.

### Features

- **Chord and key detection** rewritten to be musically informed, allocation-free on the hot path
  and AOT-safe.
- **BPM detection** moved to the Rust side over FFI.
- **ALAC** support, plus a fix for mono AAC/ALAC-in-M4A channel detection (#38).
- Hardware latency is exposed by the engine and compensated for in recordings.
- Mono capture channel adaptation and native master-output recording.

### Mobile

- `OwnAudioSharp.Mobile` ships **Android (arm, arm64, x64)** and **iOS (device + simulator)**
  natives, with the engine assemblies packed into the package.
- Device enumeration survives a missing JNI context on Android instead of throwing.
- An Android example app and a documented `BuildMobile` flag for building against the engine.

### Packaging and CI

- Multi-platform native builds in GitHub Actions: win-x64, win-arm64 (both with optional ASIO),
  linux-x64, linux-arm64, osx-x64, osx-arm64, android-arm/arm64/x64, ios-arm64 and the iOS
  simulator RIDs.
- Packages are packed from the real `OwnaudioNET*` projects and verified after packing, closing
  the regression where truncated, API-less packages could ship.
- Built natives are committed back into `runtimes/` so local development works without a Rust
  toolchain.
- `OwnAudioSharp.Basic` is a deliberately minimal audio in/out package — no ONNX, no AI features.

### Breaking changes

- **`VocalRemover` was removed** from the API along with its model asset.
- The managed platform engines, managed decoders, managed DSP and the managed SoundTouch
  implementation were removed. Code that depended on the managed mix path (`OnSamplesRead`-style
  interception) no longer receives audio.
- `MathNet.Numerics` was dropped from the full package in favour of the in-house `OwnAudioFft`;
  it remains a dependency of `OwnAudioSharp.Basic`.
- Target framework moved to **net10.0** (`net10.0-android` / `net10.0-ios` for the mobile package).
- Several device/channel properties are now backward-compatibility shims only and are documented
  as such.

### Fixes

- ASIO: one cpal device is shared between capture and playback for true duplex; device lists are
  served from a snapshot instead of fighting over the driver; engine-scoped enumeration, I32
  sample format and a 256-channel limit.
- Time-stretch stays transparent at unity, which removes the phaser artefact on tempo changes.
- Live tempo/pitch changes on file tracks no longer click, and stretch is only latched on real
  tempo/pitch use.
- `FileSource.Position` reports content time again and seeking is tempo-corrected.
- Master and track effect mirroring no longer floods the command queue.
- SmartMaster: EQ band count aligned, limiter release wired up, `ApplyConfiguration` added, and
  the subharmonic band-pass kernel rejects DC.
- Equalizer preset bypass and the `Receives` state guard were fixed.
