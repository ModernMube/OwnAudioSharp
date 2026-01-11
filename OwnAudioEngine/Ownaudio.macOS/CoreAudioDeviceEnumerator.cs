using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ownaudio.Core;
using static Ownaudio.macOS.Interop.CoreAudioInterop;

namespace Ownaudio.macOS
{
    /// <summary>
    /// Enumerates audio devices on macOS using Core Audio HAL.
    /// </summary>
    public sealed class CoreAudioDeviceEnumerator : IDeviceEnumerator
    {
        /// <summary>
        /// Enumerates all available output (render) devices.
        /// </summary>
        /// <returns>A list of output device information objects.</returns>
        public List<AudioDeviceInfo> EnumerateOutputDevices()
        {
            var devices = new List<AudioDeviceInfo>();

            try
            {
                uint[] deviceIds = GetAllDeviceIds();
                if (deviceIds == null || deviceIds.Length == 0)
                    return devices;

                uint defaultOutputId = GetDefaultOutputDeviceId();

                foreach (uint deviceId in deviceIds)
                {
                    if (HasOutputStreams(deviceId))
                    {
                        string name = GetDeviceName(deviceId);
                        bool isDefault = (deviceId == defaultOutputId);
                        AudioDeviceState state = GetDeviceState(deviceId);

                        devices.Add(new AudioDeviceInfo(
                            deviceId.ToString(),
                            name ?? $"Audio Device {deviceId}",
                            "CoreAudio",
                            false, // isInput
                            true,  // isOutput
                            isDefault,
                            state));
                    }
                }
            }
            catch
            {
                // Return empty list on error
            }

            return devices;
        }

        /// <summary>
        /// Enumerates all available input (capture) devices.
        /// </summary>
        /// <returns>A list of input device information objects.</returns>
        public List<AudioDeviceInfo> EnumerateInputDevices()
        {
            var devices = new List<AudioDeviceInfo>();

            try
            {
                uint[] deviceIds = GetAllDeviceIds();
                if (deviceIds == null || deviceIds.Length == 0)
                    return devices;

                uint defaultInputId = GetDefaultInputDeviceId();

                foreach (uint deviceId in deviceIds)
                {
                    if (HasInputStreams(deviceId))
                    {
                        string name = GetDeviceName(deviceId);
                        bool isDefault = (deviceId == defaultInputId);
                        AudioDeviceState state = GetDeviceState(deviceId);

                        devices.Add(new AudioDeviceInfo(
                            deviceId.ToString(),
                            name ?? $"Audio Device {deviceId}",
                            "CoreAudio",
                            true,  // isInput
                            false, // isOutput
                            isDefault,
                            state));
                    }
                }
            }
            catch
            {
                // Return empty list on error
            }

            return devices;
        }

        /// <summary>
        /// Gets all audio device IDs from the system.
        /// </summary>
        private uint[] GetAllDeviceIds()
        {
            var address = new AudioObjectPropertyAddress(
                kAudioHardwarePropertyDevices,
                kAudioObjectPropertyScopeGlobal,
                kAudioObjectPropertyElementMaster);

            // Get the size of the device list
            int status = AudioObjectGetPropertyDataSize(
                kAudioObjectSystemObject,
                ref address,
                0,
                IntPtr.Zero,
                out uint dataSize);

            if (!IsSuccess(status) || dataSize == 0)
                return Array.Empty<uint>();

            // Calculate number of devices
            int deviceCount = (int)(dataSize / sizeof(uint));
            uint[] deviceIds = new uint[deviceCount];

            // Get device IDs
            unsafe
            {
                fixed (uint* ptr = deviceIds)
                {
                    status = AudioObjectGetPropertyData(
                        kAudioObjectSystemObject,
                        ref address,
                        0,
                        IntPtr.Zero,
                        ref dataSize,
                        new IntPtr(ptr));

                    if (!IsSuccess(status))
                        return Array.Empty<uint>();
                }
            }

            return deviceIds;
        }

        /// <summary>
        /// Gets the default output device ID.
        /// </summary>
        private uint GetDefaultOutputDeviceId()
        {
            var address = new AudioObjectPropertyAddress(
                kAudioHardwarePropertyDefaultOutputDevice,
                kAudioObjectPropertyScopeGlobal,
                kAudioObjectPropertyElementMaster);

            uint deviceId = 0;
            uint dataSize = sizeof(uint);

            unsafe
            {
                int status = AudioObjectGetPropertyData(
                    kAudioObjectSystemObject,
                    ref address,
                    0,
                    IntPtr.Zero,
                    ref dataSize,
                    new IntPtr(&deviceId));

                return IsSuccess(status) ? deviceId : 0;
            }
        }

