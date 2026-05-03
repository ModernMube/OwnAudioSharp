using System.Reflection;
using System.Runtime.InteropServices;
using Ownaudio.Core;
using OwnaudioNET.Exceptions;
using Logger;

namespace OwnaudioNET.Engine;

/// <summary>
/// Factory for creating platform-specific audio engine instances.
/// Uses reflection to load platform implementations without hard dependencies.
/// </summary>
public static class AudioEngineFactory
{
    private static readonly object _lock = new object();

    /// <summary>
    /// Descriptor for a platform-specific audio engine.
    /// Contains metadata needed to load the engine via reflection.
    /// </summary>
    private struct PlatformEngineDescriptor
    {
        /// <summary>
        /// Gets or sets the assembly name containing the engine implementation.
        /// </summary>
        public string AssemblyName { get; set; }

        /// <summary>
        /// Gets or sets the fully qualified type name of the engine class.
        /// </summary>
        public string TypeName { get; set; }

        /// <summary>
        /// Gets or sets the display name for logging and diagnostics.
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// Gets or sets whether this engine type has been checked for availability.
        /// </summary>
        public bool Checked { get; set; }

        /// <summary>
        /// Gets or sets the cached Type object if the engine is available.
        /// </summary>
        public Type? CachedType { get; set; }
    }

    /// <summary>
    ///  Platform engine registry
    /// </summary>
    private static readonly Dictionary<string, PlatformEngineDescriptor> _platformEngines = new()
    {
        ["Native"] = new PlatformEngineDescriptor
        {
            AssemblyName = "Ownaudio.Native",
            TypeName = "Ownaudio.Native.NativeAudioEngine",
            DisplayName = "NativeAudioEngine (PortAudio/MiniAudio)"
        }
    };

    /// <summary>
    /// Creates an audio engine instance for the current platform.
    /// </summary>
    /// <param name="config">Audio configuration parameters.</param>
    /// <returns>An initialized IAudioEngine instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when config is null.</exception>
    /// <exception cref="AudioEngineException">Thrown when engine creation or initialization fails.</exception>
    public static IAudioEngine CreateEngine(AudioConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        if (!config.Validate())
            throw new AudioEngineException("Invalid audio configuration. Check SampleRate, Channels, BufferSize, and EnableInput/EnableOutput settings.");

        IAudioEngine? engine = null;
        Exception? nativeEngineException = null;

        try
        {
            engine = LoadEngine("Native");
        }
        catch (Exception ex)
        {
            nativeEngineException = ex;
            engine = null;
        }

        if (engine == null)
        {
            if (nativeEngineException != null)
            {
                Log.Error($"NativeAudioEngine not available: {nativeEngineException.Message}");
                Log.Error("Falling back to platform-specific audio engine...");
            }

            try
            {
                string? platformKey = GetCurrentPlatformKey();

                if (platformKey == null)
                {
                    throw new AudioEngineException($"Unsupported platform: {RuntimeInformation.OSDescription}. Use CreateMockEngine() for testing.");
                }

                engine = LoadEngine(platformKey);

                if (engine == null)
                    throw new AudioEngineException("Failed to create audio engine instance.");
            }
            catch (AudioEngineException)
            {
                throw;
            }
            catch (Exception ex)
            {
                engine?.Dispose();
                throw new AudioEngineException($"Failed to create audio engine: {ex.Message}", ex);
            }
        }

        try
        {
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
            throw;
        }
        catch (Exception ex)
        {
            engine?.Dispose();
            throw new AudioEngineException($"Failed to initialize audio engine: {ex.Message}", ex);
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
    /// Loads an audio engine by platform key using reflection.
    /// </summary>
    /// <param name="platformKey">The platform key (e.g., "Native", "Windows", "Linux").</param>
    /// <returns>An IAudioEngine instance, or null if the engine cannot be loaded.</returns>
    /// <exception cref="AudioEngineException">Thrown when the engine cannot be loaded or instantiated.</exception>
    private static IAudioEngine? LoadEngine(string platformKey)
    {
        lock (_lock)
        {
            if (!_platformEngines.TryGetValue(platformKey, out var descriptor))
            {
                throw new AudioEngineException($"Unknown platform key: {platformKey}");
            }

            if (descriptor.Checked && descriptor.CachedType == null)
            {
                throw new AudioEngineException(
                    $"{descriptor.DisplayName} is not available. Ensure {descriptor.AssemblyName} assembly is referenced and accessible. " +
                    "For testing without hardware, use CreateMockEngine() instead.");
            }

            if (!descriptor.Checked)
            {
                try
                {
                    Assembly assembly = Assembly.Load(descriptor.AssemblyName);
                    descriptor.CachedType = assembly.GetType(descriptor.TypeName);

                    if (descriptor.CachedType == null)
                    {
                        throw new AudioEngineException(
                            $"{descriptor.DisplayName} type not found in {descriptor.AssemblyName} assembly. " +
                            "The assembly may be corrupted or incompatible.");
                    }
                }
                catch (Exception ex) when (ex is not AudioEngineException)
                {
                    descriptor.CachedType = null;
                    throw new AudioEngineException(
                        $"Failed to load {descriptor.AssemblyName} assembly. Ensure the assembly is referenced and available. " +
                        "For testing without hardware, use CreateMockEngine() instead.",
                        ex);
                }
                finally
                {
                    descriptor.Checked = true;
                    _platformEngines[platformKey] = descriptor; // Update the struct in dictionary
                }
            }

            try
            {
                var instance = Activator.CreateInstance(descriptor.CachedType!);
                return instance as IAudioEngine;
            }
            catch (Exception ex)
            {
                throw new AudioEngineException($"Failed to instantiate {descriptor.DisplayName}: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Gets the platform key for the current operating system.
    /// Always returns "Native" since all platforms use NativeAudioEngine.
    /// </summary>
    /// <returns>Always returns "Native".</returns>
    private static string? GetCurrentPlatformKey()
    {
        return "Native";
    }

    /// <summary>
    /// Checks if a native audio engine (NativeAudioEngine or platform-specific) is available.
    /// </summary>
    /// <returns>True if a native audio engine is available for the current platform.</returns>
    public static bool IsNativeEngineAvailable()
    {
        // First, check if NativeAudioEngine is available (preferred)
        try
        {
            LoadEngine("Native");
            return true;
        }
        catch
        {
            return false;
        }

        // string? platformKey = GetCurrentPlatformKey();
        // if (platformKey == null)
        //     return false;
        //
        // try
        // {
        //     LoadEngine(platformKey);
        //     return true;
        // }
        // catch
        // {
        //     return false;
        // }
    }

    /// <summary>
    /// Gets the name of the audio engine that would be used for the current platform.
    /// </summary>
    public static string GetPlatformEngineName()
    {
        // Check if NativeAudioEngine is available first
        lock (_lock)
        {
            if (_platformEngines.TryGetValue("Native", out var nativeDescriptor))
            {
                if (!nativeDescriptor.Checked)
                {
                    try
                    {
                        LoadEngine("Native");
                    }
                    catch {}
                }

                if (nativeDescriptor.CachedType != null)
                    return nativeDescriptor.DisplayName;
            }
        }

        // Fallback to platform-specific engine names
        string? platformKey = GetCurrentPlatformKey();
        if (platformKey != null && _platformEngines.TryGetValue(platformKey, out var descriptor))
        {
            return $"{descriptor.DisplayName} ({platformKey} fallback)";
        }

        return "None";
    }
}
