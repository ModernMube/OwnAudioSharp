using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using Avalonia.Rendering;
using Ownaudio;
using Ownaudio.Core;
using Ownaudio.Monitoring;
using Ownaudio.Synchronization;
using OwnaudioNET.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Synchronization;

namespace OwnaudioNET.Mixing;

/// <summary>
/// Central audio mixing engine that combines multiple audio sources into a single output stream.
/// Provides dynamic source management, master volume control, level metering, and recording functionality.
///
/// Architecture:
/// - Main Thread: User API calls (AddSource, RemoveSource, Start, Stop, etc.)
/// - Mix Thread: High-priority thread that mixes sources and sends to AudioEngineWrapper
/// - Source Threads: Each FileSource/InputSource has its own background thread
///
/// Thread Safety:
/// - All public methods are thread-safe
/// - Source list uses ConcurrentDictionary for lock-free add/remove
/// - Master volume uses volatile field
/// </summary>
public sealed partial class AudioMixer : IDisposable
{
    // Engine integration
    private readonly IAudioEngine _engine;

    // Source management
    private readonly ConcurrentDictionary<Guid, IAudioSource> _sources;
    private IAudioSource[] _cachedSourcesArray = Array.Empty<IAudioSource>();  // OPTIMIZATION: Cache to avoid ConcurrentDictionary.Values allocation
    private volatile bool _sourcesArrayNeedsUpdate = true;  // Flag to update cache when sources change

    // Synchronization (LEGACY - deprecated but functional)
    private readonly AudioSynchronizer _synchronizer;

    // NEW: Master Clock System (v2.4.0+)
    private readonly MasterClock _masterClock;
    private readonly Dictionary<Guid, TrackPerformanceMetrics> _trackMetrics;
    private readonly object _metricsLock = new();

    // Mix thread
    private readonly Thread _mixThread;
    private readonly ManualResetEventSlim _pauseEvent;
    private volatile bool _shouldStop;
    private volatile bool _isRunning;

    // Configuration
    private readonly AudioConfig _config;
    private readonly int _bufferSizeInFrames;
    private readonly int _mixIntervalMs;

    // Master controls
    private volatile float _masterVolume;

    // Level metering (peak levels in last mix cycle)
    private volatile float _leftPeak;
    private volatile float _rightPeak;

    // Statistics
    private long _totalMixedFrames;
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value
    private long _totalUnderruns;
#pragma warning restore CS0649

    // Recording
    private WaveFileWriter? _recorder;
    private readonly object _recorderLock = new();
    private volatile bool _isRecording;

    // Effect chain (master effects applied to final mix)
    private readonly List<IEffectProcessor> _masterEffects;
    private readonly object _effectsLock = new();
    private IEffectProcessor[] _cachedEffects = Array.Empty<IEffectProcessor>(); // Cached snapshot to avoid ToArray() in hot path
    private volatile bool _effectsChanged = false; // Flag to indicate effects list changed

    // Dispose flag
    private bool _disposed;

    /// <summary>
    /// Gets the audio configuration being used by the mixer.
    /// </summary>
    public AudioConfig Config => _config;

    /// <summary>
    /// Gets whether the mixer is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Gets the number of active sources.
    /// </summary>
    public int SourceCount => _sources.Count;

