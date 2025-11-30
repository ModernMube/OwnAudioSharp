using System;

namespace Ownaudio.Core
{
    /// <summary>
    /// The type of audio drivers (host APIs) that can be used.
    /// Note: Only applicable when using PortAudio backend. MiniAudio ignores this setting.
    /// </summary>
    public enum EngineHostType
    {
        /// <summary>
        /// No driver set. Use default host API for the platform.
        /// </summary>
        None = 0,

        /// <summary>
        /// Use of ASIO API. Only on Windows systems with ASIO drivers installed.
        /// Provides lowest latency for professional audio interfaces.
        /// </summary>
        ASIO,

        /// <summary>
        /// Default audio API for macOS (Core Audio).
        /// Low latency and excellent performance on macOS systems.
        /// </summary>
        COREAUDIO,

        /// <summary>
        /// Audio API used on Linux (ALSA - Advanced Linux Sound Architecture).
        /// Direct hardware access with low latency.
        /// </summary>
        ALSA,

        /// <summary>
        /// Windows Driver Model Kernel Streaming API.
        /// More advanced than MME and DirectSound, provides lower latency.
        /// </summary>
        WDMKS,

        /// <summary>
        /// A professional audio server with low latency and real-time processing.
        /// For Linux and macOS systems. Commonly used in professional audio production.
        /// </summary>
        JACK,

        /// <summary>
        /// Microsoft's native audio API that offers low latency and excellent performance.
        /// Only on Windows Vista and later. Recommended for modern Windows applications.
        /// </summary>
        WASAPI,

        /// <summary>
        /// Only on Android systems (AAudio - Android Audio API).
        /// High-performance audio API for Android 8.0 and later.
        /// </summary>
        AAUDIO,

        /// <summary>
        /// Only on Android systems (OpenSL ES - Open Sound Library for Embedded Systems).
        /// Cross-platform audio API for Android and other embedded systems.
        /// </summary>
        OPENSL,

        /// <summary>
        /// Only on Web (Web Audio API).
        /// JavaScript-based audio API for web browsers.
        /// </summary>
        WEBAUDIO
    }
}
