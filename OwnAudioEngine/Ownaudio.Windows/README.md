# Ownaudio.Windows - WASAPI Implementation

Windows audio engine implementation for OwnAudioSharp using the native **WASAPI** (Windows Audio Session API).

## Overview

This package provides low-latency, high-performance audio playback and recording for Windows using WASAPI, Microsoft's modern audio API introduced in Windows Vista.

**IMPORTANT**: This implementation uses Windows' **native WASAPI system APIs** via COM interop. **No external native libraries are required** - the implementation uses direct P/Invoke and COM interop to the system-provided audio services.

### Key Features

- **Low-latency audio**: Optimized for real-time audio processing
- **WASAPI**: Native Windows audio with best performance
- **Shared and Exclusive modes**: Support for both audio sharing modes
- **Lock-free architecture**: Thread-safe ring buffers prevent UI thread blocking
- **Zero-allocation design**: No GC pressure in audio callback paths
- **Full device enumeration**: Complete control over audio devices
- **Hot-plug support**: Dynamic device change detection
- **Multi-architecture support**: x64, x86, ARM64

## Architecture

### Native API: WASAPI

The implementation uses **WASAPI** (Windows Audio Session API) which provides:

- Direct access to hardware audio with minimal overhead
- Shared mode for compatibility, Exclusive mode for ultra-low latency
- Event-driven and timer-driven callback modes
- Full device enumeration and hot-plug detection
- Per-application volume control and session management
- Built into Windows OS - no external dependencies needed

### Implementation Structure

```
Ownaudio.Windows/
├── WasapiEngine.cs                    # Main engine implementation
├── WasapiDeviceEnumerator.cs          # Device enumeration and management
├── WasapiDeviceNotificationClient.cs  # Hot-plug detection
├── Interop/
│   ├── WasapiInterop.cs              # WASAPI COM interfaces and structures
│   ├── IMMDeviceCollection.cs        # Device collection interface
│   ├── IPropertyStore.cs             # Device properties
│   ├── IMMNotificationClient.cs      # Device change notifications
│   ├── MediaFoundationInterop.cs     # Media Foundation for decoding
│   └── Kernel32.cs                   # Win32 APIs (threading, events)
└── Decoders/
    └── WindowsMFMp3Decoder.cs        # Media Foundation MP3 decoder

Note: No native libraries needed - WASAPI is provided by Windows OS.
```

## Threading Architecture

```
UI/Main Thread
  └─> Send() [lock-free ring buffer write, <0.1ms]
       └─> LockFreeRingBuffer<float>
            └─> WASAPI Event Thread [high priority]
                 └─> Output to IAudioRenderClient
```

**Critical**: Never call blocking methods (`Initialize()`, `Stop()`) from UI thread!

## Usage Example

### Basic Playback

```csharp
using Ownaudio.Core;
using Ownaudio.Windows;

// Create configuration
var config = new AudioConfig
{
    SampleRate = 48000,
    Channels = 2,
    BufferSize = 480,  // 10ms at 48kHz
    EnableOutput = true,
    EnableInput = false
};

// Create engine (or use AudioEngineFactory.Create for automatic platform detection)
var engine = new WasapiEngine();

// Initialize (blocking - use Task.Run in production!)
engine.Initialize(config);

// Start playback
engine.Start();

// Send audio samples (float[] or Span<float>)
float[] samples = new float[480 * 2]; // 480 frames * 2 channels
// ... fill samples with audio data ...
engine.Send(samples);

// Stop and cleanup
engine.Stop();
engine.Dispose();
```

### Using AudioEngineFactory (Recommended)

```csharp
using Ownaudio.Core;

// Automatically creates WasapiEngine on Windows
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
    Channels = 2,  // Stereo recording
    BufferSize = 480,
    EnableInput = true,
    EnableOutput = false
};

var engine = new WasapiEngine();
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

### Device Selection

```csharp
var engine = new WasapiEngine();

