using System;
using System.Collections.Generic;
using Ownaudio.Core.Common;

namespace Ownaudio.Core
{
    /// <summary>
    /// Whoever knows what audio devices this machine has.
    /// </summary>
    public interface IDeviceEnumerator
    {
        /// <summary>
        /// All render devices.
        /// </summary>
        /// <exception cref="AudioException">Thrown when device enumeration fails.</exception>
        List<AudioDeviceInfo> EnumerateOutputDevices();

        /// <summary>
        /// All capture devices.
        /// </summary>
        List<AudioDeviceInfo> EnumerateInputDevices();

        /// <summary>
        /// Both directions in one list.
        /// </summary>
        List<AudioDeviceInfo> EnumerateAllDevices();

        /// <summary>
        /// System default output, null if there isn't one.
        /// </summary>
        AudioDeviceInfo? GetDefaultOutputDevice();

        /// <summary>
        /// System default input, null if there isn't one.
        /// </summary>
        AudioDeviceInfo? GetDefaultInputDevice();

        /// <summary>
        /// Looks a device up by its platform id, null when it's not there.
        /// </summary>
        AudioDeviceInfo? GetDeviceInfo(string deviceId);
    }
}
