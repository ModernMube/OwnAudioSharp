using System.Collections.Concurrent;
using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Monitoring;
using OwnaudioNET.Synchronization;
using OwnaudioNET.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Mixing;

/// <summary>
/// Central audio mixing engine that combines multiple audio sources into a single output stream.
/// Provides dynamic source management, master volume control, level metering, and recording functionality.
/// The class follows a three-thread architecture: the main thread drives the public API,
/// a highest-priority mix thread performs all real-time audio blending, and each source
/// optionally runs its own background decode thread. All cross-thread communication is lock-free
/// on the critical audio path using volatile fields and <see cref="LockFreeRingBuffer{T}"/>.
/// </summary>
public sealed partial class AudioMixer : IDisposable
{
    /// <summary>
    /// The underlying platform audio engine that receives the final mixed output.
    /// Never written after construction; safe to read from any thread without a lock.
    /// </summary>
    private readonly IAudioEngine _engine;

    /// <summary>
    /// Optional pre-buffering wrapper around <c>_engine</c>.
    /// When non-null the mix thread writes into the wrapper's circular buffer instead
    /// of calling <c>_engine.Send</c> directly, decoupling mix and engine I/O threads.
    /// </summary>
    private readonly AudioEngineWrapper? _engineWrapper;

    /// <summary>
    /// Thread-safe dictionary of all currently registered audio sources keyed by their unique identifier.
    /// Mutations (add / remove) happen exclusively on the main thread.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, IAudioSource> _sources;

    /// <summary>
    /// Reusable point-in-time snapshot of the sources, consumed by the Rust-native control-rate sync
    /// tick. Rebuilt on the main/control thread only when the source set actually changes (in
    /// <c>RebuildSourcesCache</c>), so the tick — which runs 67×/s for hours — iterates it without
    /// allocating a fresh <c>ConcurrentDictionary.Values</c> snapshot every call. Distinct from
    /// <c>_pendingSourcesArray</c> (which the mix thread nulls after adopting), so the two consumers
    /// never interfere. Published via <see cref="Volatile.Write"/>; read on the sync thread via
    /// <see cref="Volatile.Read"/>. The array is an immutable snapshot, safe to share by reference.
    /// </summary>
    private IAudioSource[] _rustSourceSnapshot = Array.Empty<IAudioSource>();

    /// <summary>
    /// Master clock used for timeline-based synchronisation across all attached sources.
    /// Introduced in v2.4.0 as part of the Master Clock System.
    /// </summary>
    private readonly MasterClock _masterClock;

    /// <summary>
    /// Per-track performance metrics keyed by source identifier.
    /// Accessed under <c>_metricsLock</c> from both the mix thread (dropout recording)
    /// and the main thread (diagnostic reads).
    /// </summary>
    private readonly Dictionary<Guid, TrackPerformanceMetrics> _trackMetrics;

    /// <summary>
    /// Guards access to <c>_trackMetrics</c> from the mix thread and the main thread.
    /// Held only briefly to record dropout events; never held across blocking calls.
    /// </summary>
    private readonly object _metricsLock = new();

    /// <summary>
    /// Event retained for lifecycle coordination when the mixer is stopped without being disposed.
    /// </summary>
    private readonly ManualResetEventSlim _pauseEvent;

    /// <summary>
    /// Indicates whether the mixer is actively producing audio.
    /// Declared <see langword="volatile"/> so both the main and mix threads observe changes immediately.
    /// </summary>
    private volatile bool _isRunning;

    /// <summary>
    /// How often (in consecutive occurrences) a repeating background-loop error is re-reported:
    /// on the first occurrence and every multiple of this thereafter, so a persistent fault is
    /// visible without flooding subscribers.
    /// </summary>
    private const int LoopErrorReportInterval = 1000;

    /// <summary>
    /// Consecutive background-loop errors after which the loop is treated as persistently faulted
    /// and stops, rather than spinning on a deterministic failure indefinitely.
    /// </summary>
    private const int LoopErrorFaultThreshold = 500;

    /// <summary>Consecutive-error counter for the Rust-native control-rate sync tick.</summary>
    private int _rustSyncConsecutiveErrors;

    /// <summary>
    /// Immutable audio configuration (sample rate, channel count, buffer size) set at construction time.
    /// The configuration is derived from the engine and cannot change while the mixer is running.
    /// </summary>
    private readonly AudioConfig _config;

