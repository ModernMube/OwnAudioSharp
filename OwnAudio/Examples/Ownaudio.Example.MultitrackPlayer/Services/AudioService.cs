using System;
using System.Threading.Tasks;
using OwnaudioNET;
using OwnaudioNET.Mixing;

namespace MultitrackPlayer.Services;

/// <summary>
/// Singleton service for managing the OwnaudioNet engine and AudioMixer lifecycle.
/// Ensures proper initialization and cleanup of audio resources.
/// </summary>
public class AudioService : IDisposable
{
    #region Fields

    /// <summary>
    /// The singleton instance of the AudioService.
    /// </summary>
    private static AudioService? _instance;

    /// <summary>
    /// Lock object for thread-safe singleton initialization.
    /// </summary>
    private static readonly object _lock = new();

    /// <summary>
    /// The audio mixer instance for managing multiple audio sources.
    /// </summary>
    private AudioMixer? _mixer;

    /// <summary>
    /// Indicates whether the audio engine has been initialized.
    /// </summary>
    private bool _isInitialized;

    /// <summary>
    /// Indicates whether this instance has been disposed.
    /// </summary>
    private bool _disposed;

    #endregion

    #region Properties

    /// <summary>
    /// Gets the current AudioMixer instance.
    /// </summary>
    public AudioMixer? Mixer => _mixer;

    /// <summary>
    /// Gets a value indicating whether the audio engine has been initialized.
    /// </summary>
    public bool IsInitialized => _isInitialized;

    #endregion

    #region Constructor

    /// <summary>
    /// Prevents a default instance of the <see cref="AudioService"/> class from being created.
    /// Use the <see cref="Instance"/> property to access the singleton instance.
    /// </summary>
    private AudioService()
    {
    }

    #endregion

    #region Singleton Instance

    /// <summary>
    /// Gets the singleton instance of the AudioService.
    /// Thread-safe lazy initialization.
    /// </summary>
    public static AudioService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new AudioService();
                }
            }
            return _instance;
        }
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Initializes the audio engine asynchronously to avoid blocking the UI thread.
    /// Creates the AudioMixer with a 2048-frame buffer for stable playback.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        // Initialize the audio engine (may block up to 5000ms on Linux)
        var config = OwnaudioNet.CreateDefaultConfig();
        config.EnableInput = true;
        config.HostType = Ownaudio.Core.EngineHostType.None;
        await OwnaudioNet.InitializeAsync(config);

        // CRITICAL: Start the audio engine BEFORE creating mixer
        OwnaudioNet.Start();

        // Create the mixer with the underlying engine (not the wrapper)
        if (OwnaudioNet.Engine != null)
        {
            // Reverting to 4096 frames (~85ms) for stability with heavy DSP (SmartMaster)
            // The corresponding AudioEngineWrapper buffer must be increased to accommodate this size!
            _mixer = new AudioMixer(OwnaudioNet.Engine.UnderlyingEngine, bufferSizeInFrames: 4096);
            // Start the mixer once and leave it running
            _mixer.Start();
        }

        _isInitialized = true;
    }

    /// <summary>
    /// Restarts the audio engine and mixer from scratch.
    /// This is the safest way to ensure clean state after track changes.
    /// Performs complete cleanup with minimal blocking time.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RestartAsync()
    {
        if (!_isInitialized)
            return;

        // Stop and dispose existing mixer
        _mixer?.Stop();
        _mixer?.Dispose();
        _mixer = null;

        // Stop and shutdown audio engine
        await OwnaudioNet.StopAsync();
        await OwnaudioNet.ShutdownAsync();

        // Brief wait for resources to be released (reduced from 200ms to 50ms)
        await Task.Delay(50);

        // Restart audio engine
        OwnaudioNet.Initialize();
        OwnaudioNet.Start();

        // Create new mixer
        if (OwnaudioNet.Engine != null)
        {
            // Use same buffer size as InitializeAsync for consistency (4096 frames for 22+ tracks)
            _mixer = new AudioMixer(OwnaudioNet.Engine.UnderlyingEngine, bufferSizeInFrames: 4096);
            _mixer.Start();
        }
    }

    #endregion

    #region IDisposable Implementation

    /// <summary>
    /// Releases all resources used by the AudioService.
    /// Stops and disposes the mixer, and shuts down the audio engine.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _mixer?.Stop();
        _mixer?.Dispose();
        _mixer = null;

        OwnaudioNet.Stop();
        OwnaudioNet.Shutdown();

        _isInitialized = false;
        _disposed = true;
    }

    #endregion
}
