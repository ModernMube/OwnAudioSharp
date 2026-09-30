# Matchering

Reference-based audio mastering for OwnAudioSharp. The module analyzes the
spectral and dynamic character of a *target* track and reshapes a *source* track
to match it — or applies a built-in playback-system preset. Everything is driven
by a 30-band ISO spectrum analysis, a BS.1770 loudness analysis, and a
30-band EQ → OwnCompressor → OwnDynamicAmp mastering chain whose last stage is
the rider's own true-peak limiter.

Namespace root: `OwnaudioNET.Features.Matchering`
Entry class: `AudioAnalyzer` (one `partial class` split across the files below).

---

## File layout

| File | Responsibility |
| --- | --- |
| [Audiomatchering.cs](Audiomatchering.cs) | Public API, segmented FFT spectrum analysis, windowing, outlier filtering, weighted averaging. |
| [Audiomatchering.equalizer.cs](Audiomatchering.equalizer.cs) | EQ delta calculation, spectral smoothing, and the full direct-processing mastering chain. |
| [Audiomatchering.loudness.cs](Audiomatchering.loudness.cs) | `MeasureLoudness`: integrated LUFS, loudness range, true peak, noise floor, side/mid — measured the way OwnDynamicAmp measures. |
| [Audiomatchering.dynamics.cs](Audiomatchering.dynamics.cs) | OwnCompressor and OwnDynamicAmp settings from the loudness readings. |
| [Audiomatchering.chain.cs](Audiomatchering.chain.cs) | The native effects of the render, addressed by param id, plus the block renderer and latency compensation. |
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
     _calcEqAdjustments    _compressorSettings      _levelerSettings
       curve[30]           (peak to loudness)     (LUFS, loudness range)
              │                     │                      │
              └─────────────────────┼──────────────────────┘
                                    ▼
                          _applyEqProcessing
     30-Band EQ → OwnCompressor ─(measure LUFS)→ OwnDynamicAmp  →  output .wav
```

Spectrum analysis itself is **segmented**: the audio is cut into overlapping
~10 s segments, each analyzed independently, statistically filtered for
outliers, then combined by weighted average. This is far more robust than a
single whole-file FFT for real music. Next to it, every analysis measures the
whole track's loudness the way the OwnDynamicAmp does (`AudioSpectrum.LoudnessStats`),
and the dynamics settings are derived from those readings.

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

The core matching operation. Analyzes both files, computes the EQ curve and the
OwnCompressor / OwnDynamicAmp settings, and renders the processed source to `outputFile`.

```csharp
var analyzer = new AudioAnalyzer();
analyzer.ProcessEQMatching("mix.wav", "reference.wav", "mastered.wav");
```

### `ProcessWithEnhancedPreset(sourceFile, outputFile, PlaybackSystem, tempDirectory = null, eqOnlyMode = false)`

Preset-based mastering. Instead of an external reference it:

1. Extracts the **embedded base sample** (`OwnaudioNET.basesample.bin`).
2. Bakes the whole preset into that base sample — the declared EQ curve, the
   preset's own `Compressor` setup on an OwnCompressor (unless `eqOnlyMode`), then
   the level pushed to the preset's `TargetLoudness` in LUFS behind the OwnDynamicAmp's
   true-peak limiter at the preset's `Leveler.CeilingDbtp`. The result is a
   *rendered example* of what the preset is supposed to sound like.
3. Matches the source to that baked base, with the system's OwnCompressor and
   OwnDynamicAmp character (target, window, ceiling, timing — see Presets) taken from
   the preset rather than from the measurement.

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

var compressor = new OwnCompressorEffect();
var leveler = new OwnDynamicAmpEffect();

profile.Compressor.ApplyTo(compressor);   // every parameter, knee to range
profile.Leveler.ApplyTo(leveler);         // LUFS target, window, gates, true-peak ceiling
```

Chain them in that order — EQ, OwnCompressor, OwnDynamicAmp. The rider's
true-peak limiter is the last stage, so no separate limiter is needed.

**`fixedQ`** — the Q the deconvolution assumes on every band. It defaults to
`AudioAnalyzer.NativeBandQ` (4.318474), the native 30-band equalizer's own default,
so a profile calculated today produces the curve it always did. The engine takes a Q
per band now, so `fixedQ: 0` gives you the optimized per-band Qs and they do reach
the filter — the render sets frequency, Q and gain on every band.

