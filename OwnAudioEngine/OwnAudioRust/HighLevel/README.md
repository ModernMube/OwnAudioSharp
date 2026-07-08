# OwnAudioRust — HighLevel Layer

The **HighLevel** layer provides synchronized multi-track playback, mixing, and real-time native DSP effects. It wraps the Safe layer handles into clean C# classes to coordinate sample-accurate audio rendering via the native Rust audio engine.

## Role in the Stack

```
HighLevel  ← THIS LAYER: MultiTrackSession, AudioTrack, TrackEffectChain, Effects
  Safe     ← handles, error mapping, callback marshallers, AudioEngine, AudioOutputStream
 Native    ← raw LibraryImport / P/Invoke to ownaudio_ffi.dll
  Rust     ← ownaudio-ffi + ownaudio-core
```

---

## Table of Contents

1. [Quick Start](#1-quick-start)
2. [HostApi](#2-hostapi)
3. [MultiTrackSession](#3-multitracksession)
4. [AudioTrack](#4-audiotrack)
5. [TrackEffectChain](#5-trackeffectchain)
6. [Effects Reference](#6-effects-reference)
7. [Threading and Object Lifecycle](#7-threading-and-object-lifecycle)

---

## 1. Quick Start

### Multi-track playback with effects

```csharp
using Ownaudio.Audio;
using Ownaudio.Audio.Tracks;
using Ownaudio.Audio.Effects;

// Create a session for stereo rendering at 48 kHz
using var session = new MultiTrackSession(sampleRate: 48_000f, channels: 2);

// Add tracks (each wraps a native audio channel/file decoder internally)
AudioTrack track1 = session.AddTrack();
AudioTrack track2 = session.AddTrack();

track1.Gain = 0.9f;
track2.PitchSemitones = 2.0f; // pitch up by 2 semitones

// Add a reverb effect to track2
var reverb = (ReverbEffect)track2.Effects.Add(EffectType.Reverb, sampleRate: 48_000f);
reverb.RoomSize = 0.8f;
reverb.Mix      = 0.4f;

// Play all tracks simultaneously with sample accuracy
session.PlayAll();
await Task.Delay(10_000);
session.PauseAll();
```

---

## 2. HostApi

**Namespace:** `Ownaudio.Audio`

Defines the audio host API used when creating low-level safe engine streams.

```csharp
public enum HostApi
{
    Wasapi    = 0,   // Windows Audio Session API — default on Windows
    Asio      = 1,   // Steinberg ASIO — low-latency Windows
    CoreAudio = 2,   // Apple Core Audio — default on macOS
    Alsa      = 3,   // Advanced Linux Sound Architecture — default on Linux
}
```

---

## 3. MultiTrackSession

**Namespace:** `Ownaudio.Audio.Tracks`

Manages a collection of synchronized `AudioTrack` instances rendering audio against a single clock.

### Creating a Session

```csharp
// Session outputs are mixed down to the specified sample rate and channels
using var session = new MultiTrackSession(sampleRate: 48_000f, channels: 2);
```

### Track Management

```csharp
// Add track
AudioTrack track = session.AddTrack();

// Remove and dispose track
session.RemoveTrack(track);

// Access current tracks
IReadOnlyList<AudioTrack> tracks = session.Tracks;
```

### Master Controls

| Property / Method | Type | Description |
|---|---|---|
| `MasterGain` | `float` | Master output volume (0.0 to 1.0+) |
| `PlayAll()` | Method | Starts transport for all tracks simultaneously |
| `PauseAll()` | Method | Pauses transport for all tracks |
| `StopAll()` | Method | Stops all tracks and resets transport position to zero |
| `GetMasterPeaks()` | Method | Retrieves current peak levels for left/right channels |

---

## 4. AudioTrack

**Namespace:** `Ownaudio.Audio.Tracks`

Represents an individual audio track within a multi-track mixing session.

### Properties

| Property | Type | Range | Description |
|---|---|---|---|
| `Gain` | `float` | `0.0 – ∞` | Volume factor (1.0 = unity gain) |
| `Tempo` | `float` | `0.25 – 4.0` | Playback speed factor (1.0 = normal) |
| `PitchSemitones` | `float` | `−24.0 – +24.0` | Pitch shift in semitones |
| `Muted` | `bool` | — | When `true`, track is silenced |
| `Effects` | `TrackEffectChain` | — | Effect routing chain |
| `Peaks` | `(float Left, float Right)` | — | Current output level peaks |

### Control Methods

| Method | Description |
|---|---|
| `Play()` | Starts or resumes track playback |
| `Pause()` | Pauses track playback, preserving position |
| `Stop()` | Stops playback and seeks back to 0 |
| `Seek(TimeSpan)` | Seeks to the specified position |

---

## 5. TrackEffectChain

**Namespace:** `Ownaudio.Audio.Tracks`

Manages real-time DSP effects applied in series on a track.

```csharp
// Add effect
var reverb = (ReverbEffect)track.Effects.Add(EffectType.Reverb, sampleRate: 48_000f);

// Remove an effect by index
track.Effects.RemoveAt(0);

// Clear all effects
track.Effects.Clear();
```

---

## 6. Effects Reference

All DSP effects are backed by native Rust DSP processing. Properties apply immediately on the render thread.

### EffectType Enum

```csharp
public enum EffectType
{
    Reverb, Equalizer, Equalizer30, Compressor, Limiter,
    Delay, Chorus, Distortion, Overdrive, Flanger, Phaser,
    Rotary, AutoGain, Enhancer, Gate, PitchShift, DynamicAmp,
}
```

### Common Properties

All concrete effects implement a standard way of bypassing and dry/wet control:
*   `IsEnabled` (`bool`): Active vs. bypassed.
*   `Mix` (`float`): Dry/wet ratio (0.0 to 1.0).

### Concrete Effects and Parameters

*   **`ReverbEffect`**: Freeverb algorithmic reverb (`RoomSize`, `Damping`, `Width`, `WetLevel`, `DryLevel`).
*   **`EqualizerEffect`**: 10-band parametric equalizer (`Band0` to `Band9` gains in dB, max ±12 dB).
*   **`Equalizer30Effect`**: 30-band graphic equalizer (`Band0` to `Band29` gains in dB, max ±12 dB).
*   **`CompressorEffect`**: Soft-knee range compressor (`ThresholdDb`, `Ratio`, `AttackMs`, `ReleaseMs`, `MakeupGainDb`).
*   **`LimiterEffect`**: Look-ahead peak limiter (`ThresholdDb`, `ReleaseMs`).
*   **`DelayEffect`**: Stereo delay (`DelayTimeMs`, `Feedback`, `PingPong`).
*   **`ChorusEffect`**: Multi-voice chorus (`RateHz`, `Depth`, `Feedback`).
*   **`DistortionEffect`**: Hard-clipping distortion (`Drive`, `Gain`).
*   **`OverdriveEffect`**: Soft-clipping overdrive (`Drive`, `Tone`).
*   **`FlangerEffect`**: Comb-filter flanger (`RateHz`, `Depth`, `Feedback`).
*   **`PhaserEffect`**: All-pass filter phaser (`RateHz`, `Depth`, `Feedback`).
*   **`RotaryEffect`**: Leslie cabinet simulation (`SpeedHz`).
*   **`AutoGainEffect`**: Automatic gain leveler (`TargetLevelDb`, `Speed`).
*   **`EnhancerEffect`**: Harmonic exciter (`FrequencyHz`, `Drive`).
*   **`GateEffect`**: Noise gate (`ThresholdDb`, `AttackMs`, `ReleaseMs`).
*   **`PitchShiftEffect`**: Real-time pitch shifter (`Semitones`).
*   **`DynamicAmpEffect`**: Transient designer (`Attack`, `Sustain`).

---

## 7. Threading and Object Lifecycle

### Threading Model
*   The multi-track session rendering runs entirely inside a dedicated high-priority, native real-time audio thread.
*   Modifying track parameters (`Gain`, `Tempo`, effects values) from the UI thread is safe and asynchronous. These changes are queued and applied at the boundary of the next audio buffer block.

### Disposal Pattern
`MultiTrackSession` owns unmanaged handles and must be explicitly disposed to prevent memory leaks:

```csharp
using (var session = new MultiTrackSession(48_000f, 2))
{
    // ... setup and playback ...
} // Native session is cleaned up here
```
