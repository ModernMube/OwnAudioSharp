# VST

VST3 plugin hosting for OwnAudioSharp. This module lets you load a third-party
VST3 effect (or instrument), drive its editor, read/write its parameters, and
insert it into the OwnAudio effect chain as a standard
[`IEffectProcessor`](../../Interfaces/IEffectProcessor.cs).

Namespace root: `OwnaudioNET.Effects.VST`

The actual native bridge lives in the **`OwnVST3Host`** / **`OwnVST3Host.NativeWindow`**
assemblies (`ThreadedVst3Wrapper`, `OwnVst3Wrapper`, `VstEditorController`, …).
This folder is the managed-side integration layer over that bridge.

---

## File layout

| File | Responsibility |
| --- | --- |
| [VST3PluginHost.cs](VST3PluginHost.cs) | Owns a plugin's lifecycle: load, audio-init, editor, parameters, state, disposal. Also static plugin discovery. |
| [VST3EffectProcessor.cs](VST3EffectProcessor.cs) | `IEffectProcessor` adapter — runs the plugin on the audio thread with dry/wet mix and transport control. |
| [VST3BufferConverter.cs](VST3BufferConverter.cs) | Zero-allocation interleaved ↔ planar buffer conversion (VST3 wants planar). |
| [VST3ParameterInfo.cs](VST3ParameterInfo.cs) | Immutable parameter metadata (id, name, value, range, default). |
| [VST3PluginInfo.cs](VST3PluginInfo.cs) | Plugin discovery metadata (path, name, vendor, version, flags, param count). |

---

## Threading model

This is the crux of the module — get it wrong and you crash the audio thread or
the UI. Three logical threads, all mediated by `ThreadedVst3Wrapper`:

| Thread | What runs there |
| --- | --- |
| **Plugin thread** | *All* native VST operations: load, audio-init, parameter reads, state get/set, editor size. Never blocks the UI. Async methods (`…Async`) marshal here. |
| **Audio thread** | `VST3EffectProcessor.Process`. The wrapper drains a **lock-free SPSC queue** of pending parameter/tempo/transport changes before each block — no locks, no allocation. |
| **UI thread** | `SetParameter` / `SetTempo` / `SetTransportPlaying` / `ResetPosition` — lock-free enqueue, return immediately. Editor create/close must run here (VST3 / macOS Cocoa requirement). |

Reads of `IsReady`, `Enabled`, `Mix` are `volatile`-safe from any thread.

`SetParameter` (audio-thread SPSC, ~1 block latency) vs. `ApplyParametersAsync` /
`SetParametersAsync` (synchronous on the plugin thread, immediate) — use the
former for realtime automation, the latter for non-realtime work like project
state restoration.

---

## Lifecycle & ownership

**Ownership rule:** `VST3PluginHost` owns the `ThreadedVst3Wrapper`.
`VST3EffectProcessor` *shares* it but does **not** own it. Always dispose the
processor first, then the host — and only after the audio engine is stopped.

Required sequence:

```csharp
// 1. Load (async — never blocks the UI thread)
var host = await VST3PluginHost.CreateAsync(pluginPath);

// 2. Audio-init (sets State = Ready)
await host.InitializeAudioAsync(sampleRate: 48000, maxBlockSize: 512);

// 3. Get the processor — only valid once host.IsReady
var proc = host.GetProcessor();

// 4. Insert into the effect chain (Initialize() validates IsReady)
mixer.AddMasterEffect(proc);

// ... play, automate, open editor ...

// 5. Clean shutdown
mixer.Stop();          // triggers proc.Reset() → transport stopped
proc.Dispose();        // releases buffers, stops transport (NOT the wrapper)
await host.DisposeAsync();   // preferred over Dispose() on macOS
```

`GetProcessor()` and `VST3EffectProcessor.Initialize()` both throw
`InvalidOperationException` if the plugin isn't audio-initialized yet — so the
Ready gate can't be skipped.

### macOS disposal note

`DisposeAsync` (and `Dispose`) close the editor, then **yield ~200 ms** on macOS
so queued JUCE `CFRunLoop` timer callbacks can drain before the native library
unloads. Without that gap those callbacks touch freed memory and `SIGSEGV`.
Prefer `DisposeAsync` when you have an async context.

---

## Audio processing ([VST3EffectProcessor.cs](VST3EffectProcessor.cs))

