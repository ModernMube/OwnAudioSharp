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

## NEW Information

**It wasn't easy, but it's done!!!

I've been waiting and searching for years for a real, platform-independent audio engine that I can use on all systems without external dependencies (bass.net, miniaudio, portaudio, etc.). Unfortunately, no one has made such a fully managed engine yet. Now I see why no one has started! It's a serious challenge to compress the differences between platforms into a common code and handle GC independence. But I think it was worth it! I hope I'm not the only one who thinks so!

In a few days I'll upload the project so that anyone can freely create 100% managed audio applications for platform-independent systems!

I've made a website for the code so that everyone can see how the code is structured and how to use it!**

## The new API website is here!

<a href="https://modernmube.github.io/OwnAudioSharp/">
  <img src="https://img.shields.io/badge/NEW-OwnAudioSharp%20API%20website-blue" alt="OwnAudioSharp" width="400">
</a>

# 🎵 NEW: Professional Audio Matchering in OwnAudioSharp!

**Studio-grade mastering with advanced AI-driven analysis - single line of code!**

```csharp
// Process source audio to match reference characteristics
analyzer.ProcessEQMatching("source.wav", "reference.wav", "mastered.wav");

// Optimize for different playback systems
analyzer.ProcessWithEnhancedPreset("source.wav", "hifi_output.wav", PlaybackSystem.HiFiSpeakers);
```

⚡ **What you get:**
- Intelligent 10-band EQ matching
- Multiband compression across 4 frequency bands  
- Psychoacoustic weighting and spectral masking
- Distortion-protected automatic processing

🎯 **Result:** Your source audio will sound exactly like the reference track - professional mastering studio quality.

## Support My Work

If you find the code useful or use it for commercial purposes, invite me for a coffee!

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

## Acknowledgements

Special thanks to the creators of the following repositories, whose code was instrumental in the development of OwnAudio:

- [Bufdio](https://github.com/luthfiampas/Bufdio) - Audio playback library for .NET
- [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) - FFmpeg auto generated unsafe bindings for C#/.NET
- [soundtouch.net](https://github.com/owoudenberg/soundtouch.net) - .NET wrapper for the SoundTouch audio processing library
- [Avalonia](https://github.com/AvaloniaUI/Avalonia) - Cross-platform .NET UI framework
- [SoundFlow](https://github.com/LSXPrime/SoundFlow) - A powerful and extensible cross-platform .NET audio engine.
