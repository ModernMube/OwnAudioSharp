using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ownaudio.Core;
using Ownaudio.Windows.Interop;

namespace Ownaudio.Windows
{
    /// <summary>
    /// Implements IMMNotificationClient to receive Windows audio device change notifications.
    /// This class is used internally by <see cref="WasapiEngine"/> to monitor device changes.
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class WasapiDeviceNotificationClient : IMMNotificationClient
    {
        /// <summary>
        /// Event raised when a device is added.
        /// </summary>
        public event EventHandler<string> DeviceAdded;

        /// <summary>
        /// Event raised when a device is removed.
        /// </summary>
        public event EventHandler<string> DeviceRemoved;

        /// <summary>
        /// Event raised when a device state changes.
        /// </summary>
        public event EventHandler<AudioDeviceStateChangedEventArgs> DeviceStateChanged;

        /// <summary>
        /// Event raised when the default output (render) device changes.
        /// </summary>
        public event EventHandler<AudioDeviceChangedEventArgs> DefaultOutputDeviceChanged;

        /// <summary>
        /// Event raised when the default input (capture) device changes.
        /// </summary>
        public event EventHandler<AudioDeviceChangedEventArgs> DefaultInputDeviceChanged;

        /// <summary>
        /// Helper used to enumerate devices and retrieve device information.
        /// </summary>
        private readonly WasapiDeviceEnumerator _enumerator;
        /// <summary>
        /// Stores the device ID of the current default output device.
        /// </summary>
        private string _currentDefaultOutputId;
        /// <summary>
        /// Stores the device ID of the current default input device.
        /// </summary>
        private string _currentDefaultInputId;
        /// <summary>
        /// Lock object for thread-safe access to device IDs.
        /// </summary>
        private readonly object _lock = new object();

        /// <summary>
        /// Initializes a new instance of the <see cref="WasapiDeviceNotificationClient"/> class.
        /// </summary>
        public WasapiDeviceNotificationClient()
        {
            _enumerator = new WasapiDeviceEnumerator();
        }

        /// <summary>
        /// Called by COM when an audio endpoint device is added.
        /// </summary>
        /// <param name="deviceId">The identifier of the audio endpoint device.</param>
        public void OnDeviceAdded(string deviceId)
        {
            try
            {
                DeviceAdded?.Invoke(this, deviceId);
            }
            catch
            {
                // Prevent exceptions from escaping to COM
            }
        }

        /// <summary>
        /// Called by COM when an audio endpoint device is removed.
        /// </summary>
        /// <param name="deviceId">The identifier of the audio endpoint device.</param>
        public void OnDeviceRemoved(string deviceId)
        {
            try
            {
                DeviceRemoved?.Invoke(this, deviceId);
            }
            catch
            {
                // Prevent exceptions from escaping to COM
            }
        }

        /// <summary>
        /// Called by COM when the state of an audio endpoint device changes.
        /// </summary>
        /// <param name="deviceId">The identifier of the audio endpoint device.</param>
        /// <param name="newState">The new state of the device.</param>
        public void OnDeviceStateChanged(string deviceId, uint newState)
        {
            try
            {
                // Offload blocking work to ThreadPool to avoid blocking the COM notification thread
                Task.Run(() =>
                {
                    try
                    {
                        var deviceInfo = _enumerator.GetDeviceInfo(deviceId);
                        if (deviceInfo != null)
                        {
                            var args = new AudioDeviceStateChangedEventArgs(
                                deviceId,
                                (AudioDeviceState)newState,
                                deviceInfo);

                            DeviceStateChanged?.Invoke(this, args);
                        }
                    }
                    catch
                    {
                        // Log/handle errors in background task
                    }
                });
            }
            catch
            {
                // Prevent exceptions from escaping to COM
            }
        }

        /// <summary>
        /// Called by COM when the value of a property belonging to an audio endpoint device changes.
        /// </summary>
        /// <param name="deviceId">The identifier of the audio endpoint device.</param>
        /// <param name="key">The property key.</param>
        public void OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
            // Not currently used, but required by interface
        }

        /// <summary>
        /// Called by COM when the default audio endpoint device for a particular role changes.
        /// CRITICAL: This is called on a COM notification thread that must return quickly!
        /// </summary>
        /// <param name="dataFlow">The data-flow direction for the new default device (e.g., render or capture).</param>
        /// <param name="role">The role of the new default device (e.g., console, multimedia, communications).</param>
        /// <param name="defaultDeviceId">The identifier of the new default audio endpoint device.</param>
        public void OnDefaultDeviceChanged(WasapiInterop.EDataFlow dataFlow, WasapiInterop.ERole role, string defaultDeviceId)
        {
            try
            {
                // We only care about multimedia role changes
                if (role != WasapiInterop.ERole.eMultimedia)
                    return;

                string oldDeviceId = null;
                bool isOutput = (dataFlow == WasapiInterop.EDataFlow.eRender);

                // 1. Atomic update of internal state using lock
                lock (_lock)
                {
                    if (isOutput)
                    {
                        oldDeviceId = _currentDefaultOutputId;
                        _currentDefaultOutputId = defaultDeviceId;
                    }
                    else // eCapture
                    {
                        oldDeviceId = _currentDefaultInputId;
                        _currentDefaultInputId = defaultDeviceId;
                    }
                }

                // 2. Release COM thread immediately by offloading slow work to ThreadPool
                if (oldDeviceId != defaultDeviceId)
                {
                    // Fire event on ThreadPool to avoid blocking GetDeviceInfo call
                    Task.Run(() =>
                    {
                        try
                        {
                            // The blocking call is now safe here
                            var newDeviceInfo = _enumerator.GetDeviceInfo(defaultDeviceId);
                            var args = new AudioDeviceChangedEventArgs(
                                oldDeviceId,
                                defaultDeviceId,
                                newDeviceInfo);

                            if (isOutput)
                                DefaultOutputDeviceChanged?.Invoke(this, args);
                            else
                                DefaultInputDeviceChanged?.Invoke(this, args);
                        }
                        catch
                        {
                            // Log/handle errors in background task
                        }
                    });
                }
            }
            catch
            {
                // Prevent exceptions from escaping to COM
            }
        }

        /// <summary>
        /// Initializes the current default device IDs by querying the system.
        /// </summary>
        public void Initialize()
        {
            try
            {
                var defaultOutput = _enumerator.GetDefaultOutputDevice();
                if (defaultOutput != null)
                    _currentDefaultOutputId = defaultOutput.DeviceId;

                var defaultInput = _enumerator.GetDefaultInputDevice();
                if (defaultInput != null)
                    _currentDefaultInputId = defaultInput.DeviceId;
            }
            catch
            {
                // Initialization errors are not critical
            }
        }
    }
}
