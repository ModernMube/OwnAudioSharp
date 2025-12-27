# OwnAudio Multitrack Player

A professional multitrack audio player built with Avalonia UI and the OwnAudioSharp library. **Now featuring the new MasterClock timeline-based synchronization system (v2.4.0+)!**

## Features

### Core Audio Features (Updated for v2.4.0+)
- **Multitrack Playback**: Load and play multiple audio tracks simultaneously (WAV, MP3, FLAC)
- **NEW: Timeline-Based Synchronization**: Sample-accurate sync using MasterClock (timestamp-based)
- **NEW: Dropout Monitoring**: Real-time notifications and statistics for buffer underruns
- **NEW: Start Offset Support**: DAW-style regions - tracks can start at different timeline positions
- **Real-time Mixing**: Professional audio mixing with per-track volume controls
- **Global Tempo & Pitch**: Tempo and pitch shifting applied globally to all synchronized tracks (like professional DAWs)
- **Zero-Allocation Design**: Optimized for minimal GC pressure and low CPU usage

### User Interface
- **Modern Dark Theme**: Professional audio workstation aesthetic
- **Track Management**:
  - Add multiple tracks via file picker
  - Individual volume controls (0-100%)
  - Mute (M) and Solo (S) buttons per track
  - Visual track list with file names
  - Remove tracks individually

- **Master Controls**:
  - Master volume (0-100%)
  - Tempo control (50%-200% speed)
  - Pitch shift (-12 to +12 semitones)
  - Reset button for all controls

- **NEW: SmartMaster Effect** (v2.5.0+):
  - Enable/Disable toggle for intelligent audio mastering
  - Factory preset selection (Default, HiFi, Headphone, Studio, Club, Concert)
  - Auto-calibration wizard with progress tracking (requires measurement microphone)
  - Custom preset save/load functionality
  - Professional processing chain: 31-band EQ, subharmonic synthesis, compression, crossover, phase alignment, limiter

- **NEW: Sync Statistics Panel** (v2.4.0+):
  - Real-time dropout count display
  - Last dropout message (track name, missed frames, timestamp)
  - MasterClock status indicator

- **Playback Controls**: Play, Stop (with seek slider)
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

### Audio Pipeline (NEW - v2.4.0+)

```
UI Thread
  └─> MainWindowViewModel
       └─> AudioService
            └─> AudioMixer (dedicated thread)
                 ├─> MasterClock (timeline tracking)
                 │    └─> CurrentTimestamp (physical time)
                 │
                 ├─> Track 1 (FileSource + IMasterClockSource)
                 │    ├─> AttachToClock(MasterClock)
                 │    ├─> StartOffset (timeline position)
                 │    ├─> ReadSamplesAtTime(timestamp)
                 │    └─> Automatic drift correction (10ms)
                 │
                 ├─> Track 2 (FileSource + IMasterClockSource)
                 │    └─> ... (same as Track 1)
                 │
                 └─> Audio Engine (WASAPI/PulseAudio/CoreAudio)
                      └─> Dropout Events → UI Updates
```

### Key Components

1. **AudioService**: Singleton service managing:
   - OwnaudioNet engine initialization (async to avoid UI blocking)
   - AudioMixer lifecycle with MasterClock
   - Playback state management

2. **MainWindowViewModel** (NEW - v2.4.0+):
   - Track collection management
   - Playback control commands (Play, Stop)
   - MasterClock attachment and synchronization
   - Dropout event handling and UI updates
   - Per-track tempo and pitch control
   - Solo/Mute logic with zero-allocation caching

3. **MasterClock Integration**:
   - Timeline-based synchronization (timestamp in seconds)
   - Sample-accurate position tracking
   - Automatic drift correction (10ms tolerance)
   - Realtime rendering mode
   - Seek operations via timeline

4. **Dropout Monitoring**:
   - Real-time event notifications
   - UI feedback for buffer underruns
   - Statistics tracking (total count, last message)

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
   - Use volume sliders to balance individual tracks (0-100%)
   - Use "M" (Mute) to silence a track
   - Use "S" (Solo) to hear only selected tracks
   - Click "×" to remove a track
4. **Master Controls**:
   - Adjust master volume to control overall output level (0-100%)
   - Change tempo (50%-200%): affects all tracks via SoundTouch
   - Shift pitch (±12 semitones): 1 octave range
   - Click "Reset" to restore defaults (100% volume, 100% tempo, 0 semitones)
5. **Playback**:
   - Click "Play" to start synchronized playback with MasterClock
   - Drag the timeline slider to seek to any position
   - Click "Stop" to stop and reset to the beginning