// Enumerate available devices
var outputDevices = engine.GetOutputDevices();
foreach (var device in outputDevices)
{
    Console.WriteLine($"{device.Name} ({device.Id})");
}

// Select specific device by name
engine.SetOutputDeviceByName("Speakers");

// Or by index
engine.SetOutputDeviceByIndex(1);

// Initialize and use
engine.Initialize(config);
engine.Start();
```

### Exclusive Mode (Ultra-Low Latency)

```csharp
var config = new AudioConfig
{
    SampleRate = 48000,
    Channels = 2,
    BufferSize = 128,  // Very small buffer for minimal latency
    EnableOutput = true,
    ExclusiveMode = true  // Request exclusive access
};

var engine = new WasapiEngine();
try
{
    await Task.Run(() => engine.Initialize(config));
    // Exclusive mode acquired - ultra-low latency available
}
catch (AudioEngineException ex)
{
    // Fallback to shared mode if exclusive fails
    config.ExclusiveMode = false;
    await Task.Run(() => engine.Initialize(config));
}
```

## Platform Requirements

### Minimum Requirements

- **Windows Version**: Windows Vista SP1 or later (WASAPI introduced)
- **Recommended**: Windows 10/11 for best performance and reliability
- **.NET**: net9.0 or later
- **Architecture**: x64, x86, or ARM64

### System Permissions

No special permissions required - WASAPI is available to all applications.

## Performance Characteristics

### Latency

| Mode | Buffer Size | Expected Latency (48kHz) | Use Case |
|------|-------------|-------------------------|----------|
| Exclusive | 128 frames | ~2.7ms | Professional audio, music production |
| Exclusive | 256 frames | ~5.3ms | Gaming, low-latency applications |
| Shared | 480 frames | ~10ms | General audio playback |
| Shared | 960 frames | ~20ms | Background audio, streaming |

**Note**: Actual latency depends on hardware drivers and system load. Exclusive mode provides the lowest latency but requires exclusive device access.

### CPU Usage

- **Event-driven mode**: Minimal CPU overhead (default)
- **Lock-free buffers**: No mutex contention
- **COM interop**: Optimized for performance
- **Zero allocation**: No GC pressure in real-time paths

## Device Management

Windows provides comprehensive device enumeration and management:

```csharp
var engine = new WasapiEngine();

// Get all output devices
var outputDevices = engine.GetOutputDevices();
foreach (var device in outputDevices)
{
    Console.WriteLine($"Device: {device.Name}");
    Console.WriteLine($"  ID: {device.Id}");
    Console.WriteLine($"  Channels: {device.Channels}");
    Console.WriteLine($"  Sample Rate: {device.SampleRate}");
    Console.WriteLine($"  State: {device.State}");
}

// Get input devices (microphones)
var inputDevices = engine.GetInputDevices();

// Monitor device changes
engine.DeviceStateChanged += (sender, args) =>
{
    Console.WriteLine($"Device {args.DeviceInfo.Name} changed to {args.NewState}");
};
```

### Hot-Plug Detection

WASAPI automatically detects device changes:
- Device connection/disconnection
- Default device changes
- Device property changes
- Device state changes (active/disabled)

The engine raises `DeviceStateChanged` events for all changes.

## Error Handling

The engine raises the `DeviceStateChanged` event when audio errors occur:

```csharp
engine.DeviceStateChanged += (sender, args) =>
{
    Console.WriteLine($"Device {args.DeviceInfo.Name} changed to {args.NewState}");

    if (args.NewState == AudioDeviceState.Disabled)
    {
        // Handle device disconnection
        // Reinitialize or switch to different device
    }
};
```

Common error scenarios:
- **Device Disconnected**: Audio device unplugged or disabled
- **Format Mismatch**: Requested format not supported by device
- **Buffer Underrun/Overrun**: CPU overload or buffer size too small
- **Exclusive Mode Unavailable**: Another application using exclusive mode

## Best Practices

### 1. Always Use Async Initialization

```csharp
// ✅ GOOD - Non-blocking
await Task.Run(() => engine.Initialize(config));

