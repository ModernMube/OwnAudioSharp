using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ownaudio.Core;
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
    /// Lib version.
    /// </summary>
    public static Version Version { get; } = new(2, 6, 7);

    /// <summary>
    /// The wrapper, null until init.
    /// </summary>
    public static AudioEngineWrapper? Engine => _engineWrapper;

    /// <summary>
    /// One-shot init with the default cfg.
    /// </summary>
    public static void Initialize() => Initialize(CreateDefaultConfig());

    /// <summary>
    /// Init with a custom cfg. useMockEngine skips the hw (tests), bufferMultiplier
    /// sizes the ring buffer - bump to 16 for lots of srcs/fx.
    /// </summary>
    /// <param name="config"></param>
    /// <param name="useMockEngine"></param>
    /// <param name="bufferMultiplier"></param>
    public static void Initialize(AudioConfig config, bool useMockEngine = false, int bufferMultiplier = 8)
    {
        if(config == null) throw new ArgumentNullException(nameof(config));
        lock (_initLock) {
            if (_initialized) return;
            _engineWrapper = new AudioEngineWrapper(_createEngine(config, useMockEngine), config, bufferMultiplier);
            _initialized = true;
        }
    }

    /// <summary>
    /// Kick off audio processing.
    /// </summary>
    public static void Start()
    {
        lock (_initLock) {
            if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
            _engineWrapper.Start();
        }
    }

    /// <summary>
    /// Halt processing, Start() resumes it.
    /// </summary>
    public static void Stop()
    {
        lock (_initLock) {
            if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
            _engineWrapper.Stop();
        }
    }

    /// <summary>
    /// Full teardown - stops the engine, frees it and resets state.
    /// </summary>
    public static void Shutdown()
    {
        lock (_initLock) {
            _engineWrapper?.Dispose();
            _engineWrapper = null;
            _initialized = false;
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
    /// <returns></returns>
    public static Task InitializeAsync(CancellationToken cancellationToken = default)
        => InitializeAsync(CreateDefaultConfig(), cancellationToken: cancellationToken);

    /// <summary>
    /// Async init. useMockEngine skips the hw (tests), bufferMultiplier sizes the ring buffer.
    /// </summary>
    /// <param name="config"></param>
    /// <param name="useMockEngine"></param>
    /// <param name="bufferMultiplier"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task InitializeAsync(AudioConfig config, bool useMockEngine = false, int bufferMultiplier = 8, CancellationToken cancellationToken = default)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        return Task.Run(() => Initialize(config, useMockEngine, bufferMultiplier), cancellationToken);
    }

    /// <summary>
    /// BYO engine variant for custom platform impls. engine must be pre-initialized,
    /// bufferMultiplier sizes the ring buffer.
    /// </summary>
    /// <param name="engine"></param>
    /// <param name="config"></param>
    /// <param name="bufferMultiplier"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Task InitializeAsync(IAudioEngine engine, AudioConfig config, int bufferMultiplier = 8, CancellationToken cancellationToken = default)
    {
        if (engine == null) throw new ArgumentNullException(nameof(engine));
        if (config == null) throw new ArgumentNullException(nameof(config));
        return Task.Run(() =>
        {
            lock (_initLock) {
                if (_initialized) return;
                _engineWrapper = new AudioEngineWrapper(engine, config, bufferMultiplier);
                _initialized = true;
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
        if(!config.Validate()) throw new AudioEngineException("bad audio config, check SampleRate/Channels/BufferSize");

        var _engine = new RustAudioEngine();
        int _result = _engine.Initialize(config);
        if (_result < 0) {
            _engine.Dispose();
            throw new AudioEngineException($"rust engine init failed: {_result}", _result);
        }
        return _engine;
    }
}