    /// <summary>
    /// Number of audio frames per mix cycle.
    /// Determines the size of the pre-allocated mix and source buffers inside the mix thread loop.
    /// </summary>
    private readonly int _bufferSizeInFrames;

    /// <summary>
    /// Minimum sleep interval in milliseconds between mix cycles when no sources are active.
    /// Calculated as one quarter of the buffer duration to keep CPU usage low during silence.
    /// </summary>
    private readonly int _mixIntervalMs;

    /// <summary>
    /// Current master output volume in the range [0.0, 1.0].
    /// Declared <see langword="volatile"/> so writes from the main thread are immediately
    /// visible to the mix thread without a memory barrier instruction.
    /// </summary>
    private volatile float _masterVolume;

    /// <summary>
    /// Most recently measured absolute peak level for the left output channel.
    /// Updated every mix cycle; read by the main thread for metering displays.
    /// Declared <see langword="volatile"/> to avoid stale reads without a lock.
    /// </summary>
    private volatile float _leftPeak;

    /// <summary>
    /// Most recently measured absolute peak level for the right output channel.
    /// Updated every mix cycle; read by the main thread for metering displays.
    /// Declared <see langword="volatile"/> to avoid stale reads without a lock.
    /// </summary>
    private volatile float _rightPeak;


#pragma warning disable CS0649
    /// <summary>
    /// Cumulative count of buffer underrun events detected during playback.
    /// Reserved for future underrun tracking; currently not incremented.
    /// </summary>
    private long _totalUnderruns;
#pragma warning restore CS0649

    /// <summary>
    /// Ordered list of master effect processors applied to the mixed output each cycle.
    /// Mutations are protected by <c>_effectsLock</c> on the main thread; the mix thread
    /// reads only the lock-free snapshot in <c>_cachedEffects</c>.
    /// </summary>
    private readonly List<IEffectProcessor> _masterEffects;

    /// <summary>
    /// Guards mutations to <c>_masterEffects</c> on the main thread and serialises calls
    /// to <c>PublishEffectsCache()</c>. Never acquired on the real-time audio thread.
    /// </summary>
    private readonly object _effectsLock = new();

    /// <summary>
    /// Atomically published snapshot of <c>_masterEffects</c> consumed by the mix thread.
    /// Written by the main thread inside <c>_effectsLock</c> via <see cref="Volatile.Write"/>;
    /// read by the mix thread via <see cref="Volatile.Read"/> — completely lock-free on the RT path.
    /// </summary>
    private IEffectProcessor[] _cachedEffects = Array.Empty<IEffectProcessor>();

    /// <summary>
    /// Unique identifier assigned to this mixer instance at construction time.
    /// Used for registration with the <see cref="OwnaudioNet"/> static registry.
    /// </summary>
    private readonly Guid _mixerId;

    /// <summary>
    /// Tracks whether the mixer has been disposed to prevent use-after-dispose errors.
    /// Checked at the start of every public method via <c>ThrowIfDisposed()</c>.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Gets the unique identifier assigned to this <see cref="AudioMixer"/> instance at construction.
    /// Can be used to correlate mixer instances across logging and diagnostic systems.
    /// </summary>
    public Guid MixerId => _mixerId;

    /// <summary>
    /// Gets the immutable audio configuration (sample rate, channel count, buffer size)
    /// that was negotiated with the audio engine at construction time.
    /// </summary>
    public AudioConfig Config => _config;

    /// <summary>
    /// Gets a value indicating whether the mixer is currently producing audio output.
    /// Returns <see langword="true"/> between a successful <see cref="Start"/> and a
    /// subsequent <see cref="Stop"/> or <see cref="Dispose"/> call.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Gets the number of audio sources currently registered with this mixer.
    /// This value reflects the live count and is safe to read from any thread.
    /// </summary>
    public int SourceCount => _sources.Count;

