<div align="center">
  <img src="Ownaudiologo.png" alt="Logo" width="600"/>
</div>

<a href="https://www.buymeacoffee.com/ModernMube">
  <img src="https://img.shields.io/badge/Support-Buy%20Me%20A%20Coffee-orange" alt="Buy Me a Coffee">
</a>

<a href="https://www.nuget.org/packages/OwnAudioSharp">
  <img src="https://img.shields.io/badge/Nuget-OwnAudioSharp%20Nuget%20Package-blue" alt="OwnAudioSharp Package">
</a>

<a href="https://github.com/ModernMube/OwnAudioSharpDemo">
  <img src="https://img.shields.io/badge/Sample-OwnAudioSharp%20Demo%20Application-darkgreen" alt="OwnAudioSharp Package">
</a>

##

OwnAudio is a platform-independent C# audio library that provides a high-level API for audio playback, recording, and processing. By default, it uses Miniaudio for audio I/O. If FFmpeg or PortAudio is installed, it automatically uses Portaudio and FFmpeg. This way, it can work with MiniAudio without any external dependencies. The implementation of MiniAudio also allowed the API to be used on mobile platforms. It is possible to manipulate audio data in real time (pitch change, tempo change, and various real-time effects). The API is able to detect musical chords from audio and create a timed list of chords. A feature has been built in to help game developers manage sound effects.

## Features

- **Cross-platform** compatibility (Windows, macOS, Linux, Android, iOS)
- **Audio playbook** with support for various formats via FFmpeg, or MiniAudio (mp3, wav, flac) formats
- **Audio recording** capabilities through input devices
- **Time stretching and pitch shifting** using SoundTouch
- **Mixing** multiple audio sources
- **Volume control** and custom audio processing
- **Seeking** within audio files
- **Real-time audio processing** with custom sample processors
- **Audio data visualize** customizable waveform display

# 🎵 NEW: Professional Audio Matchering in OwnAudioSharp!

**Studio-grade mastering with advanced AI-driven analysis - single line of code!**

```csharp
analyzer.ProcessEQMatching("source.wav", "reference.wav", "mastered.wav");
```

⚡ **What you get:**
- Intelligent 10-band EQ matching
- Multiband compression across 4 frequency bands  
- Psychoacoustic weighting and spectral masking
- Distortion-protected automatic processing

🎯 **Result:** Your source audio will sound exactly like the reference track - professional mastering studio quality.

