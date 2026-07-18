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
/// Blends many audio sources into one output stream. Dynamic source add/remove,
/// master volume, metering, recording. Three threads: main drives the public API,
/// a top-priority mix thread does the real-time blending, and each source may run
/// its own decode thread. The hot path is lock-free (volatile fields + ring buffers).
/// </summary>
public sealed partial class AudioMixer : IDisposable
{
    /// <summary>
    /// Platform engine that gets the final mix. Set once in the ctor, read from anywhere.
    /// </summary>
    private readonly IAudioEngine _engine;

    /// <summary>
    /// Optional pre-buffer in front of _engine. When set the mix thread writes here
    /// instead of calling _engine.Send, so mix and I/O threads don't step on each other.
    /// </summary>
    private readonly AudioEngineWrapper? _engineWrapper;

    /// <summary>
    /// All registered sources by id. Add/remove happen only on the main thread.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, IAudioSource> _sources;

    /// <summary>
    /// Immutable source snapshot for the rust sync tick (runs 67×/s for hours).
    /// Rebuilt only when the source set changes, so the tick doesn't alloc a fresh
    /// Values copy every call. Separate from _pendingSourcesArray. Published/read via Volatile.
    /// </summary>
    private IAudioSource[] _rustSourceSnapshot = Array.Empty<IAudioSource>();

    /// <summary>
    /// Shared timeline clock every attached source syncs to.
    /// </summary>
    private readonly MasterClock _masterClock;

    /// <summary>
    /// Per-track metrics by id. Touched under _metricsLock from both mix and main thread.
    /// </summary>
    private readonly Dictionary<Guid, TrackPerformanceMetrics> _trackMetrics;

    /// <summary>
    /// Guards _trackMetrics. Held briefly, never across a blocking call.
    /// </summary>
    private readonly object _metricsLock = new();

    /// <summary>
    /// Kept around for the stop-without-dispose lifecycle.
    /// </summary>
    private readonly ManualResetEventSlim _pauseEvent;

    /// <summary>
    /// Are we producing audio? volatile so both threads see the flip right away.
    /// </summary>
    private volatile bool _isRunning;

    /// <summary>
    /// Re-report a repeating loop error on the 1st hit and every Nth after, so a
    /// stuck fault stays visible without flooding subscribers.
    /// </summary>
    private const int LoopErrorReportInterval = 1000;

    /// <summary>
    /// After this many errors in a row the loop gives up instead of spinning forever.
    /// </summary>
    private const int LoopErrorFaultThreshold = 500;

    /// <summary>Error streak for the rust sync tick.</summary>
    private int _rustSyncConsecutiveErrors;

    /// <summary>
    /// Immutable sample rate / channels / buffer size, pinned at ctor.
    /// </summary>
    private readonly AudioConfig _config;

    /// <summary>
    /// Frames per mix cycle; sizes the pre-allocated mix and source buffers.
    /// </summary>
    private readonly int _bufferSizeInFrames;

    /// <summary>
    /// Idle sleep (ms) between mix cycles when nothing plays — quarter buffer, keeps CPU low.
    /// </summary>
    private readonly int _mixIntervalMs;

    /// <summary>
    /// Master out volume, 0..1. volatile for immediate cross-thread visibility.
    /// </summary>
    private volatile float _masterVolume;

    /// <summary>
    /// Master pan, -1..+1 (0 = center). volatile so the sync tick sees writes at once.
    /// </summary>
    private volatile float _masterPan;

    /// <summary>
    /// Last measured peak for the left out channel. volatile to dodge stale reads.
    /// </summary>
    private volatile float _leftPeak;

    /// <summary>
    /// Last measured peak for the right out channel. volatile to dodge stale reads.
    /// </summary>
    private volatile float _rightPeak;


#pragma warning disable CS0649
    /// <summary>
    /// Reserved underrun counter — not wired up yet.
    /// </summary>
    private long _totalUnderruns;
#pragma warning restore CS0649

    /// <summary>
    /// Master effect chain — the param model for the native master bus. Mutated under
    /// _effectsLock; the paired native effects do the actual audio.
    /// </summary>
    private readonly List<IEffectProcessor> _masterEffects;

    /// <summary>
    /// Guards _masterEffects on the main thread. Never taken on the audio thread.
    /// </summary>
    private readonly object _effectsLock = new();

    /// <summary>
    /// This mixer's id, used for the OwnaudioNet registry.
    /// </summary>
    private readonly Guid _mixerId;

    /// <summary>
    /// Disposed flag, checked by ThrowIfDisposed at the top of the public methods.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// This mixer's id.
    /// </summary>
    public Guid MixerId => _mixerId;

    /// <summary>
    /// Immutable audio config negotiated with the engine at ctor.
    /// </summary>
    public AudioConfig Config => _config;

