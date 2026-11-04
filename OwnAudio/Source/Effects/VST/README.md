# VST

VST3 and AudioUnit plugin hosting for OwnAudioSharp. This module lets you load a
third-party VST3 or AudioUnit effect (or instrument), drive its editor, read/write
its parameters, and insert it into the OwnAudio effect chain as a standard
[`IEffectProcessor`](../../Interfaces/IEffectProcessor.cs).

Namespace root: `OwnaudioNET.Effects.VST`

The actual native bridge is the **`OwnAudioVst`** package (2.1.0): the
`OwnVST3Host` assembly (`ThreadedVst3Wrapper`, `OwnVst3Wrapper`,
`VstEditorController`, `PluginScanner`, …) over a JUCE-free native library built
on the VST3 SDK and Apple's `AUAudioUnit`. This folder is the managed-side
integration layer over that bridge.

## Platforms

| Platform | Formats | Editor |
| --- | --- | --- |
| Windows, Linux | VST3 | own window (`OpenEditor`) |
| macOS | VST3, AudioUnit (v2 and v3) | own window (`OpenEditor`) |
| iOS / iPadOS 15+ (Mobile package) | AudioUnit: Apple's built-in units and third-party AUv3 extensions, which run out of process | none: `OpenEditor` throws `InvalidOperationException` |
| Android | — | — |

On iOS the `OwnAudioVst` package brings `ownvst3.xcframework` with it, and a
`buildTransitive` target embeds it into the `net10.0-ios` app. OwnAudioVst 2.1.0
can also attach an AudioUnit editor to a view you own (`AttachEditorToView`),
but `VST3PluginHost` does not expose that yet.

---

## File layout

| File | Responsibility |
| --- | --- |
| [VST3PluginHost.cs](VST3PluginHost.cs) | Owns a plugin's lifecycle: load, audio-init, editor, parameters, state, disposal. Also static plugin discovery. |
| [VST3EffectProcessor.cs](VST3EffectProcessor.cs) | `IEffectProcessor` adapter — runs the plugin on the audio thread with dry/wet mix and transport control. |
| [VST3BufferConverter.cs](VST3BufferConverter.cs) | Zero-allocation interleaved ↔ planar buffer conversion (VST3 wants planar). |
| [VST3ParameterInfo.cs](VST3ParameterInfo.cs) | Immutable parameter metadata (id, name, value, range, default). |
| [VST3PluginInfo.cs](VST3PluginInfo.cs) | Plugin discovery metadata (path, name, vendor, version, flags, param count). |
| [AudioPluginBrowser.cs](AudioPluginBrowser.cs) | Scanner across both formats (`AudioPluginInfo`, `AudioPluginFormat`), with a persistable scan cache. |

---

## Threading model

This is the crux of the module — get it wrong and you crash the audio thread or
the UI. Three logical threads, all mediated by `ThreadedVst3Wrapper`:

| Thread | What runs there |
| --- | --- |
| **Plugin thread** | *All* native VST operations: load, audio-init, parameter reads, state get/set, editor size. Never blocks the UI. Async methods (`…Async`) marshal here. |
| **Audio thread** | Rust calls `VST3Plugin_ProcessAudio` through the native VST bridge. Both a mixer chain and a direct `VST3EffectProcessor.Process` go through that bridge — the mixer's twin lives on the audio thread, `Process` gets its own instance on the caller's. |
| **UI thread** | `SetParameter` / `SetTempo` / `SetTransportPlaying` / `ResetPosition` — lock-free enqueue, return immediately. Editor create/close must run here (VST3 / macOS Cocoa requirement). |

Reads of `IsReady`, `Enabled`, `Mix` are `volatile`-safe from any thread.

`Enabled` and `Mix` are the only two things the managed side pushes to the bridge;
the plugin's own parameters go straight to the plugin. Both are applied inside the
bridge against a dry path delayed by the plugin's latency, so a bypass flip does not
shift the output and a partial mix does not comb the dry against the wet. Bypass
still drives the plugin every block, so it never goes cold and resumes without a
stall. This replaced the host-level bypass the mixer used to toggle, which existed
only to keep the two paths time-aligned.

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
(`Dispose` sleeps ~150 ms) before the plugin is freed. The editor is torn down on
the main thread, and the gap lets the run loop finish with it first. The gap dates
from the JUCE backend, where queued `CFRunLoop` timer callbacks crashed with
`SIGSEGV` without it, and it is kept as a safety margin. Prefer `DisposeAsync`
when you have an async context.

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
`NativeProcessAudioPointer`) let the Rust-native mixer call the plugin's
`VST3Plugin_ProcessAudio` export directly on the Rust audio thread, bypassing the
managed `Process` path. In that mode `Enabled` and `Mix` are applied in the
bridge against the latency-delayed dry path, as described under
[Threading model](#threading-model), so a bypass flip causes no time jump.

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

These only walk VST3 folders. AudioUnits live in Apple's component registry and
have no path to find, so for both formats (and for anything at all on iOS) use
`AudioPluginBrowser`:

```csharp
if (!AudioPluginBrowser.IsSupported) return;      // no native plugin host here

var plugins = await AudioPluginBrowser.ScanAsync(AudioPluginFormat.All);  // quick scan
var host    = await VST3PluginHost.CreateAsync(plugins[0]);               // VST3 or AU

string cache = AudioPluginBrowser.GetCacheXml();  // save it; RestoreCache(cache) skips the next scan
```

A quick scan leaves the channel counts at -1; `ResolveAsync(info)` loads one
plugin to fill them in, `deepScan: true` does it for all of them. The loaded
host reports what it actually got through `Format`, `IsAudioUnit` and
`Identifier`, and the `Identifier` loads the same plugin again.

---

## Gotchas

- Never call `GetProcessor()` before awaiting `InitializeAudioAsync` — it throws.
- Dispose order is **processor → host**, and only after the audio engine stops.
- Use `DisposeAsync` on macOS; it waits for the editor teardown without blocking.
- On iOS there is no editor window: check the platform before calling `OpenEditor`.
- `SetParameter` is not immediate — it lands ~1 audio block later. For instant,
  synchronous updates (state restore) use `SetParametersAsync` /
  `ApplyParametersAsync`.

---

## Development Tools

This project is developed with the following tools:

| | |
|:--:|:--|
| ![Claude Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/main/docs/assets/tools/claude.svg) | **Anthropic** — Claude Code |
| ![Visual Studio Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/main/docs/assets/tools/vscode.svg) | **Microsoft** — Visual Studio Code |
| ![Visual Studio 2022](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/main/docs/assets/tools/visualstudio.svg) | **Microsoft** — Visual Studio 2022 |
| ![Rider](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/main/docs/assets/tools/rider.svg) | **JetBrains** — Rider |
