# OwnAudio Multitrack Player

A professional multitrack audio player built with Avalonia UI and the OwnAudioSharp library.

## Features

### Core Audio Features
- **Multitrack Playback**: Load and play multiple audio tracks simultaneously (WAV, MP3, FLAC)
- **Perfect Synchronization**: All tracks start together with millisecond precision
- **Real-time Mixing**: Professional audio mixing with per-track volume controls
- **Master Effects**: Tempo and pitch shifting applied to the master output
- **Zero-Allocation Design**: Optimized for minimal GC pressure and low CPU usage

### User Interface
- **Modern Dark Theme**: Professional audio workstation aesthetic
- **Track Management**:
  - Add multiple tracks via file picker
  - Individual volume controls (0-200%)
  - Mute (M) and Solo (S) buttons per track
  - Visual track list with file names

- **Master Controls**:
  - Master volume (0-200%)
  - Tempo control (0.5x - 2.0x speed)
  - Pitch shift (-12 to +12 semitones)

- **Playback Controls**: Play, Pause, Stop
- **Status Display**: Real-time feedback on application state

## Architecture

### Application Structure

```
MultitrackPlayer/
├── Services/
│   └── AudioService.cs          # Singleton managing audio engine lifecycle
├── Models/
│   └── TrackInfo.cs             # Model representing an audio track
├── ViewModels/
│   ├── MainWindowViewModel.cs   # Main application logic
│   └── TrackViewModel.cs        # Individual track view model
├── Effects/
│   └── MasterTimeStretchEffect.cs # Tempo/Pitch adapter for SoundTouch
└── Views/
    └── MainWindow.axaml         # UI layout
```

### Audio Pipeline

```
UI Thread
  └─> MainWindowViewModel
       └─> AudioService
            └─> AudioMixer (dedicated thread)
                 ├─> Track 1 (FileSource)
                 ├─> Track 2 (FileSource)
                 ├─> Track N (FileSource)
                 └─> Master Effects (Tempo/Pitch)
                      └─> Audio Engine (WASAPI/PulseAudio/CoreAudio)
```

### Key Components

1. **AudioService**: Singleton service managing:
   - OwnaudioNet engine initialization (async to avoid UI blocking)
   - AudioMixer lifecycle
   - Playback state management

2. **MasterTimeStretchEffect**: Adapter implementing `IEffectProcessor`:
   - Wraps `SoundTouchProcessor` for time-stretching and pitch-shifting
   - Uses `ArrayPool<float>` for zero-allocation performance
   - Handles variable output buffer sizes

3. **MainWindowViewModel**:
   - Track collection management
   - Playback control commands (Play, Pause, Stop)
   - Master volume and effects parameters
   - Solo/Mute logic

## Building and Running

### Prerequisites
- .NET 9.0 SDK
- Windows, Linux, or macOS

### Build
```bash
dotnet build MultitrackPlayer.csproj
```

### Run
```bash
dotnet run --project MultitrackPlayer.csproj
```

## Usage

1. **Launch the Application**: The audio engine initializes automatically in the background
2. **Add Tracks**: Click "Add Tracks" and select one or more audio files
3. **Adjust Track Settings**:
   - Use volume sliders to balance individual tracks
   - Use "M" (Mute) to silence a track
   - Use "S" (Solo) to hear only selected tracks
4. **Master Controls**:
   - Adjust master volume to control overall output level
   - Change tempo without affecting pitch (0.5x = half speed, 2.0x = double speed)
   - Shift pitch without affecting tempo (±12 semitones = 1 octave)
5. **Playback**:
   - Click "Play" to start synchronized playback
   - Click "Pause" to temporarily stop
   - Click "Stop" to stop and reset to the beginning

## Technical Highlights

### Zero-Allocation Audio Processing
- Uses `ArrayPool<float>` for temporary buffers
- `Span<T>` for stack-allocated audio processing
- Lock-free ring buffers for cross-thread communication
- No allocations in the audio processing hot path

### Thread Safety
- Audio initialization happens async to prevent UI freezing
- Dedicated mixer thread with high priority
- Thread-safe track addition/removal using `ConcurrentDictionary`
- Reactive UI updates with ReactiveUI

### Cross-Platform
- Single codebase for Windows, Linux, and macOS
- Platform-specific audio engines selected at runtime:
  - Windows: WASAPI
  - Linux: PulseAudio
  - macOS: Core Audio

## Performance Targets
- Mix 4+ tracks simultaneously
- Latency: < 12ms @ 512 buffer size
- CPU: < 15% single core
- Zero allocations in mix loop

## Dependencies
- **Avalonia** (11.3.7): Cross-platform UI framework
- **ReactiveUI**: MVVM framework for reactive UI
- **OwnaudioNET**: Core audio library
- **SoundTouch.NET**: Time-stretching and pitch-shifting

## License
This example is part of the OwnAudioSharp project.

## Contributing
This example demonstrates best practices for building a professional multitrack audio application with OwnAudioSharp. Feel free to use it as a starting point for your own projects!
