# Ownaudio.Android - AAudio Implementation

Android audio engine implementation for OwnAudioSharp using the native **AAudio** API.

## Overview

This package provides low-latency, high-performance audio playback and recording for Android devices using the AAudio API introduced in Android 8.0 (API level 26).

**IMPORTANT**: This implementation uses Android's **native AAudio system library** (`libaaudio.so`), which is built into Android 8.0+. **No external native libraries (like Oboe) are required** - the implementation uses direct P/Invoke to the system-provided AAudio API.

### Key Features

- **Low-latency audio**: Optimized for real-time audio processing
- **AAudio API**: Native Android audio with best performance
- **Lock-free architecture**: Thread-safe ring buffers prevent UI thread blocking
- **Zero-allocation design**: No GC pressure in audio callback paths
- **Multi-architecture support**: ARM64, ARMv7, x86, and x86_64
- **Automatic device routing**: Android handles speaker/headphone/Bluetooth routing

## Architecture

### Native API: AAudio

The implementation uses **AAudio** (Android's native low-latency audio API) which provides:

- Direct access to hardware audio with minimal overhead
- Automatic device routing (speakers, headphones, Bluetooth)
- Optimized buffer sizes for minimal latency
- Stream state management and error recovery
- Built into Android OS - no external dependencies needed

### Implementation Structure

```
Ownaudio.Android/
├── AAudioEngine.cs              # Main engine implementation
├── AAudioDeviceEnumerator.cs    # Device enumeration (simplified for Android)
└── Interop/
    └── AAudioInterop.cs         # P/Invoke definitions for AAudio native API

Note: No native libraries (libs/) are needed - AAudio is provided by Android OS.
```

## Threading Architecture

```
UI/Main Thread
  └─> Send() [lock-free ring buffer write, <0.1ms]
       └─> LockFreeRingBuffer<float>
            └─> AAudio Callback Thread [real-time priority]
                 └─> Output to AudioTrack/AAudioStream
```

**Critical**: Never call blocking methods (`Initialize()`, `Stop()`) from UI thread!

## Usage Example

### Basic Playback

```csharp
using Ownaudio.Core;
using Ownaudio.Android;

// Create configuration
var config = new AudioConfig
{
    SampleRate = 48000,
    Channels = 2,
    BufferSize = 192,  // Low latency on modern devices
    EnableOutput = true,
    EnableInput = false
};

// Create engine (or use AudioEngineFactory.Create for automatic platform detection)
var engine = new AAudioEngine();

// Initialize (blocking - use Task.Run in production!)
engine.Initialize(config);

// Start playback
engine.Start();

// Send audio samples (float[] or Span<float>)
float[] samples = new float[192 * 2]; // 192 frames * 2 channels
// ... fill samples with audio data ...
engine.Send(samples);

// Stop and cleanup
engine.Stop();
engine.Dispose();
```

### Using AudioEngineFactory (Recommended)

```csharp
using Ownaudio.Core;

// Automatically creates AAudioEngine on Android
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

var engine = new AAudioEngine();
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

- **Android API Level**: 21 (Android 5.0 Lollipop)
- **Recommended API Level**: 26+ (Android 8.0 Oreo) for AAudio
- **.NET**: net9.0-android or later
- **Architecture**: ARM64, ARMv7, x86, or x86_64

### Permissions (AndroidManifest.xml)

For recording, add the following permissions:

```xml
<uses-permission android:name="android.permission.RECORD_AUDIO" />
<uses-feature android:name="android.hardware.microphone" android:required="false" />
```

At runtime, request `RECORD_AUDIO` permission (dangerous permission on Android 6.0+).

## Performance Characteristics

### Latency

| Buffer Size | Expected Latency (48kHz) | Use Case |
|-------------|-------------------------|----------|
| 96 frames   | ~2ms                    | Ultra-low latency (music apps, games) |
| 192 frames  | ~4ms                    | Low latency (recommended) |
| 512 frames  | ~10.7ms                 | Default (balanced) |
| 1024 frames | ~21.3ms                 | High latency (background audio) |

**Note**: Actual latency depends on device hardware and Android version. Modern devices (Android 9+) typically achieve 10-20ms round-trip latency.

### CPU Usage

- **Callback-based design**: Minimal CPU overhead
- **Lock-free buffers**: No mutex contention
- **SIMD optimization**: Vectorized audio processing where available
- **Zero allocation**: No GC pressure in real-time paths

## Device Management

Android handles audio routing automatically. The engine provides simplified device enumeration:

```csharp
var engine = new AAudioEngine();

// Get available devices (returns default device only)
var outputDevices = engine.GetOutputDevices();
var inputDevices = engine.GetInputDevices();

// Android automatically routes to:
// - Built-in speaker
// - Wired headphones (when plugged)
// - Bluetooth headset (when connected)
// - USB audio (on supported devices)
```

Dynamic device switching (SetOutputDeviceByName/Index) is not implemented as Android manages this automatically.

## Error Handling

The engine raises the `DeviceStateChanged` event when audio errors occur:

```csharp
engine.DeviceStateChanged += (sender, args) =>
{
    Console.WriteLine($"Device {args.DeviceInfo.Name} changed to {args.NewState}");

    if (args.NewState == AudioDeviceState.Disabled)
    {
        // Handle device disconnection
        // Reinitialize or notify user
    }
};
```

Common error scenarios:
- **AAUDIO_ERROR_DISCONNECTED**: Audio device unplugged
- **AAUDIO_ERROR_TIMEOUT**: Buffer underrun/overrun
- **AAUDIO_ERROR_NO_SERVICE**: Audio service unavailable

## Best Practices

### 1. Always Use Async Initialization

```csharp
// ✅ GOOD - Non-blocking
await Task.Run(() => engine.Initialize(config));

// ❌ BAD - Blocks UI thread for 50-500ms!
engine.Initialize(config);
```

### 2. Use Lock-Free Wrappers

For UI applications, use `AudioEngineWrapper` from `Ownaudio.Core`:

```csharp
var wrapper = new AudioEngineWrapper(engine, config.BufferSize * 8);
wrapper.Send(samples);  // Non-blocking, uses ring buffer
```

### 3. Choose Appropriate Buffer Sizes

- **Games/Music apps**: 96-192 frames
- **General audio playback**: 512 frames (default)
- **Background audio**: 1024-2048 frames

### 4. Handle Android Lifecycle

```csharp
protected override void OnPause()
{
    base.OnPause();
    // Stop audio when app is backgrounded
    _audioEngine?.Stop();
}

protected override void OnResume()
{
    base.OnResume();
    // Resume audio when app returns to foreground
    _audioEngine?.Start();
}

protected override void OnDestroy()
{
    base.OnDestroy();
    _audioEngine?.Dispose();
}
```

### 5. Monitor Buffer Health

```csharp
// Check for underruns (xruns)
if (_outputStream != IntPtr.Zero)
{
    int xruns = AAudioInterop.AAudioStream_getXRunCount(_outputStream);
    if (xruns > 0)
    {
        Console.WriteLine($"Warning: {xruns} buffer underruns detected");
        // Consider increasing buffer size
    }
}
```

## Troubleshooting

### Issue: No Audio Output

**Possible causes**:
1. Volume is muted (check system volume)
2. Audio focus not acquired (add audio focus management)
3. Buffer underrun (increase buffer size)
4. Incorrect sample rate (try 48000 Hz - most common on Android)

**Solution**:
```csharp
// Use recommended settings
var config = AudioConfig.Default; // 48kHz, stereo, 512 frames
```

### Issue: High Latency

**Possible causes**:
1. Buffer size too large
2. Device doesn't support low-latency audio
3. Background processes consuming CPU

**Solution**:
```csharp
// Use low-latency configuration
var config = AudioConfig.LowLatency; // 48kHz, stereo, 128 frames

// Check if device supports low-latency
// (Oboe automatically selects best performance mode)
```

### Issue: Crackling/Popping Audio

**Possible causes**:
1. Buffer underruns (buffer too small)
2. CPU spikes on audio thread
3. GC pressure (allocations in callback)

**Solution**:
```csharp
// Increase buffer size
config.BufferSize = 512; // or 1024

// Ensure no allocations in Send() loop
// Pre-allocate all buffers
```

### Issue: Recording Fails

**Possible causes**:
1. Missing RECORD_AUDIO permission
2. Microphone in use by another app
3. Unsupported sample rate

**Solution**:
```csharp
// Check permission at runtime (Android 6.0+)
if (ContextCompat.CheckSelfPermission(this,
    Manifest.Permission.RecordAudio) != Permission.Granted)
{
    // Request permission
    ActivityCompat.RequestPermissions(this,
        new[] { Manifest.Permission.RecordAudio }, REQUEST_CODE);
}
```

## Implementation Details

### AAudio Callback Flow

1. **Initialization**: Create AAudioStreamBuilder with desired parameters
2. **Open Stream**: AAudioStreamBuilder_openStream creates native stream
3. **Set Callbacks**: Register data and error callbacks
4. **Start**: AAudioStream_requestStart begins audio processing
5. **Callback Execution**: AAudio calls OutputDataCallback on real-time thread
6. **Data Transfer**: Samples read from ring buffer, copied to native buffer
7. **Stop**: AAudioStream_requestStop terminates stream gracefully

### Memory Management

- **Pinned Buffers**: `GCHandle.Alloc(buffer, GCHandleType.Pinned)` prevents GC relocation
- **Native Pointers**: `IntPtr` to AAudioStream tracked and cleaned up
- **Ring Buffers**: Pre-allocated, fixed-size, lock-free SPSC queues
- **Zero Allocation**: No `new` in audio callback paths

### Thread Safety

- **Lock-Free Ring Buffers**: Single-producer, single-consumer design
- **Volatile State**: `volatile int` for state flags
- **State Lock**: `lock (_stateLock)` only for state transitions (Init/Start/Stop)
- **No Locks in Callbacks**: Audio callbacks never acquire locks

## API Reference

See [IAudioEngine.cs](../Ownaudio.Core/IAudioEngine.cs) for full interface documentation.

### Key Methods

- `Initialize(AudioConfig)` - Configure and prepare audio streams ⚠️ **BLOCKING**
- `Start()` - Begin audio processing
- `Stop()` - Stop audio processing ⚠️ **BLOCKING**
- `Send(Span<float>)` - Send audio for playback (may block if buffer full)
- `Receives(out float[])` - Receive recorded audio (may block if buffer empty)

### Key Properties

- `FramesPerBuffer` - Actual negotiated buffer size in frames
- `OwnAudioEngineActivate()` - Returns 1 if active, 0 if idle, -1 if error
- `OwnAudioEngineStopped()` - Returns 1 if stopped, 0 if running

## Building from Source

```bash
# Build Android project
dotnet build OwnAudioEngine/Ownaudio.Android/Ownaudio.Android.csproj -c Release

# Output: Ownaudio.Android.dll
# Includes: liboboe.so for all architectures (embedded)
```

## Native API Details

**AAudio**: Native Android audio API (part of Android OS since API 26)

**System Library**: `libaaudio.so` (provided automatically by Android)

**No External Dependencies**:
- ✅ No need to include native libraries in your APK
- ✅ No NDK compilation required
- ✅ No C++_shared dependency issues
- ✅ Works out-of-the-box on Android 8.0+ devices

**Supported Android Versions**:
- **Minimum**: Android 8.0 (API 26) - AAudio introduced
- **Recommended**: Android 9.0+ (API 28) - Performance improvements
- **Target**: Android 13+ (API 33) - Latest optimizations

**Architecture Support**:
All Android architectures are supported automatically:
- **arm64-v8a**: 64-bit ARM (modern devices)
- **armeabi-v7a**: 32-bit ARM (legacy devices)
- **x86**: 32-bit Intel (emulators)
- **x86_64**: 64-bit Intel (emulators)

The AAudio library is provided by the Android OS for all architectures - no per-ABI binaries needed.

## Known Limitations

1. **Device Enumeration**: Limited to default device (Android manages routing)
2. **Exclusive Mode**: Not available (Android uses shared mode)
3. **Manual Device Selection**: Not implemented (Android handles automatically)
4. **Sample Rate Changes**: Requires stream recreation (by design)
5. **Format Support**: Float32 only (internally converted if needed)

## Related Documentation

- [OwnAudioSharp Documentation](https://modernmube.github.io/OwnAudioSharp/)
- [AAudio API Guide](https://developer.android.com/ndk/guides/audio/aaudio/aaudio)
- [AAudio API Reference](https://developer.android.com/ndk/reference/group/audio)
- [Android Audio Latency](https://source.android.com/devices/audio/latency)
- [High-Performance Audio on Android](https://developer.android.com/ndk/guides/audio/)

## License

Copyright © 2025 Ownaudio Team

Part of the OwnAudioSharp project.