    /// <summary>
    /// Gets or sets the master output volume applied to the final mixed signal.
    /// Values are clamped to the range [0.0, 1.0]; values outside this range are silently clamped.
    /// Changes take effect on the next mix cycle with no audible click or ramp.
    /// </summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Clamp(value, 0.0f, 1.0f);
    }

    /// <summary>
    /// Gets the absolute peak sample level measured for the left output channel
    /// during the most recently completed mix cycle.
    /// Values range from 0.0 (silence) to 1.0 (full scale); values above 1.0
    /// indicate clipping that has been hard-limited by <c>ApplyLimiter</c>.
    /// </summary>
    public float LeftPeak => _leftPeak;

    /// <summary>
    /// Gets the absolute peak sample level measured for the right output channel
    /// during the most recently completed mix cycle.
    /// Values range from 0.0 (silence) to 1.0 (full scale); values above 1.0
    /// indicate clipping that has been hard-limited by <c>ApplyLimiter</c>.
    /// </summary>
    public float RightPeak => _rightPeak;

    /// <summary>
    /// Gets the total number of audio frames rendered since the mixer was last started, taken from
    /// the master clock the native mixer advances.
    /// </summary>
    public long TotalMixedFrames => _masterClock.CurrentSamplePosition;

    /// <summary>
    /// Gets the total number of buffer underrun events detected since the mixer was last started.
    /// Reserved for future underrun tracking; currently always returns zero.
    /// </summary>
    public long TotalUnderruns => Interlocked.Read(ref _totalUnderruns);

    /// <summary>
    /// Gets a value indicating whether the mixer is currently recording its output to a WAV file.
    /// Changes to this property are observable from any thread without a lock.
    /// </summary>
    public bool IsRecording => _isRecording;

    /// <summary>
    /// Gets the <see cref="MasterClock"/> instance used for timeline-based source synchronisation.
    /// Introduced in v2.4.0; provides a shared time reference for all master-clock-attached sources.
    /// </summary>
    public MasterClock MasterClock => _masterClock;

    /// <summary>
    /// Gets or sets the rendering mode that controls how the master clock advances time.
    /// Use <see cref="ClockMode.Realtime"/> for live playback and <see cref="ClockMode.Offline"/>
    /// for deterministic non-real-time rendering. Introduced in v2.4.0.
    /// </summary>
    public ClockMode RenderingMode
    {
        get => _masterClock.Mode;
        set => _masterClock.Mode = value;
    }

    /// <summary>
    /// Raised when the audio device reports a buffer underrun during output.
    /// Subscribers can use this event to implement error recovery or user notification.
    /// </summary>
