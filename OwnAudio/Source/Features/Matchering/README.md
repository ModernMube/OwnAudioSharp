# Matchering

Reference-based audio mastering for OwnAudioSharp. The module analyzes the
spectral and dynamic character of a *target* track and reshapes a *source* track
to match it — or applies a built-in playback-system preset. Everything is driven
by a 30-band ISO spectrum analysis plus a compressor → EQ → dynamic-amp →
limiter mastering chain.

Namespace root: `OwnaudioNET.Features.Matchering`
Entry class: `AudioAnalyzer` (one `partial class` split across the files below).

---

## File layout

| File | Responsibility |
| --- | --- |
| [Audiomatchering.cs](Audiomatchering.cs) | Public API, segmented FFT spectrum analysis, windowing, outlier filtering, weighted averaging. |
| [Audiomatchering.equalizer.cs](Audiomatchering.equalizer.cs) | EQ delta calculation, spectral smoothing, and the full direct-processing mastering chain. |
| [Audiomatchering.dynamics.cs](Audiomatchering.dynamics.cs) | Crest-factor-based dynamic-amp and compressor settings. |
| [Audiomatchering.qfactors.cs](Audiomatchering.qfactors.cs) | Per-band Q-factor optimization for the 30-band EQ. |
| [Audiomatchering.preset.cs](Audiomatchering.preset.cs) | Playback-system preset processing (single + batch), embedded base-sample. |
| [Audiomatchering.presetdata.cs](Audiomatchering.presetdata.cs) | `PlaybackSystem` enum and the preset definitions (EQ curves, loudness, compression). |
| [Audiomatchering.data.cs](Audiomatchering.data.cs) | Plain data classes (`AudioSpectrum`, `DynamicsInfo`, `AudioSegment`, config, …). |

---

## Pipeline overview

```
                 ┌──────────────────────────────────────────┐
source file  →   │ AnalyzeAudioFile → AudioSpectrum (source) │
target file  →   │ AnalyzeAudioFile → AudioSpectrum (target) │
                 └──────────────────────────────────────────┘
                                    │
              ┌─────────────────────┼──────────────────────┐
              ▼                     ▼                      ▼
   CalculateDirectEQ       CalculateDynamicAmp     CalculateCompressor
     Adjustments[30]           Settings                Settings
              │                     │                      │
              └─────────────────────┼──────────────────────┘
                                    ▼
                        ApplyDirectEQProcessing
        Compressor → 30-Band EQ → Dynamic Amp → Limiter  →  output .wav
```

Spectrum analysis itself is **segmented**: the audio is cut into overlapping
~10 s segments, each analyzed independently, statistically filtered for
outliers, then combined by weighted average. This is far more robust than a
single whole-file FFT for real music.

---

## Public API

All entry points are instance methods on `AudioAnalyzer` (default-construct it).

### `AnalyzeAudioFile(string filePath) → AudioSpectrum`

Loads a file (via `FileSource`), de-interleaves multichannel audio, analyzes
each channel with the segmented approach, and averages the channels (RMS energy
averaging). Thread-safe — guarded by a static lock during `FileSource` creation.

Throws `InvalidOperationException` if the file cannot be loaded or is shorter
than one segment (~10 s).

### `ProcessEQMatching(string sourceFile, string targetFile, string outputFile)`

The core matching operation. Analyzes both files, computes EQ / dynamics /
compressor settings, and renders the processed source to `outputFile`.

```csharp
var analyzer = new AudioAnalyzer();
analyzer.ProcessEQMatching("mix.wav", "reference.wav", "mastered.wav");
```

### `ProcessWithEnhancedPreset(sourceFile, outputFile, PlaybackSystem, tempDirectory = null, eqOnlyMode = true)`

Preset-based mastering. Instead of an external reference it:

1. Extracts the **embedded base sample** (`OwnaudioNET.basesample.bin`).
2. Applies the chosen preset's EQ curve (optionally + compression) to that base
   sample to build an enhanced *target*.
3. Runs `ProcessEQMatching` from the source to that enhanced base.

Temporary files are created in `tempDirectory` (defaults to the system temp
path) and cleaned up in a `finally` block.

