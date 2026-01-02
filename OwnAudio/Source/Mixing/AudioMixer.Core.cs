using System.Collections.Concurrent;
using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Monitoring;
using OwnaudioNET.Synchronization;
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

    // Parallel mixing buffers (Per-core buffers to avoid locking during mix)
    private float[][] _parallelMixBuffers = Array.Empty<float[]>();
    private float[][] _parallelReadBuffers = Array.Empty<float[]>();
    private readonly object _parallelMixLock = new();

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

    // Unique identifier for this mixer instance
    private readonly Guid _mixerId;

    // Dispose flag
    private bool _disposed;

    /// <summary>
    /// Gets the unique identifier for this AudioMixer instance.
    /// </summary>
    public Guid MixerId => _mixerId;

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

        // Generate unique identifier
        _mixerId = Guid.NewGuid();

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

        // Automatic registration with OwnaudioNet API
        OwnaudioNet.RegisterAudioMixer(this);
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
