using System;

namespace OwnaudioNET.Events
{
    /// <summary>
    /// An armed jump (<see cref="Mixing.AudioMixer.ArmJump"/>) happened on every track that took it,
    /// or its moment passed without that.
    /// </summary>
    public sealed class JumpTakenEventArgs : EventArgs
    {
        public JumpTakenEventArgs(ulong id, double atProjectSeconds, double toProjectSeconds, bool late)
        {
            Id = id;
            AtProjectSeconds = atProjectSeconds;
            ToProjectSeconds = toProjectSeconds;
            Late = late;
        }

        public ulong Id { get; }

        public double AtProjectSeconds { get; }

        public double ToProjectSeconds { get; }

        /// <summary>
        /// The boundary went by and not every armed track carried the jump out (or none could
        /// take it): the caller seeks there itself.
        /// </summary>
        public bool Late { get; }
    }
}
