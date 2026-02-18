# OwnAudioSharp - Multi-Model Audio Separator Example

This example demonstrates how to use the **Multi-Model Audio Separator** feature in OwnAudioSharp to process audio through multiple UVR MDX models in parallel and average their results for superior quality.

## 🎯 What is Multi-Model Averaging?

Multi-model processing in OwnAudioSharp uses an **averaging pipeline**. Instead of chaining models where one model's output is the next one's input, all models process the **original audio independently** in parallel. Their outputs (vocals and instrumentals) are then mathematically averaged together.

This technique is powerful because different models often have different "blind spots" or artifacts. By averaging them, you can cancel out specific artifacts and achieve a result that is cleaner and more balanced than any single model could produce.

### How it Works (Visualized)

```
                Original Mix
                     │
        ┌────────────┼────────────┐
        ↓            ↓            ↓
   ┌────────┐  ┌────────┐  ┌────────┐
   │Model 1 │  │Model 2 │  │Model 3 │  ← All process original
   │ (Best) │  │(Default)│  │(Karaoke)│     independently
   └────────┘  └────────┘  └────────┘
        │            │            │
        ↓            ↓            ↓
    V₁ + I₁      V₂ + I₂      V₃ + I₃
        │            │            │
        └────────────┼────────────┘
                     ↓
              ┌─────────────┐
              │  AVERAGING  │
              └─────────────┘
                     │
        ┌────────────┴────────────┐
        ↓                         ↓
  Vocals_avg           Instrumental_avg
  (V₁+V₂...)/N         (I₁+I₂...)/N
```

### Common Use Cases

1. **Vocal Refinement**: Average multiple vocal models to reduce "metallic" artifacts or robotic sounds.
2. **Instrumental Cleaning**: Combine several instrumental models to get a backing track with minimal vocal bleed.
3. **Specialized Combination**: Mix a vocal-focused model with an instrumental-focused model to get the "best of both worlds".

## 🚀 Running the Examples

### Quick Start

```bash
cd OwnAudio/Examples/Ownaudio.Example.MultimodelSeparator
dotnet run
```

The program will prompt you to choose one of the example pipelines.

### With Command-Line Arguments

```bash
# Run with custom input and output paths
dotnet run "path/to/song.mp3" "path/to/output"

# Show help
dotnet run --help
```

## 📚 Examples Included

### Example 1: Simple 2-Model Averaging

The easiest way to get started. Uses a helper method to average results from two models.

```csharp
var separator = MultiModelExtensions.CreateSimplePipeline(
    model1: InternalModel.Best,
    model2: InternalModel.Karaoke,
    outputDirectory: "output"
);

separator.Initialize();
var result = separator.Separate("song.mp3");
```

**Use case:** Basic two-model averaging for improved quality.

### Example 2: Triple Model Averaging

Demonstrates a three-model pipeline with all intermediate results saved for comparison.

```csharp
var separator = MultiModelExtensions.CreateTriplePipeline(
    model1: InternalModel.Best,
    model2: InternalModel.Default,
    model3: InternalModel.Karaoke,
    outputDirectory: "output"
);
```

**Use case:** High-quality averaging with debugging outputs.

### Example 3: Custom Averaging with Debug Mode

Full control over every aspect of the averaging, including per-model settings and specific intermediate saves.

```csharp
var options = new MultiModelSeparationOptions
{
    Models = new List<MultiModelInfo>
    {
        new MultiModelInfo
        {
            Name = "VocalExtraction",
            Model = InternalModel.Best,
            NFft = 6144,
            SaveIntermediateOutput = true
        },
        new MultiModelInfo
        {
            Name = "Enhancement",
            Model = InternalModel.Default,
            SaveIntermediateOutput = true
        }
    },
    SaveAllIntermediateResults = true
};
```

**Use case:** Production pipelines requiring fine-grained control and artifact analysis.

### Example 4: Custom Model Files

Shows how to use your own ONNX model files from disk.

```csharp
var options = new MultiModelSeparationOptions
{
    Models = new List<MultiModelInfo>
    {
        new MultiModelInfo
        {
            Name = "CustomModel1",
            ModelPath = "models/Voc_FT.onnx"
        },
        new MultiModelInfo
        {
            Name = "CustomModel2",
            ModelPath = "models/Inst_HQ_3.onnx"
        }
    }
};
```

**Use case:** Using custom-trained or community models not embedded in the library.

### Example 5: Averaging Demo with Auto-Detection

This demo shows how the system automatically detects whether a model outputs vocals or instrumentals based on its name or metadata.

### Example 6: Mixed OutputType Demo

Demonstrates how to explicitly combine models with different outputs (e.g., one vocal-focused and one instrumental-focused).

