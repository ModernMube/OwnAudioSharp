# OwnAudio.ML

AOT-compatible AI/ML audio features for Windows, macOS, and Linux.  
Part of the [OwnAudioSharp](https://github.com/modernmube/OwnAudioSharp) ecosystem.

## Features

- **Vocal Separation (HTDemucs)** — 4-stem: vocals / drums / bass / other via ONNX Runtime C API
- **Vocal Separation (MDX)** — spectral masking; supports best / default / karaoke and custom MDX models; multi-model ensemble averaging
- **Chord Detection** — HPCP chromagram + template matching; BasicPitch ONNX inference if model is present
- **Spectrum Analysis** — 30-band FFT-based EQ fingerprint with RMS / peak / loudness / dynamic range
- **EQ Matching** — per-band gain adjustments to match a source to a target spectrum
- **Model Manager** — on-demand download with SHA-256 validation; models are never embedded in the package
- **Native AOT ready** — `IsAotCompatible=true`, zero reflection, P/Invoke via `[LibraryImport]`

## Installation

```xml
<ProjectReference Include="OwnAudio.ML/OwnAudio.ML.csproj" />
```

Or (once published to NuGet):

```xml
<PackageReference Include="OwnAudioSharp.ML" Version="3.0.0" />
```

---

## Namespaces

| Namespace | Contents |
|---|---|
| `OwnAudio.ML` | `ModelManager`, `VocalSeparator`, `MdxSeparator`, `ChordDetector`, `AudioAnalyzer` |
| `OwnAudio.ML` | `SeparationResult`, `DetectedChord`, `AudioSpectrum`, `ModelDownloadProgress` |

---

## 1. Setup — Model Manager

Models are large (~17–90 MB each) and are **not** embedded in the package.  
Call `EnsureModelsAsync` once at app startup to download any missing files, then `Initialize`.

```csharp
using OwnAudio.ML;

// Default location: %LocalAppData%/OwnAudio/Models  (Windows)
//                   ~/.local/share/OwnAudio/Models   (Linux / macOS)
string modelDir = ModelManager.DefaultModelDirectory;

// Download missing / corrupted models (no-op if all are already valid)
var progress = new Progress<ModelDownloadProgress>(p =>
    Console.WriteLine($"[{p.ModelName}] {p.BytesReceived / 1024 / 1024} MB / {p.TotalBytes / 1024 / 1024} MB"));

await ModelManager.EnsureModelsAsync(modelDir, progress);

// Initialise the native runtime (call once, before any inference)
int status = ModelManager.Initialize(modelDir);
if (status != 0)
    throw new Exception($"ML runtime init failed: {status}");
```

### Load a specific model at runtime

If you manage models yourself, use `ownaudio_ml_load_model` via the interop layer, or rely on `Initialize` to scan the directory automatically.

```csharp
// Shut down cleanly when done (releases native ONNX sessions)
ModelManager.Shutdown();
```

---

## 2. Vocal Separation

`VocalSeparator.SeparateAsync` splits stereo audio into a **vocals** track and an **instrumental** track.  
Internally uses HTDemucs with chunk-based processing and cosine-crossfade overlap-add.

```csharp
using OwnAudio.ML;

// audioData: stereo interleaved float samples (left, right, left, right, …)
float[] audioData = LoadAudio("song.wav", out int sampleRate);

SeparationResult result = await VocalSeparator.SeparateAsync(audioData, sampleRate);

// result.Vocals        – stereo interleaved, same length as input
// result.Instrumental  – drums + bass + other, stereo interleaved
SaveWav("vocals.wav",       result.Vocals,        sampleRate, channels: 2);
SaveWav("instrumental.wav", result.Instrumental,  sampleRate, channels: 2);
```

> **Tip:** Separation is CPU-intensive (~10–15× real-time on a modern CPU).  
> Always call `SeparateAsync` on a background thread (the method does this internally via `Task.Run`).

### SeparationResult

| Property | Type | Description |
|---|---|---|
| `Vocals` | `float[]` | Isolated vocal track (stereo interleaved) |
| `Instrumental` | `float[]` | Sum of drums + bass + other stems (stereo interleaved) |

### Model selection

If you have multiple HTDemucs variants (e.g. `htdemucs_ft.onnx`, `htdemucs_6s.onnx`), pass the path directly to `SeparateAsync`:

```csharp
// Load a fine-tuned variant for this call only
SeparationResult result = await VocalSeparator.SeparateAsync(
    audioData, sampleRate,
    modelPath: "/path/to/models/htdemucs_ft.onnx");
```

Or pre-load explicitly and reuse across multiple calls:

```csharp
// Load once
ModelManager.LoadModel("htdemucs", "/path/to/models/htdemucs_ft.onnx");

// Check it is ready
if (ModelManager.IsModelLoaded("htdemucs"))
{
    // modelPath: null → uses the already-loaded model
    SeparationResult r1 = await VocalSeparator.SeparateAsync(audio1, sr);
    SeparationResult r2 = await VocalSeparator.SeparateAsync(audio2, sr);
}
```

| Method | Description |
|---|---|
| `ModelManager.LoadModel(name, path)` | Loads (or replaces) the named model at runtime; returns 0 on success |
| `ModelManager.IsModelLoaded(name)` | Returns `true` if the model is ready for inference |
| `SeparateAsync(..., modelPath)` | Optional per-call model path; loads it before running inference |

---

## 3. MDX Separation

`MdxSeparator` uses MDX-format ONNX models (e.g. `best.onnx`, `default.onnx`, `karaoke.onnx`).
The pipeline is STFT-based with a noise-reduction trick: each sub-chunk is inferred twice
(normal and sign-inverted input) and the results are averaged for cleaner separation.

### Single model

```csharp
using OwnAudio.ML;

// Load a specific MDX model first
ModelManager.LoadModel("best", Path.Combine(modelDir, "best.onnx"));

SeparationResult result = await MdxSeparator.SeparateAsync(audioData, sampleRate, "best");

SaveWav("vocals.wav",       result.Vocals,        sampleRate, channels: 2);
SaveWav("instrumental.wav", result.Instrumental,  sampleRate, channels: 2);
```

### Multi-model ensemble (averaged output)

Running several models and averaging their results typically gives better quality
at the cost of proportionally longer processing time.

```csharp
// Load all desired models
ModelManager.LoadModel("best",    Path.Combine(modelDir, "best.onnx"));
ModelManager.LoadModel("default", Path.Combine(modelDir, "default.onnx"));

// Ensemble: separates with both, averages the output
SeparationResult result = await MdxSeparator.SeparateEnsembleAsync(
    audioData, sampleRate,
    modelNames: ["best", "default"]);
```

> **Note:** MDX models output either Vocals or Instrumental — the complement is calculated
> as `original − output`. The separator auto-detects which stem the model produces from
> the ONNX output tensor name or the logical model name.

---

## 4. Chord Detection

`ChordDetector.DetectAsync` returns a list of chords with timestamps and confidence scores.

```csharp
using OwnAudio.ML;

float[] audioData = LoadAudio("song.wav", out int sampleRate);

IReadOnlyList<DetectedChord> chords =
    await ChordDetector.DetectAsync(audioData, sampleRate);

foreach (DetectedChord chord in chords)
{
    Console.WriteLine(
        $"{chord.StartTime:F2}s – {chord.EndTime:F2}s  " +
        $"{chord.Name,-6}  conf={chord.Confidence:P0}");
}
```

**Example output:**
```
0.00s – 1.02s  Cmaj   conf=87%
1.02s – 2.56s  Am     conf=91%
2.56s – 3.84s  F      conf=78%
3.84s – 5.12s  G7     conf=83%
```

### DetectedChord

| Property | Type | Description |
|---|---|---|
| `StartTime` | `float` | Chord start in seconds |
| `EndTime` | `float` | Chord end in seconds |
| `Name` | `string` | Chord name, e.g. `"Cmaj"`, `"Am"`, `"G7"` |
| `Confidence` | `float` | Detection confidence in [0, 1] |

> **Note:** When `nmp.onnx` (BasicPitch) is present the detector uses neural network note detection for higher accuracy.  
> Without the model it falls back to HPCP chromagram analysis — functional, but less precise on complex chords.

---

## 5. Spectrum Analysis

`AudioAnalyzer.AnalyzeAsync` computes a 30-band energy fingerprint and loudness metrics.

```csharp
using OwnAudio.ML;

float[] audioData = LoadAudio("master.wav", out int sampleRate);

AudioSpectrum spectrum = await AudioAnalyzer.AnalyzeAsync(audioData, sampleRate);

Console.WriteLine($"RMS:           {spectrum.RmsLevel:F1} dBFS");
Console.WriteLine($"Peak:          {spectrum.PeakLevel:F1} dBFS");
Console.WriteLine($"Loudness:      {spectrum.Loudness:F1} LUFS");
Console.WriteLine($"Dynamic range: {spectrum.DynamicRange:F1} dB");

// 30 ISO 1/3-octave bands from 25 Hz to 20 kHz
for (int i = 0; i < spectrum.FrequencyBands.Length; i++)
    Console.WriteLine($"  Band {i,2}: {spectrum.FrequencyBands[i]:F2} dB");
```

### AudioSpectrum

| Property | Type | Description |
|---|---|---|
| `FrequencyBands` | `float[30]` | Per-band energy (logarithmically spaced, 25 Hz – 20 kHz) |
| `RmsLevel` | `float` | RMS level in dBFS |
| `PeakLevel` | `float` | True peak in dBFS |
| `Loudness` | `float` | Integrated loudness in LUFS |
| `DynamicRange` | `float` | Dynamic range in dB |

---

## 6. EQ Matching

`AudioAnalyzer.CalculateEqAdjustmentsAsync` returns per-band gain corrections (in dB) that bring a source spectrum in line with a target.

```csharp
using OwnAudio.ML;

float[] myMix      = LoadAudio("my_mix.wav",     out int sr1);
float[] reference  = LoadAudio("reference.wav",  out int sr2);

AudioSpectrum srcSpectrum = await AudioAnalyzer.AnalyzeAsync(myMix,     sr1);
AudioSpectrum tgtSpectrum = await AudioAnalyzer.AnalyzeAsync(reference, sr2);

float[] eqGains = await AudioAnalyzer.CalculateEqAdjustmentsAsync(srcSpectrum, tgtSpectrum);

// eqGains[i] = how many dB to boost/cut band i on the source to match the target
for (int i = 0; i < eqGains.Length; i++)
    Console.WriteLine($"  Band {i,2}: {eqGains[i]:+0.0;-0.0} dB");
```

---

## 7. Error Handling

All inference methods throw `OwnAudioMlException` on failure.

```csharp
using OwnAudio.ML;

try
{
    SeparationResult result = await VocalSeparator.SeparateAsync(audioData, sampleRate);
}
catch (OwnAudioMlException ex)
{
    Console.WriteLine($"ML error: {ex.Message}");
}
```

---

## 8. Full example — Vocal separation with progress

```csharp
using OwnAudio.ML;

// 1. Ensure models are downloaded
await ModelManager.EnsureModelsAsync(
    ModelManager.DefaultModelDirectory,
    new Progress<ModelDownloadProgress>(p =>
        Console.Write($"\r{p.ModelName}: {p.Fraction:P0}   ")));
Console.WriteLine();

// 2. Initialise native runtime
ModelManager.Initialize(ModelManager.DefaultModelDirectory);

// 3. Load audio (your own loader)
float[] audio = LoadStereoWav("song.wav", out int sampleRate);

// 4. Separate
Console.WriteLine("Separating...");
SeparationResult stems = await VocalSeparator.SeparateAsync(audio, sampleRate);

// 5. Save results
SaveStereoWav("vocals.wav",       stems.Vocals,       sampleRate);
SaveStereoWav("instrumental.wav", stems.Instrumental, sampleRate);

Console.WriteLine("Done.");

// 6. Cleanup
ModelManager.Shutdown();
```

---

## Platform support

| Platform | Vocal Sep. | Chord Det. | Spectrum | EQ Match |
|---|---|---|---|---|
| Windows (x64, ARM64) | ✅ | ✅ | ✅ | ✅ |
| macOS (x64, ARM64) | ✅ | ✅ | ✅ | ✅ |
| Linux (x64, ARM64) | ✅ | ✅ | ✅ | ✅ |

Chord detection and spectrum analysis work **without any model files** (pure DSP).  
Vocal separation requires `htdemucs.onnx` to be loaded via `ModelManager`.

## AOT / Trimming

- All P/Invoke uses `[LibraryImport]` (source-generated, AOT-safe)
- No reflection, no `Assembly.Load`, no `Activator.CreateInstance`
- `IsAotCompatible=true` and `IsTrimmable=true` set in the `.csproj`
- The native `ownaudio_ml` library wraps ONNX Runtime via its stable C API — the managed layer stays fully AOT-clean