// ❌ BAD - Blocks UI thread for 50-200ms!
engine.Initialize(config);
```

### 2. Use Lock-Free Wrappers

For UI applications, use `AudioEngineWrapper` from `Ownaudio.Core`:

```csharp
var wrapper = new AudioEngineWrapper(engine, config.BufferSize * 8);
wrapper.Send(samples);  // Non-blocking, uses ring buffer
```

### 3. Choose Appropriate Buffer Sizes

- **Professional audio/DAW**: 64-128 frames (exclusive mode)
- **Games/Interactive**: 256-512 frames
- **General audio playback**: 480-960 frames (default)
- **Background audio/streaming**: 1024-2048 frames

### 4. Handle Device Changes

```csharp
engine.DeviceStateChanged += async (sender, args) =>
{
    if (args.NewState == AudioDeviceState.Disabled)
    {
        // Stop current engine
        engine.Stop();

        // Switch to default device
        engine.SetOutputDeviceByIndex(0);

        // Reinitialize
        await Task.Run(() => engine.Initialize(config));
        engine.Start();
    }
};
```

### 5. Use Exclusive Mode Wisely

```csharp
// Try exclusive mode first
config.ExclusiveMode = true;
try
{
    await Task.Run(() => engine.Initialize(config));
}
catch (AudioEngineException)
{
    // Fallback to shared mode
    config.ExclusiveMode = false;
    await Task.Run(() => engine.Initialize(config));
}
```

### 6. Monitor Audio Glitches

```csharp
// Check for glitches in WASAPI
// Monitor discontinuities in audio stream position
var position = engine.GetStreamPosition();
// Compare with expected position to detect underruns
```

## Troubleshooting

### Issue: No Audio Output

**Possible causes**:
1. Wrong device selected
2. Volume is muted (system or per-app)
3. Audio service not running
4. Driver issues

**Solution**:
```csharp
// List all devices to verify correct device
var devices = engine.GetOutputDevices();
foreach (var d in devices)
    Console.WriteLine($"{d.Name}: {d.State}");

// Use default device
engine.SetOutputDeviceByIndex(0);

// Verify sample rate matches device capabilities
config.SampleRate = 48000; // Most common on Windows
```

### Issue: High Latency

**Possible causes**:
1. Shared mode forces higher latency
2. Buffer size too large
3. System audio processing enabled

**Solution**:
```csharp
// Use exclusive mode for minimum latency
config.ExclusiveMode = true;
config.BufferSize = 128; // Small buffer

// Disable system audio enhancements in Windows Sound settings
```

### Issue: Crackling/Popping Audio

**Possible causes**:
1. Buffer underruns (buffer too small)
2. CPU spikes on audio thread
3. DPC latency issues (driver problems)

**Solution**:
```csharp
// Increase buffer size
config.BufferSize = 960; // or 1024