```csharp
new MultiModelInfo
{
    Name = "VocalModel",
    ModelPath = "path/to/vocal_model.onnx",
    OutputType = ModelOutputType.Vocals // Explicitly set output stem
},
new MultiModelInfo
{
    Name = "InstrumentalModel",
    ModelPath = "path/to/inst_model.onnx",
    OutputType = ModelOutputType.Instrumental
}
```

## ⚙️ Configuration Options

### MultiModelSeparationOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Models` | `List<MultiModelInfo>` | Required | List of models to process in sequence |
| `OutputDirectory` | `string` | `"separated_multimodel"` | Output directory for results |
| `EnableGPU` | `bool` | `true` | Enable GPU acceleration |
| `ChunkSizeSeconds` | `int` | `15` | Chunk size in seconds |
| `Margin` | `int` | `44100` | Margin size for overlapping chunks |
| `SaveAllIntermediateResults` | `bool` | `false` | Save output after each model |

### MultiModelInfo (Per-Model Settings)

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | `string` | `"Model"` | Display name for this model |
| `Model` | `InternalModel` | `None` | Embedded model to use |
| `ModelPath` | `string?` | `null` | Path to custom ONNX model file |
| `OutputType` | `ModelOutputType?` | `null` | `Vocals` or `Instrumental` (auto-detected if null) |
| `NFft` | `int` | `6144` | FFT size (auto-detected if 0) |
| `DimT` | `int` | `8` | Temporal dimension (power of 2) |
| `DimF` | `int` | `2048` | Frequency dimension |
| `DisableNoiseReduction` | `bool` | `false` | Disable noise reduction pass |
| `SaveIntermediateOutput` | `bool` | `false` | Save output from this model |

## 📊 Progress Tracking

Subscribe to events for real-time progress updates:

```csharp
separator.ProgressChanged += (sender, progress) =>
{
    Console.WriteLine($"[{progress.CurrentModelName}] " +
                      $"Chunk {progress.ProcessedChunks}/{progress.TotalChunks} " +
                      $"({progress.OverallProgress:F1}%)");
};

separator.ProcessingCompleted += (sender, result) =>
{
    Console.WriteLine($"Completed in {result.ProcessingTime}");
    Console.WriteLine($"Output: {result.OutputPath}");
};
```

## 🎛️ Performance Characteristics

### Memory Usage
- **Streaming**: Uses a streaming pipeline to process audio in small chunks, keeping memory footprint low.
- **Per Model**: ~500-800 MB for 15-second chunks.
- **Sequential Loading**: Models are loaded and processed one by one against the original audio to save memory.

### Processing Speed
- **CPU**: ~10-15x realtime per model (on modern hardware).
- **GPU**: ~50-100x realtime per model.
- **Total Time**: Sum of all models' processing times + minor overhead for averaging.

### Recommended Settings

**For Quality:**
```csharp
ChunkSizeSeconds = 15,
Margin = 44100,  // 1 second margin for smooth blending
EnableGPU = true
```

**For Speed:**
```csharp
ChunkSizeSeconds = 10,
Margin = 22050,  // 0.5 second margin
EnableGPU = true
```

**For Memory-Constrained Systems:**
```csharp
ChunkSizeSeconds = 5,
Margin = 11025,  // 0.25 second margin
```

## 🔧 Troubleshooting

### "Model file not found"
- Ensure you're using valid `InternalModel` enum values or valid paths to `.onnx` files.

### Out of Memory
- Reduce `ChunkSizeSeconds` (e.g., from 15 to 10).
- Reduce `Margin` size.

### Slow Performance
- Ensure GPU acceleration is enabled: `EnableGPU = true`.
- CoreML is used on macOS, CUDA on Windows/Linux.

### Unexpected Results
- Check the `OutputType` of your models. If auto-detection fails, explicitly set it to `Vocals` or `Instrumental`.
- Save intermediate results (`SaveAllIntermediateResults = true`) to see which model in the average is causing issues.

## 📝 Notes

- **Model Compatibility**: Only UVR MDX-style models are supported (STFT-based)
- **Input Formats**: WAV, MP3, FLAC (automatically resampled to 44.1kHz)
- **Output Format**: 16-bit stereo WAV at 44.1kHz
- **GPU Support**: CUDA on Windows/Linux, CoreML on macOS

## 🔗 Related Examples

- [Ownaudio.Example.VocalRemover](../Ownaudio.Example.VocalRemover/) - Single model separation
- [Ownaudio.Example.ChordDetect](../Ownaudio.Example.ChordDetect/) - Chord detection
- [Ownaudio.Example.Matching](../Ownaudio.Example.Matching/) - Audio matchering

## 📖 Further Reading

- [OwnAudioSharp Documentation](https://modernmube.github.io/OwnAudioSharp/)
- [CLAUDE.md](../../../CLAUDE.md) - Development guidelines
- [Main README](../../../README.md) - Project overview
