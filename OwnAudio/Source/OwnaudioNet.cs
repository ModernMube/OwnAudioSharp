using Ownaudio.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Exceptions;
using OwnaudioNET.Mixing;

namespace OwnaudioNET;

/// <summary>
/// Main entry point for the OwnaudioNET library.
/// Provides factory methods and global configuration for the audio system.
/// </summary>
public static partial class OwnaudioNet
{
    private static bool _initialized;
    private static AudioEngineWrapper? _engineWrapper;
    private static readonly object _initLock = new();

    // AudioMixer registry for NetworkSync
    private static AudioMixer? _registeredMixer;
    private static readonly object _mixerRegistryLock = new();

    /// <summary>
    /// Gets whether the audio system has been initialized.
    /// </summary>
    public static bool IsInitialized => _initialized;

    /// <summary>
    /// Gets whether the audio engine is currently running.
    /// </summary>
    public static bool IsRunning => _engineWrapper?.IsRunning ?? false;

    /// <summary>
    /// Gets the version of the OwnaudioNET library.
    /// </summary>
    public static Version Version { get; } = new Version(2, 5, 5);

    /// <summary>
    /// Gets the current audio engine wrapper (null if not initialized).
    /// </summary>
    public static AudioEngineWrapper? Engine => _engineWrapper;

