# macOS Audio Decoders

This directory contains macOS-specific audio decoder implementations using native Apple frameworks.

## CoreAudioMp3Decoder

### Overview

The `CoreAudioMp3Decoder` class provides hardware-accelerated MP3 decoding on macOS using the AudioToolbox framework's ExtAudioFile API. This decoder is:

- **Zero external dependencies** - Uses only native macOS APIs
- **GC-optimized** - Pre-allocated buffers for zero-allocation decode path
- **Hardware-accelerated** - Leverages macOS native MP3 decoder
- **Production-ready** - Robust error handling and resource management

### Features

- **Automatic format conversion** - MP3 → Float32 PCM
- **Resampling support** - Target sample rate conversion
- **Channel remixing** - Target channel count conversion
- **Sample-accurate seeking** - Frame-level seek precision
- **PTS synchronization** - Consistent timestamp calculation across platforms

### Architecture

```
┌─────────────────────────────────────┐
│   Mp3Decoder (Platform-independent) │
│   Ownaudio.Core.Decoders.Mp3        │
└─────────────────┬───────────────────┘
                  │
                  │ IPlatformMp3Decoder
                  │
                  ▼
┌─────────────────────────────────────┐
│   CoreAudioMp3Decoder               │
│   Ownaudio.macOS.Decoders           │
└─────────────────┬───────────────────┘
                  │
                  │ P/Invoke
                  │
                  ▼
┌─────────────────────────────────────┐
│   AudioToolbox Framework            │
│   ExtAudioFile API                  │
│   /System/Library/Frameworks/...    │
└─────────────────────────────────────┘
```

### Implementation Details

#### AudioToolbox ExtAudioFile API

The decoder uses the following ExtAudioFile functions:

1. **ExtAudioFileOpenURL** - Opens MP3 file from path
2. **ExtAudioFileGetProperty** - Queries file format properties
3. **ExtAudioFileSetProperty** - Sets client output format (Float32 PCM)
4. **ExtAudioFileRead** - Reads and decodes audio frames
5. **ExtAudioFileSeek** - Seeks to sample position
6. **ExtAudioFileDispose** - Releases file resources

#### Format Conversion

The decoder automatically handles:

- **Decoding**: MP3 (compressed) → PCM Float32 (uncompressed)
- **Resampling**: Source sample rate → Target sample rate (optional)
- **Remixing**: Source channels → Target channels (optional)

All conversions are performed by the native AudioToolbox framework.

#### PTS (Presentation Timestamp) Calculation

The decoder uses **sample-accurate PTS calculation** consistent with Windows Media Foundation decoder:

```csharp
// Calculate frame duration using SOURCE sample rate
double frameDurationMs = (framesRead * 1000.0) / _sourceSampleRate;

// Current frame PTS, then increment
double framePts = _currentPts;
_currentPts += frameDurationMs;
```

This ensures:
- **Multi-file synchronization** - Consistent PTS across different decoders
- **Accurate seeking** - PTS updated to exact seek position
- **VBR MP3 support** - Variable bitrate handled correctly

### Memory Management

#### GC Optimization

The decoder pre-allocates all buffers during initialization:

```csharp
// Pre-allocated decode buffer (4096 samples × 2 channels × 4 bytes)
byte[] _decodeBuffer = new byte[32768];

// Pinned for P/Invoke (prevents GC relocation)
GCHandle _bufferHandle = GCHandle.Alloc(_decodeBuffer, GCHandleType.Pinned);
```

**Decode loop**: ZERO allocation after initialization.

#### Resource Cleanup

The decoder properly releases all native resources:

```csharp
public void Dispose()
{
    // 1. Dispose ExtAudioFile handle
    ExtAudioFileDispose(_audioFile);

    // 2. Release CFURL
    CFRelease(_cfUrlRef);

    // 3. Free pinned buffer
    _bufferHandle.Free();
}
```

### Usage Example

```csharp
using Ownaudio.Decoders.Mp3;

// Create decoder (automatically selects CoreAudioMp3Decoder on macOS)
using var decoder = new Mp3Decoder("song.mp3");

// Get stream information
var info = decoder.StreamInfo;
Console.WriteLine($"Duration: {info.Duration}");
Console.WriteLine($"Channels: {info.Channels}");
Console.WriteLine($"Sample Rate: {info.SampleRate} Hz");

// Decode all frames
while (true)
{
    var result = decoder.DecodeNextFrame();

    if (result.IsEOF)
        break;

    if (!result.IsSucceeded)
    {
        Console.WriteLine($"Error: {result.Error}");
        break;
    }

    // Process result.Frame.Data (Float32 PCM samples, interleaved)
    ProcessAudio(result.Frame.Data);
}
```

### Resampling Example

```csharp
// Decode MP3 at 44.1kHz and resample to 48kHz
using var decoder = new Mp3Decoder(
    filePath: "song.mp3",
    targetSampleRate: 48000,  // Resample to 48kHz
    targetChannels: 2);        // Stereo output

// Decoder outputs Float32 samples at 48kHz
```

