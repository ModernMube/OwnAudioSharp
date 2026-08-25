using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Logger;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using OwnaudioNET.Engine;
using OwnaudioNET.Exceptions;
using OwnaudioNET.Mixing;

namespace OwnaudioNET;

/// <summary>
/// Lib entry point, rust engine under the hood (phase-3 tmp ns clone).
/// </summary>
public static partial class OwnaudioNet
{
    static bool _initialized;
    static AudioEngineWrapper? _engineWrapper;
    static readonly object _initLock = new();
    static AudioMixer? _registeredMixer;
    static readonly object _mixerLock = new();

    /// <summary>
    /// True once Initialize() ran.
    /// </summary>
    public static bool IsInitialized => _initialized;

    /// <summary>
    /// True while the engine is pushing audio.
    /// </summary>
    public static bool IsRunning => _engineWrapper?.IsRunning ?? false;

    /// <summary>
    /// Lib version, read off the assembly so it never drifts from the package.
    /// </summary>
    public static Version Version { get; } = typeof(OwnaudioNet).Assembly.GetName().Version ?? new(4, 0, 0);

    /// <summary>
    /// The wrapper, null until init.
    /// </summary>
    public static AudioEngineWrapper? Engine => _engineWrapper;

    /// <summary>
    /// One-shot init with the default cfg. logLevel opens up the console logger, it stays quiet otherwise.
    /// </summary>
    /// <param name="logLevel"></param>
    public static void Initialize(Log.Level logLevel = Log.Level.Disabled) => Initialize(CreateDefaultConfig(), logLevel: logLevel);

    /// <summary>
    /// Init with a custom cfg. useMockEngine skips the hw (tests), bufferMultiplier
    /// sizes the ring buffer - bump to 16 for lots of srcs/fx. logLevel turns the console
    /// logger on, which is off unless you ask for it.
    /// </summary>
    /// <param name="config"></param>
    /// <param name="useMockEngine"></param>
    /// <param name="bufferMultiplier"></param>
    /// <param name="logLevel"></param>
    public static void Initialize(AudioConfig config, bool useMockEngine = false, int bufferMultiplier = 8,
        Log.Level logLevel = Log.Level.Disabled)
    {
        if(config == null) throw new ArgumentNullException(nameof(config));

        Log.LoggerLevel = logLevel;

        lock (_initLock) {
            if (_initialized) {
                Log.Warning("[OwnaudioNet] Already initialized, this Initialize() call does nothing");
                return;
            }

            Log.Info($"[OwnaudioNet] {Version} starting up on {(useMockEngine ? "mock" : "rust")} engine");

            TempFileCleanup.SweepOnce();

            _engineWrapper = new AudioEngineWrapper(_createEngine(config, useMockEngine), config, bufferMultiplier);
            _initialized = true;

            Log.Info("[OwnaudioNet] Initialized");
        }
    }

    /// <summary>
    /// Kick off audio processing.
    /// </summary>
    public static void Start()
    {
        lock (_initLock) {
            if (_engineWrapper == null) {
                Log.Error("[OwnaudioNet] Transport call before Initialize()");
                throw new InvalidOperationException("call Initialize() first");
            }
            _engineWrapper.Start();
        }
    }

    /// <summary>
    /// Halt processing, Start() resumes it.
    /// </summary>
    public static void Stop()
    {
        lock (_initLock) {
            if (_engineWrapper == null) {
                Log.Error("[OwnaudioNet] Transport call before Initialize()");
                throw new InvalidOperationException("call Initialize() first");
            }
            _engineWrapper.Stop();
        }
    }

    /// <summary>
    /// Full teardown - stops the engine, frees it and resets state.
    /// </summary>
    public static void Shutdown()
    {
        lock (_initLock) {
            if (!_initialized) return;

            _engineWrapper?.Dispose();
            _engineWrapper = null;
            _initialized = false;

            Log.Info("[OwnaudioNet] Shut down");
        }
    }

