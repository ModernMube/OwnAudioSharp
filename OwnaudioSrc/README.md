##

OwnAudio is a cross-platform C# audio library that provides a high-level API for audio playback, recording, and processing. By default, it uses FFmpeg for audio decoding and PortAudio for audio I/O. If FFmpeg or PortAudio is not installed, it automatically substitutes the missing one with MiniAudio. This way, it can work without any external dependencies using MiniAudio. The implementation of MiniAudio also allowed the API to be used on mobile platforms.

## Features

- **Cross-platform** compatibility (Windows, macOS, Linux, Android, iOS)
- **Audio playback** with support for various formats via FFmpeg, or MiniAudio (mp3, wav, flac) formats
- **Audio recording** capabilities through input devices
- **Time stretching and pitch shifting** using SoundTouch
- **Mixing** multiple audio sources
- **Volume control** and custom audio processing
- **Seeking** within audio files
- **Real-time audio processing** with custom sample processors
- **Audio data visualization** customizable waveform display
- **Built-in audio effects** (Reverb, Delay, Distortion, Equalizer, Compressor, etc.)

## Sample Application

Check out the sample application [OwnAudioSharpDemo](https://github.com/ModernMube/OwnAudioSharpDemo) that demonstrates the capabilities of the OwnAudioSharp audio library through an Avalonia MVVM application using ReactiveUI. MainWindowViewModel.cs contains the core logic for audio processing, playback, effects application, and UI control.

## Documentation

 <a href="../../wiki/OwnAudio-first-steps">
  <img src="https://img.shields.io/badge/Wiki-OwnAudio%20API%20first%20step-blue" alt="Wiki OwnAudio first steps">
</a>

<a href="../../wiki/How-to-use-OwnAudio's-built‐in-effects">
  <img src="https://img.shields.io/badge/Wiki-OwnAudio%20API%20FX%20processor-blue" alt="Wiki OwnAudio FX processor">
</a>

<a href="../../wiki/OwnAudio-Library-Documentation">
  <img src="https://img.shields.io/badge/Wiki-OwnAudio%20library-darkgreen" alt="Wiki OwnAudio Library Documentation">
</a>

<a href="../../wiki/Ownaudio-SourceManager-Class-Documentation">
  <img src="https://img.shields.io/badge/Wiki-SourceManager-darkgreen" alt="Wiki Source manager documentation">
</a>

<a href="../../wiki/Ownaudio-Source-Class-Documentation">
  <img src="https://img.shields.io/badge/Wiki-Source-darkgreen" alt="Wiki Source documentation">
</a>

<a href="../../wiki/Ownaudio-Real-Time-Source-Class-Documentation">
  <img src="https://img.shields.io/badge/Wiki-Real%20time%20source-darkgreen" alt="Wiki Source documentation">
</a>

### Effects documentation

<a href="../../wiki/OwnAudio-effects-library">
  <img src="https://img.shields.io/badge/Wiki-Effects%20library%20documentation-brown" alt="Wiki Effects documentation">
</a>

## Supported Systems

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

You can add this library to your project via NuGet or by directly referencing the project.
```bash
NuGet\Install-Package OwnAudioSharp
```

## Optional dependencies: PortAudio and FFmpeg

By default, our code includes **MiniAudio**, which is ready to use for all systems, so you can get started right away!

If you want to use **PortAudio** and **FFmpeg** on certain platforms for extended functionality, you can configure them as follows:

### Windows

1. Grab the **FFmpeg 6** files and extract them to a folder.

2. Copy the **PortAudio 2** DLL file to the same folder.

3. When you initialize `OwnAudio` in your code, just point to the folder path.

### Linux

1. Use Synaptic package manager (or your distribution's equivalent) to install `portaudio19-dev` (this usually provides PortAudio v2) and `ffmpeg` (version 6 or compatible).
* For example, on Debian/Ubuntu based systems:

```bash
sudo apt update
sudo apt install portaudio19-dev ffmpeg
```
(Note: Package names may vary slightly depending on your Linux distribution. Make sure you get libraries compatible with FFmpeg version 6.)

2. `OwnAudio` is smart and will automatically find and use them if they are installed systemwide.

### macOS

1. Launch the terminal and use Homebrew:

```bash
brew install portaudio
brew install ffmpeg@6
```
2. After installation, the code will automatically detect and prioritize PortAudio and FFmpeg.

### Android and iOS

* Good news! **MiniAudio** works out of the box on Android and iOS. These platforms don't require any additional steps to handle audio.

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
using System.Threading;

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
OwnAudio.Free();
```

## Advanced Features

### Mixing Multiple Audio Sources

```csharp
// Add multiple audio files
await sourceManager.AddOutputSource("path/to/audio1.mp3");
await sourceManager.AddOutputSource("path/to/audio2.mp3");

// Adjust volume for individual sources
sourceManager.SetVolume(0, 0.8f);  // 80% volume for first source
sourceManager.SetVolume(1, 0.6f);  // 60% volume for second source

// Play mixed audio
sourceManager.Play();
```

### Audio Recording

```csharp
// Add an input source
await sourceManager.AddInputSource();

// Start recording
sourceManager.Play("output.wav", 16);  // 16-bit recording
```

### Time Stretching and Pitch Shifting

```csharp
// Change tempo without affecting pitch (value range -20 to +20)
sourceManager.SetTempo(0, 10.0);  // Speed up by 10%

// Change pitch without affecting tempo (value range -6 to +6 semitones)
sourceManager.SetPitch(0, 2.0);  // Raise pitch by 2 semitones
```

### Seeking Within Audio

```csharp
// Seek to a specific position
sourceManager.Seek(TimeSpan.FromSeconds(30));  // Seek to 30 seconds
```

## Audio Processing with Built-in Effects

OwnAudio includes a comprehensive effects library:

```csharp
// Apply reverb effect
var reverb = new Reverb(0.5f, 0.3f, 0.4f, 0.7f);
sourceManager.CustomSampleProcessor = reverb;

// Apply delay effect
var delay = new Delay(500, 0.4f, 0.3f, 44100);
sourceManager.CustomSampleProcessor = delay;

// Apply compressor
var compressor = new Compressor(0.5f, 4.0f, 100f, 200f, 1.0f, 44100f);
sourceManager.CustomSampleProcessor = compressor;

// Apply equalizer
var equalizer = new Equalizer(44100);
equalizer.SetBandGain(0, 100f, 1.4f, 3.0f);  // Boost bass
sourceManager.CustomSampleProcessor = equalizer;
```

### Available Effects

- **Reverb**: Professional quality reverb based on Freeverb algorithm
- **Delay**: Echo effect with feedback control
- **Distortion**: Overdrive and soft clipping
- **Compressor**: Dynamic range compression
- **Equalizer**: 10-band parametric EQ
- **Chorus**: Multi-voice modulation effect
- **Flanger**: Variable delay modulation
- **Phaser**: All-pass filter stages for phasing effect
- **Rotary**: Rotary speaker simulation
- **DynamicAmp**: Adaptive volume control
- **Enhancer**: Harmonic enhancement

## Custom Audio Processing

You can implement custom audio processing by implementing the `SampleProcessorBase` class:

```csharp
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

## Real-time Audio Sources

OwnAudio supports real-time audio sources for live audio generation and streaming:

```csharp
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
// Create a real-time audio source
var liveSource = sourceManager.AddRealTimeSource(1.0f, 2); // Volume, channels

// Example: Generate and stream sine wave in real-time
Task.Run(async () =>
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

Real-time and offline chord detection from musical notes.

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
// Set audio data from existing float array
waveformDisplay.SetAudioData(SourceManager.Instance.Sources[0].GetFloatAudioData(TimeSpan.Zero));

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

## Acknowledgements

Special thanks to the creators of the following repositories, whose code was instrumental in the development of OwnAudio:

- [Bufdio](https://github.com/luthfiampas/Bufdio) - Audio playback library for .NET
- [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) - FFmpeg auto generated unsafe bindings for C#/.NET
- [soundtouch.net](https://github.com/owoudenberg/soundtouch.net) - .NET wrapper for the SoundTouch audio processing library
- [Avalonia](https://github.com/AvaloniaUI/Avalonia) - Cross-platform .NET UI framework
- [SukiUI](https://github.com/kikipoulet/SukiUI) - Modern UI toolkit for Avalonia