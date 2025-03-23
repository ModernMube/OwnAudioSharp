using System;

namespace Ownaudio.Engines;

public sealed partial class OwnAudioEngine
{
    /// <summary>
    /// Define engine audio channels
    /// </summary>
    public enum EngineChannels
    {
        /// <summary>
        /// There is no channel
        /// </summary>
        None = 0,

        /// <summary>
        /// One mono channel
        /// </summary>
        Mono = 1,

        /// <summary>
        /// Stereo channel
        /// </summary>
        Stereo = 2
    }

    /// <summary>
    /// The type of audio drivers that can be used. The current system does not support all of them.
    /// </summary>
    public enum EngineHostType
    {
        /// <summary>
        /// No driver set. Default
        /// </summary>
        None,

        /// <summary>
        /// Use of Asio API. Only on Windows systems
        /// </summary>
        ASIO,

        /// <summary>
        /// Default audio API for macos
        /// </summary>
        CoreAudio,

        /// <summary>
        /// Audio API used on Linux
        /// </summary>
        ALSA,

        /// <summary>
        /// Windows Driver Model Kernel Streaming API. More advanced than MME and DirectSound.
        /// </summary>
        WDMKS,

        /// <summary>
        /// A professional audio server with low latency and real-time processing.
        /// For Linux and MacOS systems.
        /// </summary>
        JACK,

        /// <summary>
        /// Microsoft's native audio API that offers low latency and excellent performance.
        /// Only on Windows systems.
        /// </summary>
        WASAPI,
    }
}
