using Ownaudio.Core.Common;
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ownaudio.Core
{
    /// <summary>
    /// Factory for creating platform-specific audio engine instances.
    /// Automatically detects the current platform and returns the appropriate implementation.
    /// </summary>
    public static class AudioEngineFactory
    {
        private static readonly Lazy<Type?> _nativeEngineType = new Lazy<Type?>(() =>
            TryLoadType("Ownaudio.Native", "Ownaudio.Native.NativeAudioEngine"));

        private static Type? TryLoadType(string assemblyName, string typeName)
        {
            try { return Assembly.Load(assemblyName).GetType(typeName); }
            catch { return null; }
        }

        private static IAudioEngine CreateFromCachedType(Lazy<Type?> cachedType, string platformName)
        {
            var type = cachedType.Value
                ?? throw new AudioException("AudioEngineFactory ERROR: ",
                    new PlatformNotSupportedException($"{platformName} audio engine not available"));
            return (IAudioEngine)Activator.CreateInstance(type)!;
        }
        /// <summary>
        /// Creates an audio engine instance for the current platform with the specified configuration.
        /// </summary>
        /// <param name="config">Audio configuration parameters.</param>
        /// <returns>Platform-specific IAudioEngine implementation.</returns>
        /// <exception cref="PlatformNotSupportedException">Thrown when the current platform is not supported.</exception>
        /// <exception cref="AudioException">Thrown when engine initialization fails.</exception>
        public static IAudioEngine Create(AudioConfig config)
        {
            if (config == null)
                throw  new AudioException("AudioEngineFactory ERROR: ", new ArgumentNullException(nameof(config)));

            if (!config.Validate())
                throw new AudioException("AudioEngineFactory ERROR: ", new ArgumentException("Invalid audio configuration", nameof(config)));

            IAudioEngine engine;

            try
            {
                engine = CreateNativeEngine();
            }
            catch (Exception ex)
            {
                throw new AudioException("AudioEngineFactory ERROR: ",
                    new PlatformNotSupportedException(
                        $"NativeAudioEngine could not be loaded. Platform: {RuntimeInformation.OSDescription}", ex));
            }

            int result = 0;
#if WINDOWS
            // WASAPI requires COM initialization. Using an explicit MTA thread avoids
            // any STA interference from the calling thread (e.g. UI or WinForms threads).
            var initThread = new Thread(() => { result = engine.Initialize(config); }, 256 * 1024)
            {
                Name = "OwnAudio-WasapiInit",
                IsBackground = true
            };
            initThread.SetApartmentState(ApartmentState.MTA);
            initThread.Start();
            initThread.Join();
#else
            result = engine.Initialize(config);
#endif

            if (result != 0)
            {
                engine.Dispose();
                throw new AudioException($"Failed to initialize audio engine (error code: {result})", result);
            }

            return engine;
        }

        /// <summary>
        /// Creates an audio engine instance with default configuration.
        /// </summary>
        /// <returns>Platform-specific IAudioEngine implementation with default settings.</returns>
        public static IAudioEngine CreateDefault()
        {
            return Create(AudioConfig.Default);
        }

        /// <summary>
        /// Creates a low-latency audio engine instance.
        /// </summary>
        /// <returns>Platform-specific IAudioEngine implementation optimized for low latency.</returns>
        public static IAudioEngine CreateLowLatency()
        {
            return Create(AudioConfig.LowLatency);
        }

        /// <summary>
        /// Creates a high-latency audio engine instance.
        /// </summary>
        /// <returns>Platform-specific IAudioEngine implementation optimized for low latency.</returns>
        public static IAudioEngine CreateHighLatency()
        {
            return Create(AudioConfig.HighLatency);
        }

        private static IAudioEngine CreateNativeEngine() =>
            CreateFromCachedType(_nativeEngineType, "Native");

        /// <summary>
        /// Gets information about the current platform's audio capabilities.
        /// </summary>
        /// <returns>Platform information string.</returns>
        public static string GetPlatformInfo()
        {
            string platform = "Unknown";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                platform = "Windows";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                platform = "macOS";
            else if (OperatingSystem.IsIOS())
                platform = "iOS";
            else if (OperatingSystem.IsAndroid())
                platform = "Android";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                platform = "Linux";

            return $"Platform: {platform}\n" +
                   $"Implementation: NativeAudioEngine (PortAudio/MiniAudio)\n" +
                   $"OS Description: {RuntimeInformation.OSDescription}\n" +
                   $"Framework: {RuntimeInformation.FrameworkDescription}";
        }
    }
}