**`cutOnly`** — subtracts the curve's maximum from all 30 bands, so the loudest band
lands on 0 dB and everything else goes negative. The offline chain buys its headroom
with a pre-gain stage; a real-time chain built from native effects has no gain stage,
so the level comes off the EQ instead and the gain rider behind it brings it
back to `TargetLoudness` — `Leveler.InitialGainDb` already carries the shift, so
the rider starts there instead of slewing up to it. Since `_calcEqAdjustments` has already removed the broadband
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
| `Compressor` | `OwnCompressorSettings` — every OwnCompressor parameter. `ApplyTo(OwnCompressorEffect)` sets a live effect. |
| `Leveler` | `OwnDynamicAmpSettings` — every OwnDynamicAmp parameter. `ApplyTo(OwnDynamicAmpEffect)` sets a live effect. |
| `CompThresholdDb`, `CompRatio` | The same as `Compressor.ThresholdDb` / `Ratio`, kept for existing callers. |
| `TargetLoudness` | The rider's target, LUFS (the same as `Leveler.TargetLoudness`). |
| `MaxGain`, `AmpAttackSeconds`, `AmpReleaseSeconds` | Settings for the older `DynamicAmpEffect`. Come off the preset on the preset overload, otherwise the 6× / 0.1 / 0.5 default. |
| `SourceLoudness` | Integrated loudness of the source, LUFS. |
| `SourceCrestDb`, `TargetCrestDb` | Sample peak to RMS, dB. |
| `SourcePeakToLoudnessDb`, `TargetPeakToLoudnessDb` | True peak to integrated loudness, dB — what the compressor matches. |
| `CutOnlyShiftDb` | How far the curve got pushed down; 0 when `cutOnly` was off. |

No audio in it, so it serializes into a project file as is.

### `GetPresetTargetSpectrum(PlaybackSystem system, bool eqOnlyMode = false) → AudioSpectrum`

The target spectrum a preset is asking for, without touching the disk. Same steps
as `ProcessWithEnhancedPreset` — embedded base sample, deconvolved preset curve,
preset compressor, loudness normalization — but entirely in memory, ending in
`AnalyzeAudioBuffer` instead of two temp wavs. Cached per `(system, eqOnlyMode)`
and handed out as a copy, since `AudioSpectrum` has public setters.

The returned spectrum carries the preset's integrated loudness (±1.5 LU of
`TargetLoudness`) and its peak to loudness ratio in `LoudnessStats`, so the
compressor settings `CalculateProfile` derives from the source / target difference
are the preset's, not the base sample's.

### `CalculateProfile(AudioSpectrum source, PlaybackSystem system, int sampleRate, …) → MatcheringProfile`