`Process(Span<float> buffer, int frameCount)` on the audio thread:

1. Fast pass-through when disposed / disabled / not ready / buffers unallocated.
2. If `Mix < 0.999`, copy the dry signal aside.
3. `VST3BufferConverter.InterleavedToPlanar` → `ThreadedVst3Wrapper.ProcessAudio`
   (which drains the SPSC queue first) → `PlanarToInterleaved`.
4. If dry-mix is active, blend `dry*(1-Mix) + wet*Mix` in place.

Buffers (planar in/out + dry) are pre-allocated in `Initialize` and only re-grown
if a block exceeds the allocated size. Channel count follows the plugin's
`ActualOutputChannels` when available. The native process call is wrapped in a
`try/catch {}` so a misbehaving plugin can't tear down the audio thread.

`Reset()` stops the transport, rewinds its position, and clears buffers — it does
**not** re-init the plugin (that would be blocking/expensive). It's called
automatically by `SourceWithEffects.Stop()` / `AudioMixer.Stop()`.

### Native (Rust) hosting fast path

Several `internal` members (`CanHostNatively`, `NativePluginHandle`,
`NativeProcessAudioPointer`, `SetNativeBypass`) let the Rust-native mixer call
the plugin's `VST3Plugin_ProcessAudio` export directly on the Rust audio thread,
bypassing the managed `Process` path. In that mode `Enabled` is honored via
JUCE's `processBlockBypassed` (latency-compensated, no time jump) rather than a
host-side dry/wet switch.

### Latency

`LatencySamples` reads the plugin's reported latency from the native host. The
mixer uses it (via `ApplyPluginDelayCompensation`) to delay-compensate other
tracks so the plugin's output stays sample-accurately aligned.

---

## Buffer conversion ([VST3BufferConverter.cs](VST3BufferConverter.cs))

VST3 processes **planar** audio (`float[channel][frame]`), the OwnAudio chain
uses **interleaved** (`L0 R0 L1 R1 …`). This static helper converts both ways
with fast dedicated paths for mono and stereo and a generic path for N channels.
Zero-allocation and `AggressiveInlining` — it's on the hot path every block.

---

## Parameters, editor & state

Via `VST3PluginHost` (plugin thread, async) or the processor (see threading notes):

- `GetParametersAsync()` / `GetParameters()` → `VST3ParameterInfo[]`
  (id, name, current/min/max/default).
- `GetParameterAsync(id)` / `SetParameter(id, value)` /
  `SetParametersAsync(dict)`.
- `GetStateAsync()` / `SetStateAsync(byte[])` — full processor state blob; more
  reliable than per-parameter restore for complete recall.
- `OpenEditor` / `OpenEditorAsync` / `CloseEditor`, `GetEditorSizeAsync`,
  `HasEditor`, `IsEditorOpen`.

---

## Plugin discovery

Static helpers on `VST3PluginHost`:

| Method | Cost | Use |
| --- | --- | --- |
| `FindPlugins(...)` | filesystem only | Get candidate `.vst3` paths. |
| `ScanPluginsQuick(...)` | filesystem only | Build the initial list fast — name from bundle/file (macOS reads `CFBundleName` from `Info.plist`); other metadata left blank. |
| `ScanPlugins(...)` | loads each plugin | Full metadata (vendor, version, effect/instrument, param count) — slow with many plugins. |

Prefer `ScanPluginsQuick` on startup; only call `ScanPlugins` when you need the
full details.

---

## Gotchas

- Never call `GetProcessor()` before awaiting `InitializeAudioAsync` — it throws.
- Dispose order is **processor → host**, and only after the audio engine stops.
- Use `DisposeAsync` on macOS to avoid the JUCE timer-callback crash.
- `SetParameter` is not immediate — it lands ~1 audio block later. For instant,
  synchronous updates (state restore) use `SetParametersAsync` /
  `ApplyParametersAsync`.

---

## Development Tools

This project is developed with the following tools:

| | |
|:--:|:--|
| ![Claude Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/claude.svg) | **Anthropic** — Claude Code |
| ![Visual Studio Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/vscode.svg) | **Microsoft** — Visual Studio Code |
| ![Visual Studio 2022](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/visualstudio.svg) | **Microsoft** — Visual Studio 2022 |
| ![Rider](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/rider.svg) | **JetBrains** — Rider |
