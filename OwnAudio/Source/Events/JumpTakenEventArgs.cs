using System;

namespace OwnaudioNET.Events
{
    /// <summary>
    /// An armed jump (<see cref="Mixing.AudioMixer.ArmJump"/>) happened on every track that took it,
    /// or its moment passed without that.
    /// </summary>
    public sealed class JumpTakenEventArgs : EventArgs
    {
        public JumpTakenEventArgs(ulong id, double atSongSeconds, double toSongSeconds, bool late)
        {
            Id = id;
            AtSongSeconds = atSongSeconds;
            ToSongSeconds = toSongSeconds;
            Late = late;
        }

        public ulong Id { get; }

        /// <summary>
        /// Song (content) seconds, as armed.
        /// </summary>
        public double AtSongSeconds { get; }

        public double ToSongSeconds { get; }

        /// <summary>
        /// The boundary went by and not every armed track carried the jump out (or none could
        /// take it): the caller seeks there itself.
        /// </summary>
        public bool Late { get; }
    }
}