// Use event-driven mode (default)
// Ensure no allocations in Send() loop
```

### Issue: Exclusive Mode Fails

**Possible causes**:
1. Another application using exclusive mode
2. Device doesn't support requested format
3. System audio features (APOs) interfere

**Solution**:
```csharp
// Always have fallback to shared mode
try
{
    config.ExclusiveMode = true;
    engine.Initialize(config);
}
catch (AudioEngineException)
{
    config.ExclusiveMode = false;
    engine.Initialize(config);
    Console.WriteLine("Using shared mode");
}
```

### Issue: Device Enumeration Empty

**Possible causes**:
1. Audio service not running
2. No audio devices installed
3. COM initialization failed

**Solution**:
```csharp
// Check Windows Audio service status
// Verify devices in Windows Sound Control Panel
// Ensure COM is initialized (automatic in most cases)
```

## Implementation Details

### WASAPI Callback Flow

1. **Initialization**: Create IMMDeviceEnumerator, get device, activate IAudioClient
2. **Configure**: Set format (WAVEFORMATEX), buffer size, shared/exclusive mode
3. **Create Render Client**: IAudioRenderClient for output, IAudioCaptureClient for input
4. **Create Event**: Win32 event for event-driven mode
5. **Start**: IAudioClient.Start() begins audio processing
6. **Callback Execution**: Wait on event, fill buffer via GetBuffer/ReleaseBuffer
7. **Stop**: IAudioClient.Stop() terminates stream gracefully

### COM Interop

- **IMMDeviceEnumerator**: Device discovery and enumeration
- **IMMDevice**: Individual audio device
- **IAudioClient**: Main audio client interface
- **IAudioRenderClient**: Output buffer management
- **IAudioCaptureClient**: Input buffer management
- **IMMNotificationClient**: Device change notifications

All COM interfaces use `[ComImport]` and `[Guid]` attributes for interop.

### Memory Management

- **Pinned Buffers**: `GCHandle.Alloc(buffer, GCHandleType.Pinned)` prevents GC relocation
- **COM Objects**: `Marshal.ReleaseComObject()` for cleanup
- **Ring Buffers**: Pre-allocated, fixed-size, lock-free SPSC queues
- **Zero Allocation**: No `new` in audio callback paths

### Thread Safety

- **Lock-Free Ring Buffers**: Single-producer, single-consumer design
- **Volatile State**: `volatile int` for state flags
- **State Lock**: `lock (_stateLock)` only for state transitions (Init/Start/Stop)
- **No Locks in Callbacks**: Audio callbacks never acquire locks
- **Event Signaling**: Win32 events for thread coordination

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
# Build Windows project
dotnet build OwnAudioEngine/Ownaudio.Windows/Ownaudio.Windows.csproj -c Release

# Output: Ownaudio.Windows.dll
# No native dependencies required - uses Windows COM interop
```

## Native API Details

**WASAPI**: Native Windows audio API (part of Windows since Vista)

**System Library**: Built into Windows (Ole32.dll, MMDevAPI.dll)

**No External Dependencies**:
- ✅ No need to include native libraries
- ✅ No C++ runtime required
- ✅ Works out-of-the-box on all Windows versions (Vista+)
- ✅ COM interop via managed code

**Supported Windows Versions**:
- **Minimum**: Windows Vista SP1 (WASAPI introduced)
- **Recommended**: Windows 7+ (improved stability)
- **Best**: Windows 10/11 (latest optimizations)

**Architecture Support**:
All Windows architectures are supported:
- **x64**: 64-bit (most common)
- **x86**: 32-bit (legacy)
- **ARM64**: ARM 64-bit (Surface devices, Windows on ARM)

## Known Limitations

1. **Format Conversion**: WASAPI may insert sample rate converter in shared mode
2. **Exclusive Mode**: Requires exact format match, prevents other apps from using device
3. **Device Sharing**: Exclusive mode blocks other applications
4. **DPC Latency**: System drivers can affect real-time performance
5. **Loopback Recording**: Requires special configuration (not implemented yet)

## Advanced Features

### Loopback Recording (Coming Soon)

Capture audio playing on the system:

```csharp
// Future API
var config = new AudioConfig
{
    EnableInput = true,
    LoopbackMode = true  // Capture system audio
};
```

### Audio Session Management

WASAPI provides per-application volume control and session management. This is handled automatically by the engine.

### Spatial Audio

Windows 10+ supports spatial audio (Dolby Atmos, Windows Sonic). This requires additional configuration and is not currently exposed by the engine.

## Related Documentation

- [OwnAudioSharp Documentation](https://modernmube.github.io/OwnAudioSharp/)
- [WASAPI Documentation](https://docs.microsoft.com/en-us/windows/win32/coreaudio/wasapi)
- [Core Audio APIs](https://docs.microsoft.com/en-us/windows/win32/coreaudio/core-audio-apis)
- [Low Latency Audio on Windows](https://docs.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio)

## License

Copyright © 2025 Ownaudio Team

Part of the OwnAudioSharp project.
