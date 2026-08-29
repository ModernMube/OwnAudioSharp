# Matchering

Reference-based audio mastering for OwnAudioSharp. The module analyzes the
spectral and dynamic character of a *target* track and reshapes a *source* track
to match it — or applies a built-in playback-system preset. Everything is driven
by a 30-band ISO spectrum analysis plus an EQ → compressor → dynamic-amp →
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
| [Audiomatchering.profile.cs](Audiomatchering.profile.cs) | Buffer analysis, `MatcheringProfile` (settings instead of a rendered file), in-memory preset targets. |
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
        30-Band EQ → Compressor → Dynamic Amp → Limiter  →  output .wav
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

### `ProcessWithEnhancedPreset(sourceFile, outputFile, PlaybackSystem, tempDirectory = null, eqOnlyMode = false)`

Preset-based mastering. Instead of an external reference it:

1. Extracts the **embedded base sample** (`OwnaudioNET.basesample.bin`).
2. Bakes the whole preset into that base sample — the declared EQ curve, the
   preset's own compressor (unless `eqOnlyMode`), then the level pushed to the
   preset's `TargetLoudness` behind a limiter. The result is a *rendered example*
   of what the preset is supposed to sound like.
3. Matches the source to that baked base, with the AGC block taken from the
   preset's `DynamicAmp` rather than from the measurement.

The EQ curve is fed through `_deconvolveToBandGains` before it is applied, so the
declared response is what the target actually *measures*. Setting each band to its
declared value instead overshoots by 60–120% because neighbouring 1/3-octave bells
add up — which is what the old "conservative curve" (0.75–0.85 scaling, ±3.5 dB
caps) was compensating for at the wrong end.

Temporary files are created in `tempDirectory` (defaults to the system temp
path) and cleaned up in a `finally` block.

### `BatchProcessWithEnhancedPreset(sourceFiles[], baseSampleFile, outputDirectory, PlaybackSystem, fileNameSuffix = null)`

