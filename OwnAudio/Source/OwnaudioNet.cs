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

    // init done?
    public static bool IsInitialized => _initialized;

    // engine running?
    public static bool IsRunning => _engineWrapper?.IsRunning ?? false;

    public static Version Version { get; } = new(2, 6, 7);

    // wrapper, null until init
    public static AudioEngineWrapper? Engine => _engineWrapper;

    // one-shot init w/ default cfg
    public static void Initialize() => Initialize(CreateDefaultConfig());

    //<Summary>
    // init w/ custom cfg; useMockEngine = no hw needed (tests),
    // bufferMultiplier sizes the ring buffer, bump to 16 for lots of srcs/fx
    //</Summary>
    public static void Initialize(AudioConfig config, bool useMockEngine = false, int bufferMultiplier = 8)
    {
        if(config == null) throw new ArgumentNullException(nameof(config));
        lock (_initLock) {
            if (_initialized) return;
            _engineWrapper = new AudioEngineWrapper(CreateEngine(config, useMockEngine), config, bufferMultiplier);
            _initialized = true;
        }
    }

    //<Summary>
    // kick off audio processing
    //</Summary>
    public static void Start()
    {
        lock (_initLock) {
            if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
            _engineWrapper.Start();
        }
    }

    //<Summary>
    // halt processing, Start() resumes
    //</Summary>
    public static void Stop()
    {
        lock (_initLock) {
            if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
            _engineWrapper.Stop();
        }
    }

    //<Summary>
    // stop processing, free engine, reset state
    // full teardown, stops engine too
    //</Summary>
    public static void Shutdown()
    {
        lock (_initLock) {
            _engineWrapper?.Dispose();
            _engineWrapper = null;
            _initialized = false;
        }
    }

    // mixer ctor hooks in here, last one wins
    internal static void RegisterAudioMixer(AudioMixer mixer)
    {
        if (mixer == null) return;
        lock (_mixerLock) _registeredMixer = mixer;
    }

    // mixer Dispose() hooks in here
    internal static void UnregisterAudioMixer(AudioMixer mixer)
    {
        if (mixer == null) return;
        lock (_mixerLock)
            if (_registeredMixer?.MixerId == mixer.MixerId) _registeredMixer = null;
    }

    // pick which mixer NetworkSync uses
    public static void SetPrimaryAudioMixer(AudioMixer mixer)
    {
        if (mixer == null) throw new ArgumentNullException(nameof(mixer));
        lock (_mixerLock) _registeredMixer = mixer;
    }

    // current NetworkSync mixer, null if none
    public static AudioMixer? GetRegisteredAudioMixer()
    {
        lock (_mixerLock) return _registeredMixer;
    }

    // push interleaved samples to output
    public static void Send(ReadOnlySpan<float> samples)
    {
        if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
        _engineWrapper.Send(samples);
    }

    // grab captured input, give buf back via ReturnInputBuffer()
    public static float[]? Receive(out int sampleCount)
    {
        if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
        return _engineWrapper.Receive(out sampleCount);
    }

    // stop bg device polling (handy around VST editor windows)
    public static void PauseDeviceMonitoring()
    {
        if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
        _engineWrapper.PauseDeviceMonitoring();
    }

    // restart bg device polling
    public static void ResumeDeviceMonitoring()
    {
        if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
        _engineWrapper.ResumeDeviceMonitoring();
    }

    // hand pooled input buf back after Receive()
    public static void ReturnInputBuffer(float[] buffer)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
        _engineWrapper.ReturnInputBuffer(buffer);
    }

    // output device list
    public static List<AudioDeviceInfo> GetOutputDevices()
    {
        if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
        return _engineWrapper.GetOutputDevices();
    }

    // input device list
    public static List<AudioDeviceInfo> GetInputDevices()
    {
        if (_engineWrapper == null) throw new InvalidOperationException("call Initialize() first");
        return _engineWrapper.GetInputDevices();
    }

    // 48k stereo presets, only buf size differs
    public static AudioConfig CreateDefaultConfig() => new() { SampleRate = 48000, Channels = 2, BufferSize = 512 };
    public static AudioConfig CreateLowLatencyConfig() => new() { SampleRate = 48000, Channels = 2, BufferSize = 128 };
    public static AudioConfig CreateHighLatencyConfig() => new() { SampleRate = 48000, Channels = 2, BufferSize = 2048 };

    // async init so the UI thread doesn't stall
    public static Task InitializeAsync(CancellationToken cancellationToken = default)
        => InitializeAsync(CreateDefaultConfig(), cancellationToken: cancellationToken);

    // useMockEngine = no hw needed (tests), bufferMultiplier sizes the ring buffer
    public static Task InitializeAsync(AudioConfig config, bool useMockEngine = false, int bufferMultiplier = 8, CancellationToken cancellationToken = default)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        return Task.Run(() => Initialize(config, useMockEngine, bufferMultiplier), cancellationToken);
    }

    // BYO engine variant for custom platform impls, engine must be pre-initialized,
    // bufferMultiplier sizes the ring buffer
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

    // rust engine unless mock requested
    static IAudioEngine CreateEngine(AudioConfig config, bool useMockEngine)
    {
        if (useMockEngine) return OwnaudioNET.Engine.AudioEngineFactory.CreateMockEngine(config, generateTestSignal: false);
        if (!config.Validate()) throw new AudioEngineException("bad audio config, check SampleRate/Channels/BufferSize");

        var engine = new RustAudioEngine();
        int result = engine.Initialize(config);
        if (result < 0) {
            engine.Dispose();
            throw new AudioEngineException($"rust engine init failed: {result}", result);
        }
        return engine;
    }
}