📖 [Complete documentation and examples](#matchering)

The table below summarizes the supported operating systems, the APIs used, and their testing status.

| System     | APIs                           | Status       |
|------------|--------------------------------|--------------|
| Windows    | PortAudio 2, MiniAudio, FFmpeg 6 | Tested       |
| Linux      | PortAudio 2, MiniAudio, FFmpeg 6 | Tested       |
| macOS      | PortAudio 2, MiniAudio, FFmpeg 6 | Tested       |
| Android    | MiniAudio                      | This project is tested with BrowserStack  |
| iOS        | MiniAudio                      | This project is tested with BrowserStack  |

The library will attempt to find these dependencies in standard system locations but also supports specifying custom paths.

## Installation

You can add this library to your project via NuGet (when published) or by directly referencing the project.

### Required Libraries

You will find the required files in the LIBS folder in a compressed file. 
Extract the package appropriate for your operating system into the folder containing the compressed file.
Depending on your operating system, you will need the following:

#### Windows
- FFmpeg libraries (avcodec, avformat, avutil, etc.)
- portaudio.dll

#### macOS
- FFmpeg libraries (libavcodec.dylib, libavformat.dylib, libavutil.dylib, etc.)
- libportaudio.dylib

#### Linux
- FFmpeg libraries (libavcodec.so, libavformat.so, libavutil.so, etc.)
- libportaudio.so.2

---

## Support My Work

If you find this project helpful, consider buying me a coffee!

<a href="https://www.buymeacoffee.com/ModernMube" 
    target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/arial-yellow.png" 
    alt="Buy Me A Coffee" 
    style="height: 60px !important;width: 217px !important;" >
 </a>

## Basic Usage

Here's a quick example of how to use OwnAudio to play an audio file:

```csharp
using Ownaudio;
using Ownaudio.Sources;
using System;
using System.Threading.Tasks;

try 
{
    // Initialize OwnAudio
    OwnAudio.Initialize();

    // Create a source manager
    var sourceManager = SourceManager.Instance;

    // Add an audio file
    await sourceManager.AddOutputSource("path/to/audio.mp3");

    // Play the audio
    sourceManager.Play();

    // Wait for the audio to finish
    Console.WriteLine("Press any key to stop playback...");
    Console.ReadKey();

    // Stop playback and clean up
    sourceManager.Stop();
}
catch (Exception ex)
{
    Console.WriteLine($"Audio error: {ex.Message}");
}
finally
{
    OwnAudio.Free();
}
```

## Advanced Features

### Mixing Multiple Audio Sources

```csharp
using Ownaudio;
using Ownaudio.Sources;

// Add multiple audio files
await sourceManager.AddOutputSource("path/to/audio1.mp3", "Track1Name");
await sourceManager.AddOutputSource("path/to/audio2.mp3", "Track2Name");

// Adjust volume for individual sources
sourceManager["Track1Name"].Volume = 0.8f;  // 80% volume for first source
sourceManager["Track2Name"].Volume = 0.6f;  // 60% volume for second source

// Play mixed audio
sourceManager.Play();
```

### Audio Recording

```csharp
// Add an input source
await sourceManager.AddInputSource();

// Start recording to file
sourceManager.Play("output.wav", 16);  // 16-bit recording
```

### Time Stretching and Pitch Shifting

```csharp
// Change tempo without affecting pitch (value range -20 to +20)
sourceManager["Track1Name"].Tempo = 10.0;  // Speed up by 10%

// Change pitch without affecting tempo (value range -6 to +6 semitones)
sourceManager["Track1Name"].Pitch = 2.0;  // Raise pitch by 2 semitones
```

### Seeking Within Audio

```csharp
// Seek to a specific position
sourceManager.Seek(TimeSpan.FromSeconds(30));  // Seek to 30 seconds
```

## Audio Processing

- **Reverb**: Professional quality reverb based on Freeverb algorithm
- **Delay**: Echo effect with feedback control
- **Distortion**: Overdrive and soft clipping
- **Compressor**: Dynamic range compression
- **Equalizer**: 30-band parametric EQ with dynamic Q-factor optimization
- **Chorus**: Multi-voice modulation effect
- **Flanger**: Variable delay modulation
- **Phaser**: All-pass filter stages for phasing effect
- **Rotary**: Rotary speaker simulation
- **DynamicAmp**: Adaptive volume control
- **Enhancer**: Harmonic enhancement
- **Overdrive**: effect with tube-like saturation
- **Limiter**: with look-ahead and smooth gain reduction

## Custom Audio Processing

You can implement custom audio processing by implementing the `SampleProcessorBase` class:

```csharp
using Ownaudio.Processors;

public class MyAudioProcessor : SampleProcessorBase
{
    public override void Process(Span<float> samples)
    {
        // Process audio samples
        for (int i = 0; i < samples.Length; i++)
        {
            // Example: Simple gain adjustment
            samples[i] *= 0.5f;  // 50% volume
        }
    }
    
    public override void Reset()
    {
        // Reset internal state if needed
    }
}

// Apply the processor to source manager
var processor = new MyAudioProcessor();
sourceManager.CustomSampleProcessor = processor;
```

## Audio Data Read

```csharp
using System.Threading.Tasks;

// Add a real-time source
var realtimeSource = sourceManager.AddRealTimeSource(1.0f, 2); // Volume 1.0, stereo

// Submit audio samples in real-time
float[] samples = new float[1024]; // Your generated audio data
realtimeSource.SubmitSamples(samples);
```

### Live Audio Streaming and Real-time Playback

The `SourceSound` class enables real-time audio streaming, perfect for:
- Live audio synthesis
- Network audio streaming
- Real-time audio effects processing
- Dynamic audio generation

```csharp
using Ownaudio;
using Ownaudio.Sources;
using System;
using System.Threading.Tasks;

// Create a real-time audio source
var liveSource = sourceManager.AddRealTimeSource(1.0f, 2); // Volume, channels

// Example: Generate and stream sine wave in real-time
await Task.Run(async () =>
{
    int sampleRate = 44100;
    int frequency = 440; // A4 note
    float amplitude = 0.3f;
    int samplesPerBuffer = 1024;
    
    double phase = 0;
    double phaseIncrement = 2.0 * Math.PI * frequency / sampleRate;
    
    while (liveSource.State != SourceState.Idle)
    {
        float[] buffer = new float[samplesPerBuffer * 2]; // Stereo
        
        for (int i = 0; i < samplesPerBuffer; i++)
        {
            float sample = (float)(Math.Sin(phase) * amplitude);
            buffer[i * 2] = sample;     // Left channel
            buffer[i * 2 + 1] = sample; // Right channel
            
            phase += phaseIncrement;
            if (phase >= 2.0 * Math.PI)
                phase -= 2.0 * Math.PI;
        }
        
        // Submit samples for real-time playback
        liveSource.SubmitSamples(buffer);
        
        // Control timing for smooth playback
        await Task.Delay(10);
    }
});

// Start playback
sourceManager.Play();
```

### Network Audio Streaming Example

```csharp
// Example: Receive audio data from network and play in real-time
var networkSource = sourceManager.AddRealTimeSource(1.0f, 2);

// Network audio receiver (pseudo-code)
networkClient.OnAudioDataReceived += (audioData) =>
{
    // Convert received network data to float array
    float[] samples = ConvertBytesToFloats(audioData);
    
    // Submit to real-time source for immediate playback
    networkSource.SubmitSamples(samples);
};

sourceManager.Play();
```

### Custom Audio Generator

```csharp
using System;
using System.Threading.Tasks;

public class AudioGenerator
{
    private SourceSound _source;
    private int _sampleRate;
    private bool _isGenerating;
    
    public AudioGenerator(SourceManager manager, int sampleRate = 44100)
    {
        _sampleRate = sampleRate;
        _source = manager.AddRealTimeSource(1.0f, 2);
    }
    
    public void StartGeneration()
    {
        _isGenerating = true;
        
        Task.Run(async () =>
        {
            while (_isGenerating)
            {
                float[] audioBuffer = GenerateAudio(1024);
                _source.SubmitSamples(audioBuffer);
                await Task.Delay(5); // Smooth streaming
            }
        });
    }
    
    public void StopGeneration()
    {
        _isGenerating = false;
    }
    
    private float[] GenerateAudio(int samples)
    {
        // Your custom audio generation logic here
        float[] buffer = new float[samples * 2]; // Stereo
        
        // Fill buffer with generated audio data
        for (int i = 0; i < samples; i++)
        {
            float sample = GenerateSample(); // Your generation method
            buffer[i * 2] = sample;     // Left
            buffer[i * 2 + 1] = sample; // Right
        }
        
        return buffer;
    }
    
    private float GenerateSample()
    {
        // Implement your audio generation algorithm
        return 0.0f;
    }
}

// Usage
var generator = new AudioGenerator(sourceManager);
generator.StartGeneration();
sourceManager.Play();
```

## Audio Data Extraction

```csharp
// Load source audio data into a byte array
byte[] audioByte = sourceManager.Sources[0].GetByteAudioData(TimeSpan.Zero);

// Load source audio data into a float array
float[] audioFloat = sourceManager.Sources[0].GetFloatAudioData(TimeSpan.Zero);
```

# Chord Detection

Real-time or offline chord detection from musical notes.

## Core Components

### Detectors
- **BaseChordDetector** - Basic chord detection with major, minor, and 7th chords
- **ExtendedChordDetector** - Adds suspended, diminished, augmented, and add9 chords
- **OptimizedChordDetector** - Advanced detection with ambiguity handling and alternatives
- **RealTimeChordDetector** - Continuous analysis with stability filtering

### Analysis
- **SongChordAnalyzer** - Full song analysis with timed chord progressions
- **ChordAnalysis** - Detailed analysis results with confidence scores and explanations

## Quick Start

```csharp
using Ownaudio.Utilities.OwnChordDetect.Detectors;
using Ownaudio.Utilities.OwnChordDetect.Analysis;
using Ownaudio.Utilities.Extensions;

// Basic chord detection
var detector = new BaseChordDetector(confidenceThreshold: 0.7f);
var analysis = detector.AnalyzeChord(notes);
Console.WriteLine($"Detected: {analysis.ChordName} (confidence: {analysis.Confidence})");

// Extended chord detection with alternatives
var extendedDetector = new OptimizedChordDetector();
var (chord, confidence, isAmbiguous, alternatives) = extendedDetector.DetectChordAdvanced(notes);

// Real-time analysis
var realtimeDetector = new RealTimeChordDetector(bufferSize: 5);
var (stableChord, stability) = realtimeDetector.ProcessNotes(newNotes);

// Full song analysis
var songAnalyzer = new SongChordAnalyzer(windowSize: 1.0f, hopSize: 0.5f);
var timedChords = songAnalyzer.AnalyzeSong(allSongNotes);

// Complete example with SourceManager
var sourceManager = SourceManager.Instance;
await sourceManager.AddOutputSource("music.mp3", "MusicTrack");

// Detect chords from the loaded audio
var (timedChords, detectedKey, tempo) = sourceManager.DetectChords("MusicTrack", intervalSecond: 1.0f);

Console.WriteLine($"Detected Key: {detectedKey}");
Console.WriteLine($"Detected Tempo: {tempo} BPM");

foreach (var chord in timedChords)
{
    Console.WriteLine($"{chord.StartTime:F1}s-{chord.EndTime:F1}s: {chord.ChordName} ({chord.Confidence:F2})");
}
```

## Input Format

The library expects `Note` objects with:
- `Pitch` - MIDI pitch number
- `Amplitude` - Note volume (0.0-1.0)
- `StartTime` / `EndTime` - Timing in seconds

## Key Features

- **Template Matching** - Uses chromagram analysis and cosine similarity
- **Ambiguity Detection** - Identifies uncertain chord matches
- **Temporal Stability** - Real-time filtering for consistent results
- **Extensible Templates** - Add custom chord definitions
- **Song-level Analysis** - Extract complete chord progressions with timing

## Confidence Thresholds

- **0.9+** - Very confident detection
- **0.7+** - Likely correct
- **0.5+** - Possible but uncertain
- **Below 0.5** - Marked as "Unknown"

## WaveAvaloniaDisplay - Audio Visualization

A flexible, resource-efficient audio waveform visualization component for Avalonia applications.

### Key Features

- **Multiple display styles:** MinMax (classic waveform), Positive (half-wave rectified), or RMS (energy representation)
- **Zoom in/out:** Supports zooming for detailed audio inspection
- **Interactive playback position:** Users can change the playback position by clicking or dragging
- **Customizable appearance:** Colors and scaling are fully customizable
- **Optimized performance:** Minimal resource usage even with large audio files
- **File loading:** Direct loading from audio files with automatic format detection

### Usage

The following example demonstrates how to use the `WaveAvaloniaDisplay` component in an Avalonia application:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:audio="using:Ownaudio.Utilities"
        x:Class="MyAudioApp.MainWindow"
        Title="Audio Visualizer" Height="450" Width="800">

    <Grid>
        <audio:WaveAvaloniaDisplay x:Name="waveformDisplay"
                                WaveformBrush="DodgerBlue"
                                PlaybackPositionBrush="Red"
                                VerticalScale="1.0"
                                DisplayStyle="MinMax"/>
    </Grid>
</Window>
```

### C# Code

```csharp
using Ownaudio.Utilities;
using System;
using System.IO;
using System.Threading.Tasks;

// Set audio data from existing float array
waveformDisplay.SetAudioData(sourceManager.Sources[0].GetFloatAudioData(TimeSpan.Zero));

// Handle playback position changes
waveformDisplay.PlaybackPositionChanged += OnPlaybackPositionChanged;

// Load directly from audio file
waveformDisplay.LoadFromAudioFile("audio.mp3");

// Load with specific decoder preference
waveformDisplay.LoadFromAudioFile("audio.mp3", preferFFmpeg: true);

// Asynchronous loading
await waveformDisplay.LoadFromAudioFileAsync("large_audio.wav");

// Loading from stream
using var fileStream = File.OpenRead("audio.mp3");
waveformDisplay.LoadFromAudioStream(fileStream);

private void OnPlaybackPositionChanged(object sender, double position)
{
    // Update the actual audio playback position
    sourceManager.Seek(TimeSpan.FromSeconds(position * sourceManager.Duration.TotalSeconds));
}
```

### Properties

| Property | Type | Description |
|----------|------|-------------|
| WaveformBrush | IBrush | The color of the waveform |
| PlaybackPositionBrush | IBrush | The color of the playback position indicator |
| VerticalScale | double | Vertical scaling of the waveform (1.0 = original size) |
| DisplayStyle | WaveformDisplayStyle | The waveform display style (MinMax, Positive, RMS) |
| ZoomFactor | double | Zoom factor (1.0 = full view, larger values = more detailed view) |
| ScrollOffset | double | Horizontal scroll position (0.0 - 1.0) |
| PlaybackPosition | double | Current playback position (0.0 - 1.0) |

### Events

| Event | Parameter | Description |
|-------|-----------|-------------|
| PlaybackPositionChanged | double | Triggered when the user changes the playback position |

## Architecture

The library follows a layered architecture:

1. **Native Libraries** (FFmpeg & PortAudio/MiniAudio) - Low-level audio I/O and decoding
2. **Decoders** - Audio file decoding (FFmpegDecoder, MiniDecoder)
3. **Sources** - Audio source management (Source, SourceInput, SourceSound)
4. **SourceManager** - Mixing and controlling multiple sources
5. **Processors** - Custom audio processing pipeline
6. **Effects** - Built-in audio effects library
7. **Engines** - Audio engine abstraction (PortAudio/MiniAudio)

## Engine Configuration

You can configure the audio engine with specific parameters:

```csharp
using Ownaudio.Engines;

// Initialize first
OwnAudio.Initialize();

// Configure output engine options
SourceManager.OutputEngineOptions = new AudioEngineOutputOptions(
    OwnAudioEngine.EngineChannels.Stereo, 
    44100, 
    0.02 // Low latency
);

// Configure input engine options
SourceManager.InputEngineOptions = new AudioEngineInputOptions(
    OwnAudioEngine.EngineChannels.Mono, 
    44100, 
    0.02 // Low latency
);

// Set frames per buffer
SourceManager.EngineFramesPerBuffer = 512;
```

# Matchering

The professional audio matchering system in OwnAudioSharp represents a breakthrough in automated mastering technology. Using advanced psychoacoustic analysis and AI-driven processing, it automatically analyzes and adjusts the spectral and dynamic properties of source audio to match the characteristics of reference tracks with studio-grade precision.

## Core Technology

### Advanced Frequency Analysis
- **30-band precision EQ**: High-resolution frequency analysis from 20Hz to 16kHz
- **Overlapped FFT processing**: 87.5% overlap with Blackman-Harris windowing for minimal spectral leakage
- **Adaptive window sizing**: Optimal FFT sizes (4096-16384 samples) based on sample rate
- **Psychoacoustic weighting**: Frequency-specific energy calculations with perceptual modeling

### Intelligent Processing Algorithms
- **Dynamic Q-factor optimization**: Automatic bandwidth adjustment based on correction requirements
- **Spectral balance analysis**: Multi-band energy distribution with intelligent smoothing
- **Neighboring band correlation**: Context-aware frequency adjustments for musical naturalness
- **Safety-first processing**: Built-in distortion protection with frequency-specific limits

## Features

- **30-band intelligent EQ matching**: High-precision frequency spectrum analysis and equalization
- **Dynamic Q-factor adjustment**: Surgical frequency corrections with optimal bandwidth selection
- **Advanced spectral balance**: Multi-range energy analysis with psychoacoustic considerations
- **Playback system presets**: Optimized processing for different listening environments
- **Distortion protection**: Frequency-specific boost limiting with dynamic headroom calculation
- **Professional dynamics control**: Intelligent compression and amplification with temporal stability

## Basic Usage

```csharp
using Ownaudio.Utilities.Matchering;

// Professional audio matchering
var analyzer = new AudioAnalyzer();

// Process source audio to match reference characteristics
analyzer.ProcessEQMatching(
    sourceFile: "input_track.wav",     // Audio to be processed
    targetFile: "reference.wav",       // Professional reference track
    outputFile: "mastered_track.wav"   // Studio-quality output
);

// The system automatically performs:
// 1. Advanced 30-band spectral analysis with psychoacoustic weighting
// 2. Dynamic Q-factor optimization for surgical frequency corrections
// 3. Intelligent EQ curve calculation with spectral balance consideration
// 4. Multi-band compression with frequency-specific settings
// 5. Dynamic amplification with temporal stability analysis
// 6. Distortion-protected output generation with safety limiting
```

## Playback System Presets

Target your master for specific listening environments:

```csharp
// Optimize for different playback systems
analyzer.ProcessWithPreset("source.wav", "hifi_output.wav", PlaybackSystem.HiFiSpeakers);
analyzer.ProcessWithPreset("source.wav", "club_output.wav", PlaybackSystem.ClubPA);
analyzer.ProcessWithPreset("source.wav", "headphones_output.wav", PlaybackSystem.Headphones);
analyzer.ProcessWithPreset("source.wav", "streaming_output.wav", PlaybackSystem.RadioBroadcast);

// Available presets:
// - ConcertPA: Large venue sound reinforcement
// - ClubPA: Dance music optimization with enhanced bass
// - HiFiSpeakers: Neutral response for critical listening
// - StudioMonitors: Reference standard for professional mixing
// - Headphones: Compensated for typical headphone response
// - Earbuds: Enhanced for in-ear acoustics
// - CarStereo: Road noise and cabin acoustics compensation
// - Television: Dialogue clarity and late-night friendly
// - RadioBroadcast: FM/AM transmission standards
// - Smartphone: Small speaker compensation with midrange focus
```

## Advanced Analysis

```csharp
// Detailed spectrum analysis with professional metrics
var sourceSpectrum = analyzer.AnalyzeAudioFile("source.wav");

Console.WriteLine($"RMS level: {sourceSpectrum.RMSLevel:F3}");
Console.WriteLine($"Peak level: {sourceSpectrum.PeakLevel:F3}");
Console.WriteLine($"Dynamic range: {sourceSpectrum.DynamicRange:F1} dB");
Console.WriteLine($"Perceived loudness: {sourceSpectrum.Loudness:F1} LUFS");

// Access 30-band frequency analysis
for (int i = 0; i < sourceSpectrum.FrequencyBands.Length; i++)
{
    var frequencies = new[] {
        20f, 25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f,
        200f, 250f, 315f, 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f,
        2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f, 16000f
    };
    Console.WriteLine($"{frequencies[i]}Hz: {sourceSpectrum.FrequencyBands[i]:F3}");
}
```

## Technical Specifications

### 30-Band Frequency Analysis
- **Extended frequency range**: 20 Hz - 16 kHz with logarithmic distribution
- **High-precision FFT**: Adaptive window sizing (4096-16384 samples)
- **Advanced windowing**: Blackman-Harris window with 87.5% overlap
- **Psychoacoustic modeling**: Frequency-dependent bandwidth calculation
- **Spectral accuracy**: Sub-band energy interpolation with weighted RMS

### Dynamic Q-Factor Optimization
- **Frequency-dependent base Q**: Optimized for psychoacoustic perception
- **Gain-adaptive adjustment**: Surgical corrections for large adjustments
- **Neighboring band correlation**: Context-aware smoothing for musical results
- **Spectral density analysis**: Adaptive bandwidth based on energy distribution

### Advanced Safety Systems
- **Frequency-specific boost limits**: 2.5-5.5 dB maximum depending on frequency range
- **Dynamic headroom calculation**: Crest factor and loudness-based protection
- **Intelligent EQ curve smoothing**: Natural-sounding frequency transitions
- **Multi-stage limiting**: Soft limiting at -0.5dB with clipping detection
- **Spectral balance protection**: Automatic dominance reduction for harsh frequencies

### Professional Dynamics Processing
- **Adaptive compression**: Frequency-band specific threshold and ratio calculation
- **Dynamic amplification**: Temporal stability analysis with conservative gain limiting
- **Loudness matching**: LUFS-based target level adjustment with musical preservation
- **Transient preservation**: Attack/release optimization for different musical content

## Real-World Output Example

```
=== PROFESSIONAL MATCHERING ANALYSIS ===
Source Analysis:
  RMS: 0.123, Peak: 0.876, Loudness: -14.2 LUFS
  Dynamic Range: 12.4 dB, Crest Factor: 15.1 dB

Target Analysis:
  RMS: 0.187, Peak: 0.932, Loudness: -9.8 LUFS
  Dynamic Range: 8.9 dB, Crest Factor: 13.9 dB

30-Band EQ Adjustments (with Q-factor optimization):
20Hz: +2.1 dB (Q=0.35)    25Hz: +2.8 dB (Q=0.38)    31Hz: +3.4 dB (Q=0.42)
40Hz: +2.9 dB (Q=0.45)    50Hz: +2.2 dB (Q=0.48)    63Hz: +1.8 dB (Q=0.52)
80Hz: +1.4 dB (Q=0.55)    100Hz: +0.9 dB (Q=0.58)   125Hz: +0.4 dB (Q=0.62)
160Hz: -0.1 dB (Q=0.65)   200Hz: -0.5 dB (Q=0.68)   250Hz: -0.8 dB (Q=0.72)
315Hz: -0.6 dB (Q=0.78)   400Hz: -0.2 dB (Q=0.85)   500Hz: +0.3 dB (Q=0.92)
630Hz: +0.8 dB (Q=1.02)   800Hz: +1.2 dB (Q=1.15)   1kHz: +1.6 dB (Q=1.28)
1.25kHz: +1.4 dB (Q=1.35) 1.6kHz: +1.1 dB (Q=1.28)  2kHz: +0.8 dB (Q=1.15)
2.5kHz: +0.4 dB (Q=1.08)  3.15kHz: -0.1 dB (Q=1.02) 4kHz: -0.6 dB (Q=0.95)
5kHz: -1.1 dB (Q=0.88)    6.3kHz: -1.4 dB (Q=0.82)  8kHz: -1.8 dB (Q=0.75)
10kHz: -1.5 dB (Q=0.68)   12.5kHz: -1.1 dB (Q=0.62) 16kHz: -0.7 dB (Q=0.58)

Spectral Balance Analysis:
  Low (20-125Hz): +11.8dB    Low-Mid (160-630Hz): -1.2dB
  Mid (800-2.5kHz): +5.4dB   Presence (3.15-5kHz): -2.8dB
  High (6.3kHz+): -6.5dB

Safety Analysis:
  Total boost applied: 18.3dB
  Distortion risk: LOW
  Maximum single boost: +3.4dB (31Hz)
  Frequency dominance: BALANCED

Professional Processing Applied:
✓ 30-band psychoacoustic EQ with dynamic Q-factors
✓ Intelligent spectral balance optimization  
✓ Frequency-specific distortion protection
✓ Advanced dynamics control with temporal stability
✓ Professional safety limiting (-0.5dB ceiling)
✓ Real-time clipping detection and prevention
✓ Harmonic preservation algorithms
✓ Stereo field integrity maintenance
```

## Application Areas

### Music Production
- **Album mastering**: Consistent sound across tracks with different recording characteristics
- **Remix and remaster**: Bringing classic recordings to modern loudness standards
- **Reference matching**: Achieving the sonic character of commercially successful tracks
- **Genre adaptation**: Adapting masters for different musical styles and target audiences

### Broadcasting and Streaming
- **Podcast production**: Professional audio quality for spoken content
- **Radio broadcast**: Meeting transmission standards with optimal loudness
- **Streaming platform optimization**: Platform-specific loudness targets (Spotify, Apple Music, etc.)
- **Content delivery**: Consistent audio quality across different distribution channels

### Post-Production
- **Film and TV**: Matching dialogue, music, and effects to industry standards
- **Game audio**: Consistent audio atmosphere across different game elements
- **Commercial production**: Broadcast-ready audio with competitive loudness
- **Educational content**: Clear, professional audio for online learning platforms

### Live Sound and Installation
- **Venue optimization**: Adapting masters for specific acoustic environments
- **Installation audio**: Background music systems with appropriate dynamics
- **Event production**: Consistent audio quality across different playback systems
- **Museum and exhibition**: Audio content optimized for public spaces

## Advanced Features and Algorithms

### Psychoacoustic Processing
- **Equal loudness contours**: Fletcher-Munson curve consideration for frequency weighting
- **Masking effects**: Analysis and compensation for simultaneous and temporal masking
- **Critical band analysis**: Bark scale frequency grouping for perceptual accuracy
- **Temporal integration**: Time-dependent loudness perception modeling

### AI-Driven Analysis
- **Pattern recognition**: Automatic detection of musical elements and their treatment
- **Genre classification**: Style-aware processing parameters based on musical content
- **Dynamic adaptation**: Real-time parameter adjustment based on audio characteristics
- **Learning algorithms**: Continuous improvement based on processing results

### Professional Workflow Integration
- **Batch processing**: Multiple file processing with consistent parameters
- **Preset management**: Save and recall custom processing configurations
- **A/B comparison**: Real-time comparison between processed and original audio
- **Detailed reporting**: Comprehensive analysis reports for professional documentation

## Getting Started with Matchering

### Basic Workflow

```csharp
using Ownaudio.Utilities.Matchering;

// 1. Initialize the analyzer
var analyzer = new AudioAnalyzer();

// 2. Basic matchering - one line of code
analyzer.ProcessEQMatching("my_track.wav", "reference_master.wav", "mastered_output.wav");

// 3. That's it! Your track now has the sonic character of the reference
```

### Advanced Workflow

```csharp
// 1. Analyze your source material
var sourceSpectrum = analyzer.AnalyzeAudioFile("my_track.wav");
Console.WriteLine($"Source loudness: {sourceSpectrum.Loudness:F1} LUFS");
Console.WriteLine($"Dynamic range: {sourceSpectrum.DynamicRange:F1} dB");

// 2. Analyze reference track
var referenceSpectrum = analyzer.AnalyzeAudioFile("commercial_reference.wav");
Console.WriteLine($"Reference loudness: {referenceSpectrum.Loudness:F1} LUFS");

// 3. Apply matchering with full analysis output
analyzer.ProcessEQMatching("my_track.wav", "commercial_reference.wav", "mastered_track.wav");

// 4. Use presets for specific playback systems
analyzer.ProcessWithPreset("mastered_track.wav", "streaming_version.wav", PlaybackSystem.RadioBroadcast);
analyzer.ProcessWithPreset("mastered_track.wav", "club_version.wav", PlaybackSystem.ClubPA);
analyzer.ProcessWithPreset("mastered_track.wav", "audiophile_version.wav", PlaybackSystem.HiFiSpeakers);
```

### Professional Tips

1. **Choose appropriate references**: Use commercially mastered tracks in the same genre
2. **Consider your target audience**: Different playback systems require different approaches
3. **Preserve dynamics**: The system automatically protects musical dynamics while matching loudness
4. **Monitor the output**: Always listen to the processed audio in your target environment
5. **Use presets strategically**: Match your delivery format to the intended playback system

## Performance and Compatibility

### System Requirements
- **.NET 6.0 or higher**
- **Minimum 4GB RAM** (8GB recommended for large files)
- **Multi-core CPU** (processing is automatically parallelized)
- **Available disk space** for temporary processing files

### File Format Support
- **Input formats**: WAV, FLAC, MP3, AAC, OGG (via FFmpeg)
- **Output format**: 24-bit WAV (industry standard for mastering)
- **Sample rates**: 44.1kHz, 48kHz, 88.2kHz, 96kHz (automatic detection)
- **Channel support**: Mono, stereo, and multi-channel audio

### Processing Performance
- **Real-time capable**: Processing faster than real-time playback
- **Memory efficient**: Streaming processing for large files
- **Thread-safe**: Parallel processing on multi-core systems
- **Scalable**: Handles files from seconds to hours in length

The OwnAudioSharp Matchering system represents the state-of-the-art in automated mastering technology, bringing professional studio capabilities to developers and content creators. Whether you're building a music production application, podcast platform, or content delivery system, the matchering algorithms ensure broadcast-quality audio output with minimal complexity.

## Acknowledgements

Special thanks to the creators of the following repositories, whose code was instrumental in the development of OwnAudio:

- [Bufdio](https://github.com/luthfiampas/Bufdio) - Audio playback library for .NET
- [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) - FFmpeg auto generated unsafe bindings for C#/.NET
- [soundtouch.net](https://github.com/owoudenberg/soundtouch.net) - .NET wrapper for the SoundTouch audio processing library
- [Avalonia](https://github.com/AvaloniaUI/Avalonia) - Cross-platform .NET UI framework
- [SoundFlow](https://github.com/LSXPrime/SoundFlow) - A powerful and extensible cross-platform .NET audio engine.