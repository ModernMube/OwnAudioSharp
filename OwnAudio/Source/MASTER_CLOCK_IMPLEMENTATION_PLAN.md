# OwnAudioSharp Master Clock Szinkronizációs Rendszer - Implementációs Terv

## Összefoglaló

A jelenlegi GhostTrack-alapú szinkronizációs rendszer lecserélése professzionális DAW-stílusú master clock architektúrára, amely időbélyeg-alapú timeline kezelést, sample-pontos szinkronizációt, valamint realtime és offline rendering módokat támogat.

## Jelenlegi Architektúra

### Főbb Komponensek:
- **GhostTrackSource**: Csendes audio track master clock-ként
- **Observer Pattern**: FileSource implementálja az IGhostTrackObserver interfészt
- **AudioSynchronizer**: Sync group kezelés
- **Drift Correction**: 512 frame tolerancia (~10ms @ 48kHz)
- **MixThread**: AudioMixer.cs (526-638 sorok) - GhostTrack pozíció előreléptetés
- **CircularBuffer**: Lock-free SPSC buffer, 4x méret
- **SoundTouch**: Per-track tempo/pitch módosítás

### Szinkronizációs Folyamat:
```
AudioMixer.MixThread
  └─> GhostTrack.ReadSamples() [csend generálás]
  └─> FileSource.ReadSamples()
      └─> Drift check: if (abs(ghostPos - myPos) > 512) → ResyncTo()
      └─> CircularBuffer read
  └─> Mix → engine.Send() [BLOCKING]
```

## Új Architektúra: Master Clock Rendszer

### Design Döntések (User Requirements):
1. **Timeline tárolás**: Időbélyeg alapú (double seconds)
2. **Offline rendering**: Szekvenciális (determinisztikus)
3. **Dropout kezelés**: Event notification (callback)
4. **Tempo és clock**: Master clock fizikai sample pozíció (tempo-független)

### Architektúra Változások:

```
MasterClock [timestamp, sample position]
  └─> AudioMixer.MixThread
      ├─> Mode: Realtime
      │   └─> track.ReadSamplesAtTime(timestamp, buffer) → sikertelen? → silence + event
      ├─> Mode: Offline
      │   └─> WaitForTrackSamples(timestamp, buffer, timeout=5s) → blokkoló
      └─> Advance(frameCount) → master clock előreléptetés
```

## Implementációs Fázisok

### FÁZIS 1: Új Infrastruktúra Létrehozása (Nem Breaking Change)

**1.1 MasterClock Osztály** ✅ KRITIKUS
- **Fájl**: `OwnAudio/Source/Synchronization/MasterClock.cs` (ÚJ)
- **Funkciók**:
  - Timestamp tracking (double seconds)
  - Sample position tracking (long)
  - Konverziós metódusok: `TimestampToSamplePosition()`, `SamplePositionToTimestamp()`
  - Thread-safe: Interlocked operations sample pozícióra, lock timestamp-re
  - Rendering mode: `ClockMode.Realtime` / `ClockMode.Offline`
  - `Advance(frameCount)`: MixThread hívja minden buffer után
  - `SeekTo(timestamp)`: UI thread hívja
  - `Reset()`: Nullázás

**Kód váz**:
```csharp
public sealed class MasterClock : IDisposable
{
    private long _currentSamplePosition;    // Interlocked
    private double _currentTimestamp;       // lock-ed
    private readonly int _sampleRate;       // 48000 Hz
    private readonly int _channels;         // 2 (stereo)
    private volatile ClockMode _mode;
    private readonly object _positionLock = new();

    public long CurrentSamplePosition => Interlocked.Read(ref _currentSamplePosition);
    public double CurrentTimestamp { get { lock (_positionLock) { return _currentTimestamp; } } }

    public void Advance(int frameCount) { /* Interlocked.Add + lock update */ }
    public void SeekTo(double timestamp) { /* Interlocked.Exchange + lock */ }
}

public enum ClockMode { Realtime, Offline }
```

**1.2 IMasterClockSource Interface** ✅ KRITIKUS
- **Fájl**: `OwnAudio/Source/Interfaces/IMasterClockSource.cs` (ÚJ)
- **Funkciók**:
  - `ReadSamplesAtTime(timestamp, buffer, frameCount, out ReadResult)` → bool
  - `StartOffset` property (double seconds - track kezdési idő a timeline-on)
  - `AttachToClock(MasterClock)`, `DetachFromClock()`
  - `IsAttachedToClock` property

**Kód váz**:
```csharp
public interface IMasterClockSource : IAudioSource
{
    bool ReadSamplesAtTime(double masterTimestamp, Span<float> buffer,
                           int frameCount, out ReadResult result);
    double StartOffset { get; set; }
    bool IsAttachedToClock { get; }
    void AttachToClock(MasterClock clock);
    void DetachFromClock();
}

public struct ReadResult
{
    public bool Success;
    public int FramesRead;
    public string ErrorMessage;
}
```

**1.3 Dropout Event Rendszer** ✅ KRITIKUS
- **Fájl**: `OwnAudio/Source/Events/TrackDropoutEventArgs.cs` (ÚJ)
- **Funkciók**:
  - TrackId, TrackName, MasterTimestamp, MasterSamplePosition
  - MissedFrames, Reason (string)

**1.4 Performance Monitoring**
- **Fájl**: `OwnAudio/Source/Monitoring/TrackPerformanceMetrics.cs` (ÚJ)
- **Funkciók**:
  - CPU usage tracking (moving average)
  - Buffer fill percentage
  - Dropout count és timestamps
  - `RecordDropout()`, `RecordCpuSample()`, `UpdateBufferFill()`

**Tesztelés**: Unit tesztek MasterClock-ra, interface definíciókra

---

### FÁZIS 2: FileSource Refactoring (IMasterClockSource implementáció)

**2.1 FileSource.cs Módosítások** ✅ KRITIKUS
- **Fájl**: `OwnAudio/Source/Sources/FileSource.cs`
- **Változtatások**:
  - Implementálja `IMasterClockSource` interfészt
  - `private MasterClock? _masterClock` field
  - `private double _startOffset` field
  - `private double _trackLocalTime` field (track saját timeline pozíció)

**2.2 ReadSamplesAtTime() Implementáció** ✅ KRITIKUS
- **Logika**:
  1. `relativeTimestamp = masterTimestamp - _startOffset`
  2. Ha `relativeTimestamp < 0` → silence (track még nem kezdődött)
  3. `targetTrackTime = relativeTimestamp * _tempo` (tempo mapping)
  4. Drift check: `abs(targetTrackTime - _trackLocalTime) > 0.010s` → Seek()
  5. CircularBuffer read
  6. Update `_trackLocalTime += framesRead * frameDuration`
  7. Ha underrun → silence fill + `ReadResult.Success = false`
  8. ApplyVolume()

**Kód váz**:
```csharp
public bool ReadSamplesAtTime(double masterTimestamp, Span<float> buffer,
                              int frameCount, out ReadResult result)
{
    // 1. Calculate track-local timestamp
    double relativeTimestamp = masterTimestamp - _startOffset;

    // 2. Before track start → silence
    if (relativeTimestamp < 0) {
        FillWithSilence(buffer, frameCount * _streamInfo.Channels);
        result = new ReadResult { Success = true, FramesRead = frameCount };
        return true;
    }

    // 3. Convert to track-local time (tempo mapping)
    double targetTrackTime = relativeTimestamp * _tempo;

    // 4. Drift correction
    double drift = Math.Abs(targetTrackTime - _trackLocalTime);
    if (drift > 0.010) { // 10ms tolerance
        Seek(targetTrackTime);
        _trackLocalTime = targetTrackTime;
    }

    // 5. Read from buffer
    int samplesRead = _buffer.Read(buffer.Slice(0, frameCount * _streamInfo.Channels));
    int framesRead = samplesRead / _streamInfo.Channels;

    // 6. Update track time
    if (framesRead > 0) {
        _trackLocalTime += framesRead / (double)_streamInfo.SampleRate;
    }

    // 7. Underrun check
    if (framesRead < frameCount && !_isEndOfStream) {
        FillWithSilence(buffer.Slice(samplesRead), (frameCount - framesRead) * _streamInfo.Channels);
        result = new ReadResult {
            Success = false,
            FramesRead = framesRead,
            ErrorMessage = "Buffer underrun"
        };
        return false; // Dropout
    }

    // 8. Volume
    ApplyVolume(buffer, frameCount * _streamInfo.Channels);

    result = new ReadResult { Success = true, FramesRead = framesRead };
    return true;
}
```

**2.3 Backward Compatibility**
- Megtartani `ReadSamples(buffer, frameCount)` metódust
- Ha `_masterClock != null` → delegate to `ReadSamplesAtTime()`
- Különben legacy behavior

**Tesztelés**:
- Unit teszt: tempo mapping (tempo=2.0x → 1s master time = 2s track time)
- Unit teszt: start offset (offset=2s, read@t=1s → silence)
- Unit teszt: drift correction

---

### FÁZIS 3: AudioMixer Refactoring (MixThread átírás)

**3.1 AudioMixer.cs Osztály Szintű Változások** ✅ KRITIKUS
- **Fájl**: `OwnAudio/Source/Mixing/AudioMixer.cs`
- **Új fieldk**:
  - `private readonly MasterClock _masterClock`
  - `private readonly Dictionary<Guid, TrackPerformanceMetrics> _trackMetrics`
- **Új property**:
  - `public ClockMode RenderingMode { get; set; }`
- **Új event**:
  - `public event EventHandler<TrackDropoutEventArgs>? TrackDropout`

**3.2 MixThreadLoop() Teljes Átírás** ✅ KRITIKUS
- **Fájl**: `OwnAudio/Source/Mixing/AudioMixer.cs` (526-638 sorok)
- **Új folyamat**:

```
1. Get currentTimestamp from MasterClock
2. IF RenderingMode == Realtime:
     MixSourcesRealtime() → non-blocking, dropout → silence + event
   ELSE:
     MixSourcesOffline() → blocking, WaitForTrackSamples(timeout=5s)
3. ApplyMasterVolume()
4. ApplyMasterEffects()
5. CalculatePeakLevels()
6. WriteToRecorder()
7. engine.Send() [BLOCKING - natural timing]
8. masterClock.Advance(frameCount)
9. Loop
```

**Kód váz**:
```csharp
private void MixThreadLoop()
{
    float[] mixBuffer = new float[_bufferSizeInFrames * _config.Channels];
    float[] sourceBuffer = new float[_bufferSizeInFrames * _config.Channels];

    while (!_shouldStop)
    {
        if (!_isRunning) { _pauseEvent.Wait(100); continue; }

        Array.Clear(mixBuffer);
        double currentTimestamp = _masterClock.CurrentTimestamp;

        int activeSources = (_masterClock.Mode == ClockMode.Realtime)
            ? MixSourcesRealtime(mixBuffer, sourceBuffer, currentTimestamp, ...)
            : MixSourcesOffline(mixBuffer, sourceBuffer, currentTimestamp);

        if (activeSources > 0) {
            ApplyMasterVolume(mixBuffer.AsSpan());
            ApplyMasterEffects(mixBuffer.AsSpan(), _bufferSizeInFrames);
            CalculatePeakLevels(mixBuffer.AsSpan());
            if (_isRecording) WriteToRecorder(mixBuffer.AsSpan());

            _engine.Send(mixBuffer.AsSpan());
            _masterClock.Advance(_bufferSizeInFrames);
        }
    }
}
```

**3.3 MixSourcesRealtime() Implementáció**
- Loop through sources
- Ha `source is IMasterClockSource` → `ReadSamplesAtTime()`
- Ha `success == false` → dropout event + metrics
- Különben legacy `ReadSamples()`

**3.4 MixSourcesOffline() Implementáció**
- Szekvenciális: for each source
- `WaitForTrackSamples()` hívás timeout-tal (5000ms)
- Retry loop: ha fail → Thread.Sleep(1) + retry
- Timeout után → error

**Tesztelés**:
- Integration teszt: multi-track realtime mixing
- Integration teszt: offline rendering
- Performance teszt: dropout event firing

---

### FÁZIS 4: Deprecation és Backward Compatibility

**KRITIKUS: 100% Backward Compatible implementáció v2.4.0-ban!**

**4.1 Meglévő Osztályok Jelölése [Obsolete]-ként**
- **Fájlok**:
  - `OwnAudio/Source/Sources/GhostTrackSource.cs`
  - `OwnAudio/Source/Interfaces/IGhostTrackObserver.cs`
  - `OwnAudio/Source/Synchronization/AudioSynchronizer.cs`

**Kód példák [Obsolete] attribútumokkal:**

```csharp
// GhostTrackSource.cs - CLASS SZINTEN
[Obsolete("GhostTrackSource is deprecated. Use MasterClock with IMasterClockSource instead. " +
          "This class will be removed in v3.0.0. " +
          "See migration guide: https://github.com/modernmube/OwnAudioSharp/wiki/MasterClock-Migration",
          error: false)]
public class GhostTrackSource : BaseAudioSource, ISynchronizable
{
    // ... meglévő kód VÁLTOZATLAN ...
}

// IGhostTrackObserver.cs - INTERFACE SZINTEN
[Obsolete("IGhostTrackObserver is deprecated. Implement IMasterClockSource instead. " +
          "This interface will be removed in v3.0.0.",
          error: false)]
public interface IGhostTrackObserver
{
    // ... meglévő kód VÁLTOZATLAN ...
}

// AudioSynchronizer.cs - CLASS SZINTEN
[Obsolete("AudioSynchronizer is deprecated. Use AudioMixer.MasterClock directly. " +
          "This class will be removed in v3.0.0.",
          error: false)]
public class AudioSynchronizer
{
    // ... meglévő kód VÁLTOZATLAN ...
}
```

**4.2 AudioMixer Dual Mode Implementáció** ✅ KRITIKUS

**4.2.1 AudioMixer.cs Class Fields (Dual Support):**

```csharp
public sealed partial class AudioMixer : IDisposable
{
    // LEGACY: AudioSynchronizer support (deprecated but functional)
    [Obsolete("Use MasterClock property instead")]
    private readonly AudioSynchronizer? _synchronizer;

    // NEW: MasterClock support
    private readonly MasterClock _masterClock;
    private readonly Dictionary<Guid, TrackPerformanceMetrics> _trackMetrics;

    // Public API: MasterClock property
    public MasterClock MasterClock => _masterClock;

    // Constructor: inicializálja MINDKETTŐT
    public AudioMixer(IAudioEngine engine, int bufferSizeInFrames = 512)
    {
        // ... existing fields ...

        // Legacy támogatás (deprecated de működik)
        _synchronizer = new AudioSynchronizer();

        // Új MasterClock
        _masterClock = new MasterClock(
            sampleRate: 48000,
            channels: 2,
            mode: ClockMode.Realtime);

        _trackMetrics = new Dictionary<Guid, TrackPerformanceMetrics>();
    }

    // LEGACY API (megtartva, deprecated)
    [Obsolete("Use IMasterClockSource.AttachToClock() instead. This method will be removed in v3.0.0.")]
    public Guid CreateSyncGroup(IEnumerable<IAudioSource> sources)
    {
        if (_synchronizer == null)
            throw new InvalidOperationException("Synchronizer not available");

        return _synchronizer.CreateSyncGroup(sources);
    }

    [Obsolete("Use AudioMixer.Start() with IMasterClockSource tracks instead. This method will be removed in v3.0.0.")]
    public void StartSyncGroup(Guid groupId)
    {
        _synchronizer?.StartSyncGroup(groupId);
    }

    [Obsolete("Use AudioMixer.Stop() instead. This method will be removed in v3.0.0.")]
    public void StopSyncGroup(Guid groupId)
    {
        _synchronizer?.StopSyncGroup(groupId);
    }
}
```

**4.2.2 MixThreadLoop() - Automatikus Felismerés:**

```csharp
private void MixThreadLoop()
{
    float[] mixBuffer = new float[_bufferSizeInFrames * _config.Channels];
    float[] sourceBuffer = new float[_bufferSizeInFrames * _config.Channels];

    while (!_shouldStop)
    {
        if (!_isRunning) { _pauseEvent.Wait(100); continue; }

        Array.Clear(mixBuffer);

        // Get current timestamp from MasterClock
        double currentTimestamp = _masterClock.CurrentTimestamp;

        // DUAL MODE MIXING: automatikus felismerés
        int activeSources = (_masterClock.Mode == ClockMode.Realtime)
            ? MixSourcesRealtime(mixBuffer, sourceBuffer, currentTimestamp)
            : MixSourcesOffline(mixBuffer, sourceBuffer, currentTimestamp);

        if (activeSources > 0) {
            ApplyMasterVolume(mixBuffer.AsSpan());
            ApplyMasterEffects(mixBuffer.AsSpan(), _bufferSizeInFrames);
            CalculatePeakLevels(mixBuffer.AsSpan());
            if (_isRecording) WriteToRecorder(mixBuffer.AsSpan());

            _engine.Send(mixBuffer.AsSpan());
            _masterClock.Advance(_bufferSizeInFrames);
        }

        // LEGACY: Advance GhostTracks if any exist (deprecated path)
        AdvanceLegacyGhostTracks();
    }
}

// LEGACY support method
private void AdvanceLegacyGhostTracks()
{
    if (_synchronizer == null) return;

    var syncGroupIds = _synchronizer.GetSyncGroupIds();
    foreach (var groupId in syncGroupIds)
    {
        var ghostTrack = _synchronizer.GetGhostTrack(groupId);
        if (ghostTrack != null && ghostTrack.State == AudioState.Playing)
        {
            // Advance legacy GhostTrack
            float[] tempBuffer = new float[_bufferSizeInFrames * _config.Channels];
            ghostTrack.ReadSamples(tempBuffer, _bufferSizeInFrames);
        }
    }
}
```

**4.2.3 MixSourcesRealtime() - Automatikus Interface Detection:**

```csharp
private int MixSourcesRealtime(float[] mixBuffer, float[] sourceBuffer, double timestamp)
{
    int activeSources = 0;

    for (int i = 0; i < _cachedSourcesArray.Length; i++)
    {
        var source = _cachedSourcesArray[i];

        try
        {
            if (source.State != AudioState.Playing)
                continue;

            // PRIORITY 1: NEW IMasterClockSource (ha attached to clock)
            if (source is IMasterClockSource clockSource && clockSource.IsAttachedToClock)
            {
                bool success = clockSource.ReadSamplesAtTime(
                    timestamp,
                    sourceBuffer.AsSpan(),
                    _bufferSizeInFrames,
                    out ReadResult result);

                if (success && result.FramesRead > 0)
                {
                    MixIntoBuffer(mixBuffer, sourceBuffer, result.FramesRead * _config.Channels);
                    activeSources++;
                }
                else
                {
                    // Dropout occurred
                    OnTrackDropout(new TrackDropoutEventArgs(
                        source.Id,
                        source.GetType().Name,
                        timestamp,
                        _masterClock.CurrentSamplePosition,
                        _bufferSizeInFrames - result.FramesRead,
                        result.ErrorMessage ?? "Buffer underrun"));

                    if (_trackMetrics.TryGetValue(source.Id, out var metrics))
                    {
                        metrics.RecordDropout(timestamp, _bufferSizeInFrames - result.FramesRead);
                    }
                }
            }
            // PRIORITY 2: LEGACY IAudioSource (GhostTrack sync vagy standalone)
            else
            {
                // Legacy path: használja a meglévő ReadSamples() metódust
                int framesRead = source.ReadSamples(sourceBuffer, _bufferSizeInFrames);

                if (framesRead > 0)
                {
                    MixIntoBuffer(mixBuffer, sourceBuffer, framesRead * _config.Channels);
                    activeSources++;
                }
            }
        }
        catch (Exception ex)
        {
            OnSourceError(source, new AudioErrorEventArgs(
                $"Error reading from source {source.Id}: {ex.Message}", ex));
        }
    }

    return activeSources;
}
```

**4.3 FileSource Dual Mode Implementáció** ✅ KRITIKUS

**4.3.1 FileSource.cs - Dual Interface Support:**

```csharp
public partial class FileSource : BaseAudioSource,
    ISynchronizable,           // LEGACY (megtartva)
    IMasterClockSource         // NEW
{
    // LEGACY: GhostTrack support (deprecated but functional)
    private GhostTrackSource? _ghostTrack = null;

    // NEW: MasterClock support
    private MasterClock? _masterClock = null;
    private double _startOffset = 0.0;
    private double _trackLocalTime = 0.0;

    // NEW: IMasterClockSource implementation
    public double StartOffset
    {
        get => _startOffset;
        set => _startOffset = value;
    }

    public bool IsAttachedToClock => _masterClock != null;

    public void AttachToClock(MasterClock clock)
    {
        if (clock == null)
            throw new ArgumentNullException(nameof(clock));

        // Detach from GhostTrack if attached (cannot use both)
        if (_ghostTrack != null)
        {
            DetachFromGhostTrack();
        }

        DetachFromClock();  // Detach from previous clock if any
        _masterClock = clock;
        _trackLocalTime = 0.0;
    }

    public void DetachFromClock()
    {
        _masterClock = null;
    }

    // LEGACY: GhostTrack support (megtartva)
    [Obsolete("Use AttachToClock(MasterClock) instead")]
    public void AttachToGhostTrack(GhostTrackSource ghostTrack)
    {
        if (ghostTrack == null)
            throw new ArgumentNullException(nameof(ghostTrack));

        // Detach from MasterClock if attached (cannot use both)
        if (_masterClock != null)
        {
            DetachFromClock();
        }

        DetachFromGhostTrack();
        _ghostTrack = ghostTrack;
        _ghostTrack.Attach(this);
    }

    [Obsolete("Use DetachFromClock() instead")]
    public void DetachFromGhostTrack()
    {
        if (_ghostTrack != null)
        {
            _ghostTrack.Detach(this);
            _ghostTrack = null;
        }
    }
}
```

**4.3.2 FileSource ReadSamples() - Backward Compatible Fallback:**

```csharp
// EXISTING METHOD - Enhanced with dual mode support
public override int ReadSamples(Span<float> buffer, int frameCount)
{
    ThrowIfDisposed();

    // PRIORITY 1: If attached to MasterClock, delegate to new API
    if (_masterClock != null)
    {
        bool success = ReadSamplesAtTime(
            _masterClock.CurrentTimestamp,
            buffer,
            frameCount,
            out ReadResult result);

        return result.FramesRead;
    }

    // PRIORITY 2: If attached to GhostTrack, use legacy sync logic
    if (_ghostTrack != null)
    {
        // EXISTING GHOST TRACK SYNC CODE (lines 186-283 original logic)

        // Check drift with GhostTrack
        if (_buffer.Available > 0)
        {
            long ghostPosition = _ghostTrack.CurrentFrame;
            long myPosition = SamplePosition;
            long drift = Math.Abs(ghostPosition - myPosition);

            if (drift > 512)  // ~10ms @ 48kHz
            {
                ResyncTo(ghostPosition);  // Immediate resync
            }
        }

        // ... REST OF EXISTING CODE ...
    }

    // PRIORITY 3: Standalone mode (no sync)
    // EXISTING CODE for standalone playback
    int samplesToRead = frameCount * _streamInfo.Channels;
    int samplesRead = _buffer.Read(buffer.Slice(0, samplesToRead));
    int framesRead = samplesRead / _streamInfo.Channels;

    if (framesRead > 0)
    {
        UpdateSamplePosition(framesRead);
        double frameDuration = 1.0 / _streamInfo.SampleRate;
        double newPosition = _currentPosition + (framesRead * frameDuration);
        Interlocked.Exchange(ref _currentPosition, newPosition);
    }

    // Apply volume
    ApplyVolume(buffer, frameCount * _streamInfo.Channels);

    return framesRead;
}
```

**4.3.3 FileSource ReadSamplesAtTime() - NEW Implementation:**

```csharp
// NEW METHOD - IMasterClockSource implementation
public bool ReadSamplesAtTime(double masterTimestamp, Span<float> buffer,
                              int frameCount, out ReadResult result)
{
    ThrowIfDisposed();

    // 1. Calculate track-local timestamp
    double relativeTimestamp = masterTimestamp - _startOffset;

    // 2. Before track start → silence
    if (relativeTimestamp < 0)
    {
        FillWithSilence(buffer, frameCount * _streamInfo.Channels);
        result = new ReadResult { Success = true, FramesRead = frameCount, ErrorMessage = null };
        return true;
    }

    // 3. Convert to track-local time (tempo mapping)
    double targetTrackTime = relativeTimestamp * _tempo;

    // 4. Drift correction
    double drift = Math.Abs(targetTrackTime - _trackLocalTime);
    if (drift > 0.010) // 10ms tolerance
    {
        if (!Seek(targetTrackTime))
        {
            result = new ReadResult { Success = false, FramesRead = 0, ErrorMessage = "Seek failed" };
            return false;
        }
        _trackLocalTime = targetTrackTime;
    }

    // 5. Read from buffer
    int samplesRead = _buffer.Read(buffer.Slice(0, frameCount * _streamInfo.Channels));
    int framesRead = samplesRead / _streamInfo.Channels;

    // 6. Update track time
    if (framesRead > 0)
    {
        _trackLocalTime += framesRead / (double)_streamInfo.SampleRate;
        UpdateSamplePosition(framesRead);
    }

    // 7. Underrun check
    if (framesRead < frameCount && !_isEndOfStream)
    {
        FillWithSilence(buffer.Slice(samplesRead), (frameCount - framesRead) * _streamInfo.Channels);
        result = new ReadResult
        {
            Success = false,
            FramesRead = framesRead,
            ErrorMessage = "Buffer underrun"
        };
        return false; // Dropout
    }

    // 8. Volume
    ApplyVolume(buffer, frameCount * _streamInfo.Channels);

    result = new ReadResult { Success = true, FramesRead = framesRead, ErrorMessage = null };
    return true;
}

// Helper method
private void FillWithSilence(Span<float> buffer, int sampleCount)
{
    buffer.Slice(0, sampleCount).Fill(0.0f);
}
```

**4.4 Compiler Warnings és User Experience**

**4.4.1 Compiler Warning Példák:**

```csharp
// User code v2.4.0-ban:
var mixer = new AudioMixer(engine);
var syncGroup = mixer.CreateSyncGroup(new[] { track1, track2 });
// ⚠️ CS0618: 'AudioMixer.CreateSyncGroup(IEnumerable<IAudioSource>)' is obsolete:
//    'Use IMasterClockSource.AttachToClock() instead. This method will be removed in v3.0.0.'

var ghost = new GhostTrackSource(0.0, 48000, 2);
// ⚠️ CS0618: 'GhostTrackSource' is obsolete:
//    'GhostTrackSource is deprecated. Use MasterClock with IMasterClockSource instead.
//     This class will be removed in v3.0.0.
//     See migration guide: https://github.com/modernmube/OwnAudioSharp/wiki/MasterClock-Migration'
```

**4.4.2 Vegyes használat példa (OLD + NEW együtt):**

```csharp
// v2.4.0: Régi és új API együtt működik
var mixer = new AudioMixer(engine);

// Régi track: GhostTrack sync
var oldTrack1 = new FileSource("old1.wav");
var oldTrack2 = new FileSource("old2.wav");
#pragma warning disable CS0618 // Obsolete warning
var syncGroup = mixer.CreateSyncGroup(new[] { oldTrack1, oldTrack2 });
mixer.StartSyncGroup(syncGroup);
#pragma warning restore CS0618

// Új track: MasterClock sync
var newTrack1 = new FileSource("new1.wav");
var newTrack2 = new FileSource("new2.wav");
newTrack1.AttachToClock(mixer.MasterClock);
newTrack2.AttachToClock(mixer.MasterClock);
newTrack2.StartOffset = 2.0;  // Új feature

// Mindkettő hozzáadva a mixer-hez
mixer.AddSource(oldTrack1);
mixer.AddSource(oldTrack2);
mixer.AddSource(newTrack1);
mixer.AddSource(newTrack2);

mixer.Start();

// ✅ MŰKÖDIK: régi és új track-ok együtt!
```

**4.5 Dokumentáció Update**
- README.md: új master clock használat
- API dokumentáció: migration guide
- Examples update: SimplePlayer, Multitrack példák
- Wiki: Részletes migration guide v2.4.0 → v3.0.0

**4.6 Migration Guide Template:**

Létrehozandó: `OwnAudio/Source/MIGRATION_GUIDE_v3.md`

```markdown
# Migration Guide: v2.4.0 → v3.0.0

## Backward Compatibility Timeline

- **v2.4.0** (2025 Q1): Transition - mindkét API működik
- **v3.0.0** (2025 Q3): Breaking change - csak MasterClock

## Quick Migration Examples

### Before (v2.1.0 - GhostTrack):
```csharp
var synchronizer = new AudioSynchronizer();
var syncGroup = synchronizer.CreateSyncGroup(new[] { track1, track2 });
synchronizer.StartSyncGroup(syncGroup);
```

### After (v2.4.0+ - MasterClock):
```csharp
var mixer = new AudioMixer(engine);
track1.AttachToClock(mixer.MasterClock);
track2.AttachToClock(mixer.MasterClock);
mixer.Start();
```

## API Mapping Table

| Old API (Deprecated) | New API (v2.4.0+) |
|---------------------|-------------------|
| `GhostTrackSource` | `MasterClock` |
| `IGhostTrackObserver` | `IMasterClockSource` |
| `AudioSynchronizer.CreateSyncGroup()` | `FileSource.AttachToClock()` |
| `OnGhostTrackStateChanged()` | Automatic (drift correction) |
| N/A | `StartOffset` property (new feature) |
| N/A | `ClockMode.Realtime/Offline` (new feature) |
| N/A | `TrackDropout` event (new feature) |

## Step-by-Step Migration

1. Update to v2.4.0
2. Replace GhostTrack with MasterClock (warnings guide you)
3. Test thoroughly
4. Upgrade to v3.0.0 when ready
```

---

### FÁZIS 5: Testing és Validation

**5.1 Unit Tesztek**
- **Fájl**: `OwnAudioTests/Ownaudio.OwnaudioNET/MasterClockTests.cs` (ÚJ)
- Tesztek:
  - `MasterClock_AdvanceUpdatesPosition()`
  - `MasterClock_SeekToUpdatesPosition()`
  - `MasterClock_ThreadSafety()` (concurrent read/write)
  - `FileSource_ReadSamplesAtTime_AccountsForTempo()`
  - `FileSource_ReadSamplesAtTime_RespectStartOffset()`

**5.2 Integration Tesztek**
- **Fájl**: `OwnAudioTests/Ownaudio.OwnaudioNET/MultiTrackSyncTests.cs` (ÚJ)
- Tesztek:
  - `AudioMixer_SynchronizesMultipleTracks_Realtime()`
  - `AudioMixer_SynchronizesMultipleTracks_Offline()`
  - `AudioMixer_HandlesDropouts()`
  - `AudioMixer_SeekAllTracks()`

**5.3 Performance Tesztek**
- `MasterClock_AdvancePerformance()` → < 100ms for 1M operations
- `FileSource_ReadSamplesAtTime_ZeroAllocation()` → GC pressure check

---

## Kritikus Fájlok Összefoglalása

### Új Fájlok (5):
1. ✅ **OwnAudio/Source/Synchronization/MasterClock.cs** (KRITIKUS)
2. ✅ **OwnAudio/Source/Interfaces/IMasterClockSource.cs** (KRITIKUS)
3. ✅ **OwnAudio/Source/Events/TrackDropoutEventArgs.cs** (KRITIKUS)
4. **OwnAudio/Source/Monitoring/TrackPerformanceMetrics.cs**
5. **OwnAudioTests/Ownaudio.OwnaudioNET/MasterClockTests.cs**

### Módosítandó Fájlok (3):
1. ✅ **OwnAudio/Source/Mixing/AudioMixer.cs** (MAJOR REFACTOR - 526-638 sorok + class fields)
2. ✅ **OwnAudio/Source/Sources/FileSource.cs** (MAJOR REFACTOR - új interface impl)
3. **OwnAudio/Source/Sources/BaseAudioSource.cs** (minor - backwards compat)

### Deprecated Fájlok (3):
1. **OwnAudio/Source/Sources/GhostTrackSource.cs** → [Obsolete]
2. **OwnAudio/Source/Interfaces/IGhostTrackObserver.cs** → [Obsolete]
3. **OwnAudio/Source/Synchronization/AudioSynchronizer.cs** → [Obsolete]

---

## Thread Safety Stratégia

### MasterClock:
- **Sample position**: `Interlocked` operations (lock-free, single writer - MixThread)
- **Timestamp**: `lock` (rare writes - SeekTo, frequent reads)
- **Rationale**: MixThread calls `Advance()` frequently → lock-free critical path

### FileSource:
- **CircularBuffer**: Meglévő SPSC lock-free design (decoder thread → MixThread)
- **ReadSamplesAtTime**: Csak MixThread hívja → nincs extra lock
- **AttachToClock/DetachFromClock**: UI thread hívja → check `_masterClock != null` volatile read

### AudioMixer:
- **_sources**: Meglévő `ConcurrentDictionary` (thread-safe add/remove)
- **_trackMetrics**: `Dictionary` + lock (metrics ritkán módosulnak)

---

## Tempo Mapping Magyarázat

**Master Clock pozíció**: Fizikai sample pozíció (tempo-független)

**Track local time mapping**:
```
Master Clock Time (t) = Fizikai idő másodpercben
Track Local Time = (t - startOffset) * tempo

Példa:
- Master clock: t = 10s
- Track: startOffset = 2s, tempo = 2.0x
- Track local time = (10 - 2) * 2.0 = 16s
- Track a 16. másodpercnél van a saját audio fájljában
```

**SoundTouch integráció**:
- FileSource decoder thread olvassa a nyers sample-öket
- Ha `tempo != 1.0` → SoundTouch processing
- SoundTouch output → CircularBuffer
- ReadSamplesAtTime() olvassa a CircularBuffer-ből (már processált sample-ök)

---

## Sample Rate Kezelés

**Master Clock**: Fix 48000 Hz
**Source files**: Változó sample rate

**Konverzió**:
- `AudioDecoderFactory.Create(targetSampleRate: 48000)` → minden decoder 48kHz-en outputol
- Meglévő resampling funkció a decoder layerben
- FileSource nem csinál extra resample-t

---

## Seeking Implementáció

```csharp
public void SeekAllTracks(double timestamp)
{
    // 1. Master clock seek
    _masterClock.SeekTo(timestamp);

    // 2. Tracks automatikusan resync-elnek
    // A következő ReadSamplesAtTime() híváskor drift correction kezeli
    // Nincs explicit seek szükséges minden track-en
}
```

**Rationale**: Drift correction automatikusan kezeli a resync-et, nem kell explicit Seek() minden track-en.

---

## Migration Path

### v2.3.0 → v2.4.0 (Transition):
- Új MasterClock rendszer hozzáadva
- GhostTrack megtartva, de deprecated
- Dual mode: mindkét rendszer működik
- Backward compatible

### v3.0.0 (Breaking Change):
- GhostTrack, IGhostTrackObserver, AudioSynchronizer eltávolítva
- Csak MasterClock rendszer
- Minden source implementálja `IMasterClockSource`-t
- Migration guide a dokumentációban

---

## Performance Célok

- **MasterClock.Advance()**: < 10 µs (lock-free)
- **ReadSamplesAtTime()**: < 50 µs overhead a legacy ReadSamples()-hez képest
- **Zero allocation**: ReadSamplesAtTime() hot path-ban nincs GC allocation
- **Dropout latency**: < 1ms event firing
- **Offline rendering**: CPU-bound (nincs realtime constraint)

---

## User-Facing API Példák

### Realtime Mixing:
```csharp
var engine = AudioEngineFactory.CreateDefault();
await Task.Run(() => engine.Initialize(new AudioConfig { SampleRate = 48000 }));

var mixer = new AudioMixer(engine);
mixer.RenderingMode = ClockMode.Realtime;

var track1 = new FileSource("drums.wav");
var track2 = new FileSource("vocals.wav");
track2.StartOffset = 2.0; // Vocals start at 2s

mixer.AddSource(track1);
mixer.AddSource(track2);

mixer.TrackDropout += (s, e) => {
    Console.WriteLine($"Dropout: {e.TrackName} at {e.MasterTimestamp:F3}s");
};

mixer.Start();
```

### Offline Rendering:
```csharp
var engine = new NullAudioEngine(48000, 2); // No hardware output
var mixer = new AudioMixer(engine);
mixer.RenderingMode = ClockMode.Offline; // Blocking mode

mixer.AddSource(track1);
mixer.AddSource(track2);
mixer.StartRecording("output.wav");
mixer.Start();

// Wait for completion
while (mixer.MasterClock.CurrentTimestamp < 30.0) {
    Thread.Sleep(100);
}

mixer.Stop();
mixer.StopRecording();
```

---

## Összefoglalás

Ez a terv professional DAW-stílusú master clock szinkronizációt biztosít:

✅ **Időbélyeg-alapú timeline** (double seconds)
✅ **Sample-pontos szinkronizáció** (conversion to sample position)
✅ **Realtime mode** (dropout → silence + event)
✅ **Offline mode** (blocking, deterministic)
✅ **Tempo-független master clock** (physical sample position)
✅ **Per-track tempo** (SoundTouch integration)
✅ **Start offsets** (DAW regions)
✅ **Backward compatible** (dual mode támogatás)
✅ **Zero-allocation hot path** (Span<T>, object pools)
✅ **Thread-safe** (Interlocked, lock-free where possible)

**Becsült implementációs idő**: 3-4 hét (2 fejlesztő)
- Fázis 1-2: 1 hét
- Fázis 3: 1 hét
- Fázis 4-5: 1-2 hét (testing, docs)
