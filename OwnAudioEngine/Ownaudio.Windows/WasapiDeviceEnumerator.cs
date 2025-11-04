using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using Ownaudio.Windows.Interop;

namespace Ownaudio.Windows
{
    /// <summary>
    /// Windows WASAPI implementation of device enumeration.
    /// Provides methods to list and query audio devices on Windows.
    /// </summary>
    public sealed class WasapiDeviceEnumerator : IDeviceEnumerator
    {
        /// <summary>
        /// Enumerates all available output (render) devices.
        /// </summary>
        /// <returns>A list of output device information objects.</returns>
        /// <exception cref="AudioException">Thrown when device enumeration fails.</exception>
        public List<AudioDeviceInfo> EnumerateOutputDevices()
        {
            return EnumerateDevices(WasapiInterop.EDataFlow.eRender);
        }

        /// <summary>
        /// Enumerates all available input (capture) devices.
        /// </summary>
        /// <returns>A list of input device information objects.</returns>
        /// <exception cref="AudioException">Thrown when device enumeration fails.</exception>
        public List<AudioDeviceInfo> EnumerateInputDevices()
        {
            return EnumerateDevices(WasapiInterop.EDataFlow.eCapture);
        }

        /// <summary>
        /// Enumerates all available audio devices (both input and output).
        /// </summary>
        /// <returns>A list of all device information objects.</returns>
        /// <exception cref="AudioException">Thrown when device enumeration fails.</exception>
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
        /// <exception cref="AudioException">Thrown when device query fails.</exception>
        public AudioDeviceInfo GetDefaultOutputDevice()
        {
            return GetDefaultDevice(WasapiInterop.EDataFlow.eRender);
        }

        /// <summary>
        /// Gets the default input (capture) device.
        /// </summary>
        /// <returns>Information about the default input device, or null if none available.</returns>
        /// <exception cref="AudioException">Thrown when device query fails.</exception>
        public AudioDeviceInfo GetDefaultInputDevice()
        {
            return GetDefaultDevice(WasapiInterop.EDataFlow.eCapture);
        }

