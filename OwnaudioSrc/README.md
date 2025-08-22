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
- **Audio data visualization** customizable waveform display
- **Built-in audio effects** (Reverb, Delay, Distortion, Equalizer, Compressor, etc.)
- **Detecting musical chords from audio data** Real-time or offline chord detection from musical notes
- **New feature: SourceSpark**, a useful resource for game developers, is ready. [See description below!](#sourcespark)

## Prerequisites

- FFmpeg libraries
- PortAudio libraries

Check out the sample application [OwnAudioSharpDemo](https://github.com/ModernMube/OwnAudioSharpDemo) that demonstrates the capabilities of the OwnAudioSharp audio library through an Avalonia MVVM application using ReactiveUI. MainWindowViewModel.cs contains the core logic for audio processing, playback, effects application, and UI control.

## Documentation

 <a href="../../wiki">
  <img src="https://img.shields.io/badge/Wiki-OwnAudio%20API%20documentation%20step-blue" alt="Wiki OwnAudio documentation">
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

## Audio Processing with Built-in Effects

OwnAudio includes a comprehensive effects library:

```csharp
using Ownaudio.Effects;

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

## SourceSpark 
**Game-Optimized Audio Effects**

The `SourceSpark` class is a simplified audio source designed specifically for short audio clips and sound effects, making it perfect for game development. Unlike standard sources, **SourceSpark loads entire audio files into memory** for instant, zero-latency playback.

### Key Features for Game Development

- **Zero-latency playback** - Audio data stored in memory for instant access
- **Looping support** - Perfect for ambient sounds like rain, wind, or engine noise
- **Multiple simultaneous playback** - Create multiple instances for overlapping sounds
- **Automatic cleanup** - Non-looping sounds are automatically removed when finished
- **Real-time effects** - Pitch, tempo, and volume can be modified during playback

### Basic Usage

```csharp
using Ownaudio;
using Ownaudio.Sources;

// Add a simple sound effect
var gunshot = sourceManager.AddSparkSource("sounds/gunshot.wav", looping: false, volume: 0.9f);
gunshot.Play(); // Direct playback

// Add looping ambient sound
var rainSound = sourceManager.AddSparkSource("ambient/rain.wav", looping: true, volume: 0.4f);
rainSound.Play(); // Start looping

// Start the source manager to handle all sources
sourceManager.Play();
```

### Advanced Game Audio

```csharp
// Dynamic engine sound for vehicles
var engineSound = sourceManager.AddSparkSource("car_engine.wav", looping: true, volume: 0.6f);
engineSound.Play();

// Real-time sound modification based on game state
void UpdateEngineSound(float speed) {
    engineSound.Pitch = speed * 0.01; // Higher pitch = faster speed
    engineSound.Volume = Math.Min(0.8f, 0.3f + speed * 0.005f);
}

// Multiple overlapping gunshots - create separate instances
var pistol1 = sourceManager.AddSparkSource("weapons/pistol.wav");
var pistol2 = sourceManager.AddSparkSource("weapons/pistol.wav");
pistol1.Play(); // First shot
pistol2.Play(); // Second shot (overlapping)
```

### Loop Management for Ambient Audio

```csharp
// Weather system with looping sounds
var currentWeather = sourceManager.AddSparkSource("weather/clear.wav", looping: true, volume: 0.2f);
currentWeather.Play();

void ChangeWeather(WeatherType weather) {
    // Stop current weather sound
    currentWeather.Stop();
    sourceManager.RemoveSparkSource(currentWeather);
    
    // Start new weather sound
    string weatherFile = weather switch {
        WeatherType.Rain => "weather/rain.wav",
        WeatherType.Storm => "weather/thunder.wav",
        WeatherType.Wind => "weather/wind.wav",
        _ => "weather/clear.wav"
    };
    
    currentWeather = sourceManager.AddSparkSource(weatherFile, looping: true, volume: 0.4f);
    currentWeather.Play();
}
```

### Runtime Control

```csharp
// Loop control during gameplay
sparkSource.IsLooping = true;  // Enable looping
sparkSource.IsLooping = false; // Disable looping

// State management
sparkSource.Play();   // Start playback
sparkSource.Pause();  // Pause playback
sparkSource.Resume(); // Resume from pause
sparkSource.Stop();   // Stop and reset

// Real-time audio effects
sparkSource.Volume = 0.5f;     // Adjust volume
sparkSource.Pitch = 2.0;       // Raise pitch by 2 semitones
sparkSource.Tempo = 1.5;       // Speed up by 50%
```

### Memory-Based Advantages

**Perfect for:**
- UI sounds (button clicks, menu navigation)
- Weapon effects (gunshots, explosions)
- Short ambient loops (rain, wind, machinery)
- Game event sounds (collectibles, notifications)

**Benefits:**
- **Instant response** - No file I/O during gameplay
- **Reliable playback** - No streaming errors or stuttering  
- **Flexible manipulation** - Real-time pitch/tempo changes
- **Concurrent playback** - Multiple instances of same sound

**Memory Guidelines:**
- Ideal for clips under 10 seconds
- Best suited for frequently used sounds
- Perfect for looping ambient audio
- Excellent for rapid-fire sound effects

### Lifecycle Management

```csharp
// Automatic cleanup for one-shot sounds
sparkSource.StateChanged += (sender, e) => {
    if (sparkSource.HasFinished && !sparkSource.IsLooping) {
        // Non-looping sounds are automatically removed
        sourceManager.RemoveSparkSource(sparkSource);
    }
};

// Manual cleanup for persistent sounds
if (sparkSource.IsLooping) {
    sparkSource.Stop();
    sourceManager.RemoveSparkSource(sparkSource);
}
```
The memory-based approach makes SourceSpark ideal for games where audio responsiveness and reliability are crucial, trading memory usage for guaranteed performance and zero-latency audio playback.

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

## Acknowledgements

Special thanks to the creators of the following repositories, whose code was instrumental in the development of OwnAudio:

- [Bufdio](https://github.com/luthfiampas/Bufdio) - Audio playback library for .NET
- [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) - FFmpeg auto generated unsafe bindings for C#/.NET
- [soundtouch.net](https://github.com/owoudenberg/soundtouch.net) - .NET wrapper for the SoundTouch audio processing library
- [Avalonia](https://github.com/AvaloniaUI/Avalonia) - Cross-platform .NET UI framework
- [SoundFlow](https://github.com/LSXPrime/SoundFlow) - A powerful and extensible cross-platform .NET audio engine.
