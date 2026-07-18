using System;

namespace Ownaudio.Core
{
    /// <summary>
    /// Host API (driver) pick. PortAudio backend only — MiniAudio ignores it.
    /// </summary>
    public enum EngineHostType
    {
        /// <summary>
        /// Let the platform decide.
        /// </summary>
        None = 0,

        /// <summary>
        /// Windows + ASIO drivers. Lowest latency you'll get on pro interfaces.
        /// </summary>
        ASIO,

        /// <summary>
        /// macOS default. Low latency, behaves well.
        /// </summary>
        COREAUDIO,

        /// <summary>
        /// Linux, straight at the hardware.
        /// </summary>
        ALSA,

        /// <summary>
        /// Windows kernel streaming. Below MME/DirectSound, so less latency.
        /// </summary>
        WDMKS,

        /// <summary>
        /// The pro audio server on Linux and macOS.
        /// </summary>
        JACK,

        /// <summary>
        /// Windows Vista and up. The sane default for modern Windows apps.
        /// </summary>
        WASAPI,

        /// <summary>
        /// Android 8.0+, the fast path.
        /// </summary>
        AAUDIO,

        /// <summary>
        /// Older Android and embedded stuff.
        /// </summary>
        OPENSL,

        /// <summary>
        /// Browsers, via Web Audio.
        /// </summary>
        WEBAUDIO
    }
}