        /// <summary>
        /// Gets detailed information about a specific device by its ID.
        /// </summary>
        /// <param name="deviceId">The unique device identifier.</param>
        /// <returns>Device information, or null if device not found.</returns>
        /// <exception cref="AudioException">Thrown when device query fails.</exception>
        public AudioDeviceInfo GetDeviceInfo(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
                return null;

            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            bool needsUninitialize = false;

            try
            {
                // Initialize COM - track if WE initialized it
                int hr = Ole32.CoInitializeEx(IntPtr.Zero, Ole32.COINIT_MULTITHREADED);
                if (hr == WasapiInterop.S_OK)
                {
                    // We successfully initialized COM, so we need to uninitialize it
                    needsUninitialize = true;
                }
                else if (hr == WasapiInterop.S_FALSE) // Already initialized on this thread
                {
                    // COM already initialized by another call, don't uninitialize
                    needsUninitialize = false;
                }
                else if (hr == WasapiInterop.RPC_E_CHANGED_MODE)
                {
                    // COM already initialized with different threading model (e.g., STA vs MTA)
                    // This is acceptable - we can still use COM, just don't uninitialize
                    needsUninitialize = false;
                }
                else
                {
                    throw new AudioException($"COM initialization failed: 0x{hr:X8}");
                }

                // Create device enumerator
                Guid clsid = WasapiInterop.CLSID_MMDeviceEnumerator;
                Guid iid = WasapiInterop.IID_IMMDeviceEnumerator;
                hr = Ole32.CoCreateInstance(
                    ref clsid,
                    IntPtr.Zero,
                    Ole32.CLSCTX_ALL,
                    ref iid,
                    out object enumeratorObj);

                if (hr != WasapiInterop.S_OK)
                    throw new AudioException($"Failed to create device enumerator: 0x{hr:X8}");

                enumerator = (IMMDeviceEnumerator)enumeratorObj;

                // Get device directly by ID (more efficient than enumerating all)
                var enumeratorExt = enumerator as IMMDeviceEnumeratorExtensions.IMMDeviceEnumeratorWithNotifications;
                if (enumeratorExt != null)
                {
                    hr = enumeratorExt.GetDevice(deviceId, out device);
                    if (hr == WasapiInterop.S_OK && device != null)
                    {
                        // Get device state
                        hr = device.GetState(out uint state);
                        if (hr != WasapiInterop.S_OK)
                            return null;

                        // Get device friendly name
                        string friendlyName = GetDeviceFriendlyName(device);
                        if (string.IsNullOrEmpty(friendlyName))
                            friendlyName = "Audio Device";

                        // Determine device type by checking both default endpoints
                        bool isOutput = false;
                        bool isInput = false;
                        bool isDefault = false;

                        // Check if it's the default output device
                        IMMDevice defaultOutput = null;
                        try
                        {
                            hr = enumerator.GetDefaultAudioEndpoint(
                                WasapiInterop.EDataFlow.eRender,
                                WasapiInterop.ERole.eMultimedia,
                                out defaultOutput);

                            if (hr == WasapiInterop.S_OK && defaultOutput != null)
                            {
                                hr = defaultOutput.GetId(out string defaultOutputId);
                                if (hr == WasapiInterop.S_OK && defaultOutputId == deviceId)
                                {
                                    isOutput = true;
                                    isDefault = true;
                                }
                            }
                        }
                        finally
                        {
                            if (defaultOutput != null)
                                Marshal.ReleaseComObject(defaultOutput);
                        }

                        // Check if it's the default input device
                        IMMDevice defaultInput = null;
                        try
                        {
                            hr = enumerator.GetDefaultAudioEndpoint(
                                WasapiInterop.EDataFlow.eCapture,
                                WasapiInterop.ERole.eMultimedia,
                                out defaultInput);

                            if (hr == WasapiInterop.S_OK && defaultInput != null)
                            {
                                hr = defaultInput.GetId(out string defaultInputId);
                                if (hr == WasapiInterop.S_OK && defaultInputId == deviceId)
                                {
                                    isInput = true;
                                    isDefault = true;
                                }
                            }
                        }
                        finally
                        {
                            if (defaultInput != null)
                                Marshal.ReleaseComObject(defaultInput);
                        }

                        // If not default, try to determine type by enumerating
                        if (!isOutput && !isInput)
                        {
                            // Check output devices
                            IMMDeviceCollection outputCollection = null;
                            try
                            {
                                hr = enumerator.EnumAudioEndpoints(
                                    WasapiInterop.EDataFlow.eRender,
                                    (uint)AudioDeviceState.Active,
                                    out outputCollection);

                                if (hr == WasapiInterop.S_OK)
                                {
                                    hr = outputCollection.GetCount(out uint count);
                                    for (uint i = 0; i < count && !isOutput; i++)
                                    {
                                        IMMDevice testDevice = null;
                                        try
                                        {
                                            hr = outputCollection.Item(i, out testDevice);
                                            if (hr == WasapiInterop.S_OK && testDevice != null)
                                            {
                                                hr = testDevice.GetId(out string testId);
                                                if (hr == WasapiInterop.S_OK && testId == deviceId)
                                                {
                                                    isOutput = true;
                                                }
                                            }
                                        }
                                        finally
                                        {
                                            if (testDevice != null)
                                                Marshal.ReleaseComObject(testDevice);
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                if (outputCollection != null)
                                    Marshal.ReleaseComObject(outputCollection);
                            }

                            // Check input devices if not found in output
                            if (!isOutput)
                            {
                                IMMDeviceCollection inputCollection = null;
                                try
                                {
                                    hr = enumerator.EnumAudioEndpoints(
                                        WasapiInterop.EDataFlow.eCapture,
                                        (uint)AudioDeviceState.Active,
                                        out inputCollection);

                                    if (hr == WasapiInterop.S_OK)
                                    {
                                        hr = inputCollection.GetCount(out uint count);
                                        for (uint i = 0; i < count && !isInput; i++)
                                        {
                                            IMMDevice testDevice = null;
                                            try
                                            {
                                                hr = inputCollection.Item(i, out testDevice);
                                                if (hr == WasapiInterop.S_OK && testDevice != null)
                                                {
                                                    hr = testDevice.GetId(out string testId);
                                                    if (hr == WasapiInterop.S_OK && testId == deviceId)
                                                    {
                                                        isInput = true;
                                                    }
                                                }
                                            }
                                            finally
                                            {
                                                if (testDevice != null)
                                                    Marshal.ReleaseComObject(testDevice);
                                            }
                                        }
                                    }
                                }
                                finally
                                {
                                    if (inputCollection != null)
                                        Marshal.ReleaseComObject(inputCollection);
                                }
                            }
                        }

                        // Create device info
                        return new AudioDeviceInfo(
                            deviceId,
                            friendlyName,
                            isInput,
                            isOutput,
                            isDefault,
                            (AudioDeviceState)state);
                    }
                }

                return null; // Device not found
            }
            finally
            {
                if (device != null)
                    Marshal.ReleaseComObject(device);

                if (enumerator != null)
                    Marshal.ReleaseComObject(enumerator);

                // Only uninitialize COM if WE initialized it
                if (needsUninitialize)
                {
                    Ole32.CoUninitialize();
                }
            }
        }

        /// <summary>
        /// Enumerates devices for a specific data flow direction.
        /// </summary>
        /// <param name="dataFlow">The data flow direction (render/capture).</param>
        /// <returns>A list of device information objects.</returns>
        /// <exception cref="AudioException">Thrown when enumeration fails.</exception>
        private List<AudioDeviceInfo> EnumerateDevices(WasapiInterop.EDataFlow dataFlow)
        {
            var devices = new List<AudioDeviceInfo>();
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;
            IMMDevice defaultDevice = null;
            string defaultDeviceId = null;

            try
            {
                // Initialize COM
                int hr = Ole32.CoInitializeEx(IntPtr.Zero, Ole32.COINIT_MULTITHREADED);
                if (hr != WasapiInterop.S_OK &&
                    hr != WasapiInterop.S_FALSE && // Already initialized on this thread
                    hr != WasapiInterop.RPC_E_CHANGED_MODE) // Different threading model (STA vs MTA)
                {
                    throw new AudioException($"COM initialization failed: 0x{hr:X8}");
                }

                // Create device enumerator
                Guid clsid = WasapiInterop.CLSID_MMDeviceEnumerator;
                Guid iid = WasapiInterop.IID_IMMDeviceEnumerator;
                hr = Ole32.CoCreateInstance(
                    ref clsid,
                    IntPtr.Zero,
                    Ole32.CLSCTX_ALL,
                    ref iid,
                    out object enumeratorObj);

                if (hr != WasapiInterop.S_OK)
                    throw new AudioException($"Failed to create device enumerator: 0x{hr:X8}");

                enumerator = (IMMDeviceEnumerator)enumeratorObj;

                // Get default device ID
                hr = enumerator.GetDefaultAudioEndpoint(
                    dataFlow,
                    WasapiInterop.ERole.eMultimedia,
                    out defaultDevice);

                if (hr == WasapiInterop.S_OK && defaultDevice != null)
                {
                    hr = defaultDevice.GetId(out defaultDeviceId);
                }

                // Enumerate all active devices
                hr = enumerator.EnumAudioEndpoints(
                    dataFlow,
                    (uint)AudioDeviceState.Active,
                    out collection);

                if (hr != WasapiInterop.S_OK)
                    throw new AudioException($"Failed to enumerate devices: 0x{hr:X8}");

                // Get device count
                hr = collection.GetCount(out uint count);
                if (hr != WasapiInterop.S_OK)
                    throw new AudioException($"Failed to get device count: 0x{hr:X8}");

                // Iterate through devices
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    try
                    {
                        hr = collection.Item(i, out device);
                        if (hr != WasapiInterop.S_OK || device == null)
                            continue;

                        // Get device ID
                        hr = device.GetId(out string deviceId);
                        if (hr != WasapiInterop.S_OK)
                            continue;

                        // Get device state
                        hr = device.GetState(out uint state);
                        if (hr != WasapiInterop.S_OK)
                            continue;

                        // Get device friendly name
                        string friendlyName = GetDeviceFriendlyName(device);
                        if (string.IsNullOrEmpty(friendlyName))
                            friendlyName = $"Audio Device {i + 1}";

                        // Determine if this is the default device
                        bool isDefault = (deviceId == defaultDeviceId);

                        // Create device info
                        var deviceInfo = new AudioDeviceInfo(
                            deviceId,
                            friendlyName,
                            dataFlow == WasapiInterop.EDataFlow.eCapture,
                            dataFlow == WasapiInterop.EDataFlow.eRender,
                            isDefault,
                            (AudioDeviceState)state);

                        devices.Add(deviceInfo);
                    }
                    finally
                    {
                        if (device != null)
                            Marshal.ReleaseComObject(device);
                    }
                }

                return devices;
            }
            finally
            {
                if (defaultDevice != null)
                    Marshal.ReleaseComObject(defaultDevice);

                if (collection != null)
                    Marshal.ReleaseComObject(collection);

                if (enumerator != null)
                    Marshal.ReleaseComObject(enumerator);

                Ole32.CoUninitialize();
            }
        }

        /// <summary>
        /// Gets the default device for a specific data flow direction.
        /// </summary>
        /// <param name="dataFlow">The data flow direction (render/capture).</param>
        /// <returns>Device information, or null if no default device.</returns>
        /// <exception cref="AudioException">Thrown when query fails.</exception>
        private AudioDeviceInfo GetDefaultDevice(WasapiInterop.EDataFlow dataFlow)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;

            try
            {
                // Initialize COM
                int hr = Ole32.CoInitializeEx(IntPtr.Zero, Ole32.COINIT_MULTITHREADED);
                if (hr != WasapiInterop.S_OK &&
                    hr != WasapiInterop.S_FALSE && // Already initialized on this thread
                    hr != WasapiInterop.RPC_E_CHANGED_MODE) // Different threading model (STA vs MTA)
                {
                    throw new AudioException($"COM initialization failed: 0x{hr:X8}");
                }

                // Create device enumerator
                Guid clsid = WasapiInterop.CLSID_MMDeviceEnumerator;
                Guid iid = WasapiInterop.IID_IMMDeviceEnumerator;
                hr = Ole32.CoCreateInstance(
                    ref clsid,
                    IntPtr.Zero,
                    Ole32.CLSCTX_ALL,
                    ref iid,
                    out object enumeratorObj);

                if (hr != WasapiInterop.S_OK)
                    throw new AudioException($"Failed to create device enumerator: 0x{hr:X8}");

                enumerator = (IMMDeviceEnumerator)enumeratorObj;

                // Get default device
                hr = enumerator.GetDefaultAudioEndpoint(
                    dataFlow,
                    WasapiInterop.ERole.eMultimedia,
                    out device);

                if (hr != WasapiInterop.S_OK || device == null)
                    return null;

                // Get device ID
                hr = device.GetId(out string deviceId);
                if (hr != WasapiInterop.S_OK)
                    return null;

                // Get device state
                hr = device.GetState(out uint state);
                if (hr != WasapiInterop.S_OK)
                    return null;

                // Get device friendly name
                string friendlyName = GetDeviceFriendlyName(device);
                if (string.IsNullOrEmpty(friendlyName))
                    friendlyName = "Default Audio Device";

                // Create device info
                return new AudioDeviceInfo(
                    deviceId,
                    friendlyName,
                    dataFlow == WasapiInterop.EDataFlow.eCapture,
                    dataFlow == WasapiInterop.EDataFlow.eRender,
                    true, // This is the default device
                    (AudioDeviceState)state);
            }
            finally
            {
                if (device != null)
                    Marshal.ReleaseComObject(device);

                if (enumerator != null)
                    Marshal.ReleaseComObject(enumerator);

                Ole32.CoUninitialize();
            }
        }

        /// <summary>
        /// Gets the friendly name of a device from its property store.
        /// </summary>
        /// <param name="device">The IMMDevice interface.</param>
        /// <returns>The friendly name, or null if unavailable.</returns>
        private string GetDeviceFriendlyName(IMMDevice device)
        {
            IPropertyStore propertyStore = null;
            try
            {
                // Open property store
                int hr = device.OpenPropertyStore(PropertyStoreHelper.STGM_READ, out IntPtr propertyStorePtr);
                if (hr != WasapiInterop.S_OK)
                    return null;

                propertyStore = Marshal.GetObjectForIUnknown(propertyStorePtr) as IPropertyStore;
                if (propertyStore == null)
                    return null;

                // Get friendly name property
                PropertyKey key = PropertyKeys.PKEY_Device_FriendlyName;
                hr = propertyStore.GetValue(ref key, out PropVariant value);
                if (hr != WasapiInterop.S_OK)
                    return null;

                return value.GetStringValue();
            }
            catch
            {
                return null;
            }
            finally
            {
                if (propertyStore != null)
                    Marshal.ReleaseComObject(propertyStore);
            }
        }
    }
}
