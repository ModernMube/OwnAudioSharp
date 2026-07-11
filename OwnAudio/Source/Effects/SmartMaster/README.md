# SmartMaster

An intelligent "one-knob" mastering effect for OwnAudioSharp. SmartMaster wraps
a full mastering chain (graphic EQ → subharmonic synth → compressor → crossover
→ phase alignment → brick-wall limiter) behind a single
[`IEffectProcessor`](../../Interfaces/IEffectProcessor.cs), plus a microphone-based
**room-calibration measurement** system and a JSON preset library.

Namespace root: `OwnaudioNET.Effects.SmartMaster`
Public entry point: `SmartMasterEffect`.

---

## File layout

| File | Responsibility |
| --- | --- |
| [SmartMasterEffect.cs](SmartMasterEffect.cs) | Public `IEffectProcessor` facade; coordinates chain, presets, measurement, mic monitor. |
| [SmartMasterAudioChain.cs](SmartMasterAudioChain.cs) | The internal DSP chain; zero-allocation hot path with SIMD + optional parallelism. |
| [SmartMasterConfig.cs](SmartMasterConfig.cs) | Serializable configuration + `MeasurementResults`. |
| [SmartMasterPresetManager.cs](SmartMasterPresetManager.cs) | Load/save presets, create factory presets on disk. |
| [SmartMasterPresetFactory.cs](SmartMasterPresetFactory.cs) | `SpeakerType` enum + built-in speaker preset definitions. |
| [SmartMasterMeasurementService.cs](SmartMasterMeasurementService.cs) | Automatic room/speaker calibration via test-noise playback + mic recording. |
| [SmartMasterMicMonitor.cs](SmartMasterMicMonitor.cs) | Background mic-level meter for the UI. |
| [SmartMasterStatus.cs](SmartMasterStatus.cs) | `MeasurementStatus` enum + `MeasurementStatusInfo`. |
| [SmartMasterJsonContext.cs](SmartMasterJsonContext.cs) | Source-generated JSON context (AOT/trim-safe). |
| [Components/](Components/) | Reusable DSP building blocks (see below). |

### Components

| Component | Role |
| --- | --- |
| [CrossoverFilter.cs](Components/CrossoverFilter.cs) | Linkwitz-Riley 4th-order (2× cascaded Butterworth) low/high split. |
| [PhaseAlignment.cs](Components/PhaseAlignment.cs) | Per-channel (L/R/Sub) time delay + phase inversion. |
| [SubharmonicSynth.cs](Components/SubharmonicSynth.cs) | FIR bandpass (40–120 Hz) + waveshaper for synthesized sub-bass. |
| [FIRFilter.cs](Components/FIRFilter.cs) | Generic linear-phase windowed-sinc FIR (per-channel delay lines). |
| [NoiseGenerator.cs](Components/NoiseGenerator.cs) | White / pink (Voss-McCartney) / low-frequency test noise. |
| [SmartMasterSpectrumAnalyzer.cs](Components/SmartMasterSpectrumAnalyzer.cs) | FFT-based 31-band ISO spectrum + RMS for calibration. |

---

## Signal chain

`SmartMasterAudioChain.Process` runs the buffer through, in order:

```
input ─► Graphic EQ (31-band) ─► [Subharmonic Synth] ─► [Compressor] ─► Crossover chain ─► output
                                                                              │
                          ┌───────────────────────────────────────────────────┘
                          ▼   (only when phase alignment is needed)
              split L/R (highs) + summed mono Sub (lows)
                          ▼
              PhaseAlignment per L / R / Sub  ─►  recombine (L+Sub, R+Sub)  ─►  Limiter
```

Bracketed stages are skipped when disabled in the config. When no time delay or
phase inversion is configured, the crossover/phase branch is bypassed entirely
and the signal goes straight to the limiter — the common case.

### Hot-path guarantees

- `SmartMasterAudioChain.Process` **must not allocate** — it runs on the audio
  thread. All scratch buffers are pre-allocated in `Configure()` (initially
  sized for 2048 frames, grown only on the rare oversize block).
- Deinterleave / mono-sum / interleave use `System.Numerics.Vector<float>` SIMD.
- For blocks ≥ `PARALLEL_THRESHOLD` (512 frames) the crossover and phase stages
  run across channels via `Parallel.Invoke`.
- The input is sanitized for NaN/Inf in `SmartMasterEffect.Process` before the
  chain sees it, and the crossover self-heals corrupted IIR state.
- Only the limiter adds latency (its lookahead). Everything else is
  zero-latency; `LatencySamples` surfaces the limiter's value for the mixer's
  delay compensation.

### Reconfiguration is atomic

`Configure()` builds a brand-new set of components and swaps them in at the end
(field-by-field). `Load`, `LoadSpeakerPreset`, `ResetToDefaults` and measurement
completion all build a **new** `SmartMasterAudioChain` under `_configLock` and
dispose the old one, so the audio thread never observes a half-updated chain.

---

## Public API (`SmartMasterEffect`)