    /// <summary>
    /// True between a good Start and the next Stop/Dispose.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Live count of registered sources, safe from any thread.
    /// </summary>
    public int SourceCount => _sources.Count;

    /// <summary>
    /// Master output volume, clamped to 0..1. Takes effect next mix cycle, no click.
    /// </summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Clamp(value, 0.0f, 1.0f);
    }

    /// <summary>
    /// Master pan, clamped to -1..+1 (equal-power, unity at center). Sweeps without a click.
    /// </summary>
    public float MasterPan
    {
        get => _masterPan;
        set => _masterPan = Math.Clamp(value, -1.0f, 1.0f);
    }

    /// <summary>
    /// Left-channel peak from the last mix cycle. 0..1, above 1 was clipped and limited.
    /// </summary>
    public float LeftPeak => _leftPeak;

    /// <summary>
    /// Right-channel peak from the last mix cycle. 0..1, above 1 was clipped and limited.
    /// </summary>
    public float RightPeak => _rightPeak;

    /// <summary>
    /// Total frames rendered since start, straight from the master clock.
    /// </summary>
    public long TotalMixedFrames => _masterClock.CurrentSamplePosition;

    /// <summary>
    /// Underrun total — reserved, currently always zero.
    /// </summary>
    public long TotalUnderruns => Interlocked.Read(ref _totalUnderruns);

    /// <summary>
    /// True while recording the output to a WAV file.
    /// </summary>
    public bool IsRecording => _isRecording;

    /// <summary>
    /// The shared MasterClock all clock-attached sources ride.
    /// </summary>
    public MasterClock MasterClock => _masterClock;

    /// <summary>
    /// How the clock advances: Realtime for live, Offline for deterministic renders.
    /// </summary>
    public ClockMode RenderingMode
    {
        get => _masterClock.Mode;
        set => _masterClock.Mode = value;
    }

    /// <summary>
    /// Fires when the device reports a buffer underrun.
    /// </summary>
#pragma warning disable CS0067
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;
#pragma warning restore CS0067

    /// <summary>
    /// Fires when a source blows up mid-mix. Dispatched from the mix thread — handlers must be thread-safe.
    /// </summary>
    public event EventHandler<AudioErrorEventArgs>? SourceError;

    /// <summary>
    /// Fires when a clock-attached source can't hand over its frames in time (audible dropout).
    /// </summary>
    public event EventHandler<TrackDropoutEventArgs>? TrackDropout;

    /// <summary>
    /// Fires once when every source has hit EndOfStream. Resets when a new source is added.
    /// </summary>
    public event EventHandler? PlaybackEnded;

    /// <summary>
    /// Fires on a native output device-loss/backend fault (unplug, disable, sleep/wake,
    /// sample-rate change). Lets a long-running host notice the audio went silent and recover.
    /// Dispatched from the sync tick (thread-safe handlers); only while the rust chain drives output.
    /// </summary>
    public event EventHandler<AudioStreamFaultEventArgs>? StreamFaulted;

    /// <summary>
    /// Builds a mixer that routes through an AudioEngineWrapper's pre-buffer, decoupling
    /// the mix thread from the engine. Use it with 8+ sources or 2+ master effects to
    /// avoid starvation under heavy DSP.
    /// </summary>
    /// <param name="engineWrapper"></param>
    /// <param name="bufferSizeInFrames"></param>
    public static AudioMixer Create(AudioEngineWrapper engineWrapper, int bufferSizeInFrames = 512)
    {
        ArgumentNullException.ThrowIfNull(engineWrapper);
        return new AudioMixer(engineWrapper.UnderlyingEngine, bufferSizeInFrames, engineWrapper);
    }

    /// <summary>
    /// Wrapper overload — chains to the main ctor then keeps the wrapper. Only Create uses it.
    /// </summary>
    /// <param name="engine"></param>
    /// <param name="bufferSizeInFrames"></param>
    /// <param name="wrapper"></param>
    private AudioMixer(IAudioEngine engine, int bufferSizeInFrames, AudioEngineWrapper wrapper)
        : this(engine, bufferSizeInFrames)
    {
        _engineWrapper = wrapper;
    }

    /// <summary>
    /// Main ctor: wired straight to an engine. Allocates the buffers and builds the master
    /// clock. The mix thread only actually spins up on the first Start.
    /// </summary>
    /// <param name="engine"></param>
    /// <param name="bufferSizeInFrames"></param>
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
    /// One-line diagnostic dump of the current state.
    /// </summary>
    public override string ToString()
    {
        return $"AudioMixer: {_config.SampleRate}Hz {_config.Channels}ch, Buffer: {_bufferSizeInFrames} frames, " +
               $"Sources: {SourceCount}, Running: {_isRunning}, Recording: {_isRecording}, " +
               $"Master Volume: {_masterVolume:F2}, Peaks: L={_leftPeak:F2} R={_rightPeak:F2}";
    }
}
