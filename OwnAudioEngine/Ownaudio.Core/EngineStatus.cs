namespace Ownaudio.Core
{
    /// <summary>
    /// Represents the operational status of the audio engine.
    /// </summary>
    public enum EngineStatus
    {
        /// <summary>
        /// The engine is initialized but not yet started.
        /// </summary>
        Idle = 0,

        /// <summary>
        /// The engine is running and actively processing audio.
        /// </summary>
        Running = 1,

        /// <summary>
        /// The active audio device was unexpectedly disconnected (e.g. USB interface unplugged).
        /// Audio data processing continues internally; the engine is monitoring for reconnection.
        /// Playback and recording will automatically resume when the device is reconnected.
        /// </summary>
        DeviceDisconnected = 2,

        /// <summary>
        /// The engine encountered a fatal error and cannot continue.
        /// </summary>
        Error = -1
    }
}
