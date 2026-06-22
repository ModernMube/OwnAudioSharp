# OwnAudioRust — HighLevel Layer

The **HighLevel** layer is the primary developer-facing API. It wraps the Safe layer with event-driven, stateful abstractions for the most common audio scenarios: playback, capture, multi-track mixing, and device management. It also provides built-in dependency injection support.

## Role in the Stack

```
HighLevel  ← this layer: AudioEngine, AudioPlayer, AudioRecorder, MultiTrackSession
  Safe     ← handles, error mapping, callback marshalling
 Native    ← raw LibraryImport / P/Invoke to ownaudio_ffi.dll
  Rust     ← ownaudio-ffi + ownaudio-core
```

## Quick Start

```csharp
// DI registration (recommended)
services.AddOwnAudio(opts =>
{
    opts.DefaultSampleRate = 48_000;
    opts.DefaultChannels   = 2;
    opts.PreferredHostApi  = HostApi.Wasapi;  // or null for platform default
});

// Manual creation
using var engine = AudioEngine.Create();
using var player = engine.CreatePlayer();
```

## `AudioEngine`

Central entry point. One instance per application.

- `Create(options?)` — Initialises the native engine; checks ABI version immediately.
- `Devices` — Exposes `AudioDeviceManager` for enumeration.
- `CreatePlayer(options?)` / `CreateRecorder(options?)` — Factory methods; the engine tracks all created instances and disposes them on engine disposal.
- `DisposeAsync()` — Preferred disposal path; gracefully stops all players/recorders before destroying the native context.
- `Faulted` event — Fired on `ThreadPool` when an unrecoverable internal error occurs; engine moves to `Faulted` state.

`AudioEngineOptions` lets you set default sample rate, channel count, buffer size, sample format, and preferred host API. All options have sensible defaults (48 000 Hz, 2 ch, F32, platform default).

## Playback — `AudioPlayer`

Pre-loads audio into memory and plays it back with volume control, looping, and seeking.

```csharp
using var player = engine.CreatePlayer(new PlaybackOptions { Volume = 0.8f });
player.Load(pcmStream, new AudioFormat(48_000, 2, SampleFormat.Float32));
player.Play();
player.Seek(TimeSpan.FromSeconds(10));
player.Pause();
```

Key properties: `State`, `Position`, `Duration`, `Volume`, `IsLooping`.

Events (raised on `ThreadPool`):
- `StateChanged` — Whenever `PlaybackState` transitions (Stopped → Playing → Paused).
- `PlaybackEnded` — When audio reaches the end (or `Stop()` is called).

**Note:** `AudioPlayer` currently expects pre-decoded PCM data. Audio file decoding (MP3 / WAV / FLAC) is handled by the existing managed decoders in the broader OwnAudioSharp library and is not part of this layer.

## Capture — `AudioRecorder`

Opens an input stream and exposes captured buffers via events.

```csharp
using var recorder = engine.CreateRecorder(new RecorderOptions { SampleRate = 48_000 });
recorder.DataAvailable += (_, e) =>
{
    // e.Buffer is ReadOnlyMemory<float> — interleaved samples
    // ⚠ Called on the real-time audio thread — no allocation, no locking
    channel.Writer.TryWrite(e.Buffer.ToArray());
};
recorder.LevelChanged += (_, e) =>
{
    UpdateMeter(e.RmsLevel, e.PeakLevel);  // Safe for UI updates (~30 Hz)
};
recorder.Start();
```

- `DataAvailable` — Fired **synchronously on the real-time audio thread**. Use a lock-free queue or `System.Threading.Channels` to hand data to a background thread.
- `LevelChanged` — Fired on `ThreadPool` at ~30 Hz with RMS and peak levels. Safe to use directly for UI level meters.

## Multi-Track — `MultiTrackSession` + `AudioTrack`

Sample-accurate multi-track playback backed by the native `MultiTrackMixer`.

```csharp
using var session = new MultiTrackSession(sampleRate: 48_000, channels: 2);
var track1 = session.AddTrack();
var track2 = session.AddTrack();

track1.Gain           = 0.9f;
track2.PitchSemitones = 2.0f;
track2.Effects.Add(new ReverbEffect());

session.PlayAll();   // Sample-accurate simultaneous start
session.PauseAll();
```

Each `AudioTrack` exposes:
- `Gain` (0.0–∞, 1.0 = unity), `Muted`, `Tempo` (0.25–4.0), `PitchSemitones` (±24)
- `Effects` — `TrackEffectChain` to add/remove DSP effects at runtime
- `Play()` / `Pause()` / `Stop()` / `Seek(TimeSpan)` per track

## Device Management — `AudioDeviceManager`

Accessed via `engine.Devices`.

- `PlaybackDevices` / `CaptureDevices` — `IReadOnlyList<AudioDevice>`; lazily enumerated on first access.
- `DefaultPlaybackDevice` / `DefaultCaptureDevice` — Convenience properties.
- `Refresh()` — Re-enumerates from the OS; fires `DeviceListChanged` if the list changed.

`AudioDevice` properties: `Name`, `Type`, `IsDefault`, `MaxInputChannels`, `MaxOutputChannels`, `DefaultSampleRate`.

## Effects

All 17 DSP effect types from the Rust engine are exposed as C# classes under `HighLevel/Effects/`:

`ReverbEffect`, `EqualizerEffect` (10-band), `Equalizer30Effect` (1/3-octave), `CompressorEffect`, `LimiterEffect`, `DelayEffect`, `ChorusEffect`, `DistortionEffect`, `OverdriveEffect`, `FlangerEffect`, `PhaserEffect`, `RotaryEffect`, `AutoGainEffect`, `EnhancerEffect`, `GateEffect`, `PitchShiftEffect`, `DynamicAmpEffect`

All implement `IAudioEffect`. Parameters are forwarded to native via numeric param IDs. The `Enabled` property toggles bypass without removing the effect from the chain.

## Dependency Injection

```csharp
// Program.cs / Startup.cs
builder.Services.AddOwnAudio(opts =>
{
    opts.PreferredHostApi  = HostApi.Wasapi;
    opts.DefaultSampleRate = 48_000;
});

// Constructor injection
public class MyAudioService(AudioEngine engine) { ... }
```

`AddOwnAudio()` registers `AudioEngine` as a singleton. The engine is created lazily on first resolution and disposed with the DI container.

## Threading Model

| Context | Thread | Rules |
|---------|--------|-------|
| Audio callback (`DataAvailable`, output fill) | Real-time native OS thread | No allocation, no locking, no I/O |
| `StateChanged`, `PlaybackEnded`, `Faulted`, `LevelChanged`, `DeviceListChanged` | `ThreadPool` | Standard managed code rules |
| `Play()`, `Pause()`, `Stop()`, `Seek()`, `Load()` | Caller's thread | Do not call concurrently on the same instance |

## Lifecycle

```
AudioEngine
 ├─ AudioPlayer  → owns Safe.AudioOutputStream
 ├─ AudioRecorder → owns Safe.AudioInputStream
 └─ MultiTrackSession → owns Safe.MixerHandle
      └─ AudioTrack → non-owning Safe.TrackHandle
           └─ Effect → non-owning Safe.EffectHandle
```

Disposing `AudioEngine` cascades down the entire tree. Dispose child objects first if you need finer control over shutdown order.
