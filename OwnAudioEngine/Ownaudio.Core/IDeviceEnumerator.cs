using System;
using System.Collections.Generic;
using Ownaudio.Core.Common;

namespace Ownaudio.Core
{
    /// <summary>
    /// Interface for enumerating audio devices on the system.
    /// </summary>
    public interface IDeviceEnumerator
    {
        /// <summary>
        /// Enumerates all available output (render) devices.
        /// </summary>
        /// <returns>A list of output device information objects.</returns>
        /// <exception cref="AudioException">Thrown when device enumeration fails.</exception>
        List<AudioDeviceInfo> EnumerateOutputDevices();

        /// <summary>
        /// Enumerates all available input (capture) devices.
        /// </summary>
        /// <returns>A list of input device information objects.</returns>
        /// <exception cref="AudioException">Thrown when device enumeration fails.</exception>
        List<AudioDeviceInfo> EnumerateInputDevices();

        /// <summary>
        /// Enumerates all available audio devices (both input and output).
        /// </summary>
        /// <returns>A list of all device information objects.</returns>
        /// <exception cref="AudioException">Thrown when device enumeration fails.</exception>
        List<AudioDeviceInfo> EnumerateAllDevices();

        /// <summary>
        /// Gets the default output (render) device.
        /// </summary>
        /// <returns>Information about the default output device, or null if none available.</returns>
        /// <exception cref="AudioException">Thrown when device query fails.</exception>
        AudioDeviceInfo? GetDefaultOutputDevice();

        /// <summary>
        /// Gets the default input (capture) device.
        /// </summary>
        /// <returns>Information about the default input device, or null if none available.</returns>
        /// <exception cref="AudioException">Thrown when device query fails.</exception>
        AudioDeviceInfo? GetDefaultInputDevice();

        /// <summary>
        /// Gets detailed information about a specific device by its ID.
        /// </summary>
        /// <param name="deviceId">The unique device identifier.</param>
        /// <returns>Device information, or null if device not found.</returns>
        /// <exception cref="AudioException">Thrown when device query fails.</exception>
        AudioDeviceInfo? GetDeviceInfo(string deviceId);
    }
}
