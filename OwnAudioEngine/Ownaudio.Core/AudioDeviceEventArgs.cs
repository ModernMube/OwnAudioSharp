using System;

namespace Ownaudio.Core
{
    /// <summary>
    /// Event arguments for audio device change events.
    /// </summary>
    public class AudioDeviceChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the ID of the old device.
        /// </summary>
        public string OldDeviceId { get; }

        /// <summary>
        /// Gets the ID of the new device.
        /// </summary>
        public string NewDeviceId { get; }

        /// <summary>
        /// Gets information about the new device.
        /// </summary>
        public AudioDeviceInfo NewDeviceInfo { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioDeviceChangedEventArgs"/> class.
        /// </summary>
        /// <param name="oldDeviceId">The ID of the old device.</param>
        /// <param name="newDeviceId">The ID of the new device.</param>
        /// <param name="newDeviceInfo">Information about the new device.</param>
        public AudioDeviceChangedEventArgs(string oldDeviceId, string newDeviceId, AudioDeviceInfo newDeviceInfo)
        {
            OldDeviceId = oldDeviceId;
            NewDeviceId = newDeviceId;
            NewDeviceInfo = newDeviceInfo;
        }
    }

    /// <summary>
    /// Event arguments for audio device state change events.
    /// </summary>
    public class AudioDeviceStateChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the ID of the device whose state changed.
        /// </summary>
        public string DeviceId { get; }

        /// <summary>
        /// Gets the new state of the device.
        /// </summary>
        public AudioDeviceState NewState { get; }

        /// <summary>
        /// Gets information about the device.
        /// </summary>
        public AudioDeviceInfo DeviceInfo { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioDeviceStateChangedEventArgs"/> class.
        /// </summary>
        /// <param name="deviceId">The ID of the device.</param>
        /// <param name="newState">The new state of the device.</param>
        /// <param name="deviceInfo">Information about the device.</param>
        public AudioDeviceStateChangedEventArgs(string deviceId, AudioDeviceState newState, AudioDeviceInfo deviceInfo)
        {
            DeviceId = deviceId;
            NewState = newState;
            DeviceInfo = deviceInfo;
        }
    }
}
