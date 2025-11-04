using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Core;
using Ownaudio.Linux.Interop;
using static Ownaudio.Linux.Interop.PulseAudioInterop;

namespace Ownaudio.Linux
{
    /// <summary>
    /// PulseAudio device enumerator for discovering audio devices.
    /// Implements hotplug detection via PulseAudio subscription API.
    /// </summary>
    public sealed class PulseAudioDeviceEnumerator : IDeviceEnumerator, IDisposable
    {
        private IntPtr _mainLoop;
        private IntPtr _context;
        private readonly List<AudioDeviceInfo> _sinks = new List<AudioDeviceInfo>();
        private readonly List<AudioDeviceInfo> _sources = new List<AudioDeviceInfo>();
        private string? _defaultSinkName;
        private string? _defaultSourceName;
        private readonly object _lock = new object();

        // Callbacks (must be kept alive to prevent GC)
        private pa_context_notify_cb _contextStateCallback;
        private pa_sink_info_cb _sinkInfoCallback;
        private pa_source_info_cb _sourceInfoCallback;
        private pa_server_info_cb _serverInfoCallback;
        private pa_context_subscribe_cb _subscribeCallback;

        // Events
        private ManualResetEventSlim _contextReadyEvent;
        private ManualResetEventSlim _enumerationCompleteEvent;
        private ManualResetEventSlim _serverInfoEvent;

        private volatile bool _isInitialized;

        /// <summary>
        /// Event raised when a device is added, removed, or changed.
        /// </summary>
        public event EventHandler<AudioDeviceChangedEventArgs> DeviceChanged;

        /// <summary>
        /// Creates a new PulseAudio device enumerator.
        /// </summary>
        public PulseAudioDeviceEnumerator()
        {
            _contextReadyEvent = new ManualResetEventSlim(false);
            _enumerationCompleteEvent = new ManualResetEventSlim(false);
            _serverInfoEvent = new ManualResetEventSlim(false);

            Initialize();
        }

        /// <summary>
        /// Initializes the PulseAudio connection for device enumeration.
        /// </summary>
        private void Initialize()
        {
            try
            {
                // Create threaded mainloop
                _mainLoop = pa_threaded_mainloop_new();
                if (_mainLoop == IntPtr.Zero)
                    return;

                // Get mainloop API
                IntPtr api = pa_threaded_mainloop_get_api(_mainLoop);
                if (api == IntPtr.Zero)
                    return;

                // Create context
                _context = pa_context_new(api, "OwnAudio Device Enumerator");
                if (_context == IntPtr.Zero)
                    return;

                // Setup context state callback
                _contextStateCallback = OnContextStateChanged;
                pa_context_set_state_callback(_context, _contextStateCallback, IntPtr.Zero);

                // Start mainloop
                if (pa_threaded_mainloop_start(_mainLoop) != 0)
                    return;

                // Connect to PulseAudio server
                pa_threaded_mainloop_lock(_mainLoop);
                int connectResult = pa_context_connect(_context, null, 0, IntPtr.Zero);
                pa_threaded_mainloop_unlock(_mainLoop);

                if (connectResult != 0)
                    return;

                // Wait for context to be ready
                if (!_contextReadyEvent.Wait(TimeSpan.FromSeconds(5)))
                    return;

                // Setup device change notifications
                SetupDeviceNotifications();

                _isInitialized = true;
            }
            catch (Exception ex)
            {
                _isInitialized = false;
            }
        }

        /// <summary>
        /// Enumerates all output devices (sinks).
        /// </summary>
        public List<AudioDeviceInfo> EnumerateOutputDevices()
        {
            if (!_isInitialized)
                return new List<AudioDeviceInfo>();

            lock (_lock)
            {
                _sinks.Clear();
                _enumerationCompleteEvent.Reset();

                // Get server info first to know default device
                GetServerInfo();

                // Setup sink info callback
                _sinkInfoCallback = OnSinkInfo;

                pa_threaded_mainloop_lock(_mainLoop);
                IntPtr op = pa_context_get_sink_info_list(_context, _sinkInfoCallback, IntPtr.Zero);
                pa_threaded_mainloop_unlock(_mainLoop);

                if (op != IntPtr.Zero)
                {
                    // Wait for enumeration to complete
                    _enumerationCompleteEvent.Wait(TimeSpan.FromSeconds(5));
                    pa_operation_unref(op);
                }

                return new List<AudioDeviceInfo>(_sinks);
            }
        }

