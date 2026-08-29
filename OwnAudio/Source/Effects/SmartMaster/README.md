# SmartMaster

An intelligent "one-knob" mastering effect for OwnAudioSharp, laid out like a
dbx DriveRack style PA processor: everything that shapes the program runs before
the crossover, everything that protects the drivers runs after it, per band.
It sits behind a single [`IEffectProcessor`](../../Interfaces/IEffectProcessor.cs)
and comes with a microphone-based **room-calibration measurement** system and a
JSON preset library.

The real-time DSP lives in Rust
([`ownaudio-core/src/effects/smartmaster/`](../../../../OwnAudioEngineRust/ownaudio-core/src/effects/smartmaster/));
the managed side here is the parameter model, the preset owner and the
measurement (cold path), plus a managed mirror of the chain for non-native mode.

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
| [Biquad.cs](Components/Biquad.cs) | RBJ coefficient builders (HP/LP/BP/peaking/shelf) + denormal-flushed TDF-II state. |
| [SubsonicFilter.cs](Components/SubsonicFilter.cs) | 4th-order Butterworth subsonic high-pass, 24 dB/oct. |
| [ParametricEqStage.cs](Components/ParametricEqStage.cs) | 8-band sweepable input PEQ (bell / low shelf / high shelf). |
| [CrossoverFilter.cs](Components/CrossoverFilter.cs) | Linkwitz-Riley 4th-order (2× cascaded Butterworth) low/high split. |
| [PhaseAlignment.cs](Components/PhaseAlignment.cs) | Per-channel (main L / main R / Sub) time delay + phase inversion. |
| [SubharmonicSynth.cs](Components/SubharmonicSynth.cs) | Two-band octave divider (48–72→24–36 Hz, 72–112→36–56 Hz), added in parallel. |
| [FIRFilter.cs](Components/FIRFilter.cs) | Generic linear-phase windowed-sinc FIR. Unused by the chain now, kept as a utility. |
| [NoiseGenerator.cs](Components/NoiseGenerator.cs) | White / pink (Voss-McCartney) / low-frequency test noise. |
| [SmartMasterSpectrumAnalyzer.cs](Components/SmartMasterSpectrumAnalyzer.cs) | FFT-based 30-band ISO spectrum + RMS for calibration. |

---

## Signal chain

```
in ─► [Subsonic HPF] ─► Graphic EQ (30-band) ─► [Parametric EQ] ─► [Subharmonic] ─► [Compressor]
                                                                                        │
    ┌───────────────────────────────────────────────────────────────────────────────────┘
    ▼  (only when the crossover section is engaged)
  split ─┬─ main L/R (highs) ─► trim ─► delay/polarity ─► main limiter ─┐
         └─ mono sub (lows)  ─► trim ─► delay/polarity ─► sub limiter  ─┴─► sum
                                                                            │
                                                     output limiter ◄───────┘
```

Bracketed stages are skipped when disabled. The crossover section runs when
`CrossoverEnabled` is set, or when any alignment delay / polarity flip needs it;
otherwise the signal goes straight to the output limiter — the common case.

The two band limiters are driver protection, not bus limiting: at a 0 dBFS
threshold they sit open and only bite when a preset pulls them down.

### Hot-path guarantees

- `SmartMasterAudioChain.Process` **must not allocate**. All scratch buffers are
  pre-allocated in `Configure()` (sized for 2048 frames, grown only on the rare
  oversize block).
- No `Parallel.Invoke` on the audio thread — it allocated a closure per block and
  handed the work to the thread pool, which is exactly what a real-time path must
  not do.
- No logging from `Process` either; NaN/Inf input is zeroed silently and counted
  in `SanitizedSampleCount` for whoever wants to poll it.
- Every IIR state is denormal-flushed, so a decaying tail can't park in the
  subnormal range and cost a fortune on x86.
- Only the limiters add latency (their lookahead). `LatencySamples` reports the
  output limiter plus, when the crossover runs, the main-band limiter in series.

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

