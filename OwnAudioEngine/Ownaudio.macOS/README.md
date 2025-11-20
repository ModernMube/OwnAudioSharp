# Ownaudio.macOS - Core Audio Implementation

macOS audio engine implementation for OwnAudioSharp using the native **Core Audio** framework.

## Overview

This package provides low-latency, high-performance audio playback and recording for macOS using Core Audio, Apple's professional-grade audio API.

**IMPORTANT**: This implementation uses macOS' **native Core Audio framework** (`AudioToolbox.framework`, `CoreAudio.framework`) which is built into macOS. **No external native libraries are required** - the implementation uses direct P/Invoke to the system-provided audio APIs.

### Key Features

- **Ultra-low latency audio**: Optimized for professional audio applications
- **Core Audio**: Native macOS audio with best-in-class performance
- **Lock-free architecture**: Thread-safe ring buffers prevent UI thread blocking
- **Zero-allocation design**: No GC pressure in audio callback paths
- **Multi-architecture support**: Intel x64 and Apple Silicon ARM64
- **Automatic device routing**: macOS handles speaker/headphone/Bluetooth routing
- **Real-time priority**: Audio threads run at highest priority for glitch-free playback

## Architecture

### Native API: Core Audio

The implementation uses **Core Audio** (macOS' low-level audio API) which provides:

- Direct access to hardware audio with minimal overhead
- Real-time audio thread scheduling
- Automatic device routing (speakers, headphones, Bluetooth)
- Optimized buffer sizes for minimal latency
- Stream state management and error recovery
- Built into macOS - no external dependencies needed

### Implementation Structure

```
Ownaudio.macOS/
├── CoreAudioEngine.cs              # Main engine implementation
├── CoreAudioDeviceEnumerator.cs    # Device enumeration and management
└── Interop/
    ├── CoreAudioInterop.cs         # P/Invoke definitions for Core Audio
    ├── AudioToolboxInterop.cs      # AudioQueue and AudioToolbox APIs
    └── MachThreadInterop.cs        # Mach thread priority management
└── Decoders/
    └── CoreAudioMp3Decoder.cs      # Audio File Services MP3 decoder

Note: No native libraries needed - Core Audio is provided by macOS.
```

## Threading Architecture

```
UI/Main Thread
  └─> Send() [lock-free ring buffer write, <0.1ms]
       └─> LockFreeRingBuffer<float>
            └─> Core Audio Callback Thread [real-time priority]
                 └─> Output to AudioQueue/HAL
```

**Critical**: Never call blocking methods (`Initialize()`, `Stop()`) from UI thread!

## Usage Example

### Basic Playback

```csharp
using Ownaudio.Core;
using Ownaudio.macOS;

// Create configuration
var config = new AudioConfig
{
    SampleRate = 48000,
    Channels = 2,
    BufferSize = 256,  // ~5ms at 48kHz for low latency
    EnableOutput = true,
    EnableInput = false
};

// Create engine (or use AudioEngineFactory.Create for automatic platform detection)
var engine = new CoreAudioEngine();

// Initialize (blocking - use Task.Run in production!)
engine.Initialize(config);

// Start playback
engine.Start();

// Send audio samples (float[] or Span<float>)
float[] samples = new float[256 * 2]; // 256 frames * 2 channels
// ... fill samples with audio data ...
engine.Send(samples);

// Stop and cleanup
engine.Stop();
engine.Dispose();
```

### Using AudioEngineFactory (Recommended)

```csharp
using Ownaudio.Core;

// Automatically creates CoreAudioEngine on macOS
var engine = AudioEngineFactory.Create(AudioConfig.LowLatency);

engine.Start();
// ... use engine ...
engine.Stop();
engine.Dispose();
```

### Recording

```csharp
var config = new AudioConfig
{
    SampleRate = 48000,
    Channels = 1,  // Mono for recording
    BufferSize = 256,
    EnableInput = true,
    EnableOutput = false
};

var engine = new CoreAudioEngine();
await Task.Run(() => engine.Initialize(config));
engine.Start();

// Receive audio samples
while (recording)
{
    int result = engine.Receives(out float[] samples);
    if (result == 0 && samples.Length > 0)
    {
        // Process captured audio
        ProcessAudio(samples);
    }
    await Task.Delay(10);
}
```

## Platform Requirements

### Minimum Requirements

- **macOS Version**: macOS 10.13 (High Sierra) or later
- **Recommended**: macOS 11 (Big Sur) or later for best performance
- **.NET**: net9.0 or later
- **Architecture**: x64 (Intel) or ARM64 (Apple Silicon)

### Permissions (Info.plist)

For recording, add the following permissions:

```xml
<key>NSMicrophoneUsageDescription</key>
<string>This app requires microphone access for audio recording</string>
```

At runtime, the system will prompt the user to grant microphone permission.

## Performance Characteristics

### Latency

| Buffer Size | Expected Latency (48kHz) | Use Case |
|-------------|-------------------------|----------|
| 64 frames   | ~1.3ms                  | Ultra-low latency (professional audio) |
| 128 frames  | ~2.7ms                  | Low latency (music apps, games) |
| 256 frames  | ~5.3ms                  | Recommended (balanced) |
| 512 frames  | ~10.7ms                 | Default (general audio) |
| 1024 frames | ~21.3ms                 | High latency (background audio) |

**Note**: macOS Core Audio is optimized for professional audio workloads and typically achieves 3-10ms round-trip latency on modern hardware (2015+).

### CPU Usage

- **Callback-based design**: Minimal CPU overhead
- **Lock-free buffers**: No mutex contention
- **Real-time priority**: High-priority audio threads prevent glitches
- **SIMD optimization**: Vectorized audio processing via Accelerate.framework
- **Zero allocation**: No GC pressure in real-time paths

## Device Management

macOS provides robust device enumeration and management:

```csharp
var engine = new CoreAudioEngine();

// Get available devices
var outputDevices = engine.GetOutputDevices();
foreach (var device in outputDevices)
{
    Console.WriteLine($"Device: {device.Name}");
    Console.WriteLine($"  ID: {device.Id}");
    Console.WriteLine($"  Channels: {device.Channels}");
    Console.WriteLine($"  Sample Rate: {device.SampleRate}");
}

// macOS automatically routes to:
// - Built-in speakers
// - Wired headphones (when plugged)
// - Bluetooth headset (when connected)
// - AirPlay devices
// - USB audio interfaces
// - Thunderbolt/USB-C audio devices
```

Dynamic device switching is supported:

```csharp
// Switch output device by name
engine.SetOutputDeviceByName("Built-in Output");

// Or by index
engine.SetOutputDeviceByIndex(1);
```

## Error Handling

The engine raises the `DeviceStateChanged` event when audio errors occur:

```csharp
engine.DeviceStateChanged += (sender, args) =>
{
    Console.WriteLine($"Device {args.DeviceInfo.Name} changed to {args.NewState}");

    if (args.NewState == AudioDeviceState.Disabled)
    {
        // Handle device disconnection
        // Reinitialize or switch to default device
    }
};
```

Common error scenarios:
- **kAudioQueueErr_InvalidDevice**: Audio device unplugged
- **kAudioQueueErr_BufferEmpty**: Buffer underrun
- **kAudioQueueErr_CannotStart**: Cannot start audio queue
- **kAudioQueueErr_InvalidParameter**: Invalid configuration

## Best Practices

### 1. Always Use Async Initialization

```csharp
// ✅ GOOD - Non-blocking
await Task.Run(() => engine.Initialize(config));

// ❌ BAD - Blocks UI thread for 50-300ms!
engine.Initialize(config);
```

### 2. Use Lock-Free Wrappers

For UI applications, use `AudioEngineWrapper` from `Ownaudio.Core`:

```csharp
var wrapper = new AudioEngineWrapper(engine, config.BufferSize * 8);
wrapper.Send(samples);  // Non-blocking, uses ring buffer
```

### 3. Choose Appropriate Buffer Sizes

- **Professional audio/DAW**: 64-128 frames
- **Games/Interactive**: 256 frames (recommended)
- **General audio playback**: 512 frames (default)
- **Background audio**: 1024-2048 frames

### 4. Handle macOS App Lifecycle

```csharp
// NSApplication delegate example
public override void DidResignActive(NSNotification notification)
{
    // Pause audio when app loses focus
    _audioEngine?.Stop();
}

public override void DidBecomeActive(NSNotification notification)
{
    // Resume audio when app gains focus
    _audioEngine?.Start();
}

public override void WillTerminate(NSNotification notification)
{
    // Cleanup
    _audioEngine?.Dispose();
}
```

### 5. Monitor Audio Glitches

```csharp
// Check for underruns using AudioQueue properties
// Monitor callback timing to detect real-time violations
```

### 6. Use Real-Time Priority

Core Audio automatically sets real-time priority for audio threads. The engine leverages Mach thread APIs to ensure audio callbacks run at highest priority:

```csharp
// Automatically handled by CoreAudioEngine
// Audio callback thread runs at real-time priority
```

## Troubleshooting

### Issue: No Audio Output

**Possible causes**:
1. Wrong device selected
2. Volume is muted (system or app)
3. CoreAudio service issue
4. Sample rate mismatch

**Solution**:
```csharp
// List all devices to verify correct device
var devices = engine.GetOutputDevices();
foreach (var d in devices)
    Console.WriteLine($"{d.Name}: {d.State}");

// Use default device
engine.SetOutputDeviceByIndex(0);

// Use standard sample rate
config.SampleRate = 48000; // Most common on macOS
```

### Issue: High Latency

**Possible causes**:
1. Buffer size too large
2. CPU throttling (power saving mode)
3. System audio processing enabled

**Solution**:
```csharp
// Use low-latency configuration
config.BufferSize = 128; // or 256

// Ensure Mac is plugged in (not on battery)
// Disable system audio enhancements in Audio MIDI Setup
```

### Issue: Crackling/Popping Audio

**Possible causes**:
1. Buffer underruns (buffer too small)
2. CPU spikes on audio thread
3. GC pressure (allocations in callback)
4. Competing processes

**Solution**:
```csharp
// Increase buffer size
config.BufferSize = 512; // or 1024

// Ensure no allocations in Send() loop
// Pre-allocate all buffers

// Close other audio applications
```

### Issue: Recording Permission Denied

**Possible causes**:
1. Missing NSMicrophoneUsageDescription in Info.plist
2. User denied permission
3. System privacy settings

**Solution**:
```xml
<!-- Add to Info.plist -->
<key>NSMicrophoneUsageDescription</key>
<string>This app requires microphone access</string>
```

```csharp
// Check permission status
// System will automatically prompt on first recording attempt
// User can grant permission in System Preferences > Security & Privacy
```

### Issue: Device Switching Fails

**Possible causes**:
1. Device not available
2. Engine already started
3. Format not supported by device

**Solution**:
```csharp
// Stop engine before switching devices
engine.Stop();
engine.SetOutputDeviceByName("Built-in Output");
engine.Initialize(config);
engine.Start();
```

## Implementation Details

### Core Audio Callback Flow

1. **Initialization**: Create AudioQueue with desired format
2. **Allocate Buffers**: AudioQueueAllocateBuffer creates native buffers
3. **Set Callbacks**: Register output/input callbacks
4. **Start**: AudioQueueStart begins audio processing
5. **Callback Execution**: Core Audio calls callback on real-time thread
6. **Data Transfer**: Samples read from ring buffer, copied to AudioQueueBuffer
7. **Stop**: AudioQueueStop terminates queue gracefully

### P/Invoke to Core Audio

The implementation uses P/Invoke to native macOS frameworks:

```csharp
[DllImport("/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox")]
private static extern OSStatus AudioQueueNewOutput(
    ref AudioStreamBasicDescription format,
    AudioQueueOutputCallback callback,
    IntPtr userData,
    IntPtr callbackQueue,
    IntPtr callbackRunLoop,
    uint flags,
    out IntPtr audioQueue);
```

### Memory Management

- **Pinned Buffers**: `GCHandle.Alloc(buffer, GCHandleType.Pinned)` prevents GC relocation
- **Native Pointers**: `IntPtr` to AudioQueue tracked and cleaned up
- **Ring Buffers**: Pre-allocated, fixed-size, lock-free SPSC queues
- **Zero Allocation**: No `new` in audio callback paths

### Thread Safety

- **Lock-Free Ring Buffers**: Single-producer, single-consumer design
- **Volatile State**: `volatile int` for state flags
- **State Lock**: `lock (_stateLock)` only for state transitions (Init/Start/Stop)
- **No Locks in Callbacks**: Audio callbacks never acquire locks
- **Real-Time Priority**: Mach thread APIs ensure high-priority scheduling

## API Reference

See [IAudioEngine.cs](../Ownaudio.Core/IAudioEngine.cs) for full interface documentation.

### Key Methods

- `Initialize(AudioConfig)` - Configure and prepare audio streams ⚠️ **BLOCKING**
- `Start()` - Begin audio processing
- `Stop()` - Stop audio processing ⚠️ **BLOCKING**
- `Send(Span<float>)` - Send audio for playback (may block if buffer full)
- `Receives(out float[])` - Receive recorded audio (may block if buffer empty)
- `GetOutputDevices()` - List available output devices
- `GetInputDevices()` - List available input devices
- `SetOutputDeviceByName(string)` - Select output device by name
- `SetOutputDeviceByIndex(int)` - Select output device by index

### Key Properties

- `FramesPerBuffer` - Actual negotiated buffer size in frames
- `OwnAudioEngineActivate()` - Returns 1 if active, 0 if idle, -1 if error
- `OwnAudioEngineStopped()` - Returns 1 if stopped, 0 if running

### Events

- `DeviceStateChanged` - Fired when device state changes

## Building from Source

```bash
# Build macOS project
dotnet build OwnAudioEngine/Ownaudio.macOS/Ownaudio.macOS.csproj -c Release

# Output: Ownaudio.macOS.dll
# No native dependencies required - uses macOS frameworks
```

## Native API Details

**Core Audio**: Native macOS audio API (part of macOS since 10.0)

**System Frameworks**:
- `AudioToolbox.framework` - High-level audio queue APIs
- `CoreAudio.framework` - Low-level hardware abstraction layer
- `Accelerate.framework` - SIMD optimizations (optional)

**No External Dependencies**:
- ✅ No need to include native libraries
- ✅ No C++ runtime required
- ✅ Works out-of-the-box on all macOS versions (10.13+)
- ✅ P/Invoke to system frameworks

**Supported macOS Versions**:
- **Minimum**: macOS 10.13 (High Sierra)
- **Recommended**: macOS 11+ (Big Sur) - Improved M1 support
- **Best**: macOS 12+ (Monterey) - Latest optimizations

**Architecture Support**:
All macOS architectures are supported:
- **x64**: Intel 64-bit (2006-2020 Macs)
- **ARM64**: Apple Silicon (M1/M2/M3 Macs, 2020+)

Universal binaries work on both Intel and Apple Silicon automatically.

## Known Limitations

1. **Device Enumeration**: Limited compared to Windows (macOS manages more automatically)
2. **Exclusive Mode**: Not available (macOS uses shared mode only)
3. **Sample Rate Changes**: May require stream recreation
4. **Format Support**: Float32 only (internally converted if needed)
5. **AirPlay Latency**: Wireless protocols add 100-200ms latency

## Advanced Features

### Aggregate Devices

macOS supports aggregate devices (combining multiple devices). Configure via Audio MIDI Setup:

```csharp
// Engine will detect aggregate devices automatically
var devices = engine.GetOutputDevices();
// Look for "Aggregate Device" in device names
```

### Multi-Output Devices

macOS allows routing to multiple outputs simultaneously:

```csharp
// Configure via Audio MIDI Setup
// Engine will use the multi-output device if selected
engine.SetOutputDeviceByName("Multi-Output Device");
```

### Audio Unit Integration (Coming Soon)

Future versions may expose Audio Unit hosting for effects and instruments.

## Related Documentation

- [OwnAudioSharp Documentation](https://modernmube.github.io/OwnAudioSharp/)
- [Core Audio Overview](https://developer.apple.com/documentation/coreaudio)
- [Audio Queue Services](https://developer.apple.com/documentation/audiotoolbox/audio_queue_services)
- [Audio Hardware Abstraction Layer](https://developer.apple.com/documentation/coreaudio/core_audio_hardware_abstraction_layer)
- [Real-Time Audio on macOS](https://developer.apple.com/library/archive/documentation/MusicAudio/Conceptual/CoreAudioOverview/)

## License

Copyright © 2025 Ownaudio Team

Part of the OwnAudioSharp project.
