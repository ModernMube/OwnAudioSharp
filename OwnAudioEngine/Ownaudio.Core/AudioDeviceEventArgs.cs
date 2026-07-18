using System;

namespace Ownaudio.Core
{
    /// <summary>
    /// The default device swapped out from under us.
    /// </summary>
    public class AudioDeviceChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Device we were on.
        /// </summary>
        public string OldDeviceId { get; }

        /// <summary>
        /// Device we're on now.
        /// </summary>
        public string NewDeviceId { get; }

        /// <summary>
        /// Everything we know about the new one.
        /// </summary>
        public AudioDeviceInfo NewDeviceInfo { get; }

        /// <summary>
        /// </summary>
        public AudioDeviceChangedEventArgs(string oldDeviceId, string newDeviceId, AudioDeviceInfo newDeviceInfo)
        {
            OldDeviceId = oldDeviceId;
            NewDeviceId = newDeviceId;
            NewDeviceInfo = newDeviceInfo;
        }
    }

    /// <summary>
    /// A device got added, removed, enabled or disabled.
    /// </summary>
    public class AudioDeviceStateChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Which device changed.
        /// </summary>
        public string DeviceId { get; }

        /// <summary>
        /// Where it ended up.
        /// </summary>
        public AudioDeviceState NewState { get; }

        /// <summary>
        /// Details of that device.
        /// </summary>
        public AudioDeviceInfo DeviceInfo { get; }

        /// <summary>
        /// </summary>
        public AudioDeviceStateChangedEventArgs(string deviceId, AudioDeviceState newState, AudioDeviceInfo deviceInfo)
        {
            DeviceId = deviceId;
            NewState = newState;
            DeviceInfo = deviceInfo;
        }
    }

    /// <summary>
    /// A device that had dropped out came back. The engine picks up where it stopped.
    /// </summary>
    public class AudioDeviceReconnectedEventArgs : EventArgs
    {
        /// <summary>
        /// Id of the device that showed up again.
        /// </summary>
        public string DeviceId { get; }

        /// <summary>
        /// Its friendly name.
        /// </summary>
        public string DeviceName { get; }

        /// <summary>
        /// True for output, false for input.
        /// </summary>
        public bool IsOutputDevice { get; }

        /// <summary>
        /// Details of the device.
        /// </summary>
        public AudioDeviceInfo DeviceInfo { get; }

        /// <summary>
        /// isOutputDevice is true for render, false for capture.
        /// </summary>
        public AudioDeviceReconnectedEventArgs(string deviceId, string deviceName, bool isOutputDevice, AudioDeviceInfo deviceInfo)
        {
            DeviceId = deviceId;
            DeviceName = deviceName;
            IsOutputDevice = isOutputDevice;
            DeviceInfo = deviceInfo;
        }
    }
}
