# OwnAudioRust — HighLevel Layer

The **HighLevel** layer provides synchronized multi-track playback, mixing, and real-time native DSP
effects. It wraps the Safe layer handles into clean C# classes to coordinate sample-accurate audio
rendering via the native Rust audio engine.

## Role in the Stack

```
HighLevel  ← THIS LAYER: MultiTrackSession, AudioTrack, FileTrack, MemoryTrack, InputTrack, Effects
  Safe     ← handles, error mapping, callback marshallers, AudioEngine, AudioOutputStream
 Native    ← raw LibraryImport / P/Invoke to ownaudio_ffi
  Rust     ← ownaudio-ffi (cpal + Symphonia + native DSP)
```

---

## Table of Contents

1. [Quick Start](#1-quick-start)
2. [HostApi](#2-hostapi)
3. [MultiTrackSession](#3-multitracksession)
4. [AudioTrack](#4-audiotrack)
5. [Track Variants — FileTrack, MemoryTrack, InputTrack](#5-track-variants)
6. [TrackEffectChain](#6-trackeffectchain)
7. [MasterEffectChain](#7-mastereffectchain)
8. [Effects Reference](#8-effects-reference)
9. [Threading and Object Lifecycle](#9-threading-and-object-lifecycle)

---

## 1. Quick Start

### Multi-track playback with effects (managed ring-buffer tracks)

```csharp
using Ownaudio.Audio;
using Ownaudio.Audio.Tracks;
using Ownaudio.Audio.Effects;
using Ownaudio.Safe;

// Create a Safe engine and a session for stereo rendering at 48 kHz
using var engine  = AudioEngine.Create();
using var session = new MultiTrackSession(sampleRate: 48_000f, channels: 2);

// Add tracks backed by lock-free ring buffers (fill them from any thread)
AudioTrack track1 = session.AddTrack();
AudioTrack track2 = session.AddTrack();

track1.Gain = 0.9f;
track2.PitchSemitones = 2.0f; // pitch up by 2 semitones

// Add a reverb effect to track2
var reverb = (ReverbEffect)track2.Effects.Add(EffectType.Reverb, sampleRate: 48_000f);
reverb.RoomSize = 0.8f;
reverb.Mix      = 0.4f;

// Open output and start rendering (audio thread driven by native cpal)
session.OpenOutput(engine);

// Start all tracks simultaneously (sample-accurate)
session.PlayAll();
await Task.Delay(10_000);
session.PauseAll();
```

### Native file-source track (zero managed overhead)

```csharp
// The Rust prefetch thread decodes and feeds the track;
// no managed pump, no GC pause on the audio path.
FileTrack fileTrack = session.AddFileTrack("music.flac");
fileTrack.Track.Gain = 0.8f;

// Observe end-of-file
fileTrack.Completed += (_, e) =>
    Console.WriteLine($"File finished: {e.EndReason}");
```

### In-memory track (GC-free after initial copy)

```csharp
// Samples are copied into native memory once (control-thread copy).
// The audio path is then entirely in native code.
float[] samples = LoadAudioSamples("effect.wav"); // at session rate/channels
MemoryTrack memTrack = session.AddMemoryTrack(samples, loop: true);
```

### Live input capture routed into the mix

```csharp
// The capture callback writes directly into the track's ring buffer in native code.
InputTrack inputTrack = session.AddInputTrack(engine, device: null, bufferFrames: 0);
inputTrack.Play(); // start capture
```

---

## 2. HostApi

**Namespace:** `Ownaudio.Audio`

Defines the audio host API used when creating the low-level `AudioEngine`.

```csharp
public enum HostApi
{
    Wasapi    = 0,   // Windows Audio Session API — default on Windows
    Asio      = 1,   // Steinberg ASIO — low-latency Windows
    CoreAudio = 2,   // Apple Core Audio — default on macOS
    Alsa      = 3,   // Advanced Linux Sound Architecture — default on Linux
}
```

Pass to `AudioEngine.Create(HostApi? hostApi)`. `null` = platform default.

---

## 3. MultiTrackSession

**Namespace:** `Ownaudio.Audio.Tracks`

Manages a collection of synchronized `AudioTrack` instances sharing a single
sample-accurate transport clock. Owns one native `MultiTrackMixer`.

### Creating a Session

```csharp
using var session = new MultiTrackSession(sampleRate: 48_000f, channels: 2);
```

> `channels` is `ushort`. All tracks and the output stream use this rate and channel count.

### Properties

| Property | Type | Description |
|---|---|---|
| `Tracks` | `IReadOnlyList<AudioTrack>` | All registered tracks |
| `MasterGain` | `float` | Master output gain (0.0 = silence, 1.0 = unity). Ramp-applied on audio thread. |
| `MasterEffects` | `MasterEffectChain` | Effects applied after the fully summed mix |

### Track Management

```csharp
// Ring-buffer track (fill from managed code via Write)
AudioTrack track = session.AddTrack();

// Native file source (Rust prefetch thread)
FileTrack fileTrack = session.AddFileTrack("music.mp3");

// Native memory source (copied once into native memory)
MemoryTrack memTrack = session.AddMemoryTrack(samples, loop: false);

// Native input-capture source (no managed callback)
InputTrack inputTrack = session.AddInputTrack(engine, device: null);

// Remove and dispose a track
session.RemoveTrack(track);

// Access all tracks
IReadOnlyList<AudioTrack> tracks = session.Tracks;
```

### Output

```csharp
// Open the output stream (starts playing immediately)
// The mixer is moved onto the cpal audio thread — no managed callback involved.
AudioOutputStream stream = session.OpenOutput(engine, device: null);
```

Only one output stream may be opened per session. The stream is owned and disposed by the session.

### Transport (sample-accurate)

| Method | Description |
|---|---|
| `PlayAll()` | Start all tracks in a single native call (sample-accurate) |
| `PauseAll()` | Pause all tracks in a single native call |
| `StopAll()` | Stop all tracks and seek to position 0 |
| `GetMasterPeaks()` | Returns `(float Left, float Right)` peak levels for metering |

```csharp
session.PlayAll();
await Task.Delay(5_000);
session.PauseAll();

var (left, right) = session.GetMasterPeaks();
Console.WriteLine($"Peak L: {left:F3}  R: {right:F3}");
```

---

## 4. AudioTrack

**Namespace:** `Ownaudio.Audio.Tracks`

Represents a single audio track within a `MultiTrackSession`. Backed by a lock-free ring
buffer on the native side; fill it with decoded float samples by calling `Write` from any
thread.

### Properties

| Property | Type | Range | Description |
|---|---|---|---|
| `Gain` | `float` | `0.0 – ∞` | Volume factor (1.0 = unity gain). Ramp-applied on audio thread. |
| `Tempo` | `float` | `0.25 – 4.0` | Playback speed factor (1.0 = normal) |
| `PitchSemitones` | `float` | `−24.0 – +24.0` | Pitch shift in semitones (0 = no shift) |
| `Muted` | `bool` | — | When `true`, track output is silenced |
| `Effects` | `TrackEffectChain` | — | Per-track DSP effect chain |
| `Peaks` | `(float Left, float Right)` | — | Current output peak levels |

### Control Methods

| Method | Description |
|---|---|
| `Play()` | Starts or resumes track playback |
| `Pause()` | Pauses track playback, preserving position |
| `Stop()` | Stops playback and seeks back to 0 |
| `Seek(TimeSpan)` | Seeks to the specified position |
| `Write(ReadOnlySpan<float>)` | Push interleaved float samples into the ring buffer |
| `Dispose()` | Removes the track from its mixer and releases handles |

```csharp
AudioTrack track = session.AddTrack();
track.Gain = 0.8f;
track.Muted = false;
track.PitchSemitones = -3.0f;

// Feed samples from a decoder on any thread
float[] buffer = new float[4096];
while (!decoder.IsEndOfStream)
{
    int n = decoder.Read(buffer, 0, buffer.Length);
    track.Write(buffer.AsSpan(0, n));
}
```

---

## 5. Track Variants

### FileTrack

**Namespace:** `Ownaudio.Audio.Tracks`

A track whose audio source is a file decoded entirely in native Rust (Symphonia prefetch thread).
No managed pump, no GC pressure on the audio path.

```csharp
FileTrack fileTrack = session.AddFileTrack("music.flac");
AudioTrack track = fileTrack.Track;

// Events
fileTrack.Completed += (_, e) => Console.WriteLine($"Done: {e.EndReason}");

// Control — delegate to the underlying AudioTrack
track.Gain = 0.9f;
track.Seek(TimeSpan.FromSeconds(30));
```

| Member | Description |
|---|---|
| `Track` | The underlying `AudioTrack` |
| `Completed` | Raised when the file source finishes (end-of-file or explicit stop) |

### MemoryTrack

**Namespace:** `Ownaudio.Audio.Tracks`

A track whose audio source is a buffer copied into native memory once at creation time.
The audio path is entirely native — no managed copy per audio block, no GC involvement.

```csharp
// Samples must be at the session's sample rate and channel count
float[] samples = LoadPcm("hit.wav");
MemoryTrack memTrack = session.AddMemoryTrack(samples, loop: true);
memTrack.Track.Gain = 0.5f;
```

| Constructor Parameter | Description |
|---|---|
| `samples` | Interleaved float samples (at session rate/channels) |
| `loop` | `true` = seamless looping at end-of-buffer |

### InputTrack

**Namespace:** `Ownaudio.Audio.Tracks`

A track whose audio source is a live capture device. The capture callback writes into the
track's ring buffer entirely in native code — no managed callback, no GC stall risk.

```csharp
InputTrack inputTrack = session.AddInputTrack(engine, device: null, bufferFrames: 0);
inputTrack.Play();  // start capture
// ... after some time:
inputTrack.Pause();
```

| Member | Description |
|---|---|
| `Track` | The underlying `AudioTrack` |
| `Play()` | Start capture |
| `Pause()` | Pause capture |

---

## 6. TrackEffectChain

**Namespace:** `Ownaudio.Audio.Tracks`

Manages real-time DSP effects applied in series on a single track. Each effect is backed by
native Rust DSP code; parameter changes take effect on the next audio buffer.

```csharp
// Add an effect
var reverb = (ReverbEffect)track.Effects.Add(EffectType.Reverb, sampleRate: 48_000f);
reverb.RoomSize = 0.75f;
reverb.Mix      = 0.3f;

// Bypass temporarily
reverb.IsEnabled = false;

// Remove by index
track.Effects.RemoveAt(0);

// Clear all
track.Effects.Clear();

// Access current effects
IReadOnlyList<object> chain = track.Effects.Effects;
```

---

## 7. MasterEffectChain

**Namespace:** `Ownaudio.Audio.Tracks`

The master bus effect chain — applied once over the fully summed mix after all tracks are
rendered. Accessible via `MultiTrackSession.MasterEffects`.

```csharp
var masterComp = (CompressorEffect)session.MasterEffects.Add(
    EffectType.Compressor, sampleRate: 48_000f);
masterComp.ThresholdDb = -6f;
masterComp.Ratio       = 4f;
masterComp.AttackMs    = 5f;
masterComp.ReleaseMs   = 100f;

// Remove an effect from the master chain
session.MasterEffects.RemoveAt(0);
session.MasterEffects.Clear();
```

The API mirrors `TrackEffectChain` but routes through the mixer-level master effect FFI.

---

## 8. Effects Reference

All DSP effects are backed by native Rust processing. Parameter writes apply immediately via
the audio command queue and take effect on the next rendered block.

### EffectType Enum

```csharp
public enum EffectType
{
    Reverb, Equalizer, Equalizer30, Compressor, Limiter,
    Delay, Chorus, Distortion, Overdrive, Flanger, Phaser,
    Rotary, AutoGain, Enhancer, Gate, PitchShift, DynamicAmp,
}
```

### Common Properties (all effects)

| Property | Type | Description |
|---|---|---|
| `IsEnabled` | `bool` | `true` = active; `false` = bypassed (pass-through) |
| `Mix` | `float` | Dry/wet ratio (0.0 = dry only, 1.0 = wet only) |

### Concrete Effects and Parameters

| Effect class | Key Parameters |
|---|---|
| **`ReverbEffect`** | `RoomSize`, `Damping`, `Width`, `WetLevel`, `DryLevel` |
| **`EqualizerEffect`** | `Band0`…`Band9` (gains in dB, ±12 dB max) |
| **`Equalizer30Effect`** | `Band0`…`Band29` (gains in dB, ±12 dB max) |
| **`CompressorEffect`** | `ThresholdDb`, `Ratio`, `AttackMs`, `ReleaseMs`, `MakeupGainDb` |
| **`LimiterEffect`** | `ThresholdDb`, `ReleaseMs` |
| **`DelayEffect`** | `DelayTimeMs`, `Feedback`, `PingPong` |
| **`ChorusEffect`** | `RateHz`, `Depth`, `Feedback` |
| **`DistortionEffect`** | `Drive`, `Gain` |
| **`OverdriveEffect`** | `Drive`, `Tone` |
| **`FlangerEffect`** | `RateHz`, `Depth`, `Feedback` |
| **`PhaserEffect`** | `RateHz`, `Depth`, `Feedback` |
| **`RotaryEffect`** | `SpeedHz` (Leslie cabinet simulation) |
| **`AutoGainEffect`** | `TargetLevelDb`, `Speed` |
| **`EnhancerEffect`** | `FrequencyHz`, `Drive` |
| **`GateEffect`** | `ThresholdDb`, `AttackMs`, `ReleaseMs` |
| **`PitchShiftEffect`** | `Semitones` |
| **`DynamicAmpEffect`** | `Attack`, `Sustain` (transient designer) |

### Effects Usage Example

```csharp
// 10-band equalizer
var eq = (EqualizerEffect)track.Effects.Add(EffectType.Equalizer, 48_000f);
eq.Band0 = +3.0f;   // 32 Hz boost
eq.Band4 = -2.0f;   // 1 kHz cut
eq.Band9 = +4.0f;   // 16 kHz air

// Compressor + Limiter chain
var comp = (CompressorEffect)track.Effects.Add(EffectType.Compressor, 48_000f);
comp.ThresholdDb  = -12f;
comp.Ratio        = 3f;
comp.AttackMs     = 10f;
comp.ReleaseMs    = 200f;
comp.MakeupGainDb = 2f;

var limiter = (LimiterEffect)track.Effects.Add(EffectType.Limiter, 48_000f);
limiter.ThresholdDb = -0.5f;

// Reverb
var reverb = (ReverbEffect)track.Effects.Add(EffectType.Reverb, 48_000f);
reverb.RoomSize = 0.8f;
reverb.Damping  = 0.5f;
reverb.Width    = 1.0f;
reverb.Mix      = 0.25f;
```

---

## 9. Threading and Object Lifecycle

### Threading Model

- The native Rust audio thread (cpal) renders audio by draining the mixer's lock-free command
  queue and summing all active tracks. **No managed callback is involved** in the `OpenOutput`
  path — the audio data never crosses into managed memory.
- `AudioTrack.Write()` is safe to call from any thread; it writes into the native ring buffer.
- `track.Gain`, `track.Tempo`, effect parameter writes, etc. are forwarded immediately to native
  code via P/Invoke and queued for the audio thread — the changes are applied at the next block
  boundary without clicks.
- `MultiTrackSession.GetMasterPeaks()` is safe to call from any thread (polls a native atomic).

### Disposal Pattern

`MultiTrackSession` owns all tracks and the native mixer. Dispose it to release everything:

```csharp
using (var session = new MultiTrackSession(48_000f, 2))
{
    // ... setup, OpenOutput, PlayAll ...
} // output stream stopped; all tracks + mixer released

// Manual disposal order when `using` is not available:
// 1. session.PauseAll() — optional but graceful
// 2. session.Dispose() — stops output stream, disposes tracks, destroys mixer
// Never dispose individual tracks after Dispose() has been called on the session.
```

### Disposal Order Rules

```
session.Dispose()
  └── output stream disposed first (audio thread stops)
  └── file tracks disposed (prefetch threads stopped)
  └── memory tracks disposed (native memory freed)
  └── input tracks disposed (capture streams stopped)
  └── audio tracks disposed (ring buffers freed)
  └── mixer handle disposed (native mixer destroyed)
```

---

## Related Documentation

- [OwnAudioSharp Documentation](https://modernmube.github.io/OwnAudioSharp/)
- [Safe Layer](../Safe/README.md)
- [Native Layer](../Native/README.md)

## License

Copyright © 2025 Ownaudio Team  
Part of the OwnAudioSharp project.