        /// <summary>
        /// Enumerates all input devices (sources).
        /// </summary>
        public List<AudioDeviceInfo> EnumerateInputDevices()
        {
            if (!_isInitialized)
                return new List<AudioDeviceInfo>();

            lock (_lock)
            {
                _sources.Clear();
                _enumerationCompleteEvent.Reset();

                // Get server info first to know default device
                GetServerInfo();

                // Setup source info callback
                _sourceInfoCallback = OnSourceInfo;

                pa_threaded_mainloop_lock(_mainLoop);
                IntPtr op = pa_context_get_source_info_list(_context, _sourceInfoCallback, IntPtr.Zero);
                pa_threaded_mainloop_unlock(_mainLoop);

                if (op != IntPtr.Zero)
                {
                    // Wait for enumeration to complete
                    _enumerationCompleteEvent.Wait(TimeSpan.FromSeconds(5));
                    pa_operation_unref(op);
                }

                return new List<AudioDeviceInfo>(_sources);
            }
        }

        /// <summary>
        /// Gets server information (default devices).
        /// </summary>
        private void GetServerInfo()
        {
            _serverInfoEvent.Reset();

            _serverInfoCallback = OnServerInfo;

            pa_threaded_mainloop_lock(_mainLoop);
            IntPtr op = pa_context_get_server_info(_context, _serverInfoCallback, IntPtr.Zero);
            pa_threaded_mainloop_unlock(_mainLoop);

            if (op != IntPtr.Zero)
            {
                _serverInfoEvent.Wait(TimeSpan.FromSeconds(2));
                pa_operation_unref(op);
            }
        }

        /// <summary>
        /// Sets up device change notifications.
        /// </summary>
        private void SetupDeviceNotifications()
        {
            try
            {
                _subscribeCallback = OnSubscriptionEvent;

                pa_threaded_mainloop_lock(_mainLoop);

                // Set subscription callback
                pa_context_set_subscribe_callback(_context, _subscribeCallback, IntPtr.Zero);

                // Subscribe to sink and source events
                uint mask = PA_SUBSCRIPTION_MASK_SINK | PA_SUBSCRIPTION_MASK_SOURCE;
                IntPtr op = pa_context_subscribe(_context, mask, null, IntPtr.Zero);

                if (op != IntPtr.Zero)
                    pa_operation_unref(op);

                pa_threaded_mainloop_unlock(_mainLoop);
            }
            catch (Exception ex)
            {
                // Device notifications are optional
            }
        }

        /// <summary>
        /// Context state changed callback.
        /// </summary>
        private void OnContextStateChanged(IntPtr context, IntPtr userdata)
        {
            var state = pa_context_get_state(context);

            if (state == pa_context_state_t.PA_CONTEXT_READY)
            {
                _contextReadyEvent.Set();
            }
        }

        /// <summary>
        /// Server info callback.
        /// </summary>
        private void OnServerInfo(IntPtr context, IntPtr info, IntPtr userdata)
        {
            if (info == IntPtr.Zero)
                return;

            try
            {
                unsafe
                {
                    pa_server_info* serverInfo = (pa_server_info*)info;
                    _defaultSinkName = Marshal.PtrToStringAnsi(serverInfo->default_sink_name);
                    _defaultSourceName = Marshal.PtrToStringAnsi(serverInfo->default_source_name);
                }

                _serverInfoEvent.Set();
            }
            catch (Exception ex)
            {
                _serverInfoEvent.Set();
            }
        }

        /// <summary>
        /// Sink info callback.
        /// </summary>
        private void OnSinkInfo(IntPtr context, IntPtr info, int eol, IntPtr userdata)
        {
            if (eol != 0)
            {
                _enumerationCompleteEvent.Set();
                return;
            }

            if (info == IntPtr.Zero)
                return;

            try
            {
                unsafe
                {
                    pa_sink_info* sinkInfo = (pa_sink_info*)info;

                    string name = Marshal.PtrToStringAnsi(sinkInfo->name);
                    string description = Marshal.PtrToStringAnsi(sinkInfo->description);
                    bool isDefault = name == _defaultSinkName;

                    var device = new AudioDeviceInfo(
                        deviceId: name,
                        name: description ?? name,
                        isInput: false,
                        isOutput: true,
                        isDefault: isDefault,
                        state: AudioDeviceState.Active);

                    _sinks.Add(device);
                }
            }
            catch (Exception ex)
            {
                // Continue enumeration even if one device fails
            }
        }

        /// <summary>
        /// Source info callback.
        /// </summary>
        private void OnSourceInfo(IntPtr context, IntPtr info, int eol, IntPtr userdata)
        {
            if (eol != 0)
            {
                _enumerationCompleteEvent.Set();
                return;
            }

            if (info == IntPtr.Zero)
                return;

            try
            {
                unsafe
                {
                    pa_source_info* sourceInfo = (pa_source_info*)info;

                    string name = Marshal.PtrToStringAnsi(sourceInfo->name);
                    string description = Marshal.PtrToStringAnsi(sourceInfo->description);

                    // Skip monitor sources (they are outputs, not inputs)
                    if (name != null && name.EndsWith(".monitor"))
                        return;

                    bool isDefault = name == _defaultSourceName;

                    var device = new AudioDeviceInfo(
                        deviceId: name,
                        name: description ?? name,
                        isInput: true,
                        isOutput: false,
                        isDefault: isDefault,
                        state: AudioDeviceState.Active);

                    _sources.Add(device);
                }
            }
            catch (Exception ex)
            {
                // Continue enumeration even if one device fails
            }
        }