        /// <summary>
        /// Gets the default input device ID.
        /// </summary>
        private uint GetDefaultInputDeviceId()
        {
            var address = new AudioObjectPropertyAddress(
                kAudioHardwarePropertyDefaultInputDevice,
                kAudioObjectPropertyScopeGlobal,
                kAudioObjectPropertyElementMaster);

            uint deviceId = 0;
            uint dataSize = sizeof(uint);

            unsafe
            {
                int status = AudioObjectGetPropertyData(
                    kAudioObjectSystemObject,
                    ref address,
                    0,
                    IntPtr.Zero,
                    ref dataSize,
                    new IntPtr(&deviceId));

                return IsSuccess(status) ? deviceId : 0;
            }
        }

        /// <summary>
        /// Gets the name of an audio device.
        /// </summary>
        private string GetDeviceName(uint deviceId)
        {
            var address = new AudioObjectPropertyAddress(
                kAudioDevicePropertyDeviceNameCFString,
                kAudioObjectPropertyScopeGlobal,
                kAudioObjectPropertyElementMaster);

            IntPtr cfString = IntPtr.Zero;
            uint dataSize = (uint)IntPtr.Size;

            try
            {
                unsafe
                {
                    int status = AudioObjectGetPropertyData(
                        deviceId,
                        ref address,
                        0,
                        IntPtr.Zero,
                        ref dataSize,
                        new IntPtr(&cfString));

                    if (!IsSuccess(status) || cfString == IntPtr.Zero)
                        return null!;

                    string? name = CFStringToString(cfString);
                    return name!;
                }
            }
            finally
            {
                if (cfString != IntPtr.Zero)
                    CFRelease(cfString);
            }
        }

        /// <summary>
        /// Checks if a device has output streams.
        /// </summary>
        private bool HasOutputStreams(uint deviceId)
        {
            var address = new AudioObjectPropertyAddress(
                kAudioDevicePropertyStreams,
                kAudioObjectPropertyScopeOutput,
                kAudioObjectPropertyElementMaster);

            int status = AudioObjectGetPropertyDataSize(
                deviceId,
                ref address,
                0,
                IntPtr.Zero,
                out uint dataSize);

            return IsSuccess(status) && dataSize > 0;
        }

        /// <summary>
        /// Checks if a device has input streams.
        /// </summary>
        private bool HasInputStreams(uint deviceId)
        {
            var address = new AudioObjectPropertyAddress(
                kAudioDevicePropertyStreams,
                kAudioObjectPropertyScopeInput,
                kAudioObjectPropertyElementMaster);

            int status = AudioObjectGetPropertyDataSize(
                deviceId,
                ref address,
                0,
                IntPtr.Zero,
                out uint dataSize);

            return IsSuccess(status) && dataSize > 0;
        }

        /// <summary>
        /// Gets the state of an audio device.
        /// On macOS, we assume devices are active if they're enumerable.
        /// More sophisticated state checking could be added later.
        /// </summary>
        private AudioDeviceState GetDeviceState(uint deviceId)
        {
            // For now, assume all enumerated devices are active
            // Future enhancement: Check for actual device availability
            return AudioDeviceState.Active;
        }

        /// <summary>
        /// Gets the sample rate of a device.
        /// </summary>
        public int GetDeviceSampleRate(uint deviceId)
        {
            var address = new AudioObjectPropertyAddress(
                kAudioDevicePropertyNominalSampleRate,
                kAudioObjectPropertyScopeGlobal,
                kAudioObjectPropertyElementMaster);

            double sampleRate = 0;
            uint dataSize = sizeof(double);

            unsafe
            {
                int status = AudioObjectGetPropertyData(
                    deviceId,
                    ref address,
                    0,
                    IntPtr.Zero,
                    ref dataSize,
                    new IntPtr(&sampleRate));

                return IsSuccess(status) ? (int)sampleRate : 0;
            }
        }