    /// <summary>
    /// Initializes the OwnaudioNET library with default configuration (48kHz, stereo, 512 frames).
    /// This method should be called once at application startup.
    /// </summary>
    /// <exception cref="AudioEngineException">Thrown if initialization fails.</exception>
    public static void Initialize()
    {
        AudioConfig defaultConfig = new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = 512
        };
        Initialize(defaultConfig);
    }

    /// <summary>
    /// Initializes the OwnaudioNET library with custom configuration.
    /// This method should be called once at application startup.
    /// </summary>
    /// <param name="config">The audio configuration.</param>
    /// <param name="useMockEngine">If true, uses MockAudioEngine for testing (no hardware required).</param>
    /// <exception cref="ArgumentNullException">Thrown if config is null.</exception>
    /// <exception cref="AudioEngineException">Thrown if initialization fails.</exception>
    public static void Initialize(AudioConfig config, bool useMockEngine = false)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        lock (_initLock)
        {
            if (_initialized)
                return;

            try
            {
                IAudioEngine engine;
                if (useMockEngine)
                {
                    engine = OwnaudioNET.Engine.AudioEngineFactory.CreateMockEngine(config, generateTestSignal: false);
                }
                else
                {
                    engine = OwnaudioNET.Engine.AudioEngineFactory.CreateEngine(config);
                }

                // Create wrapper
                _engineWrapper = new AudioEngineWrapper(engine, config);

                _initialized = true;
            }
            catch (Exception ex) when (ex is not AudioEngineException and not ArgumentNullException)
            {
                throw new AudioEngineException("Failed to initialize audio engine.", ex);
            }
        }
    }

    /// <summary>
    /// Starts the audio engine. Call this after Initialize() to begin audio processing.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if not initialized.</exception>
    /// <exception cref="AudioEngineException">Thrown if start fails.</exception>
    public static void Start()
    {
        lock (_initLock)
        {
            if (!_initialized || _engineWrapper == null)
                throw new InvalidOperationException("OwnaudioNet must be initialized before calling Start(). Call Initialize() first.");

            _engineWrapper.Start();
        }
    }

    /// <summary>
    /// Stops the audio engine. Audio processing will cease until Start() is called again.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if not initialized.</exception>
    /// <exception cref="AudioEngineException">Thrown if stop fails.</exception>
    public static void Stop()
    {
        lock (_initLock)
        {
            if (!_initialized || _engineWrapper == null)
                throw new InvalidOperationException("OwnaudioNet must be initialized before calling Stop().");

            _engineWrapper.Stop();
        }
    }

    /// <summary>
    /// Shuts down the OwnaudioNET library and releases all resources.
    /// Automatically stops the engine if running.
    /// </summary>
    public static void Shutdown()
    {
        lock (_initLock)
        {
            if (!_initialized)
                return;

            try
            {
                _engineWrapper?.Dispose();
                _engineWrapper = null;
            }
            finally
            {
                _initialized = false;
            }
        }
    }

    /// <summary>
    /// Registers an AudioMixer instance for NetworkSync usage.
    /// Called automatically by AudioMixer constructor.
    /// If multiple mixers exist, the most recently created one is registered.
    /// </summary>
    /// <param name="mixer">The AudioMixer instance to register.</param>
    internal static void RegisterAudioMixer(AudioMixer mixer)
    {
        if (mixer == null)
            return;

        lock (_mixerRegistryLock)
        {
            _registeredMixer = mixer;
        }
    }

    /// <summary>
    /// Unregisters an AudioMixer instance.
    /// Called automatically by AudioMixer.Dispose().
    /// </summary>
    /// <param name="mixer">The AudioMixer instance to unregister.</param>
    internal static void UnregisterAudioMixer(AudioMixer mixer)
    {
        if (mixer == null)
            return;

        lock (_mixerRegistryLock)
        {
            // Only unregister if this is the currently registered mixer
            if (_registeredMixer?.MixerId == mixer.MixerId)
            {
                _registeredMixer = null;
            }
        }
    }

    /// <summary>
    /// Explicitly sets the primary AudioMixer for NetworkSync operations.
    /// Use this when you have multiple AudioMixer instances and want to specify
    /// which one should be used for network synchronization.
    /// </summary>
    /// <param name="mixer">The AudioMixer instance to use for NetworkSync.</param>
    /// <exception cref="ArgumentNullException">Thrown if mixer is null.</exception>
    public static void SetPrimaryAudioMixer(AudioMixer mixer)
    {
        if (mixer == null)
            throw new ArgumentNullException(nameof(mixer));

        lock (_mixerRegistryLock)
        {
            _registeredMixer = mixer;
        }
    }

    /// <summary>
    /// Gets the currently registered AudioMixer instance.
    /// Returns null if no mixer is registered.
    /// </summary>
    /// <returns>The registered AudioMixer, or null if none exists.</returns>
    public static AudioMixer? GetRegisteredAudioMixer()
    {
        lock (_mixerRegistryLock)
        {
            return _registeredMixer;
        }
    }

    /// <summary>
    /// Sends audio samples to the output device.
    /// </summary>
    /// <param name="samples">The audio samples to send (interleaved for stereo).</param>
    /// <exception cref="InvalidOperationException">Thrown if not initialized or not running.</exception>
    public static void Send(ReadOnlySpan<float> samples)
    {
        if (!_initialized || _engineWrapper == null)
            throw new InvalidOperationException("OwnaudioNet must be initialized and started before sending audio. Call Initialize() and Start() first.");

        _engineWrapper.Send(samples);
    }

    /// <summary>
    /// Receives audio samples from the input device (if input is enabled).
    /// </summary>
    /// <param name="sampleCount">The number of samples received.</param>
    /// <returns>A buffer containing the captured audio samples, or null if no data available.
    /// IMPORTANT: Call ReturnInputBuffer() when done to return buffer to pool.</returns>
    /// <exception cref="InvalidOperationException">Thrown if not initialized or not running.</exception>
    public static float[]? Receive(out int sampleCount)
    {
        if (!_initialized || _engineWrapper == null)
            throw new InvalidOperationException("OwnaudioNet must be initialized and started before receiving audio. Call Initialize() and Start() first.");

        return _engineWrapper.Receive(out sampleCount);
    }

    /// <summary>
    /// Pauses the background device monitoring task.
    /// This prevents device enumeration and change detection from interfering with UI operations.
    /// Automatically called when Start() is invoked, but can be called manually if needed.
    /// Recommended to call when opening VST editor windows or during critical UI operations.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if OwnaudioNet is not initialized.</exception>
    public static void PauseDeviceMonitoring()
    {
        lock (_initLock)
        {
            if (!_initialized || _engineWrapper == null)
                throw new InvalidOperationException("OwnaudioNet must be initialized before pausing device monitoring.");

            _engineWrapper.PauseDeviceMonitoring();
        }
    }

    /// <summary>
    /// Resumes the background device monitoring task.
    /// Automatically called when Stop() is invoked, but can be called manually if needed.
    /// Should be called after closing VST editor windows or when critical UI operations complete.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if OwnaudioNet is not initialized.</exception>
    public static void ResumeDeviceMonitoring()
    {
        lock (_initLock)
        {
            if (!_initialized || _engineWrapper == null)
                throw new InvalidOperationException("OwnaudioNet must be initialized before resuming device monitoring.");

            _engineWrapper.ResumeDeviceMonitoring();
        }
    }

    /// <summary>
    /// Returns an input buffer to the pool after processing.
    /// IMPORTANT: Always call this after processing a buffer received from Receive().
    /// </summary>
    /// <param name="buffer">The buffer to return.</param>
    /// <exception cref="ArgumentNullException">Thrown if buffer is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if not initialized.</exception>
    public static void ReturnInputBuffer(float[] buffer)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));

        if (!_initialized || _engineWrapper == null)
            throw new InvalidOperationException("OwnaudioNet must be initialized.");

        _engineWrapper.ReturnInputBuffer(buffer);
    }

    /// <summary>
    /// Gets the list of available output devices.
    /// </summary>
    /// <returns>List of output device information.</returns>
    /// <exception cref="InvalidOperationException">Thrown if not initialized.</exception>
    public static List<AudioDeviceInfo> GetOutputDevices()
    {
        if (!_initialized || _engineWrapper == null)
            throw new InvalidOperationException("OwnaudioNet must be initialized. Call Initialize() first.");

        return _engineWrapper.GetOutputDevices();
    }

    /// <summary>
    /// Gets the list of available input devices.
    /// </summary>
    /// <returns>List of input device information.</returns>
    /// <exception cref="InvalidOperationException">Thrown if not initialized.</exception>
    public static List<AudioDeviceInfo> GetInputDevices()
    {
        if (!_initialized || _engineWrapper == null)
            throw new InvalidOperationException("OwnaudioNet must be initialized. Call Initialize() first.");

        return _engineWrapper.GetInputDevices();
    }

    /// <summary>
    /// Creates a default audio configuration (48kHz, stereo, 512 frames).
    /// </summary>
    public static AudioConfig CreateDefaultConfig()
    {
        return new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = 512
        };
    }

    /// <summary>
    /// Creates a low-latency audio configuration (48kHz, stereo, 128 frames).
    /// </summary>
    public static AudioConfig CreateLowLatencyConfig()
    {
        return new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = 128
        };
    }

    /// <summary>
    /// Creates a high-latency audio configuration (48kHz, stereo, 2048 frames).
    /// </summary>
    public static AudioConfig CreateHighLatencyConfig()
    {
        return new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = 2048
        };
    }

    // ==========================================
    // ASYNC API - Prevents UI Thread Blocking
    // ==========================================

    /// <summary>
    /// Initializes the OwnaudioNET library asynchronously with default configuration.
    /// This method prevents UI thread blocking by running initialization on a background thread.
    /// Recommended for UI applications (WPF, WinForms, MAUI, Avalonia).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to abort initialization.</param>
    /// <exception cref="AudioEngineException">Thrown if initialization fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception>
    public static async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        AudioConfig defaultConfig = new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = 512
        };
        await InitializeAsync(defaultConfig, useMockEngine: false, cancellationToken);
    }

    /// <summary>
    /// Initializes the OwnaudioNET library asynchronously with custom configuration.
    /// This method prevents UI thread blocking by running initialization on a background thread.
    /// </summary>
    /// <param name="config">The audio configuration.</param>
    /// <param name="useMockEngine">If true, uses MockAudioEngine for testing (no hardware required).</param>
    /// <param name="cancellationToken">Cancellation token to abort initialization.</param>
    /// <exception cref="ArgumentNullException">Thrown if config is null.</exception>
    /// <exception cref="AudioEngineException">Thrown if initialization fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception>
    public static async Task InitializeAsync(AudioConfig config, bool useMockEngine = false, CancellationToken cancellationToken = default)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        // Run initialization on background thread to prevent UI blocking
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Initialize(config, useMockEngine);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Initializes the OwnaudioNET library asynchronously with a pre-initialized audio engine.
    /// This is useful for platforms not automatically detected (e.g., custom engine implementations).
    /// </summary>
    /// <param name="engine">A pre-initialized IAudioEngine instance.</param>
    /// <param name="config">The audio configuration used to initialize the engine.</param>
    /// <param name="cancellationToken">Cancellation token to abort initialization.</param>
    /// <exception cref="ArgumentNullException">Thrown if engine or config is null.</exception>
    /// <exception cref="AudioEngineException">Thrown if initialization fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception>
    public static async Task InitializeAsync(IAudioEngine engine, AudioConfig config, CancellationToken cancellationToken = default)
    {
        if (engine == null)
            throw new ArgumentNullException(nameof(engine));
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_initLock)
            {
                if (_initialized)
                    return;

                try
                {
                    // Create wrapper with the provided engine
                    _engineWrapper = new AudioEngineWrapper(engine, config);
                    _initialized = true;
                }
                catch (Exception ex) when (ex is not AudioEngineException and not ArgumentNullException)
                {
                    throw new AudioEngineException("Failed to initialize audio engine wrapper.", ex);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the audio engine asynchronously.
    /// This method prevents UI thread blocking by running stop on a background thread.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to abort the wait (not the stop itself).</param>
    /// <exception cref="InvalidOperationException">Thrown if not initialized.</exception>
    /// <exception cref="AudioEngineException">Thrown if stop fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception> 
    public static async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_initLock)
            {
                if (!_initialized || _engineWrapper == null)
                    throw new InvalidOperationException("OwnaudioNet must be initialized before calling StopAsync().");

                _engineWrapper.Stop();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shuts down the OwnaudioNET library asynchronously and releases all resources.
    /// This method prevents UI thread blocking by running shutdown on a background thread.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to abort the operation.</param>
    /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception>
    public static async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_initLock)
            {
                if (!_initialized)
                    return;

                try
                {
                    _engineWrapper?.Dispose();
                    _engineWrapper = null;
                }
                finally
                {
                    _initialized = false;
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the list of available output devices asynchronously.
    /// This method prevents UI thread blocking by running device enumeration on a background thread.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to abort the operation.</param>
    /// <returns>List of output device information.</returns>
    /// <exception cref="InvalidOperationException">Thrown if not initialized.</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception>
    public static async Task<List<AudioDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_initialized || _engineWrapper == null)
                throw new InvalidOperationException("OwnaudioNet must be initialized. Call InitializeAsync() first.");

            return _engineWrapper.GetOutputDevices();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the list of available input devices asynchronously.
    /// This method prevents UI thread blocking by running device enumeration on a background thread.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to abort the operation.</param>
    /// <returns>List of input device information.</returns>
    /// <exception cref="InvalidOperationException">Thrown if not initialized.</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception>
    public static async Task<List<AudioDeviceInfo>> GetInputDevicesAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_initialized || _engineWrapper == null)
                throw new InvalidOperationException("OwnaudioNet must be initialized. Call InitializeAsync() first.");

            return _engineWrapper.GetInputDevices();
        }, cancellationToken).ConfigureAwait(false);
    }
}
