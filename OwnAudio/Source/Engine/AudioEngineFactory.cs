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

        if (!config.Validate(out string? _invalid))
        {
            Log.Error($"[EngineFactory] Rejected config: {_invalid}");
            throw new AudioEngineException($"Invalid audio configuration: {_invalid}");
        }

        Log.Info($"[EngineFactory] Creating {GetPlatformEngineName()}: {config.SampleRate}Hz {config.Channels}ch, buffer {config.BufferSize}");

        IAudioEngine engine = new RustAudioEngine();

        try
        {
            int _result = engine.Initialize(config);

            if (_result < 0)
            {
                Log.Error($"[EngineFactory] Init failed with error code {_result}");
                engine.Dispose();
                throw new AudioEngineException(
                    $"Audio engine initialization failed with error code: {_result}", _result);
            }

            Log.Info($"[EngineFactory] Engine ready, {engine.FramesPerBuffer} frames/buffer");
            return engine;
        }
        catch (AudioEngineException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("[EngineFactory] Engine init threw, disposing the half-built engine", ex);
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

        if (!config.Validate(out string? _invalid))
        {
            Log.Error($"[EngineFactory] Rejected mock config: {_invalid}");
            throw new AudioEngineException($"Invalid audio configuration: {_invalid}");
        }

        var engine = new MockAudioEngine(generateTestSignal);

        try
        {
            int _result = engine.Initialize(config);

            if (_result < 0)
            {
                Log.Error($"[EngineFactory] Mock engine init failed with error code {_result}");
                engine.Dispose();
                throw new AudioEngineException(
                    $"Mock engine initialization failed with error code: {_result}", _result);
            }

            Log.Info($"[EngineFactory] Mock engine ready, test signal: {generateTestSignal}");
            return engine;
        }
        catch (AudioEngineException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("[EngineFactory] Mock engine init threw", ex);
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
        catch (Exception ex)
        {
            Log.Warning($"[EngineFactory] Native engine not available here: {ex.GetType().Name} {ex.Message}");
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
}
