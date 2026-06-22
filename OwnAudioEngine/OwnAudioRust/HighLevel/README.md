# OwnAudioRust — HighLevel Layer

The **HighLevel** layer is the primary developer-facing API. It wraps the Safe layer with
event-driven, stateful abstractions for the most common audio scenarios: playback, capture,
multi-track mixing, and device management. It also provides built-in dependency injection support.

## Role in the Stack

```
HighLevel  ← THIS LAYER: AudioEngine, AudioPlayer, AudioRecorder, MultiTrackSession
  Safe     ← handles, error mapping, callback marshallers
 Native    ← raw LibraryImport / P/Invoke to ownaudio_ffi.dll
  Rust     ← ownaudio-ffi + ownaudio-core
```

**Start here** unless you need direct access to the audio callback (in that case, use the
Safe layer instead).

---

## Table of Contents

1. [Quick Start](#1-quick-start)
2. [AudioEngine](#2-audioengine)
3. [AudioEngineOptions](#3-audioengineoptions)
4. [AudioEngineState](#4-audioenginestate)
5. [HostApi](#5-hostapi)
6. [AudioPlayer](#6-audioplayer)
7. [PlaybackOptions](#7-playbackoptions)
8. [PlaybackState](#8-playbackstate)
9. [AudioFormat](#9-audioformat)
10. [AudioRecorder](#10-audiorecorder)
11. [RecorderOptions](#11-recorderoptions)
12. [AudioDataAvailableEventArgs](#12-audiodataavailableeventargs)
13. [AudioDeviceManager](#13-audiodevicemanager)
14. [AudioDevice (HighLevel)](#14-audiodevice-highlevel)
15. [MultiTrackSession](#15-multitracksession)
16. [AudioTrack](#16-audiotrack)
17. [TrackEffectChain](#17-trackeffectchain)
18. [Effects Reference](#18-effects-reference)
19. [Dependency Injection](#19-dependency-injection)
20. [Threading Model](#20-threading-model)
21. [Object Lifecycle and Disposal](#21-object-lifecycle-and-disposal)
22. [Error Handling](#22-error-handling)

---

## 1. Quick Start

### Play audio

```csharp
using Ownaudio.Audio;
using Ownaudio.Audio.Playback;
using Ownaudio.Audio.Streams;

// Create the engine with defaults (44 100 Hz, stereo, Float32)
using var engine = AudioEngine.Create();

// Create a player
using var player = engine.CreatePlayer(new PlaybackOptions { Volume = 0.8f });

// Load pre-decoded PCM audio (interleaved float32 samples)
// Use OwnAudioSharp's managed decoders to decode MP3/WAV/FLAC to PCM first
using var pcmStream = File.OpenRead("audio.pcm");
player.Load(pcmStream, new AudioFormat(44_100, 2, SampleFormat.Float32));

// Subscribe to events
player.StateChanged  += (_, e) => Console.WriteLine($"State: {e.NewState}");
player.PlaybackEnded += (_, e) => Console.WriteLine($"Ended: {e.Reason}");

// Play, seek, pause, stop
player.Play();
await Task.Delay(5000);

player.Seek(TimeSpan.FromSeconds(10));

player.Pause();
await Task.Delay(1000);

player.Play();
await Task.Delay(2000);

player.Stop();
```

### Record audio

```csharp
using Ownaudio.Audio;
using Ownaudio.Audio.Capture;
using System.Threading.Channels;

using var engine = AudioEngine.Create();

using var recorder = engine.CreateRecorder(new RecorderOptions
{
    SampleRate = 44_100,
    Channels   = 1,    // mono
});

var channel = Channel.CreateBounded<float[]>(capacity: 64);

// DataAvailable fires on the real-time audio thread — keep it fast!
recorder.DataAvailable += (_, e) =>
{
    channel.Writer.TryWrite(e.Data.ToArray());
};

// LevelChanged fires on a ThreadPool thread ~30 times per second — safe for UI
recorder.LevelChanged += (_, e) =>
{
    Console.WriteLine($"RMS: {e.RmsLevel:F3}  Peak: {e.PeakLevel:F3}");
};

recorder.Start();
await Task.Delay(5000);
recorder.Stop();

channel.Writer.Complete();
await foreach (float[] buf in channel.Reader.ReadAllAsync())
{
    // Process captured samples: write to file, analyse, etc.
}
```

### Multi-track playback

```csharp
using Ownaudio.Audio.Tracks;
using Ownaudio.Audio.Effects;

using var session = new MultiTrackSession(sampleRate: 48_000, channels: 2);

AudioTrack track1 = session.AddTrack();
AudioTrack track2 = session.AddTrack();

track1.Gain = 0.9f;
track2.PitchSemitones = 2.0f;   // shift up 2 semitones

// Add a reverb effect to track2
var reverb = (ReverbEffect)track2.Effects.Add(EffectType.Reverb, sampleRate: 48_000f);
reverb.RoomSize = 0.8f;
reverb.Mix      = 0.4f;

session.PlayAll();    // sample-accurate simultaneous start
await Task.Delay(10_000);
session.PauseAll();
```

---

## 2. AudioEngine

**Namespace:** `Ownaudio.Audio`

Central entry point for the HighLevel API. One instance per application is recommended.
It owns all players, recorders, and their underlying native streams.

### Creating an Engine

```csharp
// Defaults: 44 100 Hz, 2 ch, Float32, platform default host API
using var engine = AudioEngine.Create();

// Custom options
using var engine = AudioEngine.Create(new AudioEngineOptions
{
    DefaultSampleRate  = 48_000,
    DefaultChannels    = 2,
    PreferredHostApi   = HostApi.Wasapi,
});
```

### Properties

| Property | Type | Description |
|---|---|---|
| `State` | `AudioEngineState` | Current lifecycle state of the engine |
| `Devices` | `AudioDeviceManager` | Access to available audio input/output devices |

### Methods

| Method | Description |
|---|---|
| `Create(options?)` | Static factory. Initialises the native engine and verifies the ABI version. |
| `CreatePlayer(options?)` | Creates and registers a new `AudioPlayer`. |
| `CreateRecorder(options?)` | Creates and registers a new `AudioRecorder`. |
| `Dispose()` | Synchronous disposal. Stops and disposes all child players and recorders. |
| `DisposeAsync()` | Async disposal. Preferred when calling from an async context. |

### Faulted Event

```csharp
engine.Faulted += (_, e) =>
{
    // Fires on a ThreadPool thread when the engine encounters an unrecoverable error.
    // After this, State == AudioEngineState.Faulted.
    // Dispose this engine and create a new one to recover.
    Console.Error.WriteLine($"Engine fault: {e.Exception.Message}");
};
```

### Async Disposal

```csharp
// Preferred in async contexts to avoid blocking the calling thread
await engine.DisposeAsync();
```

---

## 3. AudioEngineOptions

**Namespace:** `Ownaudio.Audio`

Passed to `AudioEngine.Create()`. All properties have sensible defaults.
Only override what your application needs.

```csharp
var options = new AudioEngineOptions
{
    DefaultSampleRate     = 48_000,          // Hz; valid: 8 000 – 192 000; default: 44 100
    DefaultChannels       = 2,               // valid: 1 – 32; default: 2
    DefaultSampleFormat   = SampleFormat.Float32,   // default: Float32
    DefaultBufferSizeFrames = 0,             // 0 = platform default; non-zero: 16 – 8 192
    PreferredHostApi      = null,            // null = platform default (WASAPI/CoreAudio/ALSA)
};
```

### Properties

| Property | Default | Description |
|---|---|---|
| `DefaultSampleRate` | `44_100` | Sample rate applied to players/recorders that do not specify one |
| `DefaultChannels` | `2` | Channel count applied to players/recorders that do not specify one |
| `DefaultSampleFormat` | `Float32` | Sample format for new streams |
| `DefaultBufferSizeFrames` | `0` | Buffer size in frames (0 = automatic) |
| `PreferredHostApi` | `null` | Specific audio host API, or null for the platform default |

> There is no automatic fallback for `PreferredHostApi`. Requesting an unavailable host API
> causes `Create()` to throw `HostApiNotAvailableException` or `AsioDriverNotFoundException`.

---

## 4. AudioEngineState

**Namespace:** `Ownaudio.Audio`

```csharp
public enum AudioEngineState
{
    Uninitialized,  // created but not yet ready (internal use)
    Running,        // active and ready to create players and recorders
    Stopped,        // disposed; cannot be used any further
    Faulted,        // unrecoverable error — see the Faulted event for details
}
```

Check `engine.State` before creating players or recorders if the engine may have faulted.

---

## 5. HostApi

**Namespace:** `Ownaudio.Audio`

```csharp
public enum HostApi
{
    Wasapi    = 0,   // Windows Audio Session API — recommended for Windows
    Asio      = 1,   // Steinberg ASIO — low-latency Windows; requires a driver
    CoreAudio = 2,   // Apple Core Audio — default for macOS
    Alsa      = 3,   // Advanced Linux Sound Architecture — default for Linux
}
```

Pass via `AudioEngineOptions.PreferredHostApi`, or leave it `null` for the platform default.

---

## 6. AudioPlayer

**Namespace:** `Ownaudio.Audio.Playback`

Plays back pre-loaded PCM audio through an output device. Obtain instances through
`AudioEngine.CreatePlayer()`.

> **Note:** `AudioPlayer` currently expects pre-decoded PCM data in memory.
> Use the managed decoders in the broader OwnAudioSharp library (MP3 / WAV / FLAC)
> to decode audio files before calling `Load()`.

### Loading Audio

```csharp
// Load from a Stream containing interleaved IEEE 754 float32 samples (little-endian)
player.Load(pcmStream, new AudioFormat(sampleRate: 44_100, channels: 2));

// The format must match the actual content of the stream.
// Any active playback is stopped before the new buffer is loaded.
```

`AudioFormat` parameters:

| Parameter | Description |
|---|---|
| `SampleRate` | Sample rate of the PCM data in Hz |
| `Channels` | Number of interleaved channels |
| `SampleType` | `SampleFormat.Float32` (default), `Int16`, or `UInt16` |

### Playback Control Methods

| Method | Description |
|---|---|
| `Play()` | Starts or resumes playback. Throws if no audio is loaded. |
| `Pause()` | Pauses playback. Position is preserved. |
| `Stop()` | Stops playback and resets position to 0. |
| `Seek(TimeSpan)` | Jumps to the specified position. Clamped to `[0, Duration]`. |

```csharp
player.Play();

// Seek to 30 seconds
player.Seek(TimeSpan.FromSeconds(30));

// Pause, then resume
player.Pause();
player.Play();

// Restart from the beginning
player.Stop();
player.Play();
```

### Properties

| Property | Type | Description |
|---|---|---|
| `State` | `PlaybackState` | Current state: `Stopped`, `Playing`, or `Paused` |
| `Position` | `TimeSpan` | Current playback position (updated each buffer cycle) |
| `Duration` | `TimeSpan?` | Total duration of the loaded audio, or `null` if nothing is loaded |
| `Volume` | `float` | Playback volume `[0.0, 1.0]`; changes take effect on the next buffer cycle |
| `IsLooping` | `bool` | When `true`, playback restarts automatically at the end |
| `Format` | `AudioFormat` | Format of the currently loaded audio |

```csharp
// Volume control
player.Volume = 0.5f;    // 50%

// Looping
player.IsLooping = true;

// Current position display
Console.WriteLine($"{player.Position:mm\\:ss} / {player.Duration:mm\\:ss}");
```

### Events

Both events may fire on a `ThreadPool` thread when triggered by natural playback
completion. Use a dispatcher or `SynchronizationContext` when updating UI from them.

| Event | When it fires |
|---|---|
| `StateChanged` | Every time `State` transitions (e.g. `Stopped → Playing`) |
| `PlaybackEnded` | When playback finishes or `Stop()` is called |

```csharp
player.StateChanged += (_, e) =>
{
    Console.WriteLine($"Playback state: {e.OldState} → {e.NewState}");
};

player.PlaybackEnded += (_, e) =>
{
    // e.Reason: Finished (natural end) or Stopped (Stop() was called)
    Console.WriteLine($"Playback ended: {e.Reason}");
};
```

### Full Example with Events and Volume Fade

```csharp
using var engine = AudioEngine.Create();
using var player = engine.CreatePlayer();

player.StateChanged  += (_, e) => Console.WriteLine($"State → {e.NewState}");
player.PlaybackEnded += (_, e) => Console.WriteLine($"Ended: {e.Reason}");

using var file = File.OpenRead("song.pcm");
player.Load(file, new AudioFormat(44_100, 2));

player.Volume    = 1.0f;
player.IsLooping = false;
player.Play();

// Fade out over 5 seconds
for (float v = 1.0f; v >= 0f; v -= 0.01f)
{
    player.Volume = v;
    await Task.Delay(50);
}

player.Stop();
```

---

## 7. PlaybackOptions

**Namespace:** `Ownaudio.Audio.Playback`

Configuration for an `AudioPlayer`, passed to `AudioEngine.CreatePlayer()`.

```csharp
var options = new PlaybackOptions
{
    DeviceName       = null,     // null = system default output device
    Volume           = 1.0f,     // initial volume [0.0, 1.0]; default: 1.0
    IsLooping        = false,     // repeat on end; default: false
    BufferSizeFrames = 0,         // 0 = platform default; non-zero: 16 – 8 192
};
```

| Property | Default | Description |
|---|---|---|
| `DeviceName` | `null` | Exact name of the output device (from `engine.Devices.PlaybackDevices`), or `null` for the system default |
| `Volume` | `1.0f` | Initial playback volume |
| `IsLooping` | `false` | Whether to loop at the end |
| `BufferSizeFrames` | `0` | Audio buffer size in frames |

---

## 8. PlaybackState

**Namespace:** `Ownaudio.Audio.Playback`

```csharp
public enum PlaybackState
{
    Stopped,   // no audio is playing; position is at 0
    Playing,   // audio is actively being output
    Paused,    // output is suspended; position is preserved
}
```

---

## 9. AudioFormat

**Namespace:** `Ownaudio.Audio.Streams`

An immutable `readonly record struct` that describes the format of PCM audio data.

```csharp
// Create an AudioFormat
var format = new AudioFormat(
    SampleRate: 44_100,
    Channels: 2,
    SampleType: SampleFormat.Float32);   // default when omitted

// Derive time/sample counts
long totalSamples = format.SamplesForDuration(TimeSpan.FromSeconds(10));
TimeSpan duration = format.DurationForSamples(totalSamples);

// Bytes per sample
int bps = format.BytesPerSample;   // 4 for Float32, 2 for Int16/UInt16
```

### SampleFormat (HighLevel)

```csharp
public enum SampleFormat
{
    Float32,   // 32-bit IEEE 754 float — recommended
    Int16,     // signed 16-bit integer
    UInt16,    // unsigned 16-bit integer
}
```

---

## 10. AudioRecorder

**Namespace:** `Ownaudio.Audio.Capture`

Captures audio from an input device and delivers it through events.
Obtain instances through `AudioEngine.CreateRecorder()`.

### Capture Control Methods

| Method | Description |
|---|---|
| `Start()` | Opens the input stream and begins capture. |
| `Stop()` | Stops capture and releases the native stream. |

### Properties

| Property | Type | Description |
|---|---|---|
| `State` | `RecorderState` | Current state: `Stopped` or `Recording` |
| `Format` | `AudioFormat` | The audio format used for capture |

### Events

| Event | Thread | Description |
|---|---|---|
| `DataAvailable` | **Real-time audio thread** | Fires for every captured buffer. Keep the handler lock-free. |
| `LevelChanged` | `ThreadPool` (~30 Hz) | Fires with RMS and peak levels. Safe for UI updates. |

```csharp
using var recorder = engine.CreateRecorder(new RecorderOptions
{
    SampleRate = 48_000,
    Channels   = 2,
});

// DataAvailable — real-time thread, MUST be fast
recorder.DataAvailable += (_, e) =>
{
    // e.Data    : ReadOnlyMemory<float> — managed copy, safe after handler returns
    // e.FrameCount : frames in this buffer
    // e.Channels   : channels per frame

    // Hand off to a background thread immediately
    myChannel.Writer.TryWrite(e.Data.ToArray());
};

// LevelChanged — ThreadPool thread, safe for UI
recorder.LevelChanged += (_, e) =>
{
    // e.RmsLevel  : RMS level [0.0, 1.0]
    // e.PeakLevel : peak level [0.0, 1.0]
    UpdateMeter(e.RmsLevel, e.PeakLevel);
};

recorder.Start();
await Task.Delay(5000);
recorder.Stop();
```

### RecorderState

```csharp
public enum RecorderState
{
    Stopped,    // no capture active
    Recording,  // capture is active; DataAvailable is firing
}
```

---

## 11. RecorderOptions

**Namespace:** `Ownaudio.Audio.Capture`

Configuration for an `AudioRecorder`, passed to `AudioEngine.CreateRecorder()`.

```csharp
var options = new RecorderOptions
{
    DeviceName       = null,              // null = system default input device
    SampleRate       = 44_100,            // Hz; valid: 8 000 – 192 000; default: 44 100
    Channels         = 1,                 // valid: 1 – 32; default: 1 (mono)
    SampleType       = SampleFormat.Float32,  // default: Float32
    BufferSizeFrames = 0,                 // 0 = platform default
};
```

| Property | Default | Description |
|---|---|---|
| `DeviceName` | `null` | Exact device name (from `engine.Devices.CaptureDevices`), or `null` for system default |
| `SampleRate` | `44_100` | Capture sample rate in Hz |
| `Channels` | `1` | Number of capture channels |
| `SampleType` | `Float32` | Sample data format |
| `BufferSizeFrames` | `0` | Buffer size in frames |

---

## 12. AudioDataAvailableEventArgs

**Namespace:** `Ownaudio.Audio.Capture`

Delivered by `AudioRecorder.DataAvailable`.

| Property | Type | Description |
|---|---|---|
| `Data` | `ReadOnlyMemory<float>` | Managed copy of the captured samples. Safe to access after the handler returns. |
| `FrameCount` | `int` | Number of audio frames in this buffer |
| `Channels` | `int` | Number of interleaved channels per frame |

`Data.Length == FrameCount * Channels`

> The `Data` buffer is a copy made inside the recorder — unlike the Safe layer's
> `AudioInputCallbackArgs.Buffer`, you do not need to copy it again.

---

## 13. AudioDeviceManager

**Namespace:** `Ownaudio.Audio.Devices`

Provides access to available audio devices. Obtained through `engine.Devices`.

### Properties

| Property | Type | Description |
|---|---|---|
| `PlaybackDevices` | `IReadOnlyList<AudioDevice>` | All output devices; populated on first access |
| `CaptureDevices` | `IReadOnlyList<AudioDevice>` | All input devices; populated on first access |
| `DefaultPlaybackDevice` | `AudioDevice?` | System default output device |
| `DefaultCaptureDevice` | `AudioDevice?` | System default input device |
| `SupportsHotPlug` | `bool` | Always `false` — hot-plug events are not yet implemented |

### Methods

| Method | Description |
|---|---|
| `Refresh()` | Re-enumerates devices from the OS. Fires `DeviceListChanged` if the list changed. |

```csharp
// List all output devices
foreach (AudioDevice dev in engine.Devices.PlaybackDevices)
{
    Console.WriteLine($"{dev.Name} (default={dev.IsDefault})");
}

// Get the default capture device
AudioDevice? mic = engine.Devices.DefaultCaptureDevice;
Console.WriteLine($"Default mic: {mic?.Name ?? "none"}");

// Re-enumerate after hardware change
engine.Devices.Refresh();
```

> **Hot-plug:** `DeviceListChanged` is declared but not yet fired automatically.
> Poll with `Refresh()` to detect device connections and disconnections.

---

## 14. AudioDevice (HighLevel)

**Namespace:** `Ownaudio.Audio.Devices`

An immutable device descriptor returned by `AudioDeviceManager`.

| Property | Type | Description |
|---|---|---|
| `Id` | `string` | Unique device identifier (equals `Name` in the current implementation) |
| `Name` | `string` | Human-readable OS device name |
| `Type` | `AudioDeviceType` | `Playback` or `Capture` |
| `IsDefault` | `bool` | True for the system default device of its type |
| `MaxInputChannels` | `int` | Maximum supported input channels |
| `MaxOutputChannels` | `int` | Maximum supported output channels |
| `DefaultSampleRate` | `int` | Device preferred sample rate |

```csharp
// Select a specific output device by name
AudioDevice? focusrite = engine.Devices.PlaybackDevices
    .FirstOrDefault(d => d.Name.Contains("Focusrite"));

// Use it when creating a player
var player = engine.CreatePlayer(new PlaybackOptions
{
    DeviceName = focusrite?.Name,   // null falls back to system default
});
```

---

## 15. MultiTrackSession

**Namespace:** `Ownaudio.Audio.Tracks`

Manages a collection of synchronized `AudioTrack` instances sharing a single
sample-accurate transport clock backed by the native `MultiTrackMixer`.

> **Note:** `MultiTrackSession` is created directly (not via `AudioEngine`).

### Creating a Session

```csharp
// sampleRate and channels define the session output format
using var session = new MultiTrackSession(sampleRate: 48_000f, channels: 2);
```

### Track Management

```csharp
// Add a track (returns AudioTrack)
AudioTrack track1 = session.AddTrack();
AudioTrack track2 = session.AddTrack();

// Remove and dispose a track
session.RemoveTrack(track1);

// Read all registered tracks
IReadOnlyList<AudioTrack> all = session.Tracks;
```

### Transport Control

| Method | Description |
|---|---|
| `PlayAll()` | Starts all tracks simultaneously against the shared clock |
| `PauseAll()` | Pauses all tracks |
| `StopAll()` | Stops all tracks and resets positions to zero |

```csharp
session.PlayAll();
await Task.Delay(5000);
session.PauseAll();
await Task.Delay(2000);
session.PlayAll();   // resumes from where each track paused
await Task.Delay(3000);
session.StopAll();
```

---

## 16. AudioTrack

**Namespace:** `Ownaudio.Audio.Tracks`

Represents a single track within a `MultiTrackSession`. Obtained from `session.AddTrack()`.

### Properties

| Property | Type | Range | Description |
|---|---|---|---|
| `Gain` | `float` | `0.0 – ∞` (1.0 = unity) | Linear amplitude scale |
| `Tempo` | `float` | `0.25 – 4.0` | Playback speed ratio (1.0 = normal) |
| `PitchSemitones` | `float` | `−24 – +24` | Pitch shift in semitones |
| `Muted` | `bool` | — | When `true`, output is silenced |
| `Effects` | `TrackEffectChain` | — | DSP effect chain for this track |

```csharp
AudioTrack track = session.AddTrack();

track.Gain           = 0.8f;    // 80% volume
track.Tempo          = 1.5f;    // 50% faster
track.PitchSemitones = -2.0f;   // lower pitch by 2 semitones
track.Muted          = false;
```

### Per-Track Transport

Each track also supports individual play/pause/stop/seek, independent of `PlayAll()`.

| Method | Description |
|---|---|
| `Play()` | Starts or resumes this track |
| `Pause()` | Pauses this track; position preserved |
| `Stop()` | Stops this track; position reset to 0 |
| `Seek(TimeSpan)` | Seeks to the given position |

```csharp
// Individual control
track1.Play();
track2.Play();

track1.Seek(TimeSpan.FromSeconds(10));
track2.Muted = true;
```

---

## 17. TrackEffectChain

**Namespace:** `Ownaudio.Audio.Tracks`

Manages the ordered list of DSP effects attached to an `AudioTrack`.
Effects are applied in the order they are added.

Accessed via `track.Effects`.

### Methods

| Method | Description |
|---|---|
| `Add(EffectType, sampleRate)` | Creates and appends a native-backed effect. Returns the typed effect object. |
| `RemoveAt(int)` | Removes and disposes the effect at the given index. |
| `Clear()` | Removes all effects. |

### Properties

| Property | Type | Description |
|---|---|---|
| `Effects` | `IReadOnlyList<object>` | Current effects in chain order |

```csharp
// Add effects
var reverb = (ReverbEffect)track.Effects.Add(EffectType.Reverb,     sampleRate: 48_000f);
var eq     = (EqualizerEffect)track.Effects.Add(EffectType.Equalizer, sampleRate: 48_000f);

// Configure
reverb.RoomSize = 0.75f;
reverb.Mix      = 0.3f;
eq.Band5        = +3.0f;   // boost 1 kHz band by 3 dB

// Bypass an effect without removing it
reverb.IsEnabled = false;   // bypassed
reverb.IsEnabled = true;    // active again

// Remove the first effect
track.Effects.RemoveAt(0);

// Remove all effects
track.Effects.Clear();
```

---

## 18. Effects Reference

All 17 DSP effect types are exposed as typed C# wrapper classes.
Each class exposes strongly-typed properties that forward immediately to the native engine.

### Adding an Effect

```csharp
// Always cast the return value to the concrete effect type
var reverb = (ReverbEffect)track.Effects.Add(EffectType.Reverb, sampleRate: 48_000f);
```

### EffectType Enum

```csharp
public enum EffectType
{
    Reverb, Equalizer, Equalizer30, Compressor, Limiter,
    Delay, Chorus, Distortion, Overdrive, Flanger, Phaser,
    Rotary, AutoGain, Enhancer, Gate, PitchShift, DynamicAmp,
}
```

### Effect Classes and Their Parameters

#### ReverbEffect

Freeverb-based algorithmic reverb.

| Property | Range | Default | Description |
|---|---|---|---|
| `IsEnabled` | bool | `true` | Active / bypassed |
| `Mix` | 0.0 – 1.0 | `0.5` | Dry/wet mix |
| `RoomSize` | 0.0 – 1.0 | `0.5` | Simulated room size |
| `Damping` | 0.0 – 1.0 | `0.5` | High-frequency absorption |
| `Width` | 0.0 – 2.0 | `1.0` | Stereo width |
| `WetLevel` | 0.0 – 1.0 | `0.33` | Wet signal level |
| `DryLevel` | 0.0 – 1.0 | `0.67` | Dry signal level |

```csharp
var reverb = (ReverbEffect)track.Effects.Add(EffectType.Reverb, 48_000f);
reverb.RoomSize = 0.8f;   // large room
reverb.Damping  = 0.3f;   // bright reverb tail
reverb.Mix      = 0.35f;
```

#### EqualizerEffect (10-band)

10-band parametric equalizer. ISO center frequencies:
31 Hz, 62 Hz, 125 Hz, 250 Hz, 500 Hz, 1 kHz, 2 kHz, 4 kHz, 8 kHz, 16 kHz.

| Property | Range | Description |
|---|---|---|
| `IsEnabled` | bool | Active / bypassed |
| `Mix` | 0.0 – 1.0 | Dry/wet mix (1.0 = full effect) |
| `Band0` – `Band9` | −12 dB – +12 dB | Gain for each frequency band |

```csharp
var eq = (EqualizerEffect)track.Effects.Add(EffectType.Equalizer, 48_000f);
eq.Band0 = +4.0f;    // boost 31 Hz (sub-bass)
eq.Band2 = -2.0f;    // cut 125 Hz (muddiness)
eq.Band5 = +3.0f;    // presence boost at 1 kHz
eq.Band9 = +2.0f;    // air boost at 16 kHz
```

#### Equalizer30Effect (1/3-octave, 30 bands)

30-band equalizer covering 20 Hz – 20 kHz in third-octave steps.
Properties: `Band0` – `Band29`, each `−12 dB – +12 dB`.

#### CompressorEffect

Dynamic range compressor. Activate / deactivate with `IsEnabled`.

#### LimiterEffect

Brickwall limiter. Activate / deactivate with `IsEnabled`.

#### DelayEffect

Tape-style delay. Activate / deactivate with `IsEnabled`.

#### ChorusEffect

Pitch modulation chorus. Activate / deactivate with `IsEnabled`.

#### DistortionEffect

Hard-clipping distortion. Activate / deactivate with `IsEnabled`.

#### OverdriveEffect

Soft-clipping overdrive. Activate / deactivate with `IsEnabled`.

#### FlangerEffect

Comb-filter flanger. Activate / deactivate with `IsEnabled`.

#### PhaserEffect

All-pass phase-shift phaser. Activate / deactivate with `IsEnabled`.

#### RotaryEffect

Leslie cabinet simulation. Activate / deactivate with `IsEnabled`.

#### AutoGainEffect

Automatic gain control. Activate / deactivate with `IsEnabled`.

#### EnhancerEffect

Harmonic exciter / enhancer. Activate / deactivate with `IsEnabled`.

#### GateEffect

Noise gate. Activate / deactivate with `IsEnabled`.

#### PitchShiftEffect

Real-time pitch shifting. Activate / deactivate with `IsEnabled`.

#### DynamicAmpEffect

Dynamic amplifier / transient shaper. Activate / deactivate with `IsEnabled`.

---

## 19. Dependency Injection

The HighLevel layer ships with a `Microsoft.Extensions.DependencyInjection` extension so you
can register `AudioEngine` as a singleton managed by your DI container.

### Registration

```csharp
// Program.cs (ASP.NET Core / Generic Host / .NET MAUI)
builder.Services.AddOwnAudio(opts =>
{
    opts.DefaultSampleRate = 48_000;
    opts.DefaultChannels   = 2;
    opts.PreferredHostApi  = HostApi.Wasapi;  // or null for platform default
});
```

- `AudioEngine` is registered as a **singleton**.
- It is created **lazily** on first resolution from the container.
- It is disposed automatically when the DI container is disposed.

### Injecting into Services

```csharp
public class MyAudioService
{
    private readonly AudioEngine _engine;

    public MyAudioService(AudioEngine engine)
    {
        _engine = engine;
    }

    public void PlayBeep()
    {
        using var player = _engine.CreatePlayer();
        // Load and play...
    }
}
```

---

## 20. Threading Model

| Context | Thread | Rules |
|---|---|---|
| `AudioPlayer` audio fill callback | Real-time native OS thread | No allocation, no locking, no blocking I/O |
| `AudioRecorder.DataAvailable` | Real-time native OS thread | No allocation, no locking, no blocking I/O |
| `AudioRecorder.LevelChanged` | `ThreadPool` | Standard managed code; safe for UI updates |
| `AudioPlayer.StateChanged` | Caller's thread or `ThreadPool` (on natural end) | Use dispatcher when updating UI |
| `AudioPlayer.PlaybackEnded` | Caller's thread or `ThreadPool` (on natural end) | Use dispatcher when updating UI |
| `AudioEngine.Faulted` | `ThreadPool` | Use dispatcher when updating UI |
| `Play()`, `Pause()`, `Stop()`, `Seek()`, `Load()` | Caller's thread | Do not call concurrently on the same instance |

### Updating UI from Events

Events that fire on a `ThreadPool` thread must be marshalled to the UI thread before
updating controls. Example for Avalonia:

```csharp
player.PlaybackEnded += (_, e) =>
{
    Dispatcher.UIThread.Post(() =>
    {
        statusLabel.Text = "Finished";
    });
};
```

For WPF use `Application.Current.Dispatcher.Invoke()`, for MAUI use
`MainThread.BeginInvokeOnMainThread()`.

---

## 21. Object Lifecycle and Disposal

### Ownership Tree

```
AudioEngine
 ├─ AudioPlayer     → owns an AudioOutputStream (Safe layer)
 ├─ AudioRecorder   → owns an AudioInputStream (Safe layer)
 └─ MultiTrackSession  → owns a MixerHandle (Safe layer)
      └─ AudioTrack  → non-owning TrackHandle
           └─ Effects (via TrackEffectChain) → non-owning EffectHandle
```

- Disposing `AudioEngine` cascades: all registered players and recorders are stopped and disposed.
- Dispose child objects first if you need finer control over shutdown order.
- `MultiTrackSession` is not tracked by `AudioEngine` — dispose it manually.

### Recommended Disposal Pattern

```csharp
// All via using — automatic and exception-safe
using var engine   = AudioEngine.Create();
using var player   = engine.CreatePlayer();
using var recorder = engine.CreateRecorder();
using var session  = new MultiTrackSession(48_000f, 2);

// ... use ...

// On scope exit: recorder, player, session, then engine — all disposed automatically
```

### Async Disposal

```csharp
await using var engine = AudioEngine.Create();
// await using calls DisposeAsync(), which stops streams on a background thread
```

---

## 22. Error Handling

The HighLevel layer wraps Safe-layer exceptions in its own exception types for cleaner
separation between layers.

### Exception Types

| HighLevel Exception | Wraps | When |
|---|---|---|
| `AudioEngineException` | `OwnAudioException` | Engine creation or fatal native error |
| `AudioStreamException` | `StreamException` | Stream open, play, or pause failure |
| `AudioDeviceException` | `DeviceException` | Device enumeration failure |

```csharp
try
{
    using var engine = AudioEngine.Create(new AudioEngineOptions
    {
        PreferredHostApi = HostApi.Asio,
    });
}
catch (AudioEngineException ex)
{
    // ex.ErrorCode carries the underlying AudioEngineErrorCode
    Console.Error.WriteLine($"Engine error [{ex.ErrorCode}]: {ex.Message}");
    Console.Error.WriteLine("Inner: " + ex.InnerException?.Message);
}

try
{
    player.Play();
}
catch (AudioStreamException ex)
{
    Console.Error.WriteLine($"Stream error [{ex.ErrorCode}]: {ex.Message}");
}
```

### CallbackError Event

If the `AudioPlayer`'s internal audio fill callback throws an unexpected exception,
the buffer is silenced and the error is surfaced via the Safe layer's `CallbackError`
mechanism. Wire it up for diagnosing rare runtime issues:

```csharp
// Currently surfaced at the Safe layer level — check Safe/AudioOutputStream.CallbackError
// if you need to intercept these errors directly.
```
