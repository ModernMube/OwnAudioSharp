using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Core;
using OwnaudioNET.Exceptions;
using Logger;

namespace OwnaudioNET.Engine;

/// <summary>
/// AOT-compatible factory that creates and initializes <see cref="IAudioEngine"/> instances
/// without any reflection or dynamic type loading. All platforms share a single
/// Rust-backed engine (<c>RustAudioEngine</c>, driven by the native cpal audio backend).
/// A <see cref="MockAudioEngine"/> is also available for unit testing without audio hardware.
/// </summary>
public static class AudioEngineFactory
{
    #region Public Factory Methods

    /// <summary>
    /// Creates and initializes the Rust-backed audio engine for the current platform.
    /// </summary>
    /// <param name="config">Audio configuration; must pass <see cref="AudioConfig.Validate"/>.</param>
    /// <returns>Initialized engine ready for playback or recording.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is null.</exception>
    /// <exception cref="AudioEngineException">Thrown on initialization failure.</exception>
    public static IAudioEngine CreateEngine(AudioConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        if (!config.Validate())
            throw new AudioEngineException(
                "Invalid audio configuration. Check SampleRate, Channels, BufferSize, and Enable* flags.");

        IAudioEngine engine = new RustAudioEngine();

        try
        {
            int result = InitializeEngine(engine, config);

            if (result < 0)
            {
                engine.Dispose();
                throw new AudioEngineException(
                    $"Audio engine initialization failed with error code: {result}", result);
            }

            return engine;
        }
        catch (AudioEngineException)
        {
            throw;
        }
        catch (Exception ex)
        {
            engine.Dispose();
            throw new AudioEngineException($"Failed to initialize audio engine: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Creates and initializes a <see cref="MockAudioEngine"/> for testing without hardware.
    /// </summary>
    /// <param name="config">Audio configuration parameters.</param>
    /// <param name="generateTestSignal">When true, generates a 440 Hz sine wave on output.</param>
    /// <returns>Initialized mock engine.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is null.</exception>
    /// <exception cref="AudioEngineException">Thrown on initialization failure.</exception>
    public static MockAudioEngine CreateMockEngine(AudioConfig config, bool generateTestSignal = false)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        if (!config.Validate())
            throw new AudioEngineException(
                "Invalid audio configuration. Check SampleRate, Channels, BufferSize, and Enable* flags.");

        var engine = new MockAudioEngine(generateTestSignal);

        try
        {
            int result = engine.Initialize(config);

            if (result < 0)
            {
                engine.Dispose();
                throw new AudioEngineException(
                    $"Mock engine initialization failed with error code: {result}", result);
            }

            return engine;
        }
        catch (AudioEngineException)
        {
            throw;
        }
        catch (Exception ex)
        {
            engine.Dispose();
            throw new AudioEngineException($"Failed to create mock audio engine: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Returns true when the Rust-backed audio engine can be created on this platform.
    /// </summary>
    public static bool IsNativeEngineAvailable()
    {
        try
        {
            using var probe = new RustAudioEngine();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the display name of the engine used on the current platform.
    /// </summary>
    public static string GetPlatformEngineName()
        => "RustAudioEngine (cpal)";

    #endregion

    #region Private Helpers

    /// <summary>
    /// Runs engine initialization; uses a dedicated MTA thread on Windows to satisfy
    /// WASAPI COM requirements without depending on the calling thread's apartment.
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