    /// <summary>
    /// Gets or sets the master volume (0.0 to 1.0).
    /// Applied to the final mixed output.
    /// </summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Clamp(value, 0.0f, 1.0f);
    }

    /// <summary>
    /// Gets the peak level for the left channel in the last mix cycle (-1.0 to 1.0).
    /// </summary>
    public float LeftPeak => _leftPeak;

    /// <summary>
    /// Gets the peak level for the right channel in the last mix cycle (-1.0 to 1.0).
    /// </summary>
    public float RightPeak => _rightPeak;

    /// <summary>
    /// Gets the total number of frames mixed since start.
    /// </summary>
    public long TotalMixedFrames => Interlocked.Read(ref _totalMixedFrames);

    /// <summary>
    /// Gets the total number of underrun events.
    /// </summary>
    public long TotalUnderruns => Interlocked.Read(ref _totalUnderruns);

    /// <summary>
    /// Gets whether recording is currently active.
    /// </summary>
    public bool IsRecording => _isRecording;

    /// <summary>
    /// Gets the master clock for timeline-based synchronization (NEW - v2.4.0+).
    /// </summary>
    public MasterClock MasterClock => _masterClock;

    /// <summary>
    /// Gets or sets the rendering mode (Realtime or Offline) (NEW - v2.4.0+).
    /// </summary>
    public ClockMode RenderingMode
    {
        get => _masterClock.Mode;
        set => _masterClock.Mode = value;
    }

    /// <summary>
    /// Event raised when a buffer underrun occurs (audio dropout).
    /// </summary>