Implements `IEffectProcessor` (`Initialize`, `Process`, `Reset`, `Enabled`,
`Mix`, `LatencySamples`, `Dispose`). Add it to a mixer/effect chain like any
other effect. Additional surface:

### Presets

```csharp
var sm = new SmartMasterEffect();
sm.Initialize(audioConfig);

sm.LoadSpeakerPreset(SpeakerType.Club);   // built-in factory preset
sm.Save("my-room");                        // persist current config
sm.Load("my-room");                        // restore it later
sm.ResetToDefaults();                      // flat/transparent + save as "default"

var cfg = sm.GetConfiguration();           // inspect current SmartMasterConfig
```

Presets are JSON files under
`%UserProfile%/.ownaudio/smartmasterpresets/*.smartmaster.json`. Factory presets
for every `SpeakerType` are written there on first `Initialize` if missing.

### Measurement (room calibration)

```csharp
sm.StartMicMonitoring();                    // live mic level for a UI meter
float db = sm.GetLastMicLevel();

await sm.StartMeasurementAsync();           // full calibration sweep
var status = sm.GetMeasurementStatus();     // poll progress / step / warnings
sm.CancelMeasurement();                     // abort in-flight
```

`StartMeasurementAsync` requires the OwnAudio engine to have **input enabled**
(`audioConfig.EnableInput = true`) and an available input device. It disables
processing during the sweep and **does not auto-apply** the result — the measured
config is saved to a `measured` preset for the user to load explicitly.

---

## Measurement pipeline ([SmartMasterMeasurementService.cs](SmartMasterMeasurementService.cs))

Reported through `MeasurementStatusInfo` (status enum + 0–1 progress + step text
+ warnings):

1. **Initializing** — verify input is enabled and a device exists.
2. **Right / Left channel** — play 2 s white noise on one channel, record via
   `InputSource`, measure RMS. Below −60 dBFS ⇒ channel error (aborts).
3. **Subwoofer** — play 2 s low-frequency noise on all channels; below −40 dBFS
   flags a weak sub (→ recommends the subharmonic synth).
4. **Analyzing spectrum** — play 4 s pink noise, record 3 s, FFT to 31 bands,
   compare against a flat reference to get per-band deviation in dB.
5. **Calculating correction** — build a fresh `SmartMasterConfig`:
   graphic-EQ gains from the inverse spectrum deviation (clamped: bass bands
   0–4 boost ≤ +3 dB, others ≤ +12 dB, all ≥ −12 dB); phase-alignment delays /
   polarity from the channel results; enable subharmonic synth if the sub was
   weak.
6. Save to `measured.smartmaster.json` and report **Completed** (with any
   warnings). The active chain is reset to defaults; the measured preset is not
   applied automatically.

Playback uses a "smart pumping" loop that watches the engine's output buffer
occupancy and only sends when there's room; each test tone fades out to avoid
clicks.

---

## Configuration model ([SmartMasterConfig.cs](SmartMasterConfig.cs))

| Field | Meaning |
| --- | --- |
| `GraphicEQGains[31]` | 31-band graphic EQ gains in dB (0 = flat). |
| `SubharmonicEnabled` / `SubharmonicMix` / `SubharmonicFreqRange` | Sub-bass synth toggle, wet mix, max frequency. |
| `CompressorEnabled` / `Threshold` / `Ratio` / `Attack` / `Release` | Compressor stage. |
| `CrossoverFrequency` | Low/high split point in Hz. |
| `TimeDelays[3]` / `PhaseInvert[3]` | Per-channel (L/R/Sub) alignment. |
| `ParametricEQGains[3][10]` | Per-branch parametric EQ gains (reserved). |
| `LimiterThreshold` / `LimiterCeiling` / `LimiterRelease` | Output limiter. |
| `MicInputGain` | Measurement/monitor mic gain (1.0 = unity). |
| `LastMeasurement` | The `MeasurementResults` that produced this config, if any. |

Serialization goes through `SmartMasterRustNextJsonContext` (System.Text.Json
source generator) so presets work under Native AOT / trimming.

### Built-in speaker presets ([SmartMasterPresetFactory.cs](SmartMasterPresetFactory.cs))

`SpeakerType`: `Default` (transparent passthrough), `HiFi`, `Headphone`,
`Studio`, `Club`, `Concert`. Each sets a tuned EQ curve, subharmonic/compressor/
limiter parameters and (for Club/Concert) a small sub delay for driver
alignment.

---

## Usage checklist

1. `new SmartMasterEffect()` → `Initialize(audioConfig)` (creates the chain once;
   later `Initialize` calls preserve state).
2. Optionally `LoadSpeakerPreset(...)` / `Load(...)`, or run
   `StartMeasurementAsync()` then load the `measured` preset.
3. Add to your mixer/effect chain; audio flows through `Process`.
4. On playback stop call `OnPlaybackStopped()` (or `Reset()`) to clear IIR state.
5. `Dispose()` when done (also disposes the mic monitor).