The preset overload. Builds the target itself, stamps the system's OwnCompressor
and OwnDynamicAmp character from the preset's `Compressor` and `Leveler` blocks
(see [Presets](#presets-audiomatchering-presetdatacs)), and fills `MaxGain`,
`AmpAttackSeconds` and `AmpReleaseSeconds` from its `DynamicAmp` block. The curve,
the compressor's threshold, ratio and range and the rider's start gain stay
measured, so a preset still behaves like matchering rather than like a fixed EQ.

### `MeasureLoudness(float[] interleaved, int sampleRate, int channels) → LoudnessInfo`

The loudness half of the analysis on its own. `AnalyzeAudioBuffer` and
`AnalyzeAudioFile` call it and put the result in `AudioSpectrum.LoudnessStats`.
A spectrum built by hand without it still works: the match then estimates the
loudness from `Loudness` and `PeakLevel`.

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

### Loudness ([Audiomatchering.loudness.cs](Audiomatchering.loudness.cs))

Measured on the whole interleaved buffer, with the same maths the OwnDynamicAmp
runs, so a number derived from it is a number the rider reads back the same way:

| Reading | How |
| --- | --- |
| `IntegratedLufs` | BS.1770-4 K-weighting (the rider's own coefficients, rebuilt per sample rate), 400 ms blocks every 100 ms, −70 LUFS absolute and −10 LU relative gate. |
| `LoudnessRangeLu` | EBU Tech 3342: 3 s short-term blocks, −70 LUFS / −20 LU gates, 95th minus 10th percentile. |
| `TruePeakDbtp` | 4× oversampled through the rider limiter's windowed sinc kernel. |
| `NoiseFloorLufs` | 10th percentile of the non-silent momentary blocks — fades, pauses, room noise. |
| `SideToMidDb` | Side energy against mid energy (−60 for mono). |
| `PeakToLoudnessDb` | `TruePeakDbtp − IntegratedLufs`, the crest the compressor works on. |

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

Built and run by [Audiomatchering.chain.cs](Audiomatchering.chain.cs), which talks to the
engine directly — native effects addressed by param id, no `IEffectProcessor` in between.
The managed effects are a parameter model for a mixer chain; routing the render through
them meant anything they don't mirror (the per-band Q, the rider's starting gain) was
dropped, and their unit conversions sat between the preset and the DSP for nothing.

Rendered chunk-by-chunk (512-frame buffers) in this fixed order:

| # | Effect | Role |
| --- | --- | --- |
| 1 | `EffectType.Equalizer30` | Shape frequency response — frequency, Q and gain per band. |
| 2 | `EffectType.OwnCompressor` | Take the source's peak to loudness ratio to the target's — no further, the range stops it. |
| 3 | `EffectType.OwnDynamicAmp` | Ride the level to the target's integrated LUFS, take out the extra loudness range, hold the true-peak ceiling. |

Before the chain, **smart headroom** pre-gain is applied: the source is
attenuated proportionally to the largest boosts (clamped to −12…0 dB) and the
compressor threshold moves down with it. The render runs in two passes: EQ and
OwnCompressor first, then the result's integrated loudness is measured and the
OwnDynamicAmp starts on exactly the gain that takes it to the target (its
`InitialGain`), so the head of the master is not spent slewing and the tolerance
window never leaves the level short. Both passes are latency compensated — every
look-ahead of the chain is pushed out of the tail and the audio slid back into
place.
Output is written as 24-bit WAV via `OwnaudioNET.Recording.WaveFile.Create`.

---

## Dynamics ([Audiomatchering.dynamics.cs](Audiomatchering.dynamics.cs))

Both settings come off the loudness readings of source and target.

**OwnCompressor** (`_compressorSettings`) — works on the peak to loudness ratio
(PLR). With `excess = PLR(source) − PLR(target)`:

| Parameter | Derivation |
| --- | --- |
| Threshold | Source LUFS + 0.35 × PLR (3…8 dB), so only the peaks cross it. A source already denser than the target (excess ≤ 1 dB): true peak − 4 dB. |
| Ratio | Takes the excess (at most 85% of the overshoot) off the true peak; 1.5:1 on a denser source. Clamped 1.2…6. |
| Range | Excess + 2 dB (2 dB on a denser source) — the compressor can never squash further than the match asks. |
| Knee | 12 dB at gentle ratios, narrowing to 4 dB as the ratio rises. |
| Attack / release | Attack 30 ms, faster with the excess (down to 5 ms); release from the source's loudness range (80…400 ms), with auto release on. |
| Look-ahead | 2 ms, so a slow attack still catches the transient. |
| Channel mode / link | Mid/side for a stereo source (side above −30 dB against the mid); the link drops from 0.8 toward 0.5 when the source is wider than the target, so its side is held on its own. |
| Key high-pass | 20…150 Hz, higher the more of the source's energy sits under 80 Hz. |

**OwnDynamicAmp** (`_levelerSettings`) — works on integrated loudness and loudness range:

| Parameter | Derivation |
| --- | --- |
| Target | Target's integrated LUFS, or the preset's `DynamicAmp.TargetLevel`. |
| Window / rates / tolerance | Source with more loudness range than the target: 3…12 s window, faster rates and a 0.5…1.5 dB tolerance, so the rider takes the extra range out. Otherwise a 10…30 s window and 1 dB tolerance that only sets the level. |
| Relative gate | Target's loudness range + 4 LU (8…20), so the quiet parts the target keeps are kept. |
| Freeze threshold | Under the source's noise floor (−70…−40 LUFS), so fades and pauses never pump up. Moved with the level the rider actually gets: the pre-gain, the cut-only shift and what the EQ and compressor left. |
| Ceiling | Target's true peak, held between −3 and −1 dBTP. |
| Initial gain / boost / cut | The loudness difference (plus the cut-only shift or the render's pre-gain), with 6 dB of room either way. |

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
deconvolution, `Compressor` (an `OwnCompressorSettings`) compresses the baked base
sample, `TargetLoudness` (LUFS) is where that sample is normalized to under
`Leveler.CeilingDbtp`, and the preset overload stamps the system's character onto
the profile:

| Taken from the preset | Stays measured |
| --- | --- |
| OwnCompressor knee, attack, release, auto release, look-ahead, detector, key high-pass | Threshold, ratio, range, channel mode, stereo link — properties of the mix |
| OwnDynamicAmp target, window, rise / fall rates, tolerance, smoothing, relative gate, ceiling, limiter timing | Initial gain, boost / cut room, freeze threshold — properties of the source's level |

The systems in short:

| System | OwnCompressor | OwnDynamicAmp |
| --- | --- | --- |
| `ConcertPA` | 1.8:1, 8 dB knee, M/S, key HPF 60 Hz | −16 LUFS, 12 s window, −1 dBTP |
| `ClubPA` | 2.5:1, 5 ms attack, fixed release, L/R, key HPF 100 Hz | −11 LUFS, 4 s window, 0.5 dB tolerance, −0.5 dBTP |
| `HiFiSpeakers` | 1.2:1, 12 dB knee, RMS, 3 dB range | −18 LUFS, 30 s window, gate −8 LU (keeps the quiet parts) |
| `StudioMonitors` | 1.1:1, RMS, 2 dB range | −20 LUFS, 30 s window, gate −6 LU |
| `Headphones` | 1.8:1, M/S link 0.7 (width held) | −14 LUFS, 10 s window |
| `Earbuds` | 2.5:1, 2 ms attack, key HPF 80 Hz | −13 LUFS, 6 s window |
| `CarStereo` | 2.8:1, RMS, 10 dB range | −11 LUFS, 3 s window, gate −20 LU (quiet parts lifted over road noise) |
| `Television` | 3:1, RMS, key HPF 120 Hz | −14 LUFS, 3 s window, −2 dBTP |
| `RadioBroadcast` | 4.5:1, 0.5 ms attack, 5 ms look-ahead, 14 dB range | −10 LUFS, 2 s window, gate −24 LU |
| `Smartphone` | 3.5:1, M/S link 0.6, key HPF 150 Hz | −11 LUFS, 3 s window |

The older `Compression` and `DynamicAmp` blocks stay, carrying the same threshold,
ratio, timing, makeup and target, for callers still on `CompressorEffect` /
`DynamicAmpEffect`. `_presetQFactors` picks the Q values the
bake solves against.

`DynamicRange` is the one field that stays advisory: it is a ceiling the system can
take, and a bake can compress a sample but cannot invent crest that the base sample
never had. `StudioMonitors` declares 24 dB and the baked target measures ~12 dB —
the base sample's own crest.

---

## Data classes ([Audiomatchering.data.cs](Audiomatchering.data.cs))

| Class | Purpose |
| --- | --- |
| `AudioSpectrum` | 30-band spectrum + RMS, peak, dynamic range, loudness, and the `LoudnessStats` of the whole track. |
| `LoudnessInfo` | Integrated LUFS, loudness range, true peak, noise floor, side/mid, peak to loudness ratio. |
| `OwnCompressorSettings` / `OwnDynamicAmpSettings` | The matched effect setups, with `ApplyTo` for a live effect. Records, so they serialize and copy with `with`. |
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
| `fixedQ` | `CalculateProfile` | Q the deconvolution solves against. `NativeBandQ` is the one the engine actually plays; `0` solves for per-band Qs that no longer reach the DSP. |
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
| ![Claude Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/main/docs/assets/tools/claude.svg) | **Anthropic** — Claude Code |
| ![Visual Studio Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/main/docs/assets/tools/vscode.svg) | **Microsoft** — Visual Studio Code |
| ![Visual Studio 2022](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/main/docs/assets/tools/visualstudio.svg) | **Microsoft** — Visual Studio 2022 |
| ![Rider](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/main/docs/assets/tools/rider.svg) | **JetBrains** — Rider |