#pragma warning disable CS0067 // Event is never used
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;
#pragma warning restore CS0067

    /// <summary>
    /// Event raised when a source error occurs during mixing.
    /// </summary>
    public event EventHandler<AudioErrorEventArgs>? SourceError;

    /// <summary>
    /// Event raised when a track dropout occurs during master clock synchronized playback (NEW - v2.4.0+).
    /// </summary>
    public event EventHandler<TrackDropoutEventArgs>? TrackDropout;

    /// <summary>
    /// Initializes a new instance of the AudioMixer class.
    /// </summary>
    /// <param name="engine">The audio engine.</param>
    /// <param name="bufferSizeInFrames">Buffer size in frames for mixing (default: 512).</param>
    /// <exception cref="ArgumentNullException">Thrown when engine is null.</exception>
    public AudioMixer(IAudioEngine engine, int bufferSizeInFrames = 512)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));

        _config = new AudioConfig
        {
            SampleRate = 48000, // Default, will be overridden
            Channels = 2,
            BufferSize = _engine.FramesPerBuffer
        };
        _bufferSizeInFrames = bufferSizeInFrames;

        // Initialize source management
        _sources = new ConcurrentDictionary<Guid, IAudioSource>();

        // Initialize synchronizer (LEGACY - deprecated but functional)
        _synchronizer = new AudioSynchronizer();

        // Initialize NEW Master Clock System (v2.4.0+)
        _masterClock = new MasterClock(
            sampleRate: 48000,
            channels: 2,
            mode: ClockMode.Realtime);

        _trackMetrics = new Dictionary<Guid, TrackPerformanceMetrics>();

        // Initialize effect chain
        _masterEffects = new List<IEffectProcessor>();

        // Initialize master controls
        _masterVolume = 1.0f;
        _leftPeak = 0.0f;
        _rightPeak = 0.0f;

        // Calculate mix interval based on buffer time
        // Use 1/4 buffer time for more responsive mixing (like Ownaudio SourceManager)
        double quarterBufferTimeMs = (_bufferSizeInFrames / 4.0) / _config.SampleRate * 1000.0;
        _mixIntervalMs = Math.Max(1, (int)Math.Round(quarterBufferTimeMs));

        // Initialize synchronization
        _pauseEvent = new ManualResetEventSlim(false);
        _shouldStop = false;
        _isRunning = false;
        _isRecording = false;

        // Create mix thread
        _mixThread = new Thread(MixThreadLoop)
        {
            Name = "AudioMixer.MixThread",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
    }

    /// <summary>
    /// Starts or resumes the audio mixer.
    /// Sources can be added dynamically after starting.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void Start()
    {
        ThrowIfDisposed();

        if (_isRunning)
            return;

        _shouldStop = false;
        _isRunning = true;
        _pauseEvent.Set();

        if (!_mixThread.IsAlive)
            _mixThread.Start();
    }

    /// <summary>
    /// Pauses the audio mixer without stopping the thread.
    /// Use <see cref="Start"/> to resume.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void Pause()
    {
        ThrowIfDisposed();

        if (!_isRunning)
            return;

        // Set running flag to false but keep thread alive
        // The thread loop will wait on _pauseEvent
        _isRunning = false;
        _pauseEvent.Reset();
    }

    /// <summary>
    /// Stops the audio mixer.
    /// All sources will be stopped but not removed.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void Stop()
    {
        ThrowIfDisposed();

        if (!_isRunning)
            return;

        _shouldStop = true;
        _pauseEvent.Reset();

        // Wait for mix thread to exit
        if (_mixThread.IsAlive)
        {
            if (!_mixThread.Join(TimeSpan.FromSeconds(2)))
            {
                // Thread didn't exit gracefully
            }
        }

        // Stop all sources
        foreach (var source in _sources.Values)
        {
            try
            {
                source.Stop();
            }
            catch
            {
                // Ignore errors when stopping sources
            }
        }

        _isRunning = false;
    }

    /// <summary>
    /// Adds an audio source to the mixer.
    /// The source can be added while the mixer is running (hot-swap).
    /// </summary>
    /// <param name="source">The audio source to add.</param>
    /// <returns>True if added successfully, false if source already exists.</returns>
    /// <exception cref="ArgumentNullException">Thrown when source is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when source format doesn't match mixer format or track limit exceeded.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public bool AddSource(IAudioSource source)
    {
        ThrowIfDisposed();

        if (source == null)
            throw new ArgumentNullException(nameof(source));

        // HARD LIMIT: Enforce maximum track count for CPU performance
        if (_sources.Count >= AudioConstants.MaxAudioSources)
        {
            throw new InvalidOperationException(
                $"Maximum track limit ({AudioConstants.MaxAudioSources}) reached. " +
                $"Cannot add more sources. This limit ensures acceptable CPU performance with SoundTouch processing.");
        }

        // Add to source dictionary
        bool added = _sources.TryAdd(source.Id, source);

        if (added)
        {
            // Subscribe to source error events
            source.Error += OnSourceError;

            // OPTIMIZATION: Invalidate cached array when sources change
            _sourcesArrayNeedsUpdate = true;

            // If mixer is running and source is not playing, start it
            if (_isRunning && source.State != AudioState.Playing)
            {
                try
                {
                    source.Play();
                }
                catch
                {
                    // Source failed to start - will be handled by error event
                }
            }
        }

        return added;
    }

    public bool RemoveSource(IAudioSource source)
    {
        ThrowIfDisposed();

        if (source == null)
            throw new ArgumentNullException(nameof(source));

        return RemoveSource(source.Id);
    }

    /// <summary>
    /// Removes an audio source from the mixer by its ID.
    /// </summary>
    /// <param name="sourceId">The ID of the source to remove.</param>
    /// <returns>True if removed successfully, false if source was not found.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public bool RemoveSource(Guid sourceId)
    {
        ThrowIfDisposed();

        if (_sources.TryRemove(sourceId, out IAudioSource? source))
        {
            // Unsubscribe from error events
            source.Error -= OnSourceError;

            // OPTIMIZATION: Invalidate cached array when sources change
            _sourcesArrayNeedsUpdate = true;

            // Stop the source
            try
            {
                source.Stop();
            }
            catch
            {
                // Ignore errors when stopping source
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Removes all sources from the mixer.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void ClearSources()
    {
        ThrowIfDisposed();

        foreach (var source in _sources.Values)
        {
            try
            {
                source.Error -= OnSourceError;
                source.Stop();
            }
            catch
            {
                // Ignore errors
            }
        }

        _sources.Clear();

        // OPTIMIZATION: Invalidate cached array when sources change
        _sourcesArrayNeedsUpdate = true;
    }

    /// <summary>
    /// Gets all active sources.
    /// </summary>
    /// <returns>Array of active sources (snapshot).</returns>
    public IAudioSource[] GetSources()
    {
        return _sources.Values.ToArray();
    }

    /// <summary>
    /// Adds a master effect to the processing chain.
    /// Effects are processed in the order they are added.
    /// </summary>
    /// <param name="effect">The effect to add.</param>
    /// <exception cref="ArgumentNullException">Thrown when effect is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void AddMasterEffect(IEffectProcessor effect)
    {
        ThrowIfDisposed();

        if (effect == null)
            throw new ArgumentNullException(nameof(effect));

        lock (_effectsLock)
        {
            // Initialize effect with current config
            effect.Initialize(_config);
            _masterEffects.Add(effect);

            // Mark effects as changed to trigger cache update
            _effectsChanged = true;
        }
    }

    /// <summary>
    /// Removes a master effect from the processing chain.
    /// </summary>
    /// <param name="effect">The effect to remove.</param>
    /// <returns>True if removed successfully, false if not found.</returns>
    /// <exception cref="ArgumentNullException">Thrown when effect is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public bool RemoveMasterEffect(IEffectProcessor effect)
    {
        ThrowIfDisposed();

        if (effect == null)
            throw new ArgumentNullException(nameof(effect));

        lock (_effectsLock)
        {
            bool removed = _masterEffects.Remove(effect);
            if (removed)
            {
                // Mark effects as changed to trigger cache update
                _effectsChanged = true;
            }
            return removed;
        }
    }

    /// <summary>
    /// Clears all master effects from the processing chain.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void ClearMasterEffects()
    {
        ThrowIfDisposed();

        lock (_effectsLock)
        {
            _masterEffects.Clear();

            // Mark effects as changed to trigger cache update
            _effectsChanged = true;
        }
    }

    /// <summary>
    /// Gets all master effects.
    /// </summary>
    /// <returns>Array of master effects (snapshot).</returns>
    public IEffectProcessor[] GetMasterEffects()
    {
        lock (_effectsLock)
        {
            return _masterEffects.ToArray();
        }
    }

    /// <summary>
    /// Starts recording the mixed audio output to a WAV file.
    /// </summary>
    /// <param name="filePath">Path to the output WAV file.</param>
    /// <exception cref="ArgumentException">Thrown when filePath is invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when already recording.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void StartRecording(string filePath)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

        lock (_recorderLock)
        {
            if (_isRecording)
                throw new InvalidOperationException("Already recording. Call StopRecording() first.");

            try
            {
                _recorder = new WaveFileWriter(filePath, _config);
                _isRecording = true;
            }
            catch (Exception ex)
            {
                _recorder?.Dispose();
                _recorder = null;
                throw new InvalidOperationException($"Failed to start recording: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Stops recording and closes the WAV file.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void StopRecording()
    {
        ThrowIfDisposed();

        lock (_recorderLock)
        {
            if (!_isRecording)
                return;

            try
            {
                _recorder?.Dispose();
            }
            catch
            {
                // Ignore errors during cleanup
            }
            finally
            {
                _recorder = null;
                _isRecording = false;
            }
        }
    }

    /// <summary>
    /// Mix thread loop - continuously mixes sources and sends to engine.
    /// This is the hot path - must be zero-allocation.
    /// OPTIMIZATION: Uses cached array instead of ConcurrentDictionary.Values to avoid enumerator allocation.
    /// </summary>
    private void MixThreadLoop()
    {
        // Pre-allocate buffers (done once, outside loop)
        int bufferSizeInSamples = _bufferSizeInFrames * _config.Channels;
        float[] mixBuffer = new float[bufferSizeInSamples];
        float[] sourceBuffer = new float[bufferSizeInSamples];

        while (!_shouldStop)
        {
            try
            {
                // Wait if paused
                if (!_isRunning)
                {
                    _pauseEvent.Wait(100);
                    continue;
                }

                // OPTIMIZATION: Update cached sources array if needed (zero allocation in steady state)
                // This avoids ConcurrentDictionary.Values enumeration which allocates an enumerator every call
                if (_sourcesArrayNeedsUpdate)
                {
                    _cachedSourcesArray = _sources.Values.ToArray();
                    _sourcesArrayNeedsUpdate = false;
                }

                // Clear mix buffer
                Array.Clear(mixBuffer, 0, bufferSizeInSamples);

                // 1. Get current timestamp from Master Clock
                double currentTimestamp = _masterClock.CurrentTimestamp;

                // 2. Mix sources based on rendering mode
                int activeSources;
                if (_masterClock.Mode == ClockMode.Realtime)
                {
                    // Realtime mode: Non-blocking, dropouts → silence + event
                    activeSources = MixSourcesRealtime(mixBuffer, sourceBuffer, currentTimestamp);
                }
                else
                {
                    // Offline mode: Blocking, deterministic
                    activeSources = MixSourcesOffline(mixBuffer, sourceBuffer, currentTimestamp);
                }

                // 3-7. Process mixed audio
                if (activeSources > 0)
                {
                    // 3. Apply master volume
                    ApplyMasterVolume(mixBuffer.AsSpan(0, bufferSizeInSamples));

                    // 4. Apply master effects
                    ApplyMasterEffects(mixBuffer.AsSpan(0, bufferSizeInSamples), _bufferSizeInFrames);

                    // 5. Calculate peak levels
                    CalculatePeakLevels(mixBuffer.AsSpan(0, bufferSizeInSamples));

                    // 6. Write to recorder if active
                    if (_isRecording)
                    {
                        WriteToRecorder(mixBuffer.AsSpan(0, bufferSizeInSamples));
                    }

                    // 7. Send to engine (BLOCKING CALL - provides natural timing)
                    _engine.Send(mixBuffer.AsSpan(0, bufferSizeInSamples));

                    // Update statistics
                    Interlocked.Add(ref _totalMixedFrames, _bufferSizeInFrames);

                    // 8. Advance Master Clock
                    _masterClock.Advance(_bufferSizeInFrames);

                    // LEGACY: Advance GhostTracks if any exist (deprecated path)
                    AdvanceLegacyGhostTracks(sourceBuffer);
                }
                else
                {
                    // No active sources - send silence
                    _engine.Send(mixBuffer.AsSpan(0, bufferSizeInSamples));

                    // Reset peak levels
                    _leftPeak = 0.0f;
                    _rightPeak = 0.0f;

                    // Still advance clock even with silence (timeline keeps moving)
                    _masterClock.Advance(_bufferSizeInFrames);

                    // Sleep longer when no sources are active
                    Thread.Sleep(_mixIntervalMs * 2);
                }
            }
            catch (Exception)
            {
                Thread.Sleep(_mixIntervalMs * 2);
            }
        }
    }

    /// <summary>
    /// Mixes sources in realtime mode (non-blocking, dropout handling).
    /// NEW - v2.4.0+ Master Clock System
    /// </summary>
    private int MixSourcesRealtime(float[] mixBuffer, float[] sourceBuffer, double timestamp)
    {
        int activeSources = 0;

        for (int i = 0; i < _cachedSourcesArray.Length; i++)
        {
            var source = _cachedSourcesArray[i];

            try
            {
                // Only mix playing sources
                if (source.State != AudioState.Playing)
                    continue;

                // PRIORITY 1: NEW - IMasterClockSource (if attached to clock)
                if (source is IMasterClockSource clockSource && clockSource.IsAttachedToClock)
                {
                    bool success = clockSource.ReadSamplesAtTime(
                        timestamp,
                        sourceBuffer.AsSpan(),
                        _bufferSizeInFrames,
                        out ReadResult result);

                    // CRITICAL FIX: Always mix whatever we got (could be silence if underrun)
                    // This ensures track timing stays aligned even during dropouts
                    if (result.FramesRead > 0)
                    {
                        MixIntoBuffer(mixBuffer, sourceBuffer, result.FramesRead * _config.Channels);
                    }

                    if (success)
                    {
                        activeSources++;
                    }
                    else
                    {
                        // Dropout occurred - fire event
                        OnTrackDropout(new TrackDropoutEventArgs(
                            source.Id,
                            source.GetType().Name,
                            timestamp,
                            _masterClock.CurrentSamplePosition,
                            _bufferSizeInFrames - result.FramesRead,
                            result.ErrorMessage ?? "Buffer underrun"));

                        // Record dropout in metrics
                        lock (_metricsLock)
                        {
                            if (_trackMetrics.TryGetValue(source.Id, out var metrics))
                            {
                                metrics.RecordDropout(timestamp, _bufferSizeInFrames - result.FramesRead);
                            }
                        }
                    }
                }
                // PRIORITY 2: LEGACY - Standard IAudioSource (GhostTrack sync or standalone)
                else
                {
                    // Legacy path: use existing ReadSamples() method
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
                // Source error - report but continue mixing other sources
                OnSourceError(source, new AudioErrorEventArgs(
                    $"Error reading from source {source.Id}: {ex.Message}", ex));
            }
        }

        return activeSources;
    }

    /// <summary>
    /// Mixes sources in offline mode (blocking, deterministic rendering).
    /// NEW - v2.4.0+ Master Clock System
    /// </summary>
    private int MixSourcesOffline(float[] mixBuffer, float[] sourceBuffer, double timestamp)
    {
        int activeSources = 0;

        for (int i = 0; i < _cachedSourcesArray.Length; i++)
        {
            var source = _cachedSourcesArray[i];

            try
            {
                // Only mix playing sources
                if (source.State != AudioState.Playing)
                    continue;

                // In offline mode, we wait for tracks to be ready
                if (source is IMasterClockSource clockSource && clockSource.IsAttachedToClock)
                {
                    // Retry loop with timeout for offline rendering
                    bool success = false;
                    ReadResult result = default;
                    int retryCount = 0;
                    int maxRetries = 5000; // 5 seconds timeout (1ms sleep per retry)

                    while (!success && retryCount < maxRetries)
                    {
                        success = clockSource.ReadSamplesAtTime(
                            timestamp,
                            sourceBuffer.AsSpan(),
                            _bufferSizeInFrames,
                            out result);

                        if (!success)
                        {
                            // Wait briefly and retry
                            Thread.Sleep(1);
                            retryCount++;
                        }
                    }

                    if (success && result.FramesRead > 0)
                    {
                        MixIntoBuffer(mixBuffer, sourceBuffer, result.FramesRead * _config.Channels);
                        activeSources++;
                    }
                    else
                    {
                        // Timeout - report error
                        OnSourceError(source, new AudioErrorEventArgs(
                            $"Offline rendering timeout for source {source.Id} at timestamp {timestamp:F3}s", null));
                    }
                }
                else
                {
                    // Legacy path
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
                    $"Error reading from source {source.Id} in offline mode: {ex.Message}", ex));
            }
        }

        return activeSources;
    }

    /// <summary>
    /// Advances legacy GhostTracks (deprecated path for backward compatibility).
    /// LEGACY - deprecated but functional
    /// </summary>
    private void AdvanceLegacyGhostTracks(float[] sourceBuffer)
    {
        var syncGroupIds = _synchronizer.GetSyncGroupIds();
        foreach (var groupId in syncGroupIds)
        {
            var ghostTrack = _synchronizer.GetGhostTrack(groupId);
            // Only advance if playing
            if (ghostTrack != null && ghostTrack.State == AudioState.Playing)
            {
                // We use the existing sourceBuffer to read samples (which are silence)
                // This advances the GhostTrack's internal position
                ghostTrack.ReadSamples(sourceBuffer, _bufferSizeInFrames);
            }
        }
    }

    /// <summary>
    /// Fires the TrackDropout event (NEW - v2.4.0+).
    /// </summary>
    private void OnTrackDropout(TrackDropoutEventArgs e)
    {
        TrackDropout?.Invoke(this, e);
    }

    /// <summary>
    /// Mixes source samples into the mix buffer (additive mixing).
    /// Zero-allocation hot path method with SIMD vectorization.
    /// Performance: 4-8x faster on modern CPUs with hardware acceleration.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MixIntoBuffer(float[] mixBuffer, float[] sourceBuffer, int sampleCount)
    {
        int i = 0;
        int simdLength = Vector<float>.Count;

        // SIMD vectorized processing (processes 4-8 floats at once depending on CPU)
        if (Vector.IsHardwareAccelerated && sampleCount >= simdLength)
        {
            // Process in SIMD chunks for optimal performance
            int simdLoopEnd = sampleCount - (sampleCount % simdLength);

            for (; i < simdLoopEnd; i += simdLength)
            {
                // Load vectors from both buffers
                var mixVec = new Vector<float>(mixBuffer, i);
                var srcVec = new Vector<float>(sourceBuffer, i);

                // Add vectors (SIMD operation - single CPU instruction)
                var result = mixVec + srcVec;

                // Store result back to mix buffer
                result.CopyTo(mixBuffer, i);
            }
        }

        // Scalar fallback for remaining samples
        for (; i < sampleCount; i++)
        {
            mixBuffer[i] += sourceBuffer[i];
        }
    }

    /// <summary>
    /// Applies master volume to the mixed buffer.
    /// Zero-allocation hot path method with SIMD vectorization.
    /// Performance: 4-8x faster on modern CPUs with hardware acceleration.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyMasterVolume(Span<float> buffer)
    {
        float volume = _masterVolume;

        if (Math.Abs(volume - 1.0f) < 0.001f)
            return; // Skip if volume is ~1.0

        int i = 0;
        int simdLength = Vector<float>.Count;

        // SIMD vectorized processing
        if (Vector.IsHardwareAccelerated && buffer.Length >= simdLength)
        {
            var volumeVec = new Vector<float>(volume);
            int simdLoopEnd = buffer.Length - (buffer.Length % simdLength);

            for (; i < simdLoopEnd; i += simdLength)
            {
                // Load vector from buffer
                var vec = new Vector<float>(buffer.Slice(i, simdLength));

                // Multiply by volume vector (SIMD operation - single CPU instruction)
                vec *= volumeVec;

                // Store result back to buffer
                vec.CopyTo(buffer.Slice(i, simdLength));
            }
        }

        // Scalar fallback for remaining samples
        for (; i < buffer.Length; i++)
        {
            buffer[i] *= volume;
        }
    }

    /// <summary>
    /// Applies master effects to the mixed buffer.
    /// Effects are processed in the order they were added.
    /// Zero-allocation hot path method using cached effect array.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyMasterEffects(Span<float> buffer, int frameCount)
    {
        if (_effectsChanged)
        {
            lock (_effectsLock)
            {
                if (_effectsChanged) // Double-check inside lock
                {
                    _cachedEffects = _masterEffects.ToArray();
                    _effectsChanged = false;
                }
            }
        }

        // Use cached array (zero allocation in steady state)
        var effects = _cachedEffects;
        if (effects.Length == 0)
            return; // No effects to apply

        // Process each effect in sequence
        foreach (var effect in effects)
        {
            try
            {
                if (effect.Enabled)
                {
                    effect.Process(buffer, frameCount);
                }
            }
            catch
            {
                // Effect processing error - skip this effect and continue
                // In production, log via ILogger
            }
        }
    }

    /// <summary>
    /// Calculates peak levels for stereo output.
    /// Updates LeftPeak and RightPeak fields.
    /// Uses SIMD vectorization for optimal performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CalculatePeakLevels(Span<float> buffer)
    {
        float leftPeak = 0.0f;
        float rightPeak = 0.0f;

        int frameCount = buffer.Length / 2; // Stereo: 2 samples per frame
        int simdLength = Vector<float>.Count;

        // SIMD vectorized processing (processes multiple samples at once)
        if (Vector.IsHardwareAccelerated && frameCount >= simdLength / 2)
        {
            var leftPeakVec = Vector<float>.Zero;
            var rightPeakVec = Vector<float>.Zero;

            int simdFrames = (frameCount / simdLength) * simdLength;
            int i = 0;

            // Pre-allocate buffers outside the loop to avoid potential stack overflow (CA2014)
            Span<float> leftSamples = stackalloc float[simdLength];
            Span<float> rightSamples = stackalloc float[simdLength];

            for (; i < simdFrames * 2; i += simdLength * 2)
            {
                // Load left channel samples (every other pair)
                for (int j = 0; j < simdLength && i + j * 2 < buffer.Length; j++)
                {
                    leftSamples[j] = Math.Abs(buffer[i + j * 2]);
                    rightSamples[j] = Math.Abs(buffer[i + j * 2 + 1]);
                }

                var leftVec = new Vector<float>(leftSamples);
                var rightVec = new Vector<float>(rightSamples);

                // Track maximum values using Vector.Max
                leftPeakVec = Vector.Max(leftPeakVec, leftVec);
                rightPeakVec = Vector.Max(rightPeakVec, rightVec);
            }

            // Extract maximum from vectors
            for (int j = 0; j < simdLength; j++)
            {
                if (leftPeakVec[j] > leftPeak)
                    leftPeak = leftPeakVec[j];
                if (rightPeakVec[j] > rightPeak)
                    rightPeak = rightPeakVec[j];
            }

            // Process remaining samples with scalar code
            for (; i < buffer.Length; i += 2)
            {
                float leftSample = Math.Abs(buffer[i]);
                float rightSample = Math.Abs(buffer[i + 1]);

                if (leftSample > leftPeak)
                    leftPeak = leftSample;
                if (rightSample > rightPeak)
                    rightPeak = rightSample;
            }
        }
        else
        {
            // Scalar fallback (original implementation)
            for (int i = 0; i < buffer.Length; i += 2)
            {
                float leftSample = Math.Abs(buffer[i]);
                float rightSample = Math.Abs(buffer[i + 1]);

                if (leftSample > leftPeak)
                    leftPeak = leftSample;

                if (rightSample > rightPeak)
                    rightPeak = rightSample;
            }
        }

        _leftPeak = leftPeak;
        _rightPeak = rightPeak;
    }

    /// <summary>
    /// Writes mixed audio to the recorder.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteToRecorder(Span<float> buffer)
    {
        lock (_recorderLock)
        {
            if (_isRecording && _recorder != null)
            {
                try
                {
                    _recorder.WriteSamples(buffer);
                }
                catch
                {
                    // Recording error - stop recording
                    _isRecording = false;
                }
            }
        }
    }

    /// <summary>
    /// Handles source error events.
    /// </summary>
    private void OnSourceError(object? sender, AudioErrorEventArgs e)
    {
        SourceError?.Invoke(sender, e);
    }

    /// <summary>
    /// Throws ObjectDisposedException if disposed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioMixer));
    }

    /// <summary>
    /// Disposes the mixer and releases all resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        // Stop mixer
        if (_isRunning)
        {
            try
            {
                Stop();
            }
            catch
            {
                // Ignore errors
            }
        }

        // Stop recording
        StopRecording();

        // Clear all sources
        ClearSources();

        // Dispose all effects
        lock (_effectsLock)
        {
            foreach (var effect in _masterEffects)
            {
                try
                {
                    effect?.Dispose();
                }
                catch
                {
                    // Ignore errors during disposal
                }
            }
            _masterEffects.Clear();
        }

        // Dispose synchronization primitives
        _pauseEvent?.Dispose();

        _disposed = true;
    }

    /// <summary>
    /// Returns a string representation of the mixer's state.
    /// </summary>
    public override string ToString()
    {
        return $"AudioMixer: {_config.SampleRate}Hz {_config.Channels}ch, Buffer: {_bufferSizeInFrames} frames, " +
               $"Sources: {SourceCount}, Running: {_isRunning}, Recording: {_isRecording}, " +
               $"Master Volume: {_masterVolume:F2}, Peaks: L={_leftPeak:F2} R={_rightPeak:F2}";
    }
}
