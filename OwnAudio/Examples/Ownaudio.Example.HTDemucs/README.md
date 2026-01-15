# HTDemucs Audio Stem Separation Example

This example demonstrates how to use the **HTDemucsAudioSeparator** class to separate audio files into individual stems (vocals, drums, bass, and other instruments).

## What is HTDemucs?

HTDemucs (Hybrid Transformer Demucs) is a state-of-the-art neural network for music source separation. It can separate a mixed audio track into:

- **Vocals**: Singing and speech
- **Drums**: Percussion instruments
- **Bass**: Bass guitar and low-frequency instruments
- **Other**: All other instruments (guitars, keyboards, strings, etc.)

## Model Setup

The HTDemucs model is **embedded as a resource** in the OwnaudioNET assembly. You don't need to download or manage the model file separately!

### Using the Embedded Model (Recommended)

```csharp
var options = new HTDemucsSeparationOptions
{
    Model = InternalModel.HTDemucs,  // Use embedded resource
    OutputDirectory = "output",
    // ... other options
};
```

### Using an External Model File (Optional)

If you have a custom HTDemucs ONNX model:

```csharp
var options = new HTDemucsSeparationOptions
{
    ModelPath = @"path/to/custom/htdemucs.onnx",
    Model = InternalModel.None,  // Don't use embedded model
    OutputDirectory = "output",
    // ... other options
};
```

## Requirements

1. **Audio File**: Any supported audio format (MP3, WAV, FLAC)

2. **GPU (Optional)**: CUDA or DirectML for faster processing
   - CPU processing works but is slower (~10-15x realtime)
   - GPU processing is much faster (~50-100x realtime)

## Usage

### 1. Update Configuration

Edit `Program.cs` and update the audio file path:

```csharp
string audioFilePath = @"path/to/your/audio/music.mp3";
string outputDirectory = @"output_htdemucs";
```

### 2. Run the Example

```bash
dotnet run
```

### 3. Output

The program will create separate WAV files for each stem in the output directory:

```
output_htdemucs/
├── music_vocals.wav
├── music_drums.wav
├── music_bass.wav
└── music_other.wav
```

## Configuration Options

### HTDemucsSeparationOptions

```csharp
var options = new HTDemucsSeparationOptions
{
    Model = InternalModel.HTDemucs,  // Use embedded model
    OutputDirectory = "output",
    ChunkSizeSeconds = 10,           // Chunk size (10-30s recommended)
    OverlapFactor = 0.25f,           // Overlap between chunks (0.25 = 25%)
    EnableGPU = true,                // Use GPU acceleration
    TargetStems = HTDemucsStem.All   // Which stems to extract
};
```

### Stem Selection

You can choose which stems to extract:

```csharp
// Extract only vocals and other
options.TargetStems = HTDemucsStem.Vocals | HTDemucsStem.Other;

// Extract only drums
options.TargetStems = HTDemucsStem.Drums;

// Extract all stems (default)
options.TargetStems = HTDemucsStem.All;
```

### Chunk Size

- **Smaller chunks (5-10s)**: Lower memory usage, slightly slower
- **Larger chunks (20-30s)**: Higher memory usage, slightly faster
- **Recommended**: 10 seconds for most use cases

### GPU Acceleration

The separator automatically tries to use GPU acceleration:

1. **CUDA** (NVIDIA GPUs) - First choice
2. **DirectML** (Windows, any GPU) - Second choice
3. **CPU** - Fallback

Set `EnableGPU = false` to force CPU processing.

## Helper Methods

The library provides convenient helper methods:

### Using Embedded Model

```csharp
// Create default separator (all stems)
using var separator = HTDemucsExtensions.CreateDefaultSeparator("output_directory");
separator.Initialize();
var result = separator.Separate("music.mp3");

// Create selector for specific stems
using var separator = HTDemucsExtensions.CreateStemSelector(
    HTDemucsStem.Vocals | HTDemucsStem.Other,
    "output_directory"
);
```

### Using External Model File

```csharp
using var separator = HTDemucsExtensions.CreateFromFile(
    "path/to/htdemucs.onnx",
    "output_directory"
);
```

## Progress Tracking

The example includes progress tracking:

```csharp
separator.ProgressChanged += (s, progress) =>
{
    Console.WriteLine($"{progress.Status}: {progress.OverallProgress:F1}%");
    Console.WriteLine($"Chunks: {progress.ProcessedChunks}/{progress.TotalChunks}");
};

separator.ProcessingCompleted += (s, result) =>
{
    Console.WriteLine($"Completed in {result.ProcessingTime}");
    Console.WriteLine($"Extracted {result.StemCount} stems");
};
```

## Performance

Typical performance on various hardware:

| Hardware | Processing Speed | Example (3 min song) |
|----------|------------------|---------------------|
| CPU (16 cores) | 10-15x realtime | ~12-18 seconds |
| GPU (NVIDIA RTX 3060) | 50-100x realtime | ~2-4 seconds |
| GPU (NVIDIA RTX 4090) | 100-150x realtime | ~1-2 seconds |

## Memory Usage

- **Chunk-based processing**: ~500-800 MB for 10s chunks
- **Full song**: Memory scales with chunk size, not total duration
- **4 stems output**: Each stem is same size as input audio

## Code Architecture

The implementation uses OwnAudioEngine's high-performance converters:

- **AudioDecoderFactory**: Loads and decodes audio files
- **AudioFormatConverter**: Resamples and converts channels
- **SimdAudioConverter**: SIMD-accelerated PCM to Float32 conversion

Key features:

- **Embedded model**: No external model file needed
- **Streaming processing**: Processes audio in chunks to minimize memory
- **Overlap-add reconstruction**: Smooth transitions between chunks
- **Zero-allocation design**: Reuses buffers to reduce GC pressure
- **Lock-free buffers**: Thread-safe audio processing

## Troubleshooting

### "Model is not set" or "Model file not found"

- If using embedded model: Ensure `Model = InternalModel.HTDemucs` is set
- If using external model: Check that `ModelPath` points to a valid .onnx file

### "Input file not found"

- Check the audio file path is correct
- Supported formats: MP3, WAV, FLAC

### "Out of memory"

- Reduce `ChunkSizeSeconds` (try 5 seconds)
- Close other applications
- Use a 64-bit build

### Slow processing

- Enable GPU acceleration: `EnableGPU = true`
- Install CUDA toolkit for NVIDIA GPUs
- Use larger chunks if you have enough memory

### Poor separation quality

- Ensure you're using a proper HTDemucs ONNX model
- Check that the embedded model was correctly included in the build
- Some audio (highly compressed, very old recordings) may not separate well

## Model Information

The embedded HTDemucs model is automatically included in the OwnaudioNET assembly as an embedded resource. This means:

✅ No need to download or manage model files
✅ Works immediately after installation
✅ Included in the NuGet package
✅ Portable across platforms

If you need to use a different or updated HTDemucs model, you can always provide an external model file path using the `ModelPath` option.

## License

This example is part of OwnAudioSharp and is licensed under the MIT License.

## Credits

- **HTDemucs Model**: Facebook Research (Hybrid Transformer Demucs)
- **OwnAudioSharp**: ModernMube
- **ONNX Runtime**: Microsoft
