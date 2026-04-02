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

    /// <summary>
    /// Event arguments raised when a previously disconnected audio device reconnects.
    /// The engine automatically resumes playback/recording from where it left off.
    /// </summary>
    public class AudioDeviceReconnectedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the ID of the device that reconnected.
        /// </summary>
        public string DeviceId { get; }

        /// <summary>
        /// Gets the name of the device that reconnected.
        /// </summary>
        public string DeviceName { get; }

        /// <summary>
        /// Gets a value indicating whether it was an output device that reconnected.
        /// </summary>
        public bool IsOutputDevice { get; }

        /// <summary>
        /// Gets information about the reconnected device.
        /// </summary>
        public AudioDeviceInfo DeviceInfo { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioDeviceReconnectedEventArgs"/> class.
        /// </summary>
        /// <param name="deviceId">The ID of the reconnected device.</param>
        /// <param name="deviceName">The name of the reconnected device.</param>
        /// <param name="isOutputDevice">True if this is an output device; false for input.</param>
        /// <param name="deviceInfo">Information about the reconnected device.</param>
        public AudioDeviceReconnectedEventArgs(
            string deviceId,
            string deviceName,
            bool isOutputDevice,
            AudioDeviceInfo deviceInfo)
        {
            DeviceId = deviceId;
            DeviceName = deviceName;
            IsOutputDevice = isOutputDevice;
            DeviceInfo = deviceInfo;
        }
    }
}
