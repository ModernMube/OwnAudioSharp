# ownaudio-core

Cross-platform audio I/O library for OwnAudioSharp, built on [`cpal`](https://github.com/RustAudio/cpal). Provides device enumeration, stream management, format conversion, sample-rate conversion, lock-free ring buffers, a real-time effect chain, and multi-track mixing — all with strict real-time constraints.

## Table of Contents

- [Features](#features)
- [Dependencies](#dependencies)
- [Quick Start](#quick-start)
- [Device Enumeration](#device-enumeration)
- [Stream Configuration](#stream-configuration)
- [Opening Streams](#opening-streams)
- [Format Conversion](#format-conversion)
- [Ring Buffer](#ring-buffer)
- [Resampler](#resampler)
- [Mixer](#mixer)
- [Effects](#effects)
  - [Effect Types](#effect-types)
  - [Universal Parameters](#universal-parameters)
  - [Effect-Specific Parameters](#effect-specific-parameters)
  - [EffectChain](#effectchain)
- [Multi-Track Mixing](#multi-track-mixing)
  - [Track Control](#track-control)
  - [SampleClock](#sampleclock)
- [Error Handling](#error-handling)
- [Real-Time Constraints](#real-time-constraints)

---

## Features

- **Cross-platform audio I/O** — Windows (WASAPI), macOS (CoreAudio), Linux (ALSA)
- **Device enumeration** — query available input/output devices and their capabilities
- **Flexible stream configuration** — sample rate, channel count, buffer size, sample format
- **Format conversion** — bidirectional i16/u16/f32 with interleave/deinterleave utilities
- **Lock-free ring buffer** — SPSC, safe to use between audio callback and application threads
- **High-quality resampler** — sinc-based SRC via `rubato`
- **17 built-in audio effects** — reverb, compressor, EQ (10 or 30 band), delay, chorus, etc.
- **Multi-track mixer** — per-track gain, mute, solo, tempo/pitch, effect chains, transport clock
- **Zero-allocation audio path** — all buffers pre-allocated; no heap activity in callbacks

---

## Dependencies

| Crate | Version | Purpose |
|-------|---------|---------|
| `cpal` | 0.18 | Cross-platform audio backend |
| `thiserror` | 1.0 | Error type derivation |
| `rtrb` | 0.3 | Lock-free SPSC ring buffer |
| `rubato` | 0.15 | Sample-rate conversion |

---

## Quick Start

```rust
use ownaudio_core::{AudioEngine, StreamConfig};

fn main() -> ownaudio_core::Result<()> {
    let engine = AudioEngine::new()?;

    let config = StreamConfig::stereo_f32(48_000);

    let mut phase = 0.0f32;
    let stream = engine.open_output_stream(None, &config, move |buffer| {
        for frame in buffer.chunks_mut(2) {
            let sample = (phase * std::f32::consts::TAU).sin() * 0.2;
            frame[0] = sample;
            frame[1] = sample;
            phase = (phase + 440.0 / 48_000.0).fract();
        }
    })?;

    stream.play()?;
    std::thread::sleep(std::time::Duration::from_secs(3));
    Ok(())
}
```

---

## Device Enumeration

```rust
use ownaudio_core::{list_output_devices, list_input_devices, default_output_device};

// List all available output devices
let outputs = list_output_devices()?;
for dev in &outputs {
    println!(
        "{} | rate: {} Hz | ch: out={} in={}{}{}",
        dev.name,
        dev.default_sample_rate,
        dev.max_output_channels,
        dev.max_input_channels,
        if dev.is_default_output { " [default out]" } else { "" },
        if dev.is_default_input  { " [default in]"  } else { "" },
    );
}

// Get the system default
let default = default_output_device()?;
println!("Default output: {}", default.name);
```

**`AudioDeviceInfo` fields:**

| Field | Type | Description |
|-------|------|-------------|
| `name` | `String` | Device display name |
| `is_default_output` | `bool` | True if OS default output |
| `is_default_input` | `bool` | True if OS default input |
| `max_output_channels` | `u16` | Maximum playback channels |
| `max_input_channels` | `u16` | Maximum capture channels |
| `default_sample_rate` | `u32` | Device preferred sample rate (Hz) |

---

## Stream Configuration

```rust
pub struct StreamConfig {
    pub sample_rate: u32,
    pub channels: u16,
    pub sample_format: SampleFormat,
    pub buffer_size_frames: Option<u32>,  // None = platform default
}

pub enum SampleFormat {
    F32,  // 32-bit IEEE float [-1.0, 1.0]  (preferred)
    I16,  // Signed 16-bit integer
    U16,  // Unsigned 16-bit integer
}
```

**Convenience constructor:**

```rust
let config = StreamConfig::stereo_f32(48_000);
// equivalent to:
// StreamConfig { sample_rate: 48_000, channels: 2, sample_format: SampleFormat::F32, buffer_size_frames: None }
```

Set `buffer_size_frames` to request a specific buffer size. The driver may round to a supported value. `None` uses the platform default (typically 256–1024 frames).

---

## Opening Streams

Both stream types are created from `AudioEngine`. Pass `None` as the device to use the system default.

```rust
let engine = AudioEngine::new()?;

// --- Output stream ---
let out_stream = engine.open_output_stream(
    Some(&device_info),  // or None for default
    &config,
    move |buffer: &mut [f32]| {
        // buffer is interleaved: [L0, R0, L1, R1, ...]
        // Fill samples here. No allocation, no blocking.
    },
)?;
out_stream.play()?;
out_stream.pause()?;  // suspend without destroying

// --- Input stream ---
let in_stream = engine.open_input_stream(
    None,
    &config,
    move |buffer: &[f32]| {
        // Captured interleaved samples. No allocation, no blocking.
    },
)?;
in_stream.play()?;
```

`OutputStream` and `InputStream` stop automatically when dropped.

**Host selection:**

```rust
// Use a specific audio backend
let engine = AudioEngine::new_with_host(cpal::host_from_id(cpal::HostId::Wasapi)?)?;
```

---

## Format Conversion

All conversions operate on slices. The output slice must be pre-allocated with the correct length.

```rust
use ownaudio_core::convert::{i16_to_f32, f32_to_i16, interleave, deinterleave};

// Integer ↔ float
let pcm_i16: Vec<i16> = vec![0, 16384, -16384];
let mut pcm_f32 = vec![0.0f32; 3];
i16_to_f32(&pcm_i16, &mut pcm_f32);   // maps [-32768, 32767] → [-1.0, 1.0]

let mut back_i16 = vec![0i16; 3];
f32_to_i16(&pcm_f32, &mut back_i16);  // clamps to i16 range

// u16 variant (32768 is the zero point)
u16_to_f32(&pcm_u16, &mut pcm_f32);
f32_to_u16(&pcm_f32, &mut pcm_u16);

// Planar ↔ interleaved
let left  = vec![0.1f32, 0.2, 0.3];
let right = vec![0.4f32, 0.5, 0.6];
let mut interleaved = vec![0.0f32; 6];
interleave(&[&left, &right], &mut interleaved);
// → [0.1, 0.4, 0.2, 0.5, 0.3, 0.6]

let mut channels: Vec<Vec<f32>> = Vec::new();
deinterleave(&interleaved, 2, &mut channels);
// → channels[0] = [0.1, 0.2, 0.3], channels[1] = [0.4, 0.5, 0.6]
```

---

## Ring Buffer

Lock-free SPSC ring buffer for safe data transfer between threads (e.g., audio callback → decode thread).

```rust
use ownaudio_core::ring_buffer;

let (mut writer, mut reader) = ring_buffer(4096);  // capacity in samples

// --- Producer side (e.g., decode thread) ---
let written = writer.write(&samples);  // returns number of samples actually written

// --- Consumer side (e.g., audio callback) ---
let available = reader.available();
let mut out = vec![0.0f32; available];
let read = reader.read(&mut out);
```

- `write()` returns the number of samples written, which may be less than the input length if the buffer is full.
- `read()` returns the number of samples read, which may be less than the output buffer length if data is not yet available.
- No blocking, no locking — safe to call from the audio callback.

---

## Resampler

High-quality sinc-based sample rate converter using `rubato`'s `SincFixedIn`. All internal buffers are allocated in `new()`.

```rust
use ownaudio_core::Resampler;

let mut resampler = Resampler::new(
    44_100,   // input_rate
    48_000,   // output_rate
    2,        // channels
    1024,     // max_input_frames per call
)?;

// Input: planar Vec<Vec<f32>>, one Vec per channel
let input: Vec<Vec<f32>> = vec![left_channel, right_channel];
let mut output: Vec<Vec<f32>> = vec![
    vec![0.0; resampler.output_frames_max()],
    vec![0.0; resampler.output_frames_max()],
];

let output_frames = resampler.process(&input, &mut output)?;
// output[ch][0..output_frames] contains the resampled data
```

| Method | Returns | Description |
|--------|---------|-------------|
| `new(in_rate, out_rate, channels, max_input_frames)` | `Result<Self>` | Allocates all internal state |
| `process(input, out)` | `Result<usize>` | Resamples input; returns frame count written to `out` |
| `output_frames_max()` | `usize` | Maximum output frames for a `process()` call |
| `input_rate()` | `u32` | Configured input sample rate |
| `output_rate()` | `u32` | Configured output sample rate |

Resampler parameters: sinc_len=256, f_cutoff=0.95, oversampling=128, BlackmanHarris2 window.

---

## Mixer

Lightweight scratch-buffer mixer for additive mixing in the audio callback.

```rust
use ownaudio_core::Mixer;

let mut mixer = Mixer::new(1024);  // pre-allocated scratch buffer size

// Additive mix — output is the sum of all sources
mixer.mix(&[&source_a, &source_b], &mut output_buffer);

// Mix with per-source gain
mixer.mix_with_gain(
    &[
        (&source_a, 0.8),  // 80% volume
        (&source_b, 0.5),  // 50% volume
    ],
    &mut output_buffer,
);

// Access the internal scratch buffer directly
let scratch = mixer.scratch_mut();
```

Slices must all be the same length. No allocation occurs during `mix()` or `mix_with_gain()`.

---

## Effects

### Effect Types

```rust
pub enum EffectType {
    Reverb       = 0,
    Equalizer    = 1,   // 10-band ISO
    Compressor   = 2,
    Limiter      = 3,
    Delay        = 4,
    Chorus       = 5,
    Distortion   = 6,
    Overdrive    = 7,
    Flanger      = 8,
    Phaser       = 9,
    Rotary       = 10,
    AutoGain     = 11,
    Enhancer     = 12,
    Gate         = 13,
    PitchShift   = 14,
    DynamicAmp   = 15,
    Equalizer30  = 16,  // 30-band 1/3-octave ISO
}
```

### Universal Parameters

Every effect supports these two parameter IDs:

| ID | Constant | Range | Description |
|----|----------|-------|-------------|
| 0 | `PARAM_ENABLED` | `0.0` / `1.0` | Bypass (`0.0`) or active (`1.0`) |
| 1 | `PARAM_MIX` | `0.0`–`1.0` | Dry/wet blend (0 = fully dry, 1 = fully wet) |

### Effect-Specific Parameters

Parameter IDs start at `2` for each effect type:

**Reverb:**

| ID | Description |
|----|-------------|
| 2 | Room size (0.0–1.0) |
| 3 | Damping (0.0–1.0) |
| 4 | Width (0.0–1.0) |
| 5 | Wet level (0.0–1.0) |
| 6 | Dry level (0.0–1.0) |

**Compressor:**

| ID | Description |
|----|-------------|
| 2 | Threshold (dBFS, e.g. -20.0) |
| 3 | Ratio (e.g. 4.0 = 4:1) |
| 4 | Attack (seconds) |
| 5 | Release (seconds) |
| 6 | Makeup gain (dB) |

**Equalizer (10-band):** Parameter IDs 2–11 correspond to the 10 ISO center frequencies. Value is gain in dB.

**Equalizer30 (30-band):** Parameter IDs 2–31. Value is gain in dB for each 1/3-octave band.

### EffectChain

```rust
pub trait Effect: Send {
    fn effect_type(&self) -> EffectType;
    fn process(&mut self, buffer: &mut [f32], channels: u16);  // in-place
    fn set_param(&mut self, param_id: u32, value: f32) -> bool;
    fn get_param(&self, param_id: u32) -> Option<f32>;
    fn reset(&mut self);
    fn is_enabled(&self) -> bool;
    fn set_enabled(&mut self, enabled: bool);
}
```

```rust
use ownaudio_core::{EffectChain, EffectType, PARAM_MIX};

let mut chain = EffectChain::new();

// Add an effect (implementation provided by downstream or via factory)
chain.push(Box::new(my_reverb_effect));

// Process in the audio callback — zero allocation
chain.process_all(&mut buffer, channels);

// Inspect and modify at runtime
if let Some(effect) = chain.effect_mut(0) {
    effect.set_param(PARAM_MIX, 0.6);
}

// Remove by index (returns the effect)
let removed = chain.remove(0);

println!("Chain has {} effect(s)", chain.len());
```

`process_all()` skips disabled effects automatically.

---

## Multi-Track Mixing

`MultiTrackMixer` manages a collection of tracks, each with its own gain, mute/solo state, transport controls, tempo/pitch settings, and an `EffectChain`.

```rust
use ownaudio_core::MultiTrackMixer;

let mut mixer = MultiTrackMixer::new(48_000.0, 2);  // sample_rate, channels

let track_a = mixer.add_track();  // returns index
let track_b = mixer.add_track();

// Configure tracks
if let Some(track) = mixer.track_mut(track_a) {
    track.gain = 0.8;
    track.muted = false;
    track.soloed = false;
    track.tempo_ratio = 1.0;       // 1.0 = normal speed
    track.pitch_semitones = 0.0;   // in semitones, e.g. 2.0 = up a whole step
    track.state = TrackState::Playing;
}

// Mix all tracks into output buffer — call from audio callback
let mut output = vec![0.0f32; frames * 2];  // frames × channels
mixer.mix(&mut output);

// Remove a track
mixer.remove_track(track_b);
```

**`Track` fields:**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `gain` | `f32` | `1.0` | Linear gain (1.0 = unity, 2.0 = +6 dB) |
| `muted` | `bool` | `false` | Silence this track |
| `soloed` | `bool` | `false` | Solo mode (other non-soloed tracks are silenced) |
| `tempo_ratio` | `f32` | `1.0` | Playback speed (0.25–4.0) |
| `pitch_semitones` | `f32` | `0.0` | Pitch offset in semitones (–24 to +24) |
| `state` | `TrackState` | `Stopped` | `Stopped` / `Playing` / `Paused` |
| `effects` | `EffectChain` | empty | Per-track effect chain |

### Track Control

```rust
pub enum TrackState {
    Stopped,
    Playing,
    Paused,
}

// Set via field:
track.state = TrackState::Playing;
```

### SampleClock

`MultiTrackMixer` maintains a shared `SampleClock` for sample-accurate transport synchronisation.

```rust
let clock = mixer.clock();

let pos_samples = clock.position();          // u64 — current position in samples
let pos_seconds = clock.position_seconds();  // f64

// Called automatically by mixer.mix(), but can be driven manually:
clock.advance(frames_processed as u64);

clock.seek(48_000 * 10);  // jump to 10 seconds
clock.reset();             // back to sample 0

let sample_pos = clock.seconds_to_samples(2.5);  // → 120_000 at 48 kHz
```

`SampleClock` uses `AtomicU64` internally; reads and writes are lock-free and safe across threads.

---

## Error Handling

```rust
pub enum AudioError {
    DeviceNotFound,
    DeviceEnumeration(String),
    UnsupportedConfig(String),
    StreamBuild(String),
    StreamControl(String),
    RingBufferOverflow  { dropped: usize },
    RingBufferUnderrun  { requested: usize, available: usize },
    ResamplerInit(String),
    ResamplerProcess(String),
}

pub type Result<T> = std::result::Result<T, AudioError>;
```

All public functions that can fail return `Result<T>`. Errors carry context strings where applicable. `AudioError` implements `std::error::Error` via `thiserror`.

---

## Real-Time Constraints

The audio callback runs on a high-priority OS thread. Violating these rules causes glitches or dropouts:

| Rule | Detail |
|------|--------|
| **No heap allocation** | No `Vec::new()`, `Box::new()`, `String::new()` inside the callback |
| **No blocking locks** | `Mutex::lock()` may block; use lock-free structures (`AtomicU64`, `rtrb`) |
| **No blocking I/O** | No file reads, no network calls |
| **Complete within buffer time** | A 512-frame buffer at 48 kHz gives ~10.7 ms; finish before then |

**How this library satisfies these constraints:**

- `Mixer`, `EffectChain`, and `MultiTrackMixer::mix()` are zero-allocation.
- `Resampler` pre-allocates all buffers in `new()`.
- `SampleClock` uses `AtomicU64` — no locks.
- `ring_buffer` is backed by `rtrb` — lock-free SPSC.
- Effect `process()` implementations must follow the same rules; user-provided effects must not allocate or block.