        /// <summary>
        /// Subscription event callback (for hotplug detection).
        /// </summary>
        private void OnSubscriptionEvent(IntPtr context, uint eventType, uint index, IntPtr userdata)
        {
            try
            {
                uint facility = eventType & PA_SUBSCRIPTION_EVENT_FACILITY_MASK;
                uint type = eventType & PA_SUBSCRIPTION_EVENT_TYPE_MASK;

                string oldDeviceId = null;
                string newDeviceId = index.ToString();

                if (facility == PA_SUBSCRIPTION_EVENT_SINK)
                {
                    if (type == PA_SUBSCRIPTION_EVENT_NEW)
                    {
                        DeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(
                            oldDeviceId: null,
                            newDeviceId: newDeviceId,
                            newDeviceInfo: null!));
                    }
                    else if (type == PA_SUBSCRIPTION_EVENT_REMOVE)
                    {
                        DeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(
                            oldDeviceId: newDeviceId,
                            newDeviceId: null,
                            newDeviceInfo: null!));
                    }
                    else if (type == PA_SUBSCRIPTION_EVENT_CHANGE)
                    {
                        DeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(
                            oldDeviceId: newDeviceId,
                            newDeviceId: newDeviceId,
                            newDeviceInfo: null!));
                    }
                }
                else if (facility == PA_SUBSCRIPTION_EVENT_SOURCE)
                {
                    if (type == PA_SUBSCRIPTION_EVENT_NEW)
                    {
                        DeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(
                            oldDeviceId: null,
                            newDeviceId: newDeviceId,
                            newDeviceInfo: null!));
                    }
                    else if (type == PA_SUBSCRIPTION_EVENT_REMOVE)
                    {
                        DeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(
                            oldDeviceId: newDeviceId,
                            newDeviceId: null,
                            newDeviceInfo: null!));
                    }
                    else if (type == PA_SUBSCRIPTION_EVENT_CHANGE)
                    {
                        DeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(
                            oldDeviceId: newDeviceId,
                            newDeviceId: newDeviceId,
                            newDeviceInfo: null!));
                    }
                }
            }
            catch (Exception ex)
            {
                // Suppress exceptions in callback
            }
        }

        /// <summary>
        /// Enumerates all audio devices (both input and output).
        /// </summary>
        public List<AudioDeviceInfo> EnumerateAllDevices()
        {
            var allDevices = new List<AudioDeviceInfo>();
            allDevices.AddRange(EnumerateOutputDevices());
            allDevices.AddRange(EnumerateInputDevices());
            return allDevices;
        }

        /// <summary>
        /// Gets the default output device.
        /// </summary>
        public AudioDeviceInfo GetDefaultOutputDevice()
        {
            var devices = EnumerateOutputDevices();
            return devices.Find(d => d.IsDefault);
        }

        /// <summary>
        /// Gets the default input device.
        /// </summary>
        public AudioDeviceInfo GetDefaultInputDevice()
        {
            var devices = EnumerateInputDevices();
            return devices.Find(d => d.IsDefault);
        }

        /// <summary>
        /// Gets detailed information about a specific device by its ID.
        /// </summary>
        public AudioDeviceInfo GetDeviceInfo(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
                return null;

            // Try output devices first
            var outputDevices = EnumerateOutputDevices();
            var device = outputDevices.Find(d => d.DeviceId == deviceId);
            if (device != null)
                return device;

            // Try input devices
            var inputDevices = EnumerateInputDevices();
            return inputDevices.Find(d => d.DeviceId == deviceId);
        }

        /// <summary>
        /// Disposes of all resources.
        /// </summary>
        public void Dispose()
        {
            if (_mainLoop != IntPtr.Zero)
            {
                pa_threaded_mainloop_lock(_mainLoop);

                if (_context != IntPtr.Zero)
                {
                    pa_context_disconnect(_context);
                    pa_context_unref(_context);
                    _context = IntPtr.Zero;
                }

                pa_threaded_mainloop_unlock(_mainLoop);

                pa_threaded_mainloop_stop(_mainLoop);
                pa_threaded_mainloop_free(_mainLoop);
                _mainLoop = IntPtr.Zero;
            }

            _contextReadyEvent?.Dispose();
            _enumerationCompleteEvent?.Dispose();
            _serverInfoEvent?.Dispose();
        }
    }
}