    /// <summary>
    /// Mixer ctor hooks in here, last one wins.
    /// </summary>
    /// <param name="mixer"></param>
    internal static void RegisterAudioMixer(AudioMixer mixer)
    {
        if (mixer == null) return;
        lock (_mixerLock) _registeredMixer = mixer;
    }

    /// <summary>
    /// Mixer Dispose() hooks in here.
    /// </summary>
    /// <param name="mixer"></param>
    internal static void UnregisterAudioMixer(AudioMixer mixer)
    {
        if (mixer == null) return;
        lock (_mixerLock)
            if (_registeredMixer?.MixerId == mixer.MixerId) _registeredMixer = null;
    }

    /// <summary>
    /// Pick which mixer NetworkSync uses.
    /// </summary>
    /// <param name="mixer"></param>
    public static void SetPrimaryAudioMixer(AudioMixer mixer)
    {
        if (mixer == null) throw new ArgumentNullException(nameof(mixer));
        lock (_mixerLock) _registeredMixer = mixer;
    }

    /// <summary>
    /// The current NetworkSync mixer, null if none.
    /// </summary>
    /// <returns></returns>
    public static AudioMixer? GetRegisteredAudioMixer()
    {
        lock (_mixerLock) return _registeredMixer;
    }

    /// <summary>
    /// Push interleaved samples to the output.
    /// </summary>
    /// <param name="samples"></param>
    public static void Send(ReadOnlySpan<float> samples)
    {
        if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
        _engineWrapper.Send(samples);
    }

    /// <summary>
    /// Grab captured input, hand the buf back via ReturnInputBuffer().
    /// </summary>
    /// <param name="sampleCount"></param>
    /// <returns></returns>
    public static float[]? Receive(out int sampleCount)
    {
        if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
        return _engineWrapper.Receive(out sampleCount);
    }

    /// <summary>
    /// Stop bg device polling (handy around VST editor windows).
    /// </summary>
    public static void PauseDeviceMonitoring()
    {
        if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
        _engineWrapper.PauseDeviceMonitoring();
    }

    /// <summary>
    /// Restart bg device polling.
    /// </summary>
    public static void ResumeDeviceMonitoring()
    {
        if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
        _engineWrapper.ResumeDeviceMonitoring();
    }