var cfg = sm.GetConfiguration();           // the live SmartMasterConfig
cfg.GraphicEQGains[5] = 2.5f;              // 63 Hz
sm.ApplyConfiguration();                   // rebuild the chain from it
sm.ApplyConfiguration(otherConfig);        // or hand it one built elsewhere
```

Editing the object `GetConfiguration()` returns does **not** change the sound on
its own — the managed chain keeps running the components it was built with until
`ApplyConfiguration` (or a preset load) swaps in a new one. In rust-native mode
the control tick mirrors the config onto the native effect every ~15 ms, so edits
are audible immediately and the call is only a no-op safety net.

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
3. **Analyzing spectrum** — play 4 s pink noise, record 3 s, fold the interleaved
   capture to mono, FFT to 30 bands. The same noise is run through the same
   analyzer as a reference, and the per-band deviation is taken against that,
   gain-aligned on 200 Hz – 2 kHz. Measuring against the analyzer's own answer
   rather than a flat line takes the window's low-frequency smearing out of the
   result, so a perfect system reads 0 dB at every band, at any playback volume.
4. **Checking low end** — the verdict comes out of the same band data, in three
   tiers, so nothing here depends on how loud the system was playing:
   - capture below −60 dBFS ⇒ no verdict (a dead mic would otherwise compare
     noise floor to noise floor and read as a healthy system),
   - 40–80 Hz more than 12 dB under the midrange ⇒ weak low end, left to the EQ,
   - 20–31.5 Hz more than 12 dB under a healthy 40–80 Hz ⇒ subharmonic synth,
     with `Mix` ramped 0.08–0.18 by the size of the deficit.

   The synth is deliberately **not** offered for the weak-low case: it is an
   octave divider, so on a box that is already down at 60 Hz it only writes
   energy further down, where even less comes out.
5. **Calculating correction** — build a fresh `SmartMasterConfig`. The deviation
   is 3-band smoothed first (a single mic position is full of narrow interference
   dips that say nothing about the system), then aimed at a house target curve
   (warm at the bottom, rolled off on top) and applied at 65 % — a room is not a
   minimum-phase system, so a 1:1 correction mostly makes it sound worse. Boosts
   are capped short (bass bands 0–4 ≤ +2 dB, others ≤ +6 dB, all ≥ −12 dB)
   because filling a null costs headroom and rarely fills it. Phase-alignment
   delays / polarity come from the channel results, the subharmonic synth from
   the low-end verdict above.
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
| `SubsonicEnabled` / `SubsonicFrequency` | 24 dB/oct subsonic high-pass on the input. |
| `GraphicEQGains[30]` | 30-band graphic EQ gains in dB (0 = flat), ISO centres 20 Hz – 16 kHz, constant-Q. |
| `ParametricEQ[8]` | Input PEQ: `Shape` (bell / low shelf / high shelf), `Frequency`, `Q`, `GainDb`. |
| `SubharmonicEnabled` / `SubharmonicMix` / `SubharmonicLowLevel` / `SubharmonicHighLevel` | Octave-divider sub synth: master level plus the 24–36 Hz and 36–56 Hz band levels. `Mix` is a parallel level, not a dry/wet crossfade. |
| `CompressorEnabled` / `Threshold` / `Ratio` / `Attack` / `Release` / `Knee` | Compressor. `Threshold` is linear 0–1; `Knee` is the OverEasy width in dB. |
| `CrossoverEnabled` / `CrossoverFrequency` | Crossover section switch and split point in Hz. |
| `OutputGains[3]` | Per-band trim in dB: main L, main R, sub. |
| `TimeDelays[3]` / `PhaseInvert[3]` | Per-band alignment (main L / main R / sub). |
| `MainLimiterThreshold` / `SubLimiterThreshold` | Driver-protection limiters in dBFS; 0 leaves them open. |
| `LimiterThreshold` / `LimiterCeiling` / `LimiterRelease` | Output limiter. |
| `MicInputGain` | Measurement/monitor mic gain (1.0 = unity). |
| `LastMeasurement` | The `MeasurementResults` that produced this config, if any. |

Band and channel counts come from `SmartMasterConfig.EqBands` (30),
`AlignChannels` (3) and `ParametricBands` (8); every array property fits what it
is given to that length, so an older 31-band preset or a hand-trimmed JSON can't
silently disable a stage.

Serialization goes through `SmartMasterRustNextJsonContext` (System.Text.Json
source generator) so presets work under Native AOT / trimming.

### Built-in speaker presets ([SmartMasterPresetFactory.cs](SmartMasterPresetFactory.cs))

`SpeakerType`: `Default` (transparent passthrough), `HiFi`, `Headphone`,
`Studio`, `Club`, `Concert`. Each sets a voicing curve on the graphic EQ plus
subsonic / subharmonic / compressor / limiter values; `Club` and `Concert` also
engage the crossover section with its own trims and driver limiters.

The parametric EQ is left **flat in every preset** on purpose — the graphic EQ
carries the voicing and those eight bands are the user's room tool, the same
split a DriveRack works with. Alignment delays are likewise left at zero: without
a measurement they only comb the crossover region.

---

## Usage checklist

1. `new SmartMasterEffect()` → `Initialize(audioConfig)` (creates the chain once;
   later `Initialize` calls preserve state).
2. Optionally `LoadSpeakerPreset(...)` / `Load(...)`, or run
   `StartMeasurementAsync()` then load the `measured` preset. Hand edits go
   through `ApplyConfiguration()`.
3. Add to your mixer/effect chain; audio flows through `Process`.
4. On playback stop call `OnPlaybackStopped()` (or `Reset()`) to clear IIR state.
5. `Dispose()` when done (also disposes the mic monitor).

---

## Development Tools

This project is developed with the following tools:

| | |
|:--:|:--|
| ![Claude Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/docs/assets/tools/claude.svg) | **Anthropic** — Claude Code |
| ![Visual Studio Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/docs/assets/tools/vscode.svg) | **Microsoft** — Visual Studio Code |
| ![Visual Studio 2022](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/docs/assets/tools/visualstudio.svg) | **Microsoft** — Visual Studio 2022 |
| ![Rider](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/docs/assets/tools/rider.svg) | **JetBrains** — Rider |
