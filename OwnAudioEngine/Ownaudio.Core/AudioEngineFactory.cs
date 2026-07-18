using System;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Core.Common;

namespace Ownaudio.Core;

/// <summary>
/// Builds engine instances without a shred of reflection, so AOT stays happy.
/// The native project registers its creator from a [ModuleInitializer], nobody has to call Register.
/// </summary>
public static class AudioEngineFactory
{
    #region Registration

    private static Func<IAudioEngine>? _creator;

    /// <summary>
    /// Hooks up the creator delegate — it must return a fresh, uninitialized engine.
    /// </summary>
    public static void Register(Func<IAudioEngine> creator)
        => _creator = creator ?? throw new ArgumentNullException(nameof(creator));

    #endregion

    #region Public Factory Methods

    /// <summary>
    /// Builds an engine and initializes it. Config has to pass <see cref="AudioConfig.Validate"/>.
    /// </summary>
    /// <exception cref="AudioException">Nothing registered, bad config, or init came back non-zero.</exception>
    public static IAudioEngine Create(AudioConfig config)
    {
        if (config == null || !config.Validate())
            throw new AudioException("AudioEngineFactory ERROR: ",
                new ArgumentException("Invalid audio configuration.", nameof(config)));

        if (_creator == null)
            throw new AudioException("AudioEngineFactory ERROR: ",
                new InvalidOperationException("No audio engine registered. Ensure the Ownaudio.Native assembly is loaded."));

        IAudioEngine engine;

        try
        {
            engine = _creator();
        }
        catch (Exception ex)
        {
            throw new AudioException("AudioEngineFactory ERROR: ",
                new PlatformNotSupportedException($"Engine creation failed. Platform: {RuntimeInformation.OSDescription}", ex));
        }

        int result = _initializeEngine(engine, config);

        if (result != 0)
        {
            engine.Dispose();
            throw new AudioException($"Failed to initialize audio engine (error code: {result})", result);
        }

        return engine;
    }

    /// <summary>
    /// Engine on <see cref="AudioConfig.Default"/>.
    /// </summary>
    public static IAudioEngine CreateDefault() => Create(AudioConfig.Default);

    /// <summary>
    /// Small buffers, low latency.
    /// </summary>
    public static IAudioEngine CreateLowLatency() => Create(AudioConfig.LowLatency);

    /// <summary>
    /// Big buffers, boringly stable.
    /// </summary>
    public static IAudioEngine CreateHighLatency() => Create(AudioConfig.HighLatency);

    /// <summary>
    /// Platform blurb for logs and bug reports.
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
               $"Implementation: RustAudioEngine (cpal)\n" +
               $"OS Description: {RuntimeInformation.OSDescription}\n" +
               $"Framework: {RuntimeInformation.FrameworkDescription}";
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// On Windows WASAPI wants MTA, and we can't trust whoever called us to be in it,
    /// so init runs on our own thread there.
    /// </summary>
    private static int _initializeEngine(IAudioEngine engine, AudioConfig config)
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
