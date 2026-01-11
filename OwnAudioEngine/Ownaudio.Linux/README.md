# Ownaudio.Linux - PulseAudio Implementation

Linux audio engine implementation for OwnAudioSharp using the native **PulseAudio** API.

## Overview

This package provides low-latency, high-performance audio playback and recording for Linux using PulseAudio, the standard audio server for most modern Linux distributions.

**IMPORTANT**: This implementation uses Linux's **native PulseAudio system library** (`libpulse.so`) which is available on most Linux distributions. **No external native libraries are required** - the implementation uses direct P/Invoke to the system-provided PulseAudio API.

### Key Features

- **Low-latency audio**: Optimized for real-time audio processing
- **PulseAudio API**: Native Linux audio with wide compatibility
- **Lock-free architecture**: Thread-safe ring buffers prevent UI thread blocking
- **Zero-allocation design**: No GC pressure in audio callback paths
- **Multi-architecture support**: x64, ARM64, ARMv7
- **Automatic device routing**: PulseAudio handles speaker/headphone/Bluetooth routing
- **Network transparency**: Support for remote audio servers

## Architecture

### Native API: PulseAudio

The implementation uses **PulseAudio** (Linux's standard audio server) which provides:

- Direct access to audio hardware with minimal overhead
- Automatic device routing (speakers, headphones, Bluetooth, USB)
- Network transparency (stream to remote servers)
- Per-application volume control
- Dynamic device management and hot-plug support
- Built into most Linux distributions - no external dependencies needed

### Implementation Structure

```
Ownaudio.Linux/
├── PulseAudioEngine.cs              # Main engine implementation
├── PulseAudioDeviceEnumerator.cs    # Device enumeration and management
└── Interop/
    ├── PulseAudioInterop.cs         # P/Invoke definitions for PulseAudio API
    └── GStreamerInterop.cs          # GStreamer for advanced decoding
└── Decoders/
    └── GStreamerMp3Decoder.cs       # GStreamer MP3 decoder

Note: No native libraries needed - PulseAudio is provided by Linux distribution.
```

## Threading Architecture

```
UI/Main Thread
  └─> Send() [lock-free ring buffer write, <0.1ms]
       └─> LockFreeRingBuffer<float>
            └─> PulseAudio Mainloop Thread [real-time priority]
                 └─> Output to PulseAudio Server
```

**Critical**: Never call blocking methods (`Initialize()`, `Stop()`) from UI thread!

## Usage Example

### Basic Playback

```csharp
using Ownaudio.Core;
using Ownaudio.Linux;

// Create configuration
var config = new AudioConfig
{
    SampleRate = 48000,
    Channels = 2,
    BufferSize = 512,  // ~10ms at 48kHz
    EnableOutput = true,
    EnableInput = false
};

// Create engine (or use AudioEngineFactory.Create for automatic platform detection)
var engine = new PulseAudioEngine();

// Initialize (blocking - use Task.Run in production!)
// NOTE: PulseAudio initialization can take 1-5 seconds on first connection!
engine.Initialize(config);

// Start playback
engine.Start();

// Send audio samples (float[] or Span<float>)
float[] samples = new float[512 * 2]; // 512 frames * 2 channels
// ... fill samples with audio data ...
engine.Send(samples);

// Stop and cleanup
engine.Stop();
engine.Dispose();
```

### Using AudioEngineFactory (Recommended)

```csharp
using Ownaudio.Core;

// Automatically creates PulseAudioEngine on Linux
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
    BufferSize = 512,
    EnableInput = true,
    EnableOutput = false
};

var engine = new PulseAudioEngine();
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

- **Linux Distribution**: Any modern distro with PulseAudio (Ubuntu 14.04+, Fedora 20+, Debian 8+, etc.)
- **PulseAudio Version**: 4.0 or later (most distros have 8.0+)
- **.NET**: net9.0 or later
- **Architecture**: x64, ARM64, or ARMv7

### System Dependencies

PulseAudio is typically pre-installed on most Linux desktop distributions. If not:

```bash
# Ubuntu/Debian
sudo apt-get install pulseaudio libpulse0

# Fedora/RHEL
sudo dnf install pulseaudio pulseaudio-libs

# Arch Linux
sudo pacman -S pulseaudio

# Verify installation
pulseaudio --version
```

### Permissions

No special permissions required for playback. For recording:

```bash
# Ensure user is in audio group
sudo usermod -a -G audio $USER

# Logout and login for changes to take effect
```

## Performance Characteristics

### Latency

| Buffer Size | Expected Latency (48kHz) | Use Case |
|-------------|-------------------------|----------|
| 256 frames  | ~5.3ms                  | Low latency (requires RT priority) |
| 512 frames  | ~10.7ms                 | Recommended (balanced) |
| 1024 frames | ~21.3ms                 | Default (general audio) |
| 2048 frames | ~42.7ms                 | High latency (background audio) |

**Note**: PulseAudio typically adds 20-50ms of additional latency compared to ALSA due to server architecture. For ultra-low latency (<5ms), consider using ALSA or JACK directly.

**Recommended**: Use 512-1024 frames for reliable playback without glitches.

### CPU Usage

- **Async mainloop**: Minimal CPU overhead
- **Lock-free buffers**: No mutex contention
- **Server architecture**: PulseAudio server handles mixing
- **Zero allocation**: No GC pressure in real-time paths

### Initialization Time

**IMPORTANT**: PulseAudio initialization can be slow:
- **First connection**: 1-5 seconds (server startup, device discovery)
- **Subsequent connections**: 100-500ms
- **Always use `Task.Run()` to avoid blocking UI thread**

```csharp
// ✅ GOOD - Non-blocking
await Task.Run(() => engine.Initialize(config));

// ❌ BAD - Blocks UI thread for 1-5 seconds!
engine.Initialize(config);
```

## Device Management

PulseAudio provides comprehensive device enumeration:

```csharp
var engine = new PulseAudioEngine();

// Get available devices
var outputDevices = engine.GetOutputDevices();
foreach (var device in outputDevices)
{
    Console.WriteLine($"Device: {device.Name}");
    Console.WriteLine($"  ID: {device.Id}");
    Console.WriteLine($"  Channels: {device.Channels}");
    Console.WriteLine($"  Sample Rate: {device.SampleRate}");
}

// PulseAudio automatically routes to:
// - Built-in speakers
// - Wired headphones (when plugged)
// - Bluetooth headset (when connected)
// - USB audio interfaces
// - HDMI audio output
// - Network audio sinks
```

Dynamic device switching is supported:

```csharp
// Switch output device by name
engine.SetOutputDeviceByName("alsa_output.pci-0000_00_1f.3.analog-stereo");

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
- **PA_ERR_CONNECTION_REFUSED**: PulseAudio server not running
- **PA_ERR_TIMEOUT**: Connection timeout (server overloaded)
- **PA_ERR_INVALID**: Invalid configuration
- **PA_ERR_NOENTITY**: Device not found

## Best Practices

### 1. Always Use Async Initialization

```csharp
// ✅ GOOD - Non-blocking
await Task.Run(() => engine.Initialize(config));

// ❌ BAD - Blocks UI thread for 1-5 seconds!
engine.Initialize(config);
```

### 2. Use Lock-Free Wrappers

For UI applications, use `AudioEngineWrapper` from `Ownaudio.Core`:

```csharp
var wrapper = new AudioEngineWrapper(engine, config.BufferSize * 8);
wrapper.Send(samples);  // Non-blocking, uses ring buffer
```

### 3. Choose Appropriate Buffer Sizes

- **Low latency (advanced users)**: 256-512 frames (requires RT priority)
- **General audio playback**: 1024 frames (recommended)
- **Background audio**: 2048-4096 frames

**Note**: Smaller buffers (<512) may cause glitches on slow systems or without real-time priority.

### 4. Handle PulseAudio Server Issues

```csharp
try
{
    await Task.Run(() => engine.Initialize(config));
}
catch (AudioEngineException ex)
{
    // Check if PulseAudio server is running
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine("Ensure PulseAudio server is running:");
    Console.WriteLine("  pulseaudio --check || pulseaudio --start");
}
```

### 5. Use Larger Buffers for Reliability

```csharp
// For production applications, use larger buffers to prevent glitches
var config = new AudioConfig
{
    SampleRate = 48000,
    Channels = 2,
    BufferSize = 1024  // More reliable than 512 on most systems
};
```

### 6. Monitor Buffer Health

```csharp
// Check for underruns via PulseAudio statistics
// pa_stream_get_underflow_index() provides underrun detection
```

## Troubleshooting

### Issue: PulseAudio Server Not Running

**Error**: `PA_ERR_CONNECTION_REFUSED`

**Solution**:
```bash
# Check if PulseAudio is running
pulseaudio --check

# Start PulseAudio
pulseaudio --start

# Or restart
pulseaudio --kill
pulseaudio --start

# Check status
systemctl --user status pulseaudio
```

### Issue: No Audio Output

**Possible causes**:
1. Wrong device selected
2. Volume is muted
3. PulseAudio server not running
4. ALSA driver issues

**Solution**:
```csharp
// List all devices to verify correct device
var devices = engine.GetOutputDevices();
foreach (var d in devices)
    Console.WriteLine($"{d.Name}: {d.State}");

// Use default device
engine.SetOutputDeviceByIndex(0);

// Check PulseAudio status
// pavucontrol (GUI) or pactl list sinks (CLI)
```

```bash
# Check volume levels
pactl list sinks | grep -i volume
pactl set-sink-volume @DEFAULT_SINK@ 65536  # 100%

# Check mute status
pactl list sinks | grep -i mute
pactl set-sink-mute @DEFAULT_SINK@ 0  # Unmute
```

### Issue: High Latency or Glitches

**Possible causes**:
1. Buffer size too small
2. No real-time priority
3. PulseAudio resampling
4. CPU throttling

**Solution**:
```csharp
// Increase buffer size
config.BufferSize = 1024; // or 2048
```

```bash
# Configure PulseAudio for lower latency
# Edit /etc/pulse/daemon.conf or ~/.config/pulse/daemon.conf

default-fragments = 4
default-fragment-size-msec = 10

# Disable resampling if possible
resample-method = copy

# Then restart PulseAudio
pulseaudio --kill
pulseaudio --start
```

### Issue: Crackling/Popping Audio

**Possible causes**:
1. Buffer underruns (buffer too small)
2. CPU spikes
3. GC pressure (allocations in callback)
4. Competing processes

**Solution**:
```csharp
// Increase buffer size
config.BufferSize = 2048;

// Ensure no allocations in Send() loop
// Pre-allocate all buffers
```

```bash
# Increase PulseAudio's process priority
# Edit /etc/pulse/daemon.conf

realtime-scheduling = yes
realtime-priority = 9

# Restart PulseAudio
pulseaudio --kill
pulseaudio --start
```

### Issue: Slow Initialization

**Expected**: First connection can take 1-5 seconds

**Solution**:
```csharp
// Always initialize asynchronously
await Task.Run(() => engine.Initialize(config));

// Show progress indicator to user
// PulseAudio initialization time is normal
```

### Issue: Device Not Found

**Error**: `PA_ERR_NOENTITY`

**Solution**:
```bash
# List available devices
pactl list sinks short
pactl list sources short

# Get device names
pacmd list-sinks | grep name:
```

```csharp
// Use exact device name from pactl
engine.SetOutputDeviceByName("alsa_output.pci-0000_00_1f.3.analog-stereo");
```

## Implementation Details

### PulseAudio Callback Flow

1. **Initialization**: Create mainloop, context, and connect to server
2. **Context Setup**: Authenticate and wait for server ready state
3. **Stream Creation**: pa_stream_new with desired format
4. **Connect Stream**: pa_stream_connect_playback/record
5. **Set Callbacks**: Register write/read callbacks
6. **Mainloop**: Async mainloop processes server messages
7. **Data Transfer**: Callbacks called when server needs/provides data
8. **Disconnect**: pa_stream_disconnect, pa_context_disconnect

### P/Invoke to PulseAudio

The implementation uses P/Invoke to native PulseAudio library:

```csharp
[DllImport("libpulse.so.0", CallingConvention = CallingConvention.Cdecl)]
public static extern IntPtr pa_simple_new(
    string server,
    string name,
    pa_stream_direction_t dir,
    string dev,
    string stream_name,
    ref pa_sample_spec ss,
    IntPtr channel_map,
    IntPtr attr,
    out int error);
```

### Memory Management

- **Pinned Buffers**: `GCHandle.Alloc(buffer, GCHandleType.Pinned)` prevents GC relocation
- **Native Pointers**: `IntPtr` to pa_stream/pa_context tracked and cleaned up
- **Ring Buffers**: Pre-allocated, fixed-size, lock-free SPSC queues
- **Zero Allocation**: No `new` in audio callback paths

### Thread Safety

- **Lock-Free Ring Buffers**: Single-producer, single-consumer design
- **Volatile State**: `volatile int` for state flags
- **State Lock**: `lock (_stateLock)` only for state transitions (Init/Start/Stop)
- **No Locks in Callbacks**: Audio callbacks never acquire locks
- **Threaded Mainloop**: pa_threaded_mainloop provides thread-safe event loop

## API Reference

See [IAudioEngine.cs](../Ownaudio.Core/IAudioEngine.cs) for full interface documentation.

### Key Methods

- `Initialize(AudioConfig)` - Configure and prepare audio streams ⚠️ **BLOCKING (1-5 seconds)**
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
# Build Linux project
dotnet build OwnAudioEngine/Ownaudio.Linux/Ownaudio.Linux.csproj -c Release

# Output: Ownaudio.Linux.dll
# No native dependencies required - uses system PulseAudio
```

## Native API Details

**PulseAudio**: Native Linux audio server (standard on most Linux distributions)

**System Library**: `libpulse.so.0` (provided by PulseAudio package)

**No External Dependencies**:
- ✅ No need to include native libraries (uses system libpulse)
- ✅ No C++ runtime required
- ✅ Works out-of-the-box on most Linux distributions
- ✅ P/Invoke to system library

**Supported Linux Distributions**:
- **Ubuntu/Debian**: 14.04+ (PulseAudio 4.0+)
- **Fedora/RHEL**: 20+ (PulseAudio 4.0+)
- **Arch Linux**: All versions (rolling release)
- **Others**: Any distro with PulseAudio 4.0+

**Architecture Support**:
All Linux architectures are supported:
- **x64**: 64-bit (most common)
- **ARM64**: 64-bit ARM (Raspberry Pi 4, servers)
- **ARMv7**: 32-bit ARM (Raspberry Pi 2/3)

## Known Limitations

1. **Higher Latency**: PulseAudio adds 20-50ms vs. direct ALSA (server architecture)
2. **Initialization Time**: First connection can take 1-5 seconds
3. **Format Support**: Float32 only (internally converted if needed)
4. **Network Transparency**: Remote servers add significant latency
5. **Real-Time Performance**: Not as robust as dedicated real-time audio servers (JACK)

## Alternative Audio Systems

For specialized use cases, consider:

### ALSA (Advanced Linux Sound Architecture)
- **Lower latency**: Direct hardware access
- **Complexity**: More complex device management
- **Compatibility**: Not all applications support ALSA directly

### JACK (JACK Audio Connection Kit)
- **Professional audio**: Designed for pro audio workflows
- **Ultra-low latency**: <5ms round-trip possible
- **Complexity**: Requires JACK server setup and management

### PipeWire
- **Modern replacement**: New audio/video server (Fedora 34+, Ubuntu 22.10+)
- **PulseAudio compatible**: Drop-in replacement
- **Lower latency**: Better performance than PulseAudio

**Note**: Future versions of OwnAudioSharp may add ALSA, JACK, and PipeWire backends.

## Advanced Features

### Network Audio Streaming

PulseAudio supports network transparent audio:

```bash
# Stream to remote server
export PULSE_SERVER=192.168.1.100

# Or specify in configuration
engine.Initialize(config, serverAddress: "192.168.1.100");
```

**Note**: Network streaming adds 100-500ms latency depending on network conditions.

### Per-Application Volume Control

PulseAudio provides per-application volume control. This is handled automatically by the audio server.

## Related Documentation

- [OwnAudioSharp Documentation](https://modernmube.github.io/OwnAudioSharp/)
- [PulseAudio Documentation](https://www.freedesktop.org/wiki/Software/PulseAudio/)
- [PulseAudio API Reference](https://freedesktop.org/software/pulseaudio/doxygen/)
- [Linux Audio Architecture](https://wiki.archlinux.org/title/sound_system)

## License

Copyright © 2025 Ownaudio Team

Part of the OwnAudioSharp project.