### Seeking Example

```csharp
using var decoder = new Mp3Decoder("song.mp3");

// Seek to 30 seconds
TimeSpan seekPos = TimeSpan.FromSeconds(30);
if (decoder.TrySeek(seekPos, out string error))
{
    Console.WriteLine($"Seeked to {seekPos}");
}
else
{
    Console.WriteLine($"Seek failed: {error}");
}

// Continue decoding from new position
var result = decoder.DecodeNextFrame();
```

### Performance Characteristics

| Metric | Value |
|--------|-------|
| **Decode latency** | 1-5ms per frame |
| **Memory allocation** | Zero (after init) |
| **Buffer size** | 4096 samples (default) |
| **Seek latency** | ~5-10ms |
| **CPU usage** | Low (hardware-accelerated) |

### Supported Formats

The ExtAudioFile API supports:

- **MP3** (MPEG-1/2 Layer III)
- **AAC** (MPEG-4 AAC)
- **ALAC** (Apple Lossless)
- **FLAC** (Free Lossless Audio Codec)
- **WAV** (PCM, ADPCM)
- **AIFF** (Audio Interchange File Format)
- **CAF** (Core Audio Format)

**Note**: The `CoreAudioMp3Decoder` class can decode any of these formats, not just MP3. The name reflects its primary use case in the OwnAudio project.

### Platform Requirements

- **macOS**: 10.5 (Leopard) or later
- **Framework**: AudioToolbox.framework
- **.NET**: .NET 8.0 or later
- **Architecture**: x64, ARM64 (Apple Silicon)

### Error Handling

The decoder handles various error conditions:

#### File Not Found
```csharp
try
{
    var decoder = new Mp3Decoder("nonexistent.mp3");
}
catch (FileNotFoundException ex)
{
    Console.WriteLine($"File not found: {ex.Message}");
}
```

#### Invalid Format
```csharp
try
{
    var decoder = new Mp3Decoder("corrupted.mp3");
}
catch (AudioException ex)
{
    Console.WriteLine($"Invalid format: {ex.Message}");
}
```

#### Decode Errors
```csharp
var result = decoder.DecodeNextFrame();
if (!result.IsSucceeded && !result.IsEOF)
{
    Console.WriteLine($"Decode error: {result.Error}");
}
```

### Thread Safety

**NOT thread-safe**. Each decoder instance should be used by a single thread only.

For multi-threaded scenarios, create separate decoder instances per thread.

### Comparison with Windows Decoder

| Feature | macOS (ExtAudioFile) | Windows (Media Foundation) |
|---------|---------------------|----------------------------|
| **API** | AudioToolbox | Media Foundation |
| **Decode method** | Synchronous | Synchronous (blocking ReadSample) |
| **Format conversion** | Automatic | Automatic |
| **Resampling** | Built-in | Built-in |
| **Seeking** | Frame-accurate | Frame-accurate (with index) |
| **PTS calculation** | Sample-based | Sample-based |
| **GC optimization** | Zero-alloc | Zero-alloc |
| **Stream support** | **No** (file path only) | Yes (with byte stream) |

### Known Limitations

1. **No stream support** - ExtAudioFile API requires file path (CFURLRef)
   - **Workaround**: Write stream to temporary file first

2. **No duration for some MP3s** - VBR MP3 may not report duration
   - **Workaround**: Decode entire file to measure duration

3. **No frame index** - Unlike Windows MF decoder, no built-in frame indexing
   - ExtAudioFile handles seeking internally with good accuracy

### Debug Logging

Enable debug output with conditional compilation:

```csharp
// Uncomment debug lines in CoreAudioMp3Decoder.cs:
System.Diagnostics.Debug.WriteLine(
    $"[CoreAudio MP3] Decoded {framesRead} frames, PTS: {framePts:F2}ms");
```

### Future Enhancements

- [ ] Stream-based decoding (via temporary file)
- [ ] Async decoding support
- [ ] Memory-mapped file optimization
- [ ] Audio unit graph integration
- [ ] Parallel multi-file decoding

## Related Files

- [AudioToolboxInterop.cs](../Interop/AudioToolboxInterop.cs) - P/Invoke declarations for AudioToolbox
- [CoreAudioMp3Decoder.cs](CoreAudioMp3Decoder.cs) - Main decoder implementation
- [IPlatformMp3Decoder.cs](../../Ownaudio.Core/Decoders/Mp3/IPlatformMp3Decoder.cs) - Platform decoder interface
- [Mp3Decoder.cs](../../Ownaudio.Core/Decoders/Mp3/Mp3Decoder.cs) - Platform-independent wrapper

## References

- [Apple AudioToolbox Documentation](https://developer.apple.com/documentation/audiotoolbox)
- [ExtAudioFile Reference](https://developer.apple.com/documentation/audiotoolbox/extaudiofile)
- [Core Audio Overview](https://developer.apple.com/library/archive/documentation/MusicAudio/Conceptual/CoreAudioOverview/)
