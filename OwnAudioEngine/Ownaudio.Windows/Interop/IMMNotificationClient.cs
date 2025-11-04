using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Windows.Interop
{
    /// <summary>
    /// IMMNotificationClient interface for receiving audio device notifications.
    /// This is a COM interface used by WASAPI to notify applications about device changes.
    /// </summary>
    [ComImport]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        /// <summary>
        /// Called when a new audio endpoint device is added.
        /// </summary>
        /// <param name="deviceId">The ID of the newly added device.</param>
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        /// <summary>
        /// Called when an audio endpoint device is removed.
        /// </summary>
        /// <param name="deviceId">The ID of the removed device.</param>
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        /// <summary>
        /// Called when the state of an audio endpoint device changes.
        /// </summary>
        /// <param name="deviceId">The ID of the device whose state changed.</param>
        /// <param name="newState">The new state of the device.</param>
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);

        /// <summary>
        /// Called when a property value changes for an audio endpoint device.
        /// </summary>
        /// <param name="deviceId">The ID of the device whose property changed.</param>
        /// <param name="key">The property key that changed.</param>
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);

        /// <summary>
        /// Called when the default audio endpoint device for a particular role changes.
        /// </summary>
        /// <param name="dataFlow">The data flow direction (render/capture).</param>
        /// <param name="role">The device role (console/multimedia/communications).</param>
        /// <param name="defaultDeviceId">The ID of the new default device.</param>
        void OnDefaultDeviceChanged(
            WasapiInterop.EDataFlow dataFlow,
            WasapiInterop.ERole role,
            [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
    }

    /// <summary>
    /// Extension methods for IMMDeviceEnumerator to register/unregister notification clients.
    /// </summary>
    internal static class IMMDeviceEnumeratorExtensions
    {
        /// <summary>
        /// Registers a client's notification callback interface.
        /// </summary>
        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumeratorWithNotifications
        {
            // All methods from IMMDeviceEnumerator
            int EnumAudioEndpoints(WasapiInterop.EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
            int GetDefaultAudioEndpoint(WasapiInterop.EDataFlow dataFlow, WasapiInterop.ERole role, out IMMDevice device);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

            /// <summary>
            /// Registers a notification callback client.
            /// </summary>
            /// <param name="client">The notification client to register.</param>
            /// <returns>HRESULT error code.</returns>
            int RegisterEndpointNotificationCallback(IMMNotificationClient client);

            /// <summary>
            /// Unregisters a notification callback client.
            /// </summary>
            /// <param name="client">The notification client to unregister.</param>
            /// <returns>HRESULT error code.</returns>
            int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
        }
    }
}
