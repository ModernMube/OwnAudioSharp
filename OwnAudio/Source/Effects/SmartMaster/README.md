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
measurement (cold path). `Process()` talks to a standalone native SmartMaster
instance — the same engine the mixer twin uses. There is no non-native
production path.

Namespace root: `OwnaudioNET.Effects.SmartMaster`
Public entry point: `SmartMasterEffect`.

---

## File layout

| File | Responsibility |
| --- | --- |
| [SmartMasterEffect.cs](SmartMasterEffect.cs) | Public `IEffectProcessor` facade; coordinates the native engine, presets, measurement, mic monitor. |
| [SmartMasterConfig.cs](SmartMasterConfig.cs) | Serializable configuration + `MeasurementResults`. |
| [SmartMasterPresetManager.cs](SmartMasterPresetManager.cs) | Load/save presets, create factory presets on disk. |
| [SmartMasterPresetFactory.cs](SmartMasterPresetFactory.cs) | `SpeakerType` enum + built-in speaker preset definitions. |
| [SmartMasterMeasurementService.cs](SmartMasterMeasurementService.cs) | Automatic room/speaker calibration: pink noise through the mixer, mic back through the monitor. |
| [SmartMasterMicMonitor.cs](SmartMasterMicMonitor.cs) | The measurement mic: a muted `InputSource` on the mixer read through an effect tap; level meter + band averaging. |
| [SmartMasterStatus.cs](SmartMasterStatus.cs) | `MeasurementStatus` enum + `MeasurementStatusInfo`. |
| [SmartMasterJsonContext.cs](SmartMasterJsonContext.cs) | Source-generated JSON context (AOT/trim-safe). |
| [Components/](Components/) | DSP building blocks the measurement uses, plus the public filter types (see below). |

### Components

The mastering chain itself is rust now, so nothing here sits on an audio path any
more. The measurement uses the bottom two; the rest are public standalone filter
types, kept because they are on the frozen API surface and are usable on their own.

| Component | Role | Used by |
| --- | --- | --- |
| [PinkNoise.cs](Components/PinkNoise.cs) | Streaming Voss-McCartney pink noise with a per-channel mask. | measurement |
| [SmartMasterSpectrumAnalyzer.cs](Components/SmartMasterSpectrumAnalyzer.cs) | FFT-based 30-band ISO third-octave levels, streamed or from a buffer. | measurement, mic monitor |
| [Biquad.cs](Components/Biquad.cs) | RBJ coefficient builders (HP/LP/BP/peaking/shelf) + denormal-flushed TDF-II state. | the filters below |
| [SubsonicFilter.cs](Components/SubsonicFilter.cs) | 4th-order Butterworth subsonic high-pass, 24 dB/oct. | callers only |
| [ParametricEqStage.cs](Components/ParametricEqStage.cs) | 8-band sweepable input PEQ (bell / low shelf / high shelf). | callers only |
| [CrossoverFilter.cs](Components/CrossoverFilter.cs) | Linkwitz-Riley 4th-order (2× cascaded Butterworth) low/high split. | callers only |
| [PhaseAlignment.cs](Components/PhaseAlignment.cs) | Per-channel (main L / main R / Sub) time delay + phase inversion. | callers only |
| [SubharmonicSynth.cs](Components/SubharmonicSynth.cs) | Two-band octave divider (48–72→24–36 Hz, 72–112→36–56 Hz), added in parallel. | callers only |
| [FIRFilter.cs](Components/FIRFilter.cs) | Generic linear-phase windowed-sinc FIR. | callers only |

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

The DSP is in rust, so the guarantees live there. On the managed side:

- `Process()` scans the block for NaN/Inf, zeroes what it finds and counts it in
  `SanitizedSampleCount` — no logging, nothing else per block.
- A bypassed effect returns before that scan.
- Only the limiters add latency (their lookahead). `LatencySamples` comes from the
  native instance.

### Reconfiguration

`Load`, `LoadSpeakerPreset`, `ResetToDefaults`, `ApplyConfiguration` and a finished
measurement all store the config under `_configLock` and push it onto the native
effect. `Process()` takes the same lock, so a block never straddles a half-applied
config.

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
its own — call `ApplyConfiguration` (or load a preset) to push it onto the native
effect. On a mixer the control tick mirrors it every ~15 ms anyway, so there the
call is only a safety net.

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

Both need the effect **on a mixer's master bus** (`mixer.AddMasterEffect(sm)`) —
the noise plays and the mic is captured through that mixer, since in rust-native
mode the mixer's session owns the device and nothing sent past it reaches the
speakers. `StartMeasurementAsync` also needs the **mixer running** and the engine
started with **input enabled** (`audioConfig.EnableInput = true`). Stop the
program material first, it would be measured too. The mic source is muted, so
nothing is fed back into the speakers.

The effect is bypassed during the sweep, and the result is **not auto-applied** —
the measured config is saved to a `measured` preset for the user to load
explicitly. The live settings are left as they were, whether the measurement
succeeds or fails.

---

## Measurement pipeline ([SmartMasterMeasurementService.cs](SmartMasterMeasurementService.cs))

Reported through `MeasurementStatusInfo` (status enum + 0–1 progress + step text
+ warnings):

The noise is a `StreamingSource` added to the mixer for the duration of the
run; the mic is the monitor's `InputSource` + `EffectTap`, which sits ahead of
the track gain, so the capture comes in at full level with the monitor muted.

1. **Initializing** — the reference spectrum (8 s of the same pink noise through
   the same analyzer, cached per sample rate).
2. **Left / Right channel** — pink noise on one channel only, 1.6 s averaged,
   midrange level against the reference. Neither channel reaching the mic
   (under −70 dB), or one channel 18 dB under the other ⇒ error (aborts).
3. **Analyzing spectrum** — pink noise on L+R, 1 s settle, then 4 s of mic
   capture averaged into the 30 third-octave bands. The same noise is run through the same
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
   is 1-2-1 smoothed first (a single mic position is full of narrow interference
   dips that say nothing about the system), then aimed at a house target curve
   (warm at the bottom, rolled off on top) and applied at 65 % — a room is not a
   minimum-phase system, so a 1:1 correction mostly makes it sound worse. Boosts
   are capped short (bass bands 0–4 ≤ +2 dB, others ≤ +6 dB, all ≥ −12 dB)
   because filling a null costs headroom and rarely fills it. Phase-alignment
   delays / polarity come from the channel results, the subharmonic synth from
   the low-end verdict above.
6. Save to `measured.smartmaster.json` and report **Completed** (with any
   warnings). The measured preset is not applied automatically.

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
| ![Claude Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/main/docs/assets/tools/claude.svg) | **Anthropic** — Claude Code |
| ![Visual Studio Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/main/docs/assets/tools/vscode.svg) | **Microsoft** — Visual Studio Code |
| ![Visual Studio 2022](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/main/docs/assets/tools/visualstudio.svg) | **Microsoft** — Visual Studio 2022 |
| ![Rider](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/main/docs/assets/tools/rider.svg) | **JetBrains** — Rider |
