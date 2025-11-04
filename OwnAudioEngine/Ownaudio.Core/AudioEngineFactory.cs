using Ownaudio.Core.Common;
using System;
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

            // Initialize the engine
            int result = 0;
#if WINDOWS
            Thread initThread = new System.Threading.Thread(() => { result = engine.Initialize(config); });
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

        private static IAudioEngine CreateWindowsEngine()
        {
            // Use reflection to avoid hard dependency on Windows assembly
            var assembly = System.Reflection.Assembly.Load("Ownaudio.Windows");
            var type = assembly.GetType("Ownaudio.Windows.WasapiEngine");
            return (IAudioEngine)Activator.CreateInstance(type);
        }

        private static IAudioEngine CreateMacOSEngine()
        {
            // macOS implementation (to be completed)
            try
            {
                var assembly = System.Reflection.Assembly.Load("Ownaudio.macOS");
                var type = assembly.GetType("Ownaudio.macOS.CoreAudioEngine");
                return (IAudioEngine)Activator.CreateInstance(type);
            }
            catch
            {
                throw new AudioException("AudioEngineFactory ERROR: ", new PlatformNotSupportedException("macOS audio engine not yet implemented"));
            }
        }

        private static IAudioEngine CreateIOSEngine()
        {
            // iOS implementation (to be completed)
            try
            {
                var assembly = System.Reflection.Assembly.Load("Ownaudio.iOS");
                var type = assembly.GetType("Ownaudio.iOS.CoreAudioIOSEngine");
                return (IAudioEngine)Activator.CreateInstance(type);
            }
            catch
            {
                throw new AudioException("AudioEngineFactory ERROR: ", new PlatformNotSupportedException("iOS audio engine not yet implemented"));
            }
        }

        private static IAudioEngine CreateAndroidEngine()
        {
            // Android implementation (to be completed)
            try
            {
                var assembly = System.Reflection.Assembly.Load("Ownaudio.Android");
                var type = assembly.GetType("Ownaudio.Android.AAudioEngine");
                return (IAudioEngine)Activator.CreateInstance(type);
            }
            catch
            {
                throw new AudioException("AudioEngineFactory ERROR: ", new PlatformNotSupportedException("Android audio engine not yet implemented"));
            }
        }

        private static IAudioEngine CreateLinuxEngine()
        {
            // Linux PulseAudio implementation
            try
            {
                var assembly = System.Reflection.Assembly.Load("Ownaudio.Linux");
                var type = assembly.GetType("Ownaudio.Linux.PulseAudioEngine");
                return (IAudioEngine)Activator.CreateInstance(type);
            }
            catch
            {
                throw new AudioException("AudioEngineFactory ERROR: ", new PlatformNotSupportedException("Linux audio engine not yet implemented"));
            }
        }

        /// <summary>
        /// Gets information about the current platform's audio capabilities.
        /// </summary>
        /// <returns>Platform information string.</returns>
        public static string GetPlatformInfo()
        {
            string platform = "Unknown";
            string implementation = "Not Available";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                platform = "Windows";
                implementation = "WASAPI (Windows Audio Session API)";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                platform = "macOS";
                implementation = "Core Audio (AudioQueue)";
            }
            else if (OperatingSystem.IsIOS())
            {
                platform = "iOS";
                implementation = "Core Audio (AudioUnit/RemoteIO)";
            }
            else if (OperatingSystem.IsAndroid())
            {
                platform = "Android";
                implementation = "AAudio";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                platform = "Linux";
                implementation = "PulseAudio";
            }

            return $"Platform: {platform}\n" +
                   $"Implementation: {implementation}\n" +
                   $"OS Description: {RuntimeInformation.OSDescription}\n" +
                   $"Framework: {RuntimeInformation.FrameworkDescription}";
        }
    }
}