    /// <summary>
    /// Hand the pooled input buf back after Receive().
    /// </summary>
    /// <param name="buffer"></param>
    public static void ReturnInputBuffer(float[] buffer)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
        _engineWrapper.ReturnInputBuffer(buffer);
    }

    /// <summary>
    /// Output device list.
    /// </summary>
    /// <returns></returns>
    public static List<AudioDeviceInfo> GetOutputDevices()
    {
        if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
        return _engineWrapper.GetOutputDevices();
    }

    /// <summary>
    /// Input device list.
    /// </summary>
    /// <returns></returns>
    public static List<AudioDeviceInfo> GetInputDevices()
    {
        if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
        return _engineWrapper.GetInputDevices();
    }

    /// <summary>
    /// Playback latency in frames, 0 before Initialize. Divide by SampleRate for seconds.
    /// </summary>
    public static int OutputLatencyFrames => _engineWrapper?.OutputLatencyFrames ?? 0;

    /// <summary>
    /// Capture latency in frames — subtract from the capture position to align a take. 0 before Initialize.
    /// </summary>
    public static int InputLatencyFrames => _engineWrapper?.InputLatencyFrames ?? 0;

    /// <summary>
    /// Capture frames lost so far because Receive() fell behind the input ring. Anything above 0
    /// means the take has a hole in it — worth surfacing to the user.
    /// </summary>
    public static long TotalInputOverflowFrames => _engineWrapper?.TotalInputOverflowFrames ?? 0;

    /// <summary>
    /// 48k stereo presets, only the buf size differs.
    /// </summary>
    /// <returns></returns>
    public static AudioConfig CreateDefaultConfig() => new() { SampleRate = 48000, Channels = 2, BufferSize = 512 };
    public static AudioConfig CreateLowLatencyConfig() => new() { SampleRate = 48000, Channels = 2, BufferSize = 128 };
    public static AudioConfig CreateHighLatencyConfig() => new() { SampleRate = 48000, Channels = 2, BufferSize = 2048 };

    /// <summary>
    /// Async init so the UI thread doesn't stall.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <param name="logLevel"></param>
    /// <returns></returns>
    public static Task InitializeAsync(CancellationToken cancellationToken = default, Log.Level logLevel = Log.Level.Disabled)
        => InitializeAsync(CreateDefaultConfig(), cancellationToken: cancellationToken, logLevel: logLevel);

    /// <summary>
    /// Async init. useMockEngine skips the hw (tests), bufferMultiplier sizes the ring buffer,
    /// logLevel opens up the otherwise silent console logger.
    /// </summary>
    /// <param name="config"></param>
    /// <param name="useMockEngine"></param>
    /// <param name="bufferMultiplier"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="logLevel"></param>
    /// <returns></returns>
    public static Task InitializeAsync(AudioConfig config, bool useMockEngine = false, int bufferMultiplier = 8,
        CancellationToken cancellationToken = default, Log.Level logLevel = Log.Level.Disabled)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        return Task.Run(() => Initialize(config, useMockEngine, bufferMultiplier, logLevel), cancellationToken);
    }

    /// <summary>
    /// BYO engine variant for custom platform impls. engine must be pre-initialized,
    /// bufferMultiplier sizes the ring buffer.
    /// </summary>
    /// <param name="engine"></param>
    /// <param name="config"></param>
    /// <param name="bufferMultiplier"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="logLevel"></param>
    /// <returns></returns>
    public static Task InitializeAsync(IAudioEngine engine, AudioConfig config, int bufferMultiplier = 8,
        CancellationToken cancellationToken = default, Log.Level logLevel = Log.Level.Disabled)
    {
        if (engine == null) throw new ArgumentNullException(nameof(engine));
        if (config == null) throw new ArgumentNullException(nameof(config));

        Log.LoggerLevel = logLevel;

        return Task.Run(() =>
        {
            lock (_initLock) {
                if (_initialized) {
                    Log.Warning("[OwnaudioNet] Already initialized, this InitializeAsync() call does nothing");
                    return;
                }

                Log.Info($"[OwnaudioNet] {Version} starting up on a caller-supplied {engine.GetType().Name}");

                _engineWrapper = new AudioEngineWrapper(engine, config, bufferMultiplier);
                _initialized = true;

                Log.Info("[OwnaudioNet] Initialized");
            }
        }, cancellationToken);
    }

    public static Task StopAsync(CancellationToken cancellationToken = default) => Task.Run(Stop, cancellationToken);
    public static Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.Run(Shutdown, cancellationToken);
    public static Task<List<AudioDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default) => Task.Run(GetOutputDevices, cancellationToken);
    public static Task<List<AudioDeviceInfo>> GetInputDevicesAsync(CancellationToken cancellationToken = default) => Task.Run(GetInputDevices, cancellationToken);

    /// <summary>
    /// Rust engine unless a mock was asked for.
    /// </summary>
    /// <param name="config"></param>
    /// <param name="useMockEngine"></param>
    /// <returns></returns>
    static IAudioEngine _createEngine(AudioConfig config, bool useMockEngine)
    {
        if (useMockEngine) return OwnaudioNET.Engine.AudioEngineFactory.CreateMockEngine(config, generateTestSignal: false);
        if (!config.Validate(out string? _invalid))
        {
            Log.Error($"[Ownaudio] Invalid audio configuration: {_invalid}");
            throw new AudioEngineException($"Invalid audio configuration: {_invalid}");
        }

        var _engine = new RustAudioEngine();
        int _result = _engine.Initialize(config);
        if (_result < 0) {
            _engine.Dispose();
            throw new AudioEngineException($"rust engine init failed: {_result}", _result);
        }
        return _engine;
    }
}
