using Ownaudio.Core;
using OwnaudioNET;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;

namespace OwnaudioShowcase.Services;

/// <summary>
/// Centralized audio engine service implementation.
/// Manages OwnaudioNET engine lifecycle and provides high-level audio operations.
/// Thread-safe singleton service for the application.
/// </summary>
public class AudioEngineService : IAudioEngineService, IDisposable
{
    private AudioMixer? _mixer;
    private AudioConfig? _config;
    private bool _initialized;
    private bool _running;
    private bool _disposed;
    private readonly object _lock = new();

    public bool IsInitialized => _initialized;
    public bool IsRunning => _running;
    public AudioMixer? Mixer => _mixer;
    public AudioConfig? Config => _config;

    /// <summary>
    /// Initializes the audio engine asynchronously with default configuration (48kHz, stereo, 512 frames).
    /// </summary>
    public async Task InitializeAsync()
    {
        var defaultConfig = new AudioConfig
        {
            SampleRate = 48000,
            Channels = 2,
            BufferSize = 512,
            HostType = EngineHostType.None
        };
        await InitializeAsync(defaultConfig);
    }

    /// <summary>
    /// Initializes the audio engine asynchronously with custom configuration.
    /// CRITICAL: Uses async to prevent UI thread blocking (can take 50-5000ms depending on platform).
    /// </summary>
    public async Task InitializeAsync(AudioConfig config)
    {
        if (_initialized)
            return;

        lock (_lock)
        {
            if (_initialized)
                return;
        }

        _config = config ?? throw new ArgumentNullException(nameof(config));

        // Initialize OwnaudioNET engine (async to prevent UI blocking)
        await OwnaudioNet.InitializeAsync(_config);

        // Get and log the engine type being used
        string engineType = OwnaudioNET.Engine.AudioEngineFactory.GetPlatformEngineName();
        string engineInfo = OwnaudioNet.Engine?.UnderlyingEngine?.GetType()?.FullName ?? "Unknown";

        Console.WriteLine($"=== OwnAudio Engine Initialized ===");
        Console.WriteLine($"Engine Type: {engineType}");
        Console.WriteLine($"Actual Engine: {engineInfo}");
        Console.WriteLine($"Configuration: {_config.SampleRate}Hz, {_config.Channels} channels, {_config.BufferSize} buffer");
        Console.WriteLine($"===================================");

        // Create AudioMixer using the underlying IAudioEngine from the wrapper
        _mixer = new AudioMixer(OwnaudioNet.Engine!.UnderlyingEngine, bufferSizeInFrames: _config.BufferSize);

        lock (_lock)
        {
            _initialized = true;
        }
    }

    /// <summary>
    /// Starts the audio engine and mixer.
    /// Must be called after Initialize and before any audio playback.
    /// </summary>
    public void Start()
    {
        if (!_initialized)
            throw new InvalidOperationException("Audio engine must be initialized before starting. Call InitializeAsync() first.");

        if (_running)
            return;

        OwnaudioNet.Start();
        _mixer?.Start();

        lock (_lock)
        {
            _running = true;
        }
    }

    /// <summary>
    /// Stops the audio engine and mixer asynchronously.
    /// CRITICAL: Uses async to prevent UI thread blocking (can take up to 2000ms).
    /// </summary>
    public async Task StopAsync()
    {
        if (!_initialized || !_running)
            return;

        _mixer?.Stop();
        await OwnaudioNet.StopAsync();

        lock (_lock)
        {
            _running = false;
        }
    }

    /// <summary>
    /// Shuts down the audio engine and releases all resources.
    /// Automatically stops the engine if running.
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (!_initialized)
            return;

        if (_running)
        {
            await StopAsync();
        }

        _mixer?.Dispose();
        _mixer = null;

        await OwnaudioNet.ShutdownAsync();