6. **Monitor Synchronization** (NEW - v2.4.0+):
   - Watch "Sync Statistics" panel for dropout count
   - View last dropout details (track, frames, timestamp)
   - Zero dropouts = perfect synchronization!

## Technical Highlights

### NEW: MasterClock Synchronization (v2.4.0+)
- **Timeline-Based**: Physical time in seconds (not frame-based)
- **Sample-Accurate**: Long precision sample position tracking
- **Drift Correction**: Automatic resyncing with 10ms tolerance
- **Global Tempo**: Tempo is applied globally to all synchronized tracks (like professional DAWs)
- **Start Offset Support**: DAW-style regions (tracks start at different times)
- **Dropout Events**: Real-time notifications for buffer underruns
- **Zero Overhead**: Single null check when not using sync features

### Zero-Allocation Audio Processing
- Uses cached arrays for track iteration (no LINQ allocations)
- `Span<T>` for stack-allocated audio processing
- Lock-free ring buffers for cross-thread communication
- No allocations in the audio processing hot path
- Debounced slider updates (250ms) to reduce GC pressure

### Thread Safety
- Audio initialization happens async to prevent UI freezing
- Dedicated mixer thread with highest priority
- Thread-safe track addition/removal
- Interlocked operations for MasterClock updates
- Event-driven UI updates via Dispatcher

### Cross-Platform
- Single codebase for Windows, Linux, and macOS
- Platform-specific audio engines selected at runtime:
  - Windows: WASAPI
  - Linux: PulseAudio
  - macOS: Core Audio

## Performance Targets
- Mix 4+ tracks simultaneously (tested with 22+ tracks)
- Latency: < 12ms @ 512 buffer size, ~85ms @ 4096 buffer (high track counts)
- CPU: < 15% single core
- Zero allocations in mix loop
- Zero dropouts under normal operation
- Drift correction: < 10ms deviation
- UI updates: 4 times/second (250ms interval)

## Dependencies
- **Avalonia** (11.3.9): Cross-platform UI framework
- **CommunityToolkit.Mvvm** (8.4.0): Modern MVVM toolkit
- **OwnaudioNET** (v2.4.0+): Core audio library with MasterClock
- **SoundTouch.NET** (2.3.2): Time-stretching and pitch-shifting
- **OwnAudioEngine**: Platform-specific audio I/O (WASAPI/PulseAudio/CoreAudio)

## What's New in v2.4.0

### MasterClock Timeline Synchronization
This version introduces a major architectural upgrade from the legacy GhostTrack system to the new **MasterClock** timeline-based synchronization.

**Legacy (Deprecated - will be removed in v3.0.0):**
```csharp
// OLD: GhostTrack + AudioSynchronizer
mixer.CreateSyncGroup("MainTracks", sources);
mixer.SetSyncGroupTempo("MainTracks", 1.5f);
mixer.StartSyncGroup("MainTracks");
```

**NEW (v2.4.0+):**
```csharp
// NEW: MasterClock direct attachment
foreach (var source in sources) {
    if (source is IMasterClockSource clockSource) {
        clockSource.AttachToClock(mixer.MasterClock);
        clockSource.StartOffset = 0.0; // Optional: DAW regions
    }
    mixer.AddSource(source);
    source.Play();
}
```

**Benefits:**
- ✅ Timeline-based (seconds) vs frame-based synchronization
- ✅ Sample-accurate drift correction (10ms tolerance)
- ✅ Real-time dropout event notifications
- ✅ Global tempo control (all tracks synchronized, like professional DAWs)
- ✅ Start offset support (DAW-style regions)
- ✅ Simpler API (no sync groups needed)
- ✅ Zero overhead when not using sync features

### Migration Guide
The application has been fully migrated to the new MasterClock API:
- `Timer_Tick()`: Uses `mixer.MasterClock.CurrentTimestamp`
- `PlayAsync()`: Direct clock attachment per track
- `StopAsync()`: Individual track stop + clock reset
- `EndSeek()`: Direct clock seek operations
- `OnTempoPercentChanged()`: Direct per-track tempo updates

**Legacy code still works** but shows deprecation warnings. Update to the new API before v3.0.0 release.

## License
This example is part of the OwnAudioSharp project.

## Contributing
This example demonstrates best practices for building a professional multitrack audio application with OwnAudioSharp. It showcases the new MasterClock synchronization system introduced in v2.4.0. Feel free to use it as a starting point for your own projects!
