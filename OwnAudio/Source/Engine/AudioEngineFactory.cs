using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Core;
using OwnaudioNET.Exceptions;
using Logger;

namespace OwnaudioNET.Engine;

/// <summary>
/// AOT friendly factory, no reflection. Every platform gets the same Rust/cpal engine, plus a mock for tests.
/// </summary>
public static class AudioEngineFactory
{
    #region Public Factory Methods

    /// <summary>
    /// Creates and inits the Rust engine for this platform.
    /// </summary>
    /// <param name="config"></param>
    /// <returns></returns>
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
            int _result = _initializeEngine(engine, config);

            if (_result < 0)
            {
                engine.Dispose();
                throw new AudioEngineException(
                    $"Audio engine initialization failed with error code: {_result}", _result);
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
    /// Same thing without hardware, for tests. The flag turns on a 440 Hz sine on the output.
    /// </summary>
    /// <param name="config"></param>
    /// <param name="generateTestSignal"></param>
    /// <returns></returns>
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
            int _result = engine.Initialize(config);

            if (_result < 0)
            {
                engine.Dispose();
                throw new AudioEngineException(
                    $"Mock engine initialization failed with error code: {_result}", _result);
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
    /// True when the native engine can be spun up here.
    /// </summary>
    /// <returns></returns>
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
    /// Display name of the engine in use.
    /// </summary>
    /// <returns></returns>
    public static string GetPlatformEngineName()
        => "RustAudioEngine (cpal)";

    #endregion

    #region Private Helpers

    /// <summary>
    /// Runs init. On Windows it goes to a dedicated MTA thread, WASAPI COM wants that regardless of the caller.
    /// </summary>
    /// <param name="engine"></param>
    /// <param name="config"></param>
    /// <returns></returns>
    private static int _initializeEngine(IAudioEngine engine, AudioConfig config)
    {
        int _result = 0;

#if WINDOWS
        var _thread = new Thread(() => _result = engine.Initialize(config), 256 * 1024)
        {
            Name = "OwnAudio-WasapiInit",
            IsBackground = true
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _thread.Join();
#else
        _result = engine.Initialize(config);
#endif

        return _result;
    }

    #endregion
}
