using System;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Core.Common;

namespace Ownaudio.Core;

/// <summary>
/// AOT-compatible factory for creating platform-specific audio engine instances.
/// Engine implementations register themselves via a <see cref="Func{IAudioEngine}"/> delegate,
/// eliminating all reflection-based assembly loading. The Native project performs registration
/// automatically through a <c>[ModuleInitializer]</c> attribute so callers need no setup code.
/// </summary>
public static class AudioEngineFactory
{
    #region Registration

    private static Func<IAudioEngine>? _creator;

    /// <summary>
    /// Registers the engine creator. Called automatically by the Native project at module load time.
    /// </summary>
    /// <param name="creator">Factory function that returns a new, uninitialized engine instance.</param>
    public static void Register(Func<IAudioEngine> creator)
        => _creator = creator ?? throw new ArgumentNullException(nameof(creator));

    #endregion

    #region Public Factory Methods

    /// <summary>
    /// Creates and initializes an engine with the supplied configuration.
    /// </summary>
    /// <param name="config">Audio configuration; must pass <see cref="AudioConfig.Validate"/>.</param>
    /// <returns>Initialized <see cref="IAudioEngine"/> ready for use.</returns>
    /// <exception cref="AudioException">Thrown when no engine is registered, config is invalid,
    /// or initialization returns a non-zero error code.</exception>
    public static IAudioEngine Create(AudioConfig config)
    {
        if (config == null)
            throw new AudioException("AudioEngineFactory ERROR: ",
                new ArgumentNullException(nameof(config)));

        if (!config.Validate())
            throw new AudioException("AudioEngineFactory ERROR: ",
                new ArgumentException("Invalid audio configuration.", nameof(config)));

        if (_creator == null)
            throw new AudioException("AudioEngineFactory ERROR: ",
                new InvalidOperationException(
                    "No audio engine registered. Ensure the Ownaudio.Native assembly is loaded."));

        IAudioEngine engine;

        try
        {
            engine = _creator();
        }
        catch (Exception ex)
        {
            throw new AudioException("AudioEngineFactory ERROR: ",
                new PlatformNotSupportedException(
                    $"Engine creation failed. Platform: {RuntimeInformation.OSDescription}", ex));
        }

        int result = InitializeEngine(engine, config);

        if (result != 0)
        {
            engine.Dispose();
            throw new AudioException($"Failed to initialize audio engine (error code: {result})", result);
        }

        return engine;
    }

    /// <summary>
    /// Creates an engine with <see cref="AudioConfig.Default"/> settings.
    /// </summary>
    public static IAudioEngine CreateDefault() => Create(AudioConfig.Default);

    /// <summary>
    /// Creates an engine optimized for low latency.
    /// </summary>
    public static IAudioEngine CreateLowLatency() => Create(AudioConfig.LowLatency);

    /// <summary>
    /// Creates an engine with larger buffers for stability.
    /// </summary>
    public static IAudioEngine CreateHighLatency() => Create(AudioConfig.HighLatency);

    /// <summary>
    /// Returns a human-readable summary of the current platform and engine.
    /// </summary>
    public static string GetPlatformInfo()
    {
        string platform = "Unknown";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))    platform = "Windows";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))   platform = "macOS";
        else if (OperatingSystem.IsIOS())                            platform = "iOS";
        else if (OperatingSystem.IsAndroid())                        platform = "Android";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) platform = "Linux";

        return $"Platform: {platform}\n" +
               $"Implementation: NativeAudioEngine (PortAudio/MiniAudio)\n" +
               $"OS Description: {RuntimeInformation.OSDescription}\n" +
               $"Framework: {RuntimeInformation.FrameworkDescription}";
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Runs engine initialization, using a dedicated MTA thread on Windows to satisfy
    /// WASAPI COM requirements without depending on the calling thread's apartment state.
    /// </summary>
    private static int InitializeEngine(IAudioEngine engine, AudioConfig config)
    {
        int result = 0;

#if WINDOWS
        var thread = new Thread(() => result = engine.Initialize(config), 256 * 1024)
        {
            Name = "OwnAudio-WasapiInit",
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
#else
        result = engine.Initialize(config);
#endif

        return result;
    }

    #endregion
}