### `BatchProcessWithEnhancedPreset(sourceFiles[], baseSampleFile, outputDirectory, PlaybackSystem, fileNameSuffix = null)`

Applies one preset to many files. Creates the output directory and a shared temp
directory, processes each file (errors on one file don't stop the batch), then
deletes the temp directory.

### `GetAvailablePresets() → Dictionary<PlaybackSystem, PlaybackPreset>` *(static)*

Returns a copy of all built-in presets for inspection/UI listing.

---

## The 30-band model

Everything works on **30 ISO standard bands** from 20 Hz to 16 kHz
(`FrequencyBands` in [Audiomatchering.cs](Audiomatchering.cs)). Every spectrum,
EQ curve, and Q-factor array is a `float[30]` indexed identically to this table:

```
0:20Hz 1:25 2:31.5 3:40 4:50 5:63 6:80 7:100 8:125 9:160
10:200 11:250 12:315 13:400 14:500 15:630 16:800 17:1k 18:1.25k 19:1.6k
20:2k 21:2.5k 22:3.15k 23:4k 24:5k 25:6.3k 26:8k 27:10k 28:12.5k 29:16k
```

---

## Analysis internals ([Audiomatchering.cs](Audiomatchering.cs))

**Segmentation** — `CreateAudioSegments` splits audio into
`SegmentLengthSeconds` (default 10 s) windows with `OverlapRatio` (default 20%)
overlap, tagging each with its RMS energy.

**Per-segment analysis** — `AnalyzeSegments` skips segments quieter than
`MinSegmentEnergyThreshold` (−60 dBFS), then for each remaining segment runs:
- `AnalyzeFrequencySpectrumAbsolute` — overlapped FFT (75% overlap) with a
  **Flat-Top window** (chosen for amplitude accuracy over frequency resolution).
  FFT size scales with sample rate (8192 / 16384 / 32768). Per-band energy uses
  proportional bandwidth (`centerFreq × 0.23`) and linear distance weighting.
- `AnalyzeAbsoluteDynamics` — absolute RMS, peak, loudness (dBFS), dynamic range.
- `CalculateSegmentWeight` — weights each segment by energy, closeness to a
  15 dB "ideal" dynamic range, and position (middle sections slightly boosted).

**Outlier rejection** — `FilterOutlierSegments` computes per-band mean/σ and
scores each segment by how many bands exceed `OutlierThreshold` (2.5σ). Segments
that are outliers in more than 30% of bands are discarded.

**Combination** — `CalculateWeightedAverageSpectrum` produces the final
`AudioSpectrum` (peak is taken as a max, not averaged).

---

## EQ matching ([Audiomatchering.equalizer.cs](Audiomatchering.equalizer.cs))

1. `CalculateDirectEQAdjustments` — smooths both spectra
   (`SmoothSpectrum`), converts to dB, and takes the per-band difference
   `target − source`.
2. `ApplyIntelligentScaling` — clamps every band to ±18 dB (the EQ's capacity).
3. `ApplyDirectEQProcessing` — builds and runs the mastering chain (see below).

`ApplyRefinedSpectralBalance` is available to tame excessive 2–5 kHz presence and
low-end dominance, though the default path uses the simpler scaling.

### Mastering chain

Rendered chunk-by-chunk (512-frame buffers) in this fixed order:

| # | Effect | Role |
| --- | --- | --- |
| 1 | `CompressorEffect` | Stabilize dynamics first (settings from crest-factor analysis). |
| 2 | `Equalizer30BandEffect` | Shape frequency response with optimized per-band Q. |
| 3 | `DynamicAmpEffect` | AGC toward the target loudness (gain capped ~3×, gentle). |
| 4 | `LimiterEffect` | True-peak safety (−0.5 dB threshold, −0.2 dB ceiling). |

Before the chain, **smart headroom** pre-gain is applied: the source is
attenuated proportionally to the largest boosts (clamped to −12…0 dB) and the
dynamic amp compensates back, avoiding intersample clipping from EQ boosts.
Output is written as 24-bit WAV via `OwnaudioNET.Recording.WaveFile.Create`.

---

## Dynamics ([Audiomatchering.dynamics.cs](Audiomatchering.dynamics.cs))

Both `CalculateDynamicAmpSettings` and `CalculateCompressorSettings` compare the
**crest factor** (peak-to-RMS ratio) of source vs. target. A source with more
crest than the target gets a higher compression ratio to match; results are
clamped to musical ranges (ratio 1–10, threshold −30…−2 dB).

---

## Q-factor optimization ([Audiomatchering.qfactors.cs](Audiomatchering.qfactors.cs))

`CalculateOptimalQFactors` derives a Q per band by weighted combination of four
signals, then clamps to 2.5…8.0:

- **`GetFrequencyBasedQ`** — psychoacoustic base Q (wider in the low end,
  ~1/3-octave in the mids).
- **`CalculateGainBasedQ`** — larger boosts/cuts tighten Q for surgical moves.
- **`CalculateNeighboringBandsQ`** — correlated neighbors → wider Q for smooth
  curves; isolated corrections → narrower Q.
- **`CalculateSpectralDensityQ`** — bigger source/target level ratio → narrower Q.

`CombineQFactors` weights these (base Q dominant at 0.6) with frequency-dependent
tweaks: lows favor smoothness, highs favor surgical precision.

---

## Presets ([Audiomatchering.presetdata.cs](Audiomatchering.presetdata.cs))

`PlaybackSystem` enumerates 10 target systems, each with a `PlaybackPreset`
(30-band EQ curve, target LUFS, dynamic range, compressor and dynamic-amp
settings):

`ConcertPA`, `ClubPA`, `HiFiSpeakers`, `StudioMonitors`, `Headphones`,
`Earbuds`, `CarStereo`, `Television`, `RadioBroadcast`, `Smartphone`.

When applied, `CreateConservativePresetCurve` scales the raw preset curve down
per frequency range and caps boosts (~3–3.5 dB) so it makes a realistic
matchering *target* rather than overdriving the source, and
`CalculateEnhancedPresetQFactors` picks preset-appropriate Q values.

---

## Data classes ([Audiomatchering.data.cs](Audiomatchering.data.cs))

| Class | Purpose |
| --- | --- |
| `AudioSpectrum` | 30-band spectrum + RMS, peak, dynamic range, loudness. |
| `DynamicsInfo` | RMS, peak, dynamic range, loudness for one segment. |
| `AudioSegment` | Segment samples + timing, energy, sample rate. |
| `SegmentAnalysis` | Per-segment spectrum + dynamics + weight + outlier score. |
| `SegmentedAnalysisConfig` | Segment length, overlap, outlier & energy thresholds. |
| `CompressionSettings` / `DynamicAmpSettings` | Effect parameter bundles. |

---

## Tuning cheat-sheet

| Knob | Where | Effect |
| --- | --- | --- |
| `SegmentLengthSeconds` / `OverlapRatio` | `SegmentedAnalysisConfig` | Analysis granularity vs. cost. |
| `MinSegmentEnergyThreshold` | `SegmentedAnalysisConfig` | Skips quiet/silent segments. |
| `OutlierThreshold` | `SegmentedAnalysisConfig` | Aggressiveness of outlier rejection. |
| `maxBoost` / `maxCut` (±18 dB) | `ApplyIntelligentScaling` | EQ delta clamp. |
| `smoothingFactor` | `SmoothSpectrum` | Curve smoothness before diffing. |
| Q clamp (2.5…8.0) | `CalculateOptimalQFactors` | EQ band width limits. |
| `eqOnlyMode` | `ProcessWithEnhancedPreset` | EQ-only vs. full effects for presets. |

## Requirements & notes

- Input audio must be **longer than one segment (~10 s)** or analysis throws.
- Output is always **24-bit WAV**.
- Progress and detailed diagnostics are emitted through the `Logger` (`Log.Info`
  / `Log.Warning` / `Log.Error`); some legacy diagnostics still use `Console`.

---

## Development Tools

This project is developed with the following tools:

| | |
|:--:|:--|
| ![Claude Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/claude.svg) | **Anthropic** — Claude Code |
| ![Visual Studio Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/vscode.svg) | **Microsoft** — Visual Studio Code |
| ![Visual Studio 2022](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/visualstudio.svg) | **Microsoft** — Visual Studio 2022 |
| ![Rider](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/rider.svg) | **JetBrains** — Rider |
