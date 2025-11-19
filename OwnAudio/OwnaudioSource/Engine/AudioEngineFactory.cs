using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Ownaudio.Core;
using OwnaudioNET.Exceptions;

namespace OwnaudioNET.Engine;

/// <summary>
/// Factory for creating platform-specific audio engine instances.
/// Uses reflection to load platform implementations without hard dependencies.
/// </summary>
public static class AudioEngineFactory
{
    private static readonly object _lock = new object();
    private static bool _wasapiChecked = false;
    private static Type? _wasapiEngineType = null;
    private static bool _pulseaudioChecked = false;
    private static Type? _pulseaudioEngineType = null;
    private static bool _coreaudioChecked = false;
    private static Type? _coreaudioEngineType = null;
    private static bool _aaudioChecked = false;
    private static Type? _aaudioEngineType = null;

    /// <summary>
    /// Creates an audio engine instance for the current platform.
    /// </summary>
    /// <param name="config">Audio configuration parameters.</param>
    /// <returns>An initialized IAudioEngine instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when config is null.</exception>
    /// <exception cref="AudioEngineException">Thrown when engine creation or initialization fails.</exception>
    /// <remarks>
    /// Platform detection order:
    /// 1. Windows: Attempts to load WasapiEngine via reflection
    /// 2. macOS: Attempts to load CoreAudioEngine via reflection
    /// 3. Linux: Attempts to load PulseAudioEngine via reflection (includes Android detection)
    /// 4. Android: Attempts to load AAudioEngine via reflection
    ///
    /// For testing purposes, use <see cref="CreateMockEngine"/> instead.
    /// </remarks>
    public static IAudioEngine CreateEngine(AudioConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        if (!config.Validate())
            throw new AudioEngineException("Invalid audio configuration. Check SampleRate, Channels, BufferSize, and EnableInput/EnableOutput settings.");

        IAudioEngine? engine = null;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                engine = CreateWasapiEngine();
            }
            else if (OperatingSystem.IsMacOS())
            {
                engine = CreateCoreAudioEngine();
            }
            else if (OperatingSystem.IsLinux())
            {
                engine = CreatePulseAudioEngine();
            }
            else if(OperatingSystem.IsAndroid())
            {
                engine = CreateAAudioEngine();
            }

            else
            {
                throw new AudioEngineException($"Unsupported platform: {RuntimeInformation.OSDescription}. Use CreateMockEngine() for testing.");
            }

            if (engine == null)
                throw new AudioEngineException("Failed to create audio engine instance.");

            // Initialize the engine
            int result = engine.Initialize(config);
            if (result < 0)
            {
                engine.Dispose();
                throw new AudioEngineException($"Audio engine initialization failed with error code: {result}", result);
            }