        lock (_lock)
        {
            _initialized = false;
            _running = false;
        }
    }

    /// <summary>
    /// Loads an audio file and returns a FileSource wrapped in SourceWithEffects.
    /// Automatically handles format detection (MP3, WAV, FLAC) and resampling.
    /// </summary>
    /// <param name="filePath">Path to the audio file</param>
    /// <param name="bufferSizeInFrames">Buffer size for the source (default: 8192)</param>
    /// <returns>Audio source ready to be added to mixer</returns>
    public async Task<IAudioSource> LoadAudioFileAsync(string filePath, int bufferSizeInFrames = 8192)
    {
        if (!_initialized)
            throw new InvalidOperationException("Audio engine must be initialized. Call InitializeAsync() first.");

        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Audio file not found: {filePath}", filePath);

        // Load file source on background thread to avoid blocking UI
        return await Task.Run(() =>
        {
            // Create FileSource with target config matching engine
            var fileSource = new FileSource(
                filePath,
                bufferSizeInFrames: bufferSizeInFrames,
                targetSampleRate: _config!.SampleRate,
                targetChannels: _config.Channels
            );

            // Wrap in SourceWithEffects to enable effect support
            var sourceWithEffects = new SourceWithEffects(fileSource);

            return (IAudioSource)sourceWithEffects;
        });
    }

    /// <summary>
    /// Adds an audio source to the mixer.
    /// The source will start playing if the mixer is running.
    /// </summary>
    public void AddSourceToMixer(IAudioSource source)
    {
        if (!_initialized || _mixer == null)
            throw new InvalidOperationException("Audio engine must be initialized. Call InitializeAsync() first.");

        if (source == null)
            throw new ArgumentNullException(nameof(source));

        _mixer.AddSource(source);
    }

    /// <summary>
    /// Removes an audio source from the mixer and stops it.
    /// </summary>
    public void RemoveSourceFromMixer(IAudioSource source)
    {
        if (!_initialized || _mixer == null)
            throw new InvalidOperationException("Audio engine must be initialized.");

        if (source == null)
            throw new ArgumentNullException(nameof(source));

        _mixer.RemoveSource(source);
    }

    /// <summary>
    /// Creates a synchronized group for multiple audio sources.
    /// All sources in a sync group will play in perfect synchronization.
    /// </summary>
    /// <param name="groupName">Unique name for the sync group</param>
    /// <param name="sources">Audio sources to include in the sync group</param>
    public void CreateSyncGroup(string groupName, params IAudioSource[] sources)
    {
        if (!_initialized || _mixer == null)
            throw new InvalidOperationException("Audio engine must be initialized. Call InitializeAsync() first.");

        if (string.IsNullOrEmpty(groupName))
            throw new ArgumentException("Group name cannot be null or empty.", nameof(groupName));

        if (sources == null || sources.Length == 0)
            throw new ArgumentException("At least one source must be provided.", nameof(sources));

        _mixer.CreateSyncGroup(groupName, sources);
    }

    /// <summary>
    /// Starts playback for all sources in a synchronized group.
    /// </summary>
    /// <param name="groupName">Name of the sync group to start</param>
    public void StartSyncGroup(string groupName)
    {
        if (!_initialized || _mixer == null)
            throw new InvalidOperationException("Audio engine must be initialized.");

        if (string.IsNullOrEmpty(groupName))
            throw new ArgumentException("Group name cannot be null or empty.", nameof(groupName));

        _mixer.StartSyncGroup(groupName);
    }

    /// <summary>
    /// Stops playback for all sources in a synchronized group.
    /// </summary>
    /// <param name="groupName">Name of the sync group to stop</param>
    public void StopSyncGroup(string groupName)
    {
        if (!_initialized || _mixer == null)
            throw new InvalidOperationException("Audio engine must be initialized.");

        if (string.IsNullOrEmpty(groupName))
            throw new ArgumentException("Group name cannot be null or empty.", nameof(groupName));

        _mixer.StopSyncGroup(groupName);
    }

    /// <summary>
    /// Sets the tempo/speed for all sources in a synchronized group.
    /// </summary>
    /// <param name="groupName">Name of the sync group</param>
    /// <param name="tempo">Tempo multiplier (1.0 = normal speed, 0.5 = half speed, 2.0 = double speed)</param>
    public void SetSyncGroupTempo(string groupName, float tempo)
    {
        if (!_initialized || _mixer == null)
            throw new InvalidOperationException("Audio engine must be initialized.");

        if (string.IsNullOrEmpty(groupName))
            throw new ArgumentException("Group name cannot be null or empty.", nameof(groupName));

        _mixer.SetSyncGroupTempo(groupName, tempo);
    }

    /// <summary>
    /// Checks and resyncs all sync groups if sources have drifted.
    /// </summary>
    /// <param name="toleranceInFrames">Tolerance in frames before resyncing (default: 30)</param>
    public void CheckAndResyncAllGroups(int toleranceInFrames = 30)
    {
        if (!_initialized || _mixer == null)
            throw new InvalidOperationException("Audio engine must be initialized.");

        _mixer.CheckAndResyncAllGroups(toleranceInFrames);
    }

    /// <summary>
    /// Enables or disables automatic drift correction for synchronized groups.
    /// When enabled, the mixer will automatically correct timing drift between sources.
    /// </summary>
    public bool EnableAutoDriftCorrection
    {
        get => _mixer?.EnableAutoDriftCorrection ?? false;
        set
        {
            if (_mixer != null)
            {
                _mixer.EnableAutoDriftCorrection = value;
            }
        }
    }

    /// <summary>
    /// Gets the list of available input devices asynchronously.
    /// </summary>
    public async Task<List<AudioDeviceInfo>> GetInputDevicesAsync()
    {
        if (!_initialized)
            throw new InvalidOperationException("Audio engine must be initialized. Call InitializeAsync() first.");

        return await OwnaudioNet.GetInputDevicesAsync();
    }

    /// <summary>
    /// Gets the list of available output devices asynchronously.
    /// </summary>
    public async Task<List<AudioDeviceInfo>> GetOutputDevicesAsync()
    {
        if (!_initialized)
            throw new InvalidOperationException("Audio engine must be initialized. Call InitializeAsync() first.");

        return await OwnaudioNet.GetOutputDevicesAsync();
    }

    /// <summary>
    /// Disposes the audio engine service and releases all resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        // Shutdown synchronously in Dispose (acceptable for cleanup)
        if (_initialized)
        {
            ShutdownAsync().GetAwaiter().GetResult();
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