#pragma warning disable CS0067
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;
#pragma warning restore CS0067

    /// <summary>
    /// Raised when an individual audio source encounters an error during the mix cycle.
    /// The event is dispatched from the mix thread; handlers must be thread-safe.
    /// </summary>
    public event EventHandler<AudioErrorEventArgs>? SourceError;

    /// <summary>
    /// Raised when a master-clock-attached source cannot supply the required frames
    /// in time during a mix cycle, resulting in an audible dropout.
    /// Introduced in v2.4.0 as part of the Master Clock dropout-detection pipeline.
    /// </summary>
    public event EventHandler<TrackDropoutEventArgs>? TrackDropout;

    /// <summary>
    /// Raised once when every registered source has transitioned to <see cref="AudioState.EndOfStream"/>.
    /// The event fires at most once per playback session and is reset automatically
    /// when a new source is added via <see cref="AddSource"/> or <see cref="AddSourcePrepared"/>.
    /// </summary>
    public event EventHandler? PlaybackEnded;

    /// <summary>
    /// Raised when the native output stream reports a device-loss or backend fault
    /// (device unplugged, disabled, or lost on sleep/wake or a sample-rate change).
    /// The Rust-native output renders on the audio thread and previously died
    /// unobserved on such a fault; this event lets a long-running host detect the
    /// "audio went silent" condition and recover (e.g. reopen the output).
    /// The event is dispatched from the control-rate sync tick; handlers must be
    /// thread-safe. Only fires while the Rust-native chain drives output.
    /// </summary>
    public event EventHandler<AudioStreamFaultEventArgs>? StreamFaulted;

    /// <summary>
    /// Creates a new <see cref="AudioMixer"/> instance routed through an <see cref="AudioEngineWrapper"/>
    /// for pre-buffered, decoupled audio output.
    /// Routes mixer output through the wrapper's internal circular buffer, decoupling the mix thread
    /// from the platform audio engine. Use this factory when running eight or more simultaneous sources
    /// or two or more master effects to avoid audio starvation under heavy DSP load.
    /// </summary>
    /// <param name="engineWrapper">
    /// The audio engine wrapper that provides the pre-buffer circular buffer. Must not be null.
    /// </param>
    /// <param name="bufferSizeInFrames">Number of frames per mix cycle (default: 512).</param>
    /// <returns>A new <see cref="AudioMixer"/> backed by the wrapper's circular buffer.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="engineWrapper"/> is null.</exception>
    public static AudioMixer Create(AudioEngineWrapper engineWrapper, int bufferSizeInFrames = 512)
    {
        ArgumentNullException.ThrowIfNull(engineWrapper);
        return new AudioMixer(engineWrapper.UnderlyingEngine, bufferSizeInFrames, engineWrapper);
    }

    /// <summary>
    /// Initializes a new <see cref="AudioMixer"/> instance with a pre-buffering wrapper
    /// by delegating to the primary constructor and then storing the wrapper reference.
    /// This private overload is only called from the <see cref="Create"/> factory method.
    /// </summary>
    /// <param name="engine">The underlying audio engine.</param>
    /// <param name="bufferSizeInFrames">Number of frames per mix cycle.</param>
    /// <param name="wrapper">The engine wrapper to route output through.</param>
    private AudioMixer(IAudioEngine engine, int bufferSizeInFrames, AudioEngineWrapper wrapper)
        : this(engine, bufferSizeInFrames)
    {
        _engineWrapper = wrapper;
    }

    /// <summary>
    /// Initializes a new <see cref="AudioMixer"/> instance connected directly to an <see cref="IAudioEngine"/>.
    /// Allocates all internal buffers, constructs the master clock, and starts the high-priority mix thread.
    /// The mix thread is created but not started until <see cref="Start"/> is first called.
    /// </summary>
    /// <param name="engine">
    /// The platform audio engine that will receive the final mixed output. Must not be null.
    /// </param>
    /// <param name="bufferSizeInFrames">
    /// Number of frames per mix cycle. Defaults to 512; smaller values lower latency at the
    /// cost of higher CPU overhead.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="engine"/> is null.</exception>
    public AudioMixer(IAudioEngine engine, int bufferSizeInFrames = 512)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));

        _rustNative = RustNativeChain.Enabled;

        _mixerId = Guid.NewGuid();

        int sampleRate = OwnaudioNet.Engine?.Config.SampleRate ?? 48000;
        int channels = OwnaudioNet.Engine?.Config.Channels ?? 2;

        _config = new AudioConfig
        {
            SampleRate = sampleRate,
            Channels = channels,
            BufferSize = _engine.FramesPerBuffer
        };
        _bufferSizeInFrames = bufferSizeInFrames;

        _sources = new ConcurrentDictionary<Guid, IAudioSource>();

        _masterClock = new MasterClock(
            sampleRate: sampleRate,
            channels: channels,
            mode: ClockMode.Realtime);

        _trackMetrics = new Dictionary<Guid, TrackPerformanceMetrics>();

        _masterEffects = new List<IEffectProcessor>();

        _masterVolume = 1.0f;
        _leftPeak = 0.0f;
        _rightPeak = 0.0f;

        double quarterBufferTimeMs = (_bufferSizeInFrames / 4.0) / _config.SampleRate * 1000.0;
        _mixIntervalMs = Math.Max(1, (int)Math.Round(quarterBufferTimeMs));

        _pauseEvent = new ManualResetEventSlim(false);
        _isRunning = false;
        _isRecording = false;

        OwnaudioNet.RegisterAudioMixer(this);
    }

    /// <summary>
    /// Returns a human-readable diagnostic string describing the current state of this mixer,
    /// including sample rate, channel count, buffer size, source count, recording state,
    /// master volume, and left/right peak levels.
    /// </summary>
    /// <returns>A formatted diagnostic string for logging or display purposes.</returns>
    public override string ToString()
    {
        return $"AudioMixer: {_config.SampleRate}Hz {_config.Channels}ch, Buffer: {_bufferSizeInFrames} frames, " +
               $"Sources: {SourceCount}, Running: {_isRunning}, Recording: {_isRecording}, " +
               $"Master Volume: {_masterVolume:F2}, Peaks: L={_leftPeak:F2} R={_rightPeak:F2}";
    }
}
