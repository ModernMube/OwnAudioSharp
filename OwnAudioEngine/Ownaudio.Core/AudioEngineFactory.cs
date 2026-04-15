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
        // -----------------------------------------------------------------------
        // Cached engine types – Assembly.Load + GetType run only once per type.
        // -----------------------------------------------------------------------
        private static readonly Lazy<Type?> _nativeEngineType = new Lazy<Type?>(() =>
            TryLoadType("Ownaudio.Native", "Ownaudio.Native.NativeAudioEngine"));

        private static readonly Lazy<Type?> _windowsEngineType = new Lazy<Type?>(() =>
            TryLoadType("Ownaudio.Windows", "Ownaudio.Windows.WasapiEngine"));

        private static readonly Lazy<Type?> _macOSEngineType = new Lazy<Type?>(() =>
            TryLoadType("Ownaudio.macOS", "Ownaudio.macOS.CoreAudioEngine"));

        private static readonly Lazy<Type?> _iOSEngineType = new Lazy<Type?>(() =>
            TryLoadType("Ownaudio.iOS", "Ownaudio.iOS.CoreAudioIOSEngine"));

        private static readonly Lazy<Type?> _androidEngineType = new Lazy<Type?>(() =>
            TryLoadType("Ownaudio.Android", "Ownaudio.Android.AAudioEngine"));

        private static readonly Lazy<Type?> _linuxEngineType = new Lazy<Type?>(() =>
            TryLoadType("Ownaudio.Linux", "Ownaudio.Linux.PulseAudioEngine"));

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
            catch (Exception)
            {
                // Detect platform and create appropriate engine
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Windows WASAPI
                    engine = CreateWindowsEngine();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    // macOS Core Audio
                    engine = CreateMacOSEngine();
                }
                else if (OperatingSystem.IsIOS())
                {
                    // iOS Core Audio
                    engine = CreateIOSEngine();
                }
                else if (OperatingSystem.IsAndroid())
                {
                    // Android AAudio
                    engine = CreateAndroidEngine();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // Linux ALSA
                    engine = CreateLinuxEngine();
                }
                else
                {
                    throw new AudioException("AudioEngineFactory ERROR: ", new PlatformNotSupportedException(
                        $"Audio engine not implemented for platform: {RuntimeInformation.OSDescription}"));
                }
            }

            // Initialize the engine
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

        private static IAudioEngine CreateWindowsEngine() =>
            CreateFromCachedType(_windowsEngineType, "Windows WASAPI");

        private static IAudioEngine CreateMacOSEngine() =>
            CreateFromCachedType(_macOSEngineType, "macOS Core Audio");

        private static IAudioEngine CreateIOSEngine() =>
            CreateFromCachedType(_iOSEngineType, "iOS Core Audio");

        private static IAudioEngine CreateAndroidEngine() =>
            CreateFromCachedType(_androidEngineType, "Android AAudio");

        private static IAudioEngine CreateLinuxEngine() =>
            CreateFromCachedType(_linuxEngineType, "Linux PulseAudio");

        /// <summary>
        /// Gets information about the current platform's audio capabilities.
        /// </summary>
        /// <returns>Platform information string.</returns>
        public static string GetPlatformInfo()
        {
            string platform = "Unknown";
            string primaryImplementation = "NativeAudioEngine (PortAudio/MiniAudio)";
            string fallbackImplementation = "Not Available";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                platform = "Windows";
                fallbackImplementation = "WASAPI (Windows Audio Session API)";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                platform = "macOS";
                fallbackImplementation = "Core Audio (AudioQueue)";
            }
            else if (OperatingSystem.IsIOS())
            {
                platform = "iOS";
                fallbackImplementation = "Core Audio (AudioUnit/RemoteIO)";
            }
            else if (OperatingSystem.IsAndroid())
            {
                platform = "Android";
                fallbackImplementation = "AAudio";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                platform = "Linux";
                fallbackImplementation = "PulseAudio";
            }

            return $"Platform: {platform}\n" +
                   $"Primary Implementation: {primaryImplementation}\n" +
                   $"Fallback Implementation: {fallbackImplementation}\n" +
                   $"OS Description: {RuntimeInformation.OSDescription}\n" +
                   $"Framework: {RuntimeInformation.FrameworkDescription}";
        }
    }
}
