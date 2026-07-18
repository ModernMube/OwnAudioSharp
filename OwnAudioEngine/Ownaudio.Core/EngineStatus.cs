namespace Ownaudio.Core
{
    /// <summary>
    /// Where the engine currently stands.
    /// </summary>
    public enum EngineStatus
    {
        /// <summary>
        /// Initialized, not started yet.
        /// </summary>
        Idle = 0,

        /// <summary>
        /// Chewing on audio right now.
        /// </summary>
        Running = 1,

        /// <summary>
        /// Device went away (USB yanked). We keep processing internally and watch for it
        /// to come back, then playback/recording resumes on its own.
        /// </summary>
        DeviceDisconnected = 2,

        /// <summary>
        /// Dead, can't continue.
        /// </summary>
        Error = -1
    }
}
