using System;
using System.Collections.Generic;
using Ownaudio.Core;

namespace Ownaudio.Android
{
    /// <summary>
    /// Device enumerator for Android AAudio.
    /// Note: Android AAudio has limited device enumeration capabilities compared to other platforms.
    /// Most apps use the default audio device selected by the Android audio system.
    /// </summary>
    public sealed class AAudioDeviceEnumerator
    {
        /// <summary>
        /// Gets all available output (playback) devices.
        /// Android typically routes audio automatically, so this returns a simplified list.
        /// </summary>
        /// <returns>List of available output devices.</returns>
        public static List<AudioDeviceInfo> GetOutputDevices()
        {
            var devices = new List<AudioDeviceInfo>
            {
                new AudioDeviceInfo(
                    deviceId: "0",
                    name: "Default Output Device",
                    engineName: "AAudio",
                    isInput: false,
                    isOutput: true,
                    isDefault: true,
                    state: AudioDeviceState.Active)
            };

            // Android handles device routing automatically (speaker, headphones, Bluetooth, etc.)
            // The system will route to the appropriate output based on what's connected
            return devices;
        }

        /// <summary>
        /// Gets all available input (recording) devices.
        /// Android typically routes audio automatically, so this returns a simplified list.
        /// </summary>
        /// <returns>List of available input devices.</returns>
        public static List<AudioDeviceInfo> GetInputDevices()
        {
            var devices = new List<AudioDeviceInfo>
            {
                new AudioDeviceInfo(
                    deviceId: "0",
                    name: "Default Input Device",
                    engineName: "AAudio",
                    isInput: true,
                    isOutput: false,
                    isDefault: true,
                    state: AudioDeviceState.Active)
            };

            // Android handles microphone routing automatically (built-in mic, headset mic, etc.)
            return devices;
        }

        /// <summary>
        /// Gets the default output device.
        /// </summary>
        /// <returns>Information about the default output device.</returns>
        public static AudioDeviceInfo GetDefaultOutputDevice()
        {
            return new AudioDeviceInfo(
                deviceId: "0",
                name: "Default Output Device",
                engineName: "AAudio",
                isInput: false,
                isOutput: true,
                isDefault: true,
                state: AudioDeviceState.Active);
        }

        /// <summary>
        /// Gets the default input device.
        /// </summary>
        /// <returns>Information about the default input device.</returns>
        public static AudioDeviceInfo GetDefaultInputDevice()
        {
            return new AudioDeviceInfo(
                deviceId: "0",
                name: "Default Input Device",
                engineName: "AAudio",
                isInput: true,
                isOutput: false,
                isDefault: true,
                state: AudioDeviceState.Active);
        }
    }
}