        /// <summary>
        /// Gets the number of channels for a device.
        /// </summary>
        public int GetDeviceChannelCount(uint deviceId, bool isInput)
        {
            // Try to get preferred stereo channels first
            var address = new AudioObjectPropertyAddress(
                kAudioDevicePropertyPreferredChannelsForStereo,
                isInput ? kAudioObjectPropertyScopeInput : kAudioObjectPropertyScopeOutput,
                kAudioObjectPropertyElementMaster);

            uint[] channels = new uint[2];
            uint dataSize = sizeof(uint) * 2;

            unsafe
            {
                fixed (uint* ptr = channels)
                {
                    int status = AudioObjectGetPropertyData(
                        deviceId,
                        ref address,
                        0,
                        IntPtr.Zero,
                        ref dataSize,
                        new IntPtr(ptr));

                    if (IsSuccess(status))
                    {
                        return 2; // Stereo
                    }
                }
            }

            // Fallback to getting stream configuration
            return GetStreamChannelCount(deviceId, isInput);
        }

        /// <summary>
        /// Gets channel count from stream configuration.
        /// </summary>
        private int GetStreamChannelCount(uint deviceId, bool isInput)
        {
            var address = new AudioObjectPropertyAddress(
                kAudioDevicePropertyStreams,
                isInput ? kAudioObjectPropertyScopeInput : kAudioObjectPropertyScopeOutput,
                kAudioObjectPropertyElementMaster);

            // Get stream list size
            int status = AudioObjectGetPropertyDataSize(
                deviceId,
                ref address,
                0,
                IntPtr.Zero,
                out uint dataSize);

            if (!IsSuccess(status) || dataSize == 0)
                return 0;

            // For simplicity, assume stereo (2 channels) by default
            // A full implementation would iterate through all streams
            return 2;
        }

        /// <summary>
        /// Enumerates all available audio devices (both input and output).
        /// </summary>
        /// <returns>A list of all device information objects.</returns>
        public List<AudioDeviceInfo> EnumerateAllDevices()
        {
            var devices = new List<AudioDeviceInfo>();
            devices.AddRange(EnumerateOutputDevices());
            devices.AddRange(EnumerateInputDevices());
            return devices;
        }

        /// <summary>
        /// Gets the default output (render) device.
        /// </summary>
        /// <returns>Information about the default output device, or null if none available.</returns>
        public AudioDeviceInfo? GetDefaultOutputDevice()
        {
            try
            {
                uint defaultOutputId = GetDefaultOutputDeviceId();
                if (defaultOutputId == 0)
                    return null;

                string name = GetDeviceName(defaultOutputId);
                AudioDeviceState state = GetDeviceState(defaultOutputId);

                return new AudioDeviceInfo(
                    defaultOutputId.ToString(),
                    name ?? "Default Output Device",
                    "CoreAudio",
                    false, // isInput
                    true,  // isOutput
                    true,  // isDefault
                    state);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets the default input (capture) device.
        /// </summary>
        /// <returns>Information about the default input device, or null if none available.</returns>
        public AudioDeviceInfo? GetDefaultInputDevice()
        {
            try
            {
                uint defaultInputId = GetDefaultInputDeviceId();
                if (defaultInputId == 0)
                    return null;

                string name = GetDeviceName(defaultInputId);
                AudioDeviceState state = GetDeviceState(defaultInputId);

                return new AudioDeviceInfo(
                    defaultInputId.ToString(),
                    name ?? "Default Input Device",
                    "CoreAudio",
                    true,  // isInput
                    false, // isOutput
                    true,  // isDefault
                    state);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets detailed information about a specific device by its ID.
        /// </summary>
        /// <param name="deviceId">The unique device identifier.</param>
        /// <returns>Device information, or null if device not found.</returns>
        public AudioDeviceInfo? GetDeviceInfo(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
                return null;

            try
            {
                if (!uint.TryParse(deviceId, out uint deviceIdValue))
                    return null;

                // Try to find device in output devices
                var outputDevices = EnumerateOutputDevices();
                foreach (var device in outputDevices)
                {
                    if (device.DeviceId == deviceId)
                        return device;
                }

                // Try to find device in input devices
                var inputDevices = EnumerateInputDevices();
                foreach (var device in inputDevices)
                {
                    if (device.DeviceId == deviceId)
                        return device;
                }

                return null; // Device not found
            }
            catch
            {
                return null;
            }
        }
    }
}
