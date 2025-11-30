using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Mixing;

namespace OwnaudioShowcase.Services;

/// <summary>
/// Centralized service for managing the OwnaudioNET audio engine.
/// Provides high-level audio operations and abstracts away engine complexity.
/// </summary>
public interface IAudioEngineService
{
    /// <summary>
    /// Gets whether the audio engine has been initialized.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Gets whether the audio engine is currently running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Gets the audio mixer instance (null if not initialized).
    /// </summary>
    AudioMixer? Mixer { get; }

    /// <summary>
    /// Gets the current audio configuration.
    /// </summary>
    AudioConfig? Config { get; }

    /// <summary>
    /// Initializes the audio engine asynchronously with default configuration.
    /// Must be called before any audio operations.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Initializes the audio engine asynchronously with custom configuration.
    /// </summary>
    /// <param name="config">Audio configuration to use</param>
    Task InitializeAsync(AudioConfig config);

    /// <summary>
    /// Starts the audio engine and mixer.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops the audio engine and mixer asynchronously.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Shuts down the audio engine and releases all resources.
    /// </summary>
    Task ShutdownAsync();

    /// <summary>
    /// Loads an audio file and returns an audio source.
    /// </summary>
    /// <param name="filePath">Path to the audio file</param>
    /// <param name="bufferSizeInFrames">Buffer size for the source (default: 8192)</param>
    /// <returns>Audio source instance</returns>
    Task<IAudioSource> LoadAudioFileAsync(string filePath, int bufferSizeInFrames = 8192);

    /// <summary>
    /// Adds an audio source to the mixer.
    /// </summary>
    /// <param name="source">Audio source to add</param>
    void AddSourceToMixer(IAudioSource source);

    /// <summary>
    /// Removes an audio source from the mixer.
    /// </summary>
    /// <param name="source">Audio source to remove</param>
    void RemoveSourceFromMixer(IAudioSource source);

    /// <summary>
    /// Creates a synchronized group for multiple audio sources.
    /// All sources in a sync group will play in perfect synchronization.
    /// </summary>
    /// <param name="groupName">Unique name for the sync group</param>
    /// <param name="sources">Audio sources to include in the sync group</param>
    void CreateSyncGroup(string groupName, params IAudioSource[] sources);

    /// <summary>
    /// Starts playback for all sources in a synchronized group.
    /// </summary>
    /// <param name="groupName">Name of the sync group to start</param>
    void StartSyncGroup(string groupName);

    /// <summary>
    /// Stops playback for all sources in a synchronized group.
    /// </summary>
    /// <param name="groupName">Name of the sync group to stop</param>
    void StopSyncGroup(string groupName);

    /// <summary>
    /// Sets the tempo/speed for all sources in a synchronized group.
    /// </summary>
    /// <param name="groupName">Name of the sync group</param>
    /// <param name="tempo">Tempo multiplier (1.0 = normal speed)</param>
    void SetSyncGroupTempo(string groupName, float tempo);

    /// <summary>
    /// Checks and resyncs all sync groups if sources have drifted.
    /// </summary>
    /// <param name="toleranceInFrames">Tolerance in frames before resyncing</param>
    void CheckAndResyncAllGroups(int toleranceInFrames = 30);

    /// <summary>
    /// Enables or disables automatic drift correction for synchronized groups.
    /// </summary>
    bool EnableAutoDriftCorrection { get; set; }

    /// <summary>
    /// Gets the list of available input devices asynchronously.
    /// </summary>
    Task<List<AudioDeviceInfo>> GetInputDevicesAsync();

    /// <summary>
    /// Gets the list of available output devices asynchronously.
    /// </summary>
    Task<List<AudioDeviceInfo>> GetOutputDevicesAsync();
}
