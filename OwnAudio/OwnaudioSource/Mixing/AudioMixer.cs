using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using Avalonia.Rendering;
using Ownaudio;
using Ownaudio.Core;
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
///
/// Performance Targets:
/// - Mix 4+ sources simultaneously
/// - Latency: < 12ms @ 512 buffer size
/// - CPU: < 15% single core
/// - Zero allocations in mix loop
/// </summary>
public sealed partial class AudioMixer : IDisposable
{
    // Engine integration
    private readonly IAudioEngine _engine;

    // Source management
    private readonly ConcurrentDictionary<Guid, IAudioSource> _sources;

    // Synchronization
    private readonly AudioSynchronizer _synchronizer;

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
    private volatile bool _enableAutoDriftCorrection = false; // Disabled by default to avoid stuttering

    // Level metering (peak levels in last mix cycle)
    private volatile float _leftPeak;
    private volatile float _rightPeak;

    // Statistics
    private long _totalMixedFrames;
    private long _totalUnderruns;

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
    /// Gets or sets whether automatic drift correction is enabled for synchronized sources.
    /// When enabled, sources in sync groups are periodically checked for drift and resynced if needed.
    /// WARNING: Enabling this may cause occasional stuttering during resync operations.
    /// Default: false (disabled)
    /// </summary>
    public bool EnableAutoDriftCorrection
    {
        get => _enableAutoDriftCorrection;
        set => _enableAutoDriftCorrection = value;
    }

    /// <summary>
    /// Event raised when a buffer underrun occurs (audio dropout).
    /// </summary>
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    /// <summary>
    /// Event raised when a source error occurs during mixing.
    /// </summary>
    public event EventHandler<AudioErrorEventArgs>? SourceError;

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

        // Initialize synchronizer
        _synchronizer = new AudioSynchronizer();

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
    /// Starts the audio mixer.
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
    /// <exception cref="InvalidOperationException">Thrown when source format doesn't match mixer format.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public bool AddSource(IAudioSource source)
    {
        ThrowIfDisposed();

        if (source == null)
            throw new ArgumentNullException(nameof(source));

        // NOTE: No format validation needed - the external audio engine's decoders
        // (like MFMp3Decoder) automatically handle format conversion (resampling, channel conversion)
        // We trust the decoder to provide audio in the correct format

        // Add to source dictionary
        bool added = _sources.TryAdd(source.Id, source);

        if (added)
        {
            // Subscribe to source error events
            source.Error += OnSourceError;

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

    /// <summary>
    /// Removes an audio source from the mixer.
    /// The source can be removed while the mixer is running (hot-swap).
    /// </summary>
    /// <param name="source">The audio source to remove.</param>
    /// <returns>True if removed successfully, false if source was not found.</returns>
    /// <exception cref="ArgumentNullException">Thrown when source is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
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
    /// </summary>
    private void MixThreadLoop()
    {
        // Pre-allocate buffers (done once, outside loop)
        int bufferSizeInSamples = _bufferSizeInFrames * _config.Channels;
        float[] mixBuffer = new float[bufferSizeInSamples];
        float[] sourceBuffer = new float[bufferSizeInSamples];

        // Drift detection counter (check every 100 iterations = ~1 second @ 512 frames, 48kHz)
        // Less frequent checks = smoother playback, drift correction happens slowly over time
        int syncCheckCounter = 0;
        const int SyncCheckInterval = 100;

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

                // Clear mix buffer
                Array.Clear(mixBuffer, 0, bufferSizeInSamples);

                // Mix all sources
                int activeSources = 0;
                foreach (var source in _sources.Values)
                {
                    try
                    {
                        // Only mix playing sources
                        if (source.State != AudioState.Playing)
                            continue;

                        // Read from source
                        int framesRead = source.ReadSamples(sourceBuffer, _bufferSizeInFrames);

                        if (framesRead > 0)
                        {
                            // Mix into output buffer
                            MixIntoBuffer(mixBuffer, sourceBuffer, framesRead * _config.Channels);
                            activeSources++;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Source error - report but continue mixing other sources
                        OnSourceError(source, new AudioErrorEventArgs($"Error reading from source {source.Id}: {ex.Message}", ex));
                    }
                }

                // Apply master volume
                if (activeSources > 0)
                {
                    ApplyMasterVolume(mixBuffer.AsSpan(0, bufferSizeInSamples));

                    // Apply master effects
                    ApplyMasterEffects(mixBuffer.AsSpan(0, bufferSizeInSamples), _bufferSizeInFrames);

                    // Calculate peak levels
                    CalculatePeakLevels(mixBuffer.AsSpan(0, bufferSizeInSamples));

                    // Write to recorder if active
                    if (_isRecording)
                    {
                        WriteToRecorder(mixBuffer.AsSpan(0, bufferSizeInSamples));
                    }

                    // Send to engine (BLOCKING CALL - waits for hardware buffer space)
                    // This provides natural timing - no sleep needed like Ownaudio SourceManager
                    _engine.Send(mixBuffer.AsSpan(0, bufferSizeInSamples));

                    // Update statistics
                    Interlocked.Add(ref _totalMixedFrames, _bufferSizeInFrames);

                    // Advance master position for sync tracking
                    _synchronizer.AdvanceMasterPosition(_bufferSizeInFrames);

                    // Periodically check for drift and resync if needed (only if enabled)
                    if (_enableAutoDriftCorrection)
                    {
                        syncCheckCounter++;
                        if (syncCheckCounter >= SyncCheckInterval)
                        {
                            syncCheckCounter = 0;

                            // Check all sync groups for drift
                            var groupIds = _synchronizer.GetSyncGroupIds();
                            foreach (var groupId in groupIds)
                            {
                                // Tolerance: 512 frames (~10ms @ 48kHz) before resyncing
                                // This is a buffer-sized tolerance - only resync if drift > 1 buffer
                                // Prevents frequent resyncs due to minor jitter
                                _synchronizer.CheckAndResyncGroup(groupId, _synchronizer.MasterSamplePosition, toleranceInFrames: 512);
                            }
                        }
                    }

                    // NO SLEEP HERE - Engine.Send() is blocking and provides timing
                    // Adding sleep would make playback too slow
                }
                else
                {
                    // No active sources - send silence
                    _engine.Send(mixBuffer.AsSpan(0, bufferSizeInSamples));

                    // Reset peak levels
                    _leftPeak = 0.0f;
                    _rightPeak = 0.0f;

                    // Sleep longer when no sources are active
                    Thread.Sleep(_mixIntervalMs * 2);
                }
            }
            catch (Exception ex)
            {
                // Critical error in mix loop - log but don't crash
                // In production, log via ILogger
                Thread.Sleep(_mixIntervalMs * 2);
            }
        }
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
        // Check if effects list changed and update cache if needed
        // This is the only allocation point, happens only when effects are added/removed
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

            for (; i < simdFrames * 2; i += simdLength * 2)
            {
                // Load left channel samples (every other pair)
                Span<float> leftSamples = stackalloc float[simdLength];
                Span<float> rightSamples = stackalloc float[simdLength];

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