Applies one preset to many files. Creates the output directory and a shared temp
directory, processes each file (errors on one file don't stop the batch), then
deletes the temp directory.

### `GetAvailablePresets() → Dictionary<PlaybackSystem, PlaybackPreset>` *(static)*

Returns a copy of all built-in presets for inspection/UI listing.

---

## Real-time API — settings instead of a rendered file

The four methods above are offline renderers: file in, file out. A live mastering
chain needs the *intermediate* result — the numbers the effects have to be set to.
That is what [Audiomatchering.profile.cs](Audiomatchering.profile.cs) exposes. All
of it is additive; the offline path is unchanged.

### `AnalyzeAudioBuffer(float[] interleaved, int sampleRate, int channels) → AudioSpectrum`

Exactly what `AnalyzeAudioFile` does, on samples you already have in memory — no
`FileSource`, no static lock, no decode round trip. `AnalyzeAudioFile` now delegates
to it after loading the file, so the two can never drift apart.

Throws `ArgumentException` on an empty buffer, and the usual
`InvalidOperationException` if the material is shorter than one ~10 s segment.

### `CalculateProfile(source, target, sampleRate, fixedQ = NativeBandQ, cutOnly = true) → MatcheringProfile`

Runs the whole match but stops before the render:

```csharp
var analyzer = new AudioAnalyzer();

AudioSpectrum mix = analyzer.AnalyzeAudioBuffer(masterSum, 48000, 2);
AudioSpectrum reference = analyzer.AnalyzeAudioFile("reference.wav");

MatcheringProfile profile = analyzer.CalculateProfile(mix, reference, 48000);

for (int band = 0; band < 30; band++)
    eq.SetBandGain(band, Centre(band), profile.QFactors[band], profile.BandGainsDb[band]);

compressor.Threshold = CompressorEffect.DbToLinear(profile.CompThresholdDb);
compressor.Ratio = profile.CompRatio;
```

**`fixedQ`** — the Q the deconvolution assumes on every band. It defaults to
`AudioAnalyzer.NativeBandQ` (4.318474), the constant Q of the *native* 30-band
equalizer, because the engine has no per-band Q parameter: that is the only Q that
ever actually plays. Handing the deconvolution the optimized per-band Qs would solve
for a filter bank that isn't the one making the sound. Pass `fixedQ: 0` to get the
per-band Qs anyway (that is what the offline render uses, since it runs the managed
EQ where the Q does get through).

**`cutOnly`** — subtracts the curve's maximum from all 30 bands, so the loudest band
lands on 0 dB and everything else goes negative. The offline chain buys its headroom
with a pre-gain stage; a real-time chain built from native effects has no gain stage,
so the level comes off the EQ instead and the `DynamicAmpEffect` behind it brings it
back to `TargetLoudness`. Since `_calcEqAdjustments` has already removed the broadband
offset, this shift is a pure level change and leaves the tonal shape alone. The shift
stops short if it would push the deepest cut past the ±9 dB clamp — a curve that hits
the rail is no longer the curve that was measured. `CutOnlyShiftDb` reports what was
actually applied.

### `MatcheringProfile`

| Member | Meaning |
| --- | --- |
| `WantedCurveDb[30]` | The curve we want to hear, dB. The one worth drawing. |
| `BandGainsDb[30]` | What the filter bank has to be *set to* for that curve to come out (deconvolved). |
| `QFactors[30]` | The Q the deconvolution assumed per band. |
| `CompThresholdDb`, `CompRatio` | Compressor settings. The threshold is dB — `CompressorEffect` wants it linear, run it through `CompressorEffect.DbToLinear`. |
| `TargetLoudness`, `MaxGain` | AGC target and gain ceiling for `DynamicAmpEffect`. |
| `AmpAttackSeconds`, `AmpReleaseSeconds` | AGC timing. Comes off the preset on the preset overload, otherwise the 0.1 / 0.5 default. |
| `SourceLoudness`, `SourceCrestDb`, `TargetCrestDb` | Measured values, for a status readout. |
| `CutOnlyShiftDb` | How far the curve got pushed down; 0 when `cutOnly` was off. |

No audio in it, so it serializes into a project file as is.

### `GetPresetTargetSpectrum(PlaybackSystem system, bool eqOnlyMode = false) → AudioSpectrum`

The target spectrum a preset is asking for, without touching the disk. Same steps
as `ProcessWithEnhancedPreset` — embedded base sample, deconvolved preset curve,
preset compressor, loudness normalization — but entirely in memory, ending in
`AnalyzeAudioBuffer` instead of two temp wavs. Cached per `(system, eqOnlyMode)`
and handed out as a copy, since `AudioSpectrum` has public setters.

The returned spectrum carries the preset's loudness (±1 dB of `TargetLoudness`) and
its crest, so the compressor settings `CalculateProfile` derives from the source /
target crest difference are the preset's, not the base sample's.

### `CalculateProfile(AudioSpectrum source, PlaybackSystem system, int sampleRate, …) → MatcheringProfile`

The preset overload. Builds the target itself and stamps `TargetLoudness`,
`MaxGain`, `AmpAttackSeconds` and `AmpReleaseSeconds` from the preset's own
`DynamicAmp` block — the curve and the compressor stay measured, so a preset
still behaves like matchering rather than like a fixed EQ.

```csharp
MatcheringProfile profile = analyzer.CalculateProfile(mix, PlaybackSystem.ClubPA, 48000);
```

Analysis is seconds of work on a full song — call all of this off the UI thread.

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
- overlapped FFT (75% overlap) with a **Hann window**, normalized by the window's
  noise power (`2·Σ|X|² / (N·Σw²)`) so a band reading is an absolute RMS that does
  not depend on the window or the FFT size. Flat-Top used to be used here, but its
  ~9-bin main lobe smears broadband material across neighbouring bands — it is a
  window for reading an isolated sine, not for band energy.
  FFT size comes from a fixed 0.35 s analysis window, so 44.1k and 48k files get
  the same resolution instead of being 2× apart. Band edges are the geometric
  1/3-octave ones (`fc / 2^(1/6)` … `fc · 2^(1/6)`), widened where the FFT cannot
  resolve them — at 20 Hz the natural band is about one bin wide, and a one-bin
  reading is noise, not a measurement.
- `AnalyzeAbsoluteDynamics` — absolute RMS, peak, loudness (dBFS), dynamic range.
- `CalculateSegmentWeight` — weights each segment by energy, closeness to a
  15 dB "ideal" dynamic range, and position (middle sections slightly boosted).

**Outlier rejection** — `FilterOutlierSegments` computes per-band mean/σ **in dB** and
scores each segment by how many bands exceed `OutlierThreshold` (2.5σ). Segments
that are outliers in more than 30% of bands are discarded.

**Combination** — `CalculateWeightedAverageSpectrum` produces the final
`AudioSpectrum` (peak is taken as a max, not averaged).

---

## EQ matching ([Audiomatchering.equalizer.cs](Audiomatchering.equalizer.cs))

1. `_calcEqAdjustments` — smooths both spectra, converts to dB, takes the per-band
   difference `target − source`, then **removes the broadband offset**: an overall
   level difference is a gain change, not an EQ move, and leaving it in meant the
   EQ and the AGC downstream both corrected for the same thing. What is left is
   clamped to ±9 dB — matching is a tonal balance job, and the old ±18 dB let one
   bad measurement wreck a master.
2. `_deconvolveToBandGains` — turns the wanted curve into the gains the filter bank
   must actually be *set* to. A 1/3-octave bell still bleeds into its neighbours,
   so setting every band to its wanted value overshoots by 60–120%. This solves the
   bank's response matrix (ridge-regularised least squares over the 30×30 system),
   which brings the realised curve to within ~0.1–0.35 dB of what was asked for,
   against 1.3–2.4 dB for the naive assignment.
3. `_applyEqProcessing` — builds and runs the mastering chain (see below).

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

All of it is live. `FrequencyResponse` is applied at full strength (clamped only by
the ±9 dB `MaxBandCorrectionDb` rail, which no preset reaches) through the
deconvolution, `Compression` drives the compressor on the baked base sample,
`TargetLoudness` is where that sample is normalized to, and `DynamicAmp` is stamped
onto the profile by the preset overload. `_presetQFactors` picks the Q values the
bake solves against.

`DynamicRange` is the one field that stays advisory: it is a ceiling the system can
take, and a bake can compress a sample but cannot invent crest that the base sample
never had. `StudioMonitors` declares 24 dB and the baked target measures ~12 dB —
the base sample's own crest.

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
| `MaxBandCorrectionDb` (±9 dB) | `_calcEqAdjustments` | Per-band correction limit. |
| `smoothingFactor` | `SmoothSpectrum` | Curve smoothness before diffing. |
| Q clamp (2.5…8.0) | `CalculateOptimalQFactors` | EQ band width limits. |
| `eqOnlyMode` | `ProcessWithEnhancedPreset`, `GetPresetTargetSpectrum` | Leaves the preset compressor out of the bake; the loudness normalization runs either way. |
| `fixedQ` | `CalculateProfile` | Q the deconvolution solves against; `NativeBandQ` for the native EQ, `0` for per-band Qs. |
| `cutOnly` | `CalculateProfile` | Cut-only curve for a chain with no gain stage in front. |

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
| ![Claude Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/docs/assets/tools/claude.svg) | **Anthropic** — Claude Code |
| ![Visual Studio Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/docs/assets/tools/vscode.svg) | **Microsoft** — Visual Studio Code |
| ![Visual Studio 2022](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/docs/assets/tools/visualstudio.svg) | **Microsoft** — Visual Studio 2022 |
| ![Rider](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/docs/assets/tools/rider.svg) | **JetBrains** — Rider |