            return engine;
        }
        catch (AudioEngineException)
        {
            // Re-throw AudioEngineException as-is
            throw;
        }
        catch (Exception ex)
        {
            engine?.Dispose();
            throw new AudioEngineException($"Failed to create audio engine: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Creates a mock audio engine for testing purposes.
    /// The mock engine simulates audio I/O without actual hardware access.
    /// </summary>
    /// <param name="config">Audio configuration parameters.</param>
    /// <param name="generateTestSignal">If true, generates a 440Hz sine wave for testing.</param>
    /// <returns>An initialized MockAudioEngine instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when config is null.</exception>
    /// <exception cref="AudioEngineException">Thrown when initialization fails.</exception>
    /// <remarks>
    /// The mock engine is useful for:
    /// - Unit testing without audio hardware
    /// - Developing on unsupported platforms
    /// - Testing audio processing logic in isolation
    /// </remarks>
    public static MockAudioEngine CreateMockEngine(AudioConfig config, bool generateTestSignal = false)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        if (!config.Validate())
            throw new AudioEngineException("Invalid audio configuration. Check SampleRate, Channels, BufferSize, and EnableInput/EnableOutput settings.");

        try
        {
            MockAudioEngine mockEngine = new MockAudioEngine(generateTestSignal);
            int result = mockEngine.Initialize(config);

            if (result < 0)
            {
                mockEngine.Dispose();
                throw new AudioEngineException($"Mock audio engine initialization failed with error code: {result}", result);
            }

            return mockEngine;
        }
        catch (AudioEngineException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AudioEngineException($"Failed to create mock audio engine: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Creates a WasapiEngine instance using reflection to avoid hard dependency.
    /// </summary>
    /// <returns>A WasapiEngine instance, or null if assembly cannot be loaded.</returns>
    /// <exception cref="AudioEngineException">Thrown when WasapiEngine cannot be loaded.</exception>
    private static IAudioEngine? CreateWasapiEngine()
    {
        lock (_lock)
        {
            // Check if we've already tried to load WASAPI
            if (_wasapiChecked && _wasapiEngineType == null)
            {
                throw new AudioEngineException(
                    "WasapiEngine is not available. Ensure Ownaudio.Windows assembly is referenced and accessible. " +
                    "For testing without hardware, use CreateMockEngine() instead.");
            }

            // Try to load the type on first call
            if (!_wasapiChecked)
            {
                try
                {
                    // Attempt to load the Ownaudio.Windows assembly
                    Assembly wasapiAssembly = Assembly.Load("Ownaudio.Windows");
                    _wasapiEngineType = wasapiAssembly.GetType("Ownaudio.Windows.WasapiEngine");

                    if (_wasapiEngineType == null)
                    {
                        throw new AudioEngineException(
                            "WasapiEngine type not found in Ownaudio.Windows assembly. " +
                            "The assembly may be corrupted or incompatible.");
                    }
                }
                catch (Exception ex) when (ex is not AudioEngineException)
                {
                    _wasapiEngineType = null;
                    throw new AudioEngineException(
                        "Failed to load Ownaudio.Windows assembly. Ensure the assembly is referenced and available. " +
                        "For testing without hardware, use CreateMockEngine() instead.",
                        ex);
                }
                finally
                {
                    _wasapiChecked = true;
                }
            }

            // Create instance using reflection
            try
            {
                var instance = Activator.CreateInstance(_wasapiEngineType!);
                return instance as IAudioEngine;
            }
            catch (Exception ex)
            {
                throw new AudioEngineException($"Failed to instantiate WasapiEngine: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Creates a PulseAudioEngine instance using reflection to avoid hard dependency.
    /// </summary>
    /// <returns>A PulseAudioEngine instance, or null if assembly cannot be loaded.</returns>
    /// <exception cref="AudioEngineException">Thrown when PulseAudioEngine cannot be loaded.</exception>
    private static IAudioEngine? CreatePulseAudioEngine()
    {
        lock (_lock)
        {
            // Check if we've already tried to load PulseAudio
            if (_pulseaudioChecked && _pulseaudioEngineType == null)
            {
                throw new AudioEngineException(
                    "PulseAudioEngine is not available. Ensure Ownaudio.Linux assembly is referenced and accessible. " +
                    "For testing without hardware, use CreateMockEngine() instead.");
            }

            // Try to load the type on first call
            if (!_pulseaudioChecked)
            {
                try
                {
                    // Attempt to load the Ownaudio.Linux assembly
                    Assembly pulseaudioAssembly = Assembly.Load("Ownaudio.Linux");
                    _pulseaudioEngineType = pulseaudioAssembly.GetType("Ownaudio.Linux.PulseAudioEngine");

                    if (_pulseaudioEngineType == null)
                    {
                        throw new AudioEngineException(
                            "PulseAudioEngine type not found in Ownaudio.Linux assembly. " +
                            "The assembly may be corrupted or incompatible.");
                    }
                }
                catch (Exception ex) when (ex is not AudioEngineException)
                {
                    _pulseaudioEngineType = null;
                    throw new AudioEngineException(
                        "Failed to load Ownaudio.Linux assembly. Ensure the assembly is referenced and available. " +
                        "For testing without hardware, use CreateMockEngine() instead.",
                        ex);
                }
                finally
                {
                    _pulseaudioChecked = true;
                }
            }

            // Create instance using reflection
            try
            {
                var instance = Activator.CreateInstance(_pulseaudioEngineType!);
                return instance as IAudioEngine;
            }
            catch (Exception ex)
            {
                throw new AudioEngineException($"Failed to instantiate PulseAudioEngine: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Creates a CoreAudioEngine instance using reflection to avoid hard dependency.
    /// </summary>
    /// <returns>A CoreAudioEngine instance, or null if assembly cannot be loaded.</returns>
    /// <exception cref="AudioEngineException">Thrown when CoreAudioEngine cannot be loaded.</exception>
    private static IAudioEngine? CreateCoreAudioEngine()
    {
        lock (_lock)
        {
            // Check if we've already tried to load CoreAudio
            if (_coreaudioChecked && _coreaudioEngineType == null)
            {
                throw new AudioEngineException(
                    "CoreAudioEngine is not available. Ensure Ownaudio.macOS assembly is referenced and accessible. " +
                    "For testing without hardware, use CreateMockEngine() instead.");
            }

            // Try to load the type on first call
            if (!_coreaudioChecked)
            {
                try
                {
                    // Attempt to load the Ownaudio.macOS assembly
                    Assembly coreaudioAssembly = Assembly.Load("Ownaudio.macOS");
                    _coreaudioEngineType = coreaudioAssembly.GetType("Ownaudio.macOS.CoreAudioEngine");

                    if (_coreaudioEngineType == null)
                    {
                        throw new AudioEngineException(
                            "CoreAudioEngine type not found in Ownaudio.macOS assembly. " +
                            "The assembly may be corrupted or incompatible.");
                    }
                }
                catch (Exception ex) when (ex is not AudioEngineException)
                {
                    _coreaudioEngineType = null;
                    throw new AudioEngineException(
                        "Failed to load Ownaudio.macOS assembly. Ensure the assembly is referenced and available. " +
                        "For testing without hardware, use CreateMockEngine() instead.",
                        ex);
                }
                finally
                {
                    _coreaudioChecked = true;
                }
            }

            // Create instance using reflection
            try
            {
                var instance = Activator.CreateInstance(_coreaudioEngineType!);
                return instance as IAudioEngine;
            }
            catch (Exception ex)
            {
                throw new AudioEngineException($"Failed to instantiate CoreAudioEngine: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Creates an AAudioEngine instance using reflection to avoid hard dependency.
    /// </summary>
    /// <returns>An AAudioEngine instance, or null if assembly cannot be loaded.</returns>
    /// <exception cref="AudioEngineException">Thrown when AAudioEngine cannot be loaded.</exception>
    private static IAudioEngine? CreateAAudioEngine()
    {
        lock (_lock)
        {
            // Check if we've already tried to load AAudio
            if (_aaudioChecked && _aaudioEngineType == null)
            {
                throw new AudioEngineException(
                    "AAudioEngine is not available. Ensure Ownaudio.Android assembly is referenced and accessible. " +
                    "For testing without hardware, use CreateMockEngine() instead.");
            }

            // Try to load the type on first call
            if (!_aaudioChecked)
            {
                try
                {
                    // Attempt to load the Ownaudio.Android assembly
                    Assembly aaudioAssembly = Assembly.Load("Ownaudio.Android");
                    _aaudioEngineType = aaudioAssembly.GetType("Ownaudio.Android.AAudioEngine");

                    if (_aaudioEngineType == null)
                    {
                        throw new AudioEngineException(
                            "AAudioEngine type not found in Ownaudio.Android assembly. " +
                            "The assembly may be corrupted or incompatible.");
                    }
                }
                catch (Exception ex) when (ex is not AudioEngineException)
                {
                    _aaudioEngineType = null;
                    throw new AudioEngineException(
                        "Failed to load Ownaudio.Android assembly. Ensure the assembly is referenced and available. " +
                        "For testing without hardware, use CreateMockEngine() instead.",
                        ex);
                }
                finally
                {
                    _aaudioChecked = true;
                }
            }

            // Create instance using reflection
            try
            {
                var instance = Activator.CreateInstance(_aaudioEngineType!);
                return instance as IAudioEngine;
            }
            catch (Exception ex)
            {
                throw new AudioEngineException($"Failed to instantiate AAudioEngine: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Checks if a platform-specific audio engine is available.
    /// </summary>
    /// <returns>True if a native audio engine is available for the current platform.</returns>
    public static bool IsNativeEngineAvailable()
    {
        if (OperatingSystem.IsWindows())
        {
            lock (_lock)
            {
                if (_wasapiChecked)
                    return _wasapiEngineType != null;

                try
                {
                    Assembly wasapiAssembly = Assembly.Load("Ownaudio.Windows");
                    _wasapiEngineType = wasapiAssembly.GetType("Ownaudio.Windows.WasapiEngine");
                    _wasapiChecked = true;
                    return _wasapiEngineType != null;
                }
                catch
                {
                    _wasapiChecked = true;
                    _wasapiEngineType = null;
                    return false;
                }
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            lock (_lock)
            {
                if (_pulseaudioChecked)
                    return _pulseaudioEngineType != null;

                try
                {
                    Assembly pulseaudioAssembly = Assembly.Load("Ownaudio.Linux");
                    _pulseaudioEngineType = pulseaudioAssembly.GetType("Ownaudio.Linux.PulseAudioEngine");
                    _pulseaudioChecked = true;
                    return _pulseaudioEngineType != null;
                }
                catch
                {
                    _pulseaudioChecked = true;
                    _pulseaudioEngineType = null;
                    return false;
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            lock (_lock)
            {
                if (_coreaudioChecked)
                    return _coreaudioEngineType != null;

                try
                {
                    Assembly coreaudioAssembly = Assembly.Load("Ownaudio.macOS");
                    _coreaudioEngineType = coreaudioAssembly.GetType("Ownaudio.macOS.CoreAudioEngine");
                    _coreaudioChecked = true;
                    return _coreaudioEngineType != null;
                }
                catch
                {
                    _coreaudioChecked = true;
                    _coreaudioEngineType = null;
                    return false;
                }
            }
        }
        else if(OperatingSystem.IsAndroid())
        {
            lock (_lock)
            {
                if (_aaudioChecked)
                    return _aaudioEngineType != null;

                try
                {
                    Assembly aaudioAssembly = Assembly.Load("Ownaudio.Android");
                    _aaudioEngineType = aaudioAssembly.GetType("Ownaudio.Android.AAudioEngine");
                    _aaudioChecked = true;
                    return _aaudioEngineType != null;
                }
                catch
                {
                    _aaudioChecked = true;
                    _aaudioEngineType = null;
                    return false;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the name of the platform-specific audio engine for the current platform.
    /// </summary>
    /// <returns>The engine name (e.g., "WasapiEngine", "CoreAudio"), or "None" if no native engine is available.</returns>
    public static string GetPlatformEngineName()
    {
        if (OperatingSystem.IsWindows())
            return "WasapiEngine";
        else if (OperatingSystem.IsMacOS())
            return "CoreAudioEngine";
        else if (OperatingSystem.IsLinux())
            return "PulseAudioEngine";
        else if (OperatingSystem.IsAndroid())
            return "AAudioEngine";
        else
            return "None";
    }
}
