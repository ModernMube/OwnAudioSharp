using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Logger;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using Ownaudio.Native.Utils;
using Ownaudio.Native.PortAudio;
using Ownaudio.Native.MiniAudio;
using static Ownaudio.Native.PortAudio.PaBinding;
using static Ownaudio.Native.MiniAudio.MaBinding;

namespace Ownaudio.Native
{
    /// <summary>
    /// Native cross-platform audio engine using PortAudio (when available) or MiniAudio.
    /// This is the PRIMARY audio engine for OwnAudioSharp 2.1.0+
    ///
    /// Strategy:
    /// - Windows x64: Try PortAudio first, fallback to MiniAudio
    /// - All other platforms: Use MiniAudio
    /// - Decoder: Always use MiniAudio decoder (supports MP3, WAV, FLAC)
    /// </summary>
    public sealed partial class NativeAudioEngine : IAudioEngine
    {
        #region Common Fields

        /// <summary>
        /// The audio engine backend currently in use (PortAudio or MiniAudio).
        /// </summary>
        private AudioEngineBackend _backend;

        /// <summary>
        /// The audio configuration for this engine instance.
        /// Initialized in Initialize() method.
        /// </summary>
        private AudioConfig _config = null!;

        /// <summary>
        /// Indicates whether the engine has been disposed.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Running state: 0 = stopped, 1 = running.
        /// </summary>
        private volatile int _isRunning;

        /// <summary>
        /// Active state: 0 = idle, 1 = active, -1 = error.
        /// </summary>
#pragma warning disable CS0414 // Field is assigned but its value is never used
        private volatile int _isActive;
#pragma warning restore CS0414

        /// <summary>
        /// Pre-buffering state: 0 = not buffering, 1 = buffering (waiting for initial data).
        /// When buffering, callbacks output silence until the buffer reaches the threshold.
        /// </summary>
        private volatile int _isBuffering;

        /// <summary>
        /// Number of frames per audio buffer.
        /// </summary>
        private int _framesPerBuffer;

        /// <summary>
        /// Minimum number of samples required in the buffer before starting playback.
        /// This prevents the audio from playing too fast when the buffer is nearly empty.
        /// Set to 2x the buffer size to ensure smooth playback.
        /// </summary>
        private int _prebufferThreshold;

        /// <summary>
        /// Lock-free ring buffer for output (playback) audio data.
        /// Initialized in Initialize() method.
        /// </summary>
        private LockFreeRingBuffer<float> _outputRing = null!;

        /// <summary>
        /// Lock-free ring buffer for input (recording) audio data.
        /// Initialized in Initialize() method.
        /// </summary>
        private LockFreeRingBuffer<float> _inputRing = null!;

        /// <summary>
        /// Number of physical output channels to open on the device.
        /// Equals _config.Channels unless OutputChannelSelectors is used,
        /// in which case it is max(OutputChannelSelectors)+1.
        /// </summary>
        private int _physicalOutputChannels;

        /// <summary>
        /// Number of physical input channels to open on the device.
        /// Equals _config.Channels unless InputChannelSelectors is used,
        /// in which case it is max(InputChannelSelectors)+1.
        /// </summary>
        private int _physicalInputChannels;

        #endregion

        #region Events

        /// <summary>
        /// Raised when the output device changes.
        /// </summary>
#pragma warning disable CS0067 // Events are part of the public IAudioEngine API; not yet fired internally for PortAudio device changes
        public event EventHandler<AudioDeviceChangedEventArgs>? OutputDeviceChanged;

        /// <summary>
        /// Raised when the input device changes.
        /// </summary>
        public event EventHandler<AudioDeviceChangedEventArgs>? InputDeviceChanged;
#pragma warning restore CS0067

        /// <summary>
        /// Raised when the device state changes (e.g., device removed).
        /// </summary>
        public event EventHandler<AudioDeviceStateChangedEventArgs>? DeviceStateChanged;

        /// <summary>
        /// Raised when a previously disconnected audio device reconnects.
        /// Playback and recording automatically resume.
        /// </summary>
        public event EventHandler<AudioDeviceReconnectedEventArgs>? DeviceReconnected;

        #endregion

        #region Device Monitoring Fields

        /// <summary>
        /// Device monitoring interval in milliseconds during normal operation.
        /// </summary>
        private const int DeviceMonitorIntervalMs = 1000;

        /// <summary>
        /// Device monitoring interval in milliseconds when a device is disconnected.
        /// More frequent polling to detect reconnection quickly.
        /// </summary>
        private const int DeviceReconnectPollIntervalMs = 500;

        /// <summary>
        /// Cancellation token source for device monitoring task.
        /// </summary>
        private CancellationTokenSource? _deviceMonitorCts;

        /// <summary>
        /// Background task for monitoring device changes.
        /// </summary>
        private Task? _deviceMonitorTask;

        /// <summary>
        /// Last known output devices list.
        /// </summary>
        private List<AudioDeviceInfo>? _lastKnownOutputDevices;

        /// <summary>
        /// Last known input devices list.
        /// </summary>
        private List<AudioDeviceInfo>? _lastKnownInputDevices;

        /// <summary>
        /// Last active output device ID.
        /// </summary>
        private string? _lastActiveOutputDeviceId;

        /// <summary>
        /// Last active input device ID.
        /// </summary>
        private string? _lastActiveInputDeviceId;

        /// <summary>
        /// Last known total number of devices (input + output).
        /// Used for lightweight change detection.
        /// </summary>
        private int _lastRawDeviceCount = -1;

        /// <summary>
        /// Indicates whether device monitoring is currently paused.
        /// When true, the DeviceMonitorLoop will skip device change detection.
        /// </summary>
        private volatile bool _isMonitoringPaused;

        /// <summary>
        /// Current operational status of the audio engine.
        /// Cast to/from <see cref="EngineStatus"/> as needed.
        /// 0=Idle, 1=Running, 2=DeviceDisconnected, -1=Error
        /// </summary>
        private volatile int _engineStatusValue;

        /// <summary>
        /// Indicates whether the active device has been unexpectedly disconnected.
        /// 0 = connected, 1 = disconnected (waiting for reconnect).
        /// </summary>
        private volatile int _isDeviceDisconnected;

        /// <summary>
        /// The friendly name of the output device that was disconnected.
        /// Used to identify and reconnect to the same device.
        /// </summary>
        private string? _disconnectedOutputDeviceName;

        /// <summary>
        /// The friendly name of the input device that was disconnected.
        /// Used to identify and reconnect to the same device.
        /// </summary>
        private string? _disconnectedInputDeviceName;

        /// <summary>
        /// The original output device ID before disconnect.
        /// Restored when the device reconnects.
        /// </summary>
        private string? _originalOutputDeviceId;

        /// <summary>
        /// The original input device ID before disconnect.
        /// Restored when the device reconnects.
        /// </summary>
        private string? _originalInputDeviceId;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the number of frames per audio buffer.
        /// </summary>
        public int FramesPerBuffer => _framesPerBuffer;

        /// <summary>
        /// Gets the current operational status of the audio engine.
        /// </summary>
        public EngineStatus Status => (EngineStatus)_engineStatusValue;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="NativeAudioEngine"/> class.
        /// </summary>
        public NativeAudioEngine()
        {
            _isRunning = 0;
            _isActive = 0;
            _isBuffering = 0;
            _selectedHostApiType = (PaHostApiTypeId)(-1); // Initialize to invalid value
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the audio engine with the specified configuration.
        /// </summary>
        /// <param name="config">The audio configuration to use.</param>
        /// <returns>0 on success, negative value on error.</returns>
        public int Initialize(AudioConfig config)
        {
            if (config == null || !config.Validate())
                return -1;

            // Clear ASIO cache on new initialization to ensure fresh probing if devices changed
            _asioChannelCache.Clear();

            _config = config;
            _framesPerBuffer = config.BufferSize;

            // Compute physical channel counts based on channel selectors.
            // If selectors are set, we need to open enough physical channels to cover
            // the highest-numbered selected channel (e.g. selector [4,5] needs 6 physical channels).
            _physicalOutputChannels = (config.OutputChannelSelectors != null && config.OutputChannelSelectors.Length > 0)
                ? config.OutputChannelSelectors.Max() + 1
                : config.Channels;

            _physicalInputChannels = (config.InputChannelSelectors != null && config.InputChannelSelectors.Length > 0)
                ? config.InputChannelSelectors.Max() + 1
                : config.Channels;

            if (_physicalOutputChannels != config.Channels)
                Log.Info($"Channel routing active: {config.Channels} logical → {_physicalOutputChannels} physical output channels [{string.Join(", ", config.OutputChannelSelectors!)}]");
            if (_physicalInputChannels != config.Channels)
                Log.Info($"Channel routing active: {config.Channels} logical → {_physicalInputChannels} physical input channels [{string.Join(", ", config.InputChannelSelectors!)}]");

            // Set prebuffer threshold to 2x buffer size (in samples)
            _prebufferThreshold = config.BufferSize * _physicalOutputChannels * 2;

            // Determine which backend to use
            _backend = DetermineBackend();

            int result;
            try
            {
                result = _backend == AudioEngineBackend.PortAudio
                    ? InitializePortAudio()
                    : InitializeMiniAudio();
            }
            catch (Exception ex)
            {
                // If PortAudio fails, try MiniAudio as fallback
                if (_backend == AudioEngineBackend.PortAudio)
                {
                    Log.Error($"PortAudio initialization failed: {ex.Message}. Falling back to MiniAudio...");
                    _backend = AudioEngineBackend.MiniAudio;
                    result = InitializeMiniAudio();
                }
                else
                {
                    throw;
                }
            }

            // Start device monitoring if initialization was successful
            if (result == 0)
            {
                StartDeviceMonitor();
            }

            return result;
        }

        /// <summary>
        /// Determines which audio backend to use (PortAudio or MiniAudio).
        /// Tries PortAudio first, falls back to MiniAudio if unavailable.
        /// </summary>
        /// <returns>The selected audio backend.</returns>
        private AudioEngineBackend DetermineBackend()
        {
            try
            {
                _portAudioLoader = new LibraryLoader("libportaudio");
                PaBinding.InitializeBindings(_portAudioLoader);
                Log.Info($"PortAudio loaded successfully from: {_portAudioLoader.LibraryPath}");
                return AudioEngineBackend.PortAudio;
            }
            catch (DllNotFoundException ex)
            {
                // PortAudio not available - this is expected on most platforms
                Log.Warning($"PortAudio not found: {ex.Message}");
                Log.Warning("Falling back to MiniAudio (bundled with application)");
                _portAudioLoader?.Dispose();
                _portAudioLoader = null;
            }
            catch (Exception ex)
            {
                // PortAudio found but failed to initialize
                Log.Error($"PortAudio initialization failed: {ex.Message}");
                Log.Error("Falling back to MiniAudio");
                _portAudioLoader?.Dispose();
                _portAudioLoader = null;
            }

            // Fallback: Use MiniAudio (bundled for all platforms)
            MaBinding.EnsureInitialized();
            Log.Info($"MiniAudio loaded successfully");
            return AudioEngineBackend.MiniAudio;
        }

        #endregion

        #region Device Monitoring

        /// <summary>
        /// Starts the device monitoring task.
        /// </summary>
        private void StartDeviceMonitor()
        {
            try
            {
                // Stop any existing monitor
                StopDeviceMonitor();

                // Initialize device lists
                _lastKnownOutputDevices = GetOutputDevices();
                _lastKnownInputDevices = _config.EnableInput ? GetInputDevices() : null;

                // Initialize raw device count for lightweight monitoring
                _lastRawDeviceCount = GetRawDeviceCount();

                // Store current active device IDs
                if (_backend == AudioEngineBackend.PortAudio)
                {
                    var outputDevices = _lastKnownOutputDevices;
                    // Find device by matching the active device index
                    var activeOutput = outputDevices?.Find(d => d.DeviceId == _activeOutputDeviceIndex.ToString());
                    _lastActiveOutputDeviceId = activeOutput?.DeviceId;

                    if (_config.EnableInput)
                    {
                        var inputDevices = _lastKnownInputDevices;
                        var activeInput = inputDevices?.Find(d => d.DeviceId == _activeInputDeviceIndex.ToString());
                        _lastActiveInputDeviceId = activeInput?.DeviceId;
                    }
                }
                else // MiniAudio
                {
                    // For MiniAudio, we use the default device (null ID means default)
                    var defaultOutput = _lastKnownOutputDevices?.Find(d => d.IsDefault);
                    _lastActiveOutputDeviceId = defaultOutput?.DeviceId;

                    if (_config.EnableInput)
                    {
                        var defaultInput = _lastKnownInputDevices?.Find(d => d.IsDefault);
                        _lastActiveInputDeviceId = defaultInput?.DeviceId;
                    }
                }

                // Start monitoring task
                _deviceMonitorCts = new CancellationTokenSource();
                _deviceMonitorTask = Task.Run(() => DeviceMonitorLoop(_deviceMonitorCts.Token), _deviceMonitorCts.Token);

                Log.Info("Device monitoring started");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to start device monitor: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops the device monitoring task.
        /// </summary>
        private void StopDeviceMonitor()
        {
            try
            {
                if (_deviceMonitorCts != null)
                {
                    _deviceMonitorCts.Cancel();
                    _deviceMonitorTask?.Wait(2000); // Wait up to 2 seconds
                    _deviceMonitorCts.Dispose();
                    _deviceMonitorCts = null;
                    _deviceMonitorTask = null;
                    Log.Info("Device monitoring stopped");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error stopping device monitor: {ex.Message}");
            }
        }

        /// <summary>
        /// Background loop that monitors for device changes.
        /// Uses a faster polling rate when a device is disconnected so reconnection is detected quickly.
        /// </summary>
        private async Task DeviceMonitorLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Poll faster while waiting for a disconnected device to reappear
                    int delay = _isDeviceDisconnected == 1
                        ? DeviceReconnectPollIntervalMs
                        : DeviceMonitorIntervalMs;

                    await Task.Delay(delay, ct);

                    // Always run checks when disconnected (ignoring _isMonitoringPaused),
                    // because we need to detect reconnection regardless of UI state.
                    if (_isMonitoringPaused && _isDeviceDisconnected == 0)
                        continue;

                    CheckForDeviceChanges();
                }
                catch (OperationCanceledException)
                {
                    // Normal cancellation, exit loop
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"Error in device monitor loop: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Gets the raw count of available devices from the backend.
        /// This is a lightweight operation compared to full enumeration.
        /// </summary>
        /// <returns>Total number of devices reported by the backend.</returns>
        private int GetRawDeviceCount()
        {
            return _backend == AudioEngineBackend.PortAudio
                ? GetPortAudioRawDeviceCount()
                : GetMiniAudioRawDeviceCount();
        }

        /// <summary>
        /// Pauses the background device monitoring task.
        /// This prevents device enumeration and change detection from interfering with UI operations.
        /// Recommended to call when opening VST editor windows or during critical UI operations.
        /// </summary>
        public void PauseDeviceMonitoring()
        {
            _isMonitoringPaused = true;
            Log.Info("Device monitoring paused");
        }

        /// <summary>
        /// Resumes the background device monitoring task.
        /// Should be called after closing VST editor windows or when critical UI operations complete.
        /// </summary>
        public void ResumeDeviceMonitoring()
        {
            _isMonitoringPaused = false;
            Log.Info("Device monitoring resumed");
        }

        /// <summary>
        /// Checks for device changes and handles device removal or reconnection.
        /// When a device is disconnected, this method monitors for the original device
        /// to reappear instead of immediately switching to a default device.
        /// Uses lightweight counting first to avoid expensive full enumeration.
        /// </summary>
        private void CheckForDeviceChanges()
        {
            try
            {
                if (_isRunning == 0)
                    return;

                // --- RECONNECT MODE ---
                // If a device is disconnected, check if it has reappeared.
                if (_isDeviceDisconnected == 1)
                {
                    var currentOutputDevices = GetOutputDevices();
                    // Check by device name for the disconnected output device
                    if (_disconnectedOutputDeviceName != null)
                    {
                        var found = currentOutputDevices?.Find(d =>
                            d.Name.Equals(_disconnectedOutputDeviceName, StringComparison.OrdinalIgnoreCase));

                        if (found != null)
                        {
                            Log.Info($"Disconnected output device '{_disconnectedOutputDeviceName}' has reappeared. Reconnecting...");
                            _ = HandleDeviceReconnected(true, found);
                            return;
                        }
                    }

                    // Check for reconnected input device
                    if (_config.EnableInput && _disconnectedInputDeviceName != null)
                    {
                        var currentInputDevices = GetInputDevices();
                        var foundInput = currentInputDevices?.Find(d =>
                            d.Name.Equals(_disconnectedInputDeviceName, StringComparison.OrdinalIgnoreCase));

                        if (foundInput != null)
                        {
                            Log.Info($"Disconnected input device '{_disconnectedInputDeviceName}' has reappeared. Reconnecting...");
                            _ = HandleDeviceReconnected(false, foundInput);
                            return;
                        }
                    }

                    // Device still not available — update raw count baseline and keep waiting
                    _lastRawDeviceCount = GetRawDeviceCount();
                    return;
                }

                // --- NORMAL MONITORING MODE ---
                // Lightweight check: compare raw device count first
                int currentRawCount = GetRawDeviceCount();
                if (currentRawCount == _lastRawDeviceCount)
                    return; // No change

                _lastRawDeviceCount = currentRawCount;
                Log.Info($"Device count changed to {currentRawCount}. Performing full device enumeration.");

                var outputDevices = GetOutputDevices();
                var inputDevices = _config.EnableInput ? GetInputDevices() : null;

                // Check if active output device was removed
                if (_lastActiveOutputDeviceId != null)
                {
                    var activeOutputExists = outputDevices?.Any(d => d.DeviceId == _lastActiveOutputDeviceId) ?? false;
                    if (!activeOutputExists)
                    {
                        Log.Warning($"Active output device removed (ID: {_lastActiveOutputDeviceId})");
                        _ = HandleDeviceRemoved(true);
                        return;
                    }
                }

                // Check if active input device was removed
                if (_config.EnableInput && _lastActiveInputDeviceId != null)
                {
                    var activeInputExists = inputDevices?.Any(d => d.DeviceId == _lastActiveInputDeviceId) ?? false;
                    if (!activeInputExists)
                    {
                        Log.Warning($"Active input device removed (ID: {_lastActiveInputDeviceId})");
                        _ = HandleDeviceRemoved(false);
                        return;
                    }
                }

                // Check if default device changed (only when using default device)
                if (_config.OutputDeviceId == null && _disconnectedOutputDeviceName == null)
                {
                    var currentDefault = outputDevices?.Find(d => d.IsDefault);
                    if (currentDefault != null && currentDefault.DeviceId != _lastActiveOutputDeviceId)
                    {
                        Log.Info($"Default output device changed to: {currentDefault.Name}");
                        _ = HandleDeviceRemoved(true);
                        return;
                    }
                }

                // Update last known device lists
                _lastKnownOutputDevices = outputDevices;
                _lastKnownInputDevices = inputDevices;
            }
            catch (Exception ex)
            {
                Log.Error($"Error checking for device changes: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles unexpected device removal.
        /// The engine does NOT stop — audio data processing continues internally.
        /// Only the hardware stream is stopped. The engine enters DeviceDisconnected state
        /// and waits for the device to reconnect via the monitoring loop.
        /// </summary>
        /// <param name="isOutputDevice">
        /// True if the output (playback) device was removed; false for the input device.
        /// </param>
        private async Task HandleDeviceRemoved(bool isOutputDevice)
        {
            // Guard against multiple invocations
            if (Interlocked.CompareExchange(ref _isDeviceDisconnected, 1, 0) != 0)
                return;

            try
            {
                string removedDeviceName;

                if (isOutputDevice)
                {
                    // Look up the friendly name of the removed device
                    removedDeviceName = _lastKnownOutputDevices
                        ?.Find(d => d.DeviceId == _lastActiveOutputDeviceId)?.Name
                        ?? _lastActiveOutputDeviceId
                        ?? "Unknown output device";

                    _disconnectedOutputDeviceName = removedDeviceName;
                    _originalOutputDeviceId = _config.OutputDeviceId;

                    Log.Warning($"Output device '{removedDeviceName}' disconnected. Engine entering DeviceDisconnected state.");
                }
                else
                {
                    removedDeviceName = _lastKnownInputDevices
                        ?.Find(d => d.DeviceId == _lastActiveInputDeviceId)?.Name
                        ?? _lastActiveInputDeviceId
                        ?? "Unknown input device";

                    _disconnectedInputDeviceName = removedDeviceName;
                    _originalInputDeviceId = _config.InputDeviceId;

                    Log.Warning($"Input device '{removedDeviceName}' disconnected. Engine entering DeviceDisconnected state.");
                }

                // Update engine status — data processing continues, only hardware stream stops
                _engineStatusValue = (int)EngineStatus.DeviceDisconnected;

                // Stop only the hardware stream, NOT the engine logic
                // _isRunning remains 1 so Send() / Receives() continue to operate
                if (_backend == AudioEngineBackend.PortAudio)
                    StopPortAudio();
                else
                    StopMiniAudio();

                // Short delay to let the OS fully release the device handle
                await Task.Delay(300);

                // Fire the state-changed event so the UI can update
                var deviceInfo = new AudioDeviceInfo(
                    deviceId: isOutputDevice ? (_lastActiveOutputDeviceId ?? "unknown") : (_lastActiveInputDeviceId ?? "unknown"),
                    name: removedDeviceName,
                    engineName: _backend.ToString(),
                    isInput: !isOutputDevice,
                    isOutput: isOutputDevice,
                    isDefault: false,
                    state: AudioDeviceState.Unplugged);

                DeviceStateChanged?.Invoke(this, new AudioDeviceStateChangedEventArgs(
                    deviceInfo.DeviceId,
                    AudioDeviceState.Unplugged,
                    deviceInfo));

                Log.Info($"Engine status: DeviceDisconnected. Monitoring for '{(isOutputDevice ? _disconnectedOutputDeviceName : _disconnectedInputDeviceName)}' to reconnect...");
            }
            catch (Exception ex)
            {
                Log.Error($"Error handling device removal: {ex.Message}");
                _isDeviceDisconnected = 0;
                _engineStatusValue = (int)EngineStatus.Error;
            }
        }

        /// <summary>
        /// Handles a previously disconnected audio device reconnecting.
        /// Reinitializes the hardware stream and resumes playback/recording from
        /// the current ring buffer position without interrupting data processing.
        /// </summary>
        /// <param name="isOutputDevice">True if the output device reconnected; false for input.</param>
        /// <param name="reconnectedDevice">Information about the reconnected device.</param>
        private async Task HandleDeviceReconnected(bool isOutputDevice, AudioDeviceInfo reconnectedDevice)
        {
            try
            {
                Log.Info($"Reconnecting {(isOutputDevice ? "output" : "input")} device '{reconnectedDevice.Name}'...");

                // Short delay to let the OS finish registering the device
                await Task.Delay(500);

                // Restore original device selection in config
                if (isOutputDevice)
                {
                    // Use the reconnected device's ID (may differ from original e.g. after OS remapping)
                    _config.OutputDeviceId = reconnectedDevice.DeviceId;

                    if (_backend == AudioEngineBackend.PortAudio)
                    {
                        if (int.TryParse(reconnectedDevice.DeviceId, out int paIdx))
                            _activeOutputDeviceIndex = paIdx;
                    }

                    _lastActiveOutputDeviceId = reconnectedDevice.DeviceId;
                    _lastRawDeviceCount = GetRawDeviceCount();
                }
                else
                {
                    _config.InputDeviceId = reconnectedDevice.DeviceId;

                    if (_backend == AudioEngineBackend.PortAudio)
                    {
                        if (int.TryParse(reconnectedDevice.DeviceId, out int paIdx))
                            _activeInputDeviceIndex = paIdx;
                    }

                    _lastActiveInputDeviceId = reconnectedDevice.DeviceId;
                    _lastRawDeviceCount = GetRawDeviceCount();
                }

                // Reinitialize the hardware stream.
                // Ring buffers are reused as long as capacity is unchanged, preserving accumulated data.
                int result = _backend == AudioEngineBackend.PortAudio
                    ? ReinitializePortAudioStream()
                    : ReinitializeMiniAudioDevice();

                if (result != 0)
                {
                    Log.Error($"Failed to reinitialize hardware stream after reconnect: {result}");
                    _engineStatusValue = (int)EngineStatus.Error;
                    return;
                }

                // Start hardware stream
                result = _backend == AudioEngineBackend.PortAudio
                    ? StartPortAudio()
                    : StartMiniAudio();

                if (result != 0)
                {
                    Log.Error($"Failed to start hardware stream after reconnect: {result}");
                    _engineStatusValue = (int)EngineStatus.Error;
                    return;
                }

                // Enable pre-buffering so existing ring buffer data is drained smoothly
                _isBuffering = 1;

                // Clear disconnect state and restore Running status
                _isDeviceDisconnected = 0;
                _disconnectedOutputDeviceName = null;
                _disconnectedInputDeviceName = null;
                _originalOutputDeviceId = null;
                _originalInputDeviceId = null;
                _engineStatusValue = (int)EngineStatus.Running;

                Log.Info($"Device '{reconnectedDevice.Name}' reconnected. Playback resumed.");

                // Fire reconnect event
                DeviceReconnected?.Invoke(this, new AudioDeviceReconnectedEventArgs(
                    reconnectedDevice.DeviceId,
                    reconnectedDevice.Name,
                    isOutputDevice,
                    reconnectedDevice));
            }
            catch (Exception ex)
            {
                Log.Error($"Error handling device reconnect: {ex.Message}");
                _isDeviceDisconnected = 0;
                _engineStatusValue = (int)EngineStatus.Error;
            }
        }

        #endregion

        #region Start/Stop

        /// <summary>
        /// Starts audio processing.
        /// </summary>
        /// <returns>0 on success, negative value on error.</returns>
        public int Start()
        {
            if (Interlocked.CompareExchange(ref _isRunning, 1, 0) == 1)
                return 0; // Already running

            // Enable pre-buffering to prevent playback until buffer has enough data
            _isBuffering = 1;

            // NOTE: Device monitoring is intentionally NOT paused during normal Start().
            // The monitoring loop must stay active so it can detect device disconnections.
            // Previously this called PauseDeviceMonitoring(), which prevented hot-plug detection.

            int result = _backend == AudioEngineBackend.PortAudio
                ? StartPortAudio()
                : StartMiniAudio();

            if (result != 0)
            {
                _isRunning = 0;
                _isBuffering = 0;
                _engineStatusValue = (int)EngineStatus.Error;
            }
            else
            {
                _engineStatusValue = (int)EngineStatus.Running;
            }

            return result;
        }

        /// <summary>
        /// Stops audio processing.
        /// </summary>
        /// <returns>0 on success, negative value on error.</returns>
        public int Stop()
        {
            if (Interlocked.CompareExchange(ref _isRunning, 0, 1) == 0)
                return 0; // Already stopped

            _isBuffering = 0;
            _isDeviceDisconnected = 0;
            _engineStatusValue = (int)EngineStatus.Idle;

            int result = _backend == AudioEngineBackend.PortAudio
                ? StopPortAudio()
                : StopMiniAudio();

            _isActive = 0;

            return result;
        }

        #endregion

        #region Audio I/O

        /// <summary>
        /// Sends audio samples to the output buffer for playback.
        /// BLOCKS until all samples are written to the ring buffer.
        /// This provides natural timing for the AudioMixer.
        /// </summary>
        /// <param name="samples">Audio samples to send.</param>
        /// <exception cref="AudioException">Thrown when the engine is not running.</exception>
        public void Send(Span<float> samples)
        {
            if (_isRunning == 0)
                throw new AudioException("Engine not running");

            int totalSamples = samples.Length;
            int written = 0;
            var lastProgressTime = Environment.TickCount64;

            // During device disconnect the hardware callback thread is stopped, so the ring buffer
            // will not be drained. We use a much larger timeout to keep the pipeline alive while
            // waiting for the device to reconnect. Once reconnected, _isDeviceDisconnected is
            // cleared and the callback resumes draining the buffer.
            const long normalTimeoutMs = 1000;
            const long disconnectTimeoutMs = 30_000; // 30 s – enough for a USB device to reappear

            // Write samples in a loop, blocking until all samples are written
            while (written < totalSamples && _isRunning == 1)
            {
                int remainingSamples = totalSamples - written;
                int samplesWritten = _outputRing.Write(samples.Slice(written, remainingSamples));

                if (samplesWritten > 0)
                {
                    written += samplesWritten;
                    lastProgressTime = Environment.TickCount64; // Reset timeout on progress
                }
                else
                {
                    long timeoutMs = _isDeviceDisconnected == 1 ? disconnectTimeoutMs : normalTimeoutMs;
                    long currentTime = Environment.TickCount64;

                    if (currentTime - lastProgressTime > timeoutMs)
                    {
                        if (_isDeviceDisconnected == 1)
                            throw new AudioException($"Send timeout: Device has been disconnected for {timeoutMs / 1000}s and has not reconnected.");
                        else
                            throw new AudioException($"Send timeout: No progress for {timeoutMs}ms. Audio thread may have stopped.");
                    }

                    // Buffer is full. Yield CPU time.
                    // When disconnected, the buffer drains as soon as the device reconnects.
                    Thread.Sleep(1);
                }
            }
        }

        /// <summary>
        /// Receives audio samples from the input buffer (recording).
        /// </summary>
        /// <param name="samples">Output array containing received audio samples.</param>
        /// <returns>Number of samples read on success, -1 on error or if input is not enabled.</returns>
        public int Receives(out float[] samples)
        {
            if (_isRunning == 0)
            {
                samples = null!;
                return -1;
            }

            if (!_config.EnableInput)
            {
                samples = null!;
                return -1;
            }

            int sampleCount = _config.BufferSize * _config.Channels;
            samples = new float[sampleCount];

            int samplesRead = _inputRing.Read(samples);
            return samplesRead;
        }

        #endregion

        #region Channel Routing Helpers

        /// <summary>
        /// Routes logical output channels into a physical device output buffer.
        /// The physical buffer has <paramref name="physicalChannels"/> interleaved channels.
        /// The logical (ring buffer) data has <paramref name="logicalChannels"/> channels.
        /// Selected physical channel indices are given by <see cref="AudioConfig.OutputChannelSelectors"/>.
        /// </summary>
        private unsafe void RouteOutputChannels(
            float* physicalOutput,
            Span<float> logicalBuffer,
            int frameCount,
            int physicalChannels,
            int logicalChannels,
            int[] selectors)
        {
            for (int frame = 0; frame < frameCount; frame++)
            {
                // Zero-fill the entire physical frame first
                for (int pc = 0; pc < physicalChannels; pc++)
                    physicalOutput[frame * physicalChannels + pc] = 0f;

                // Copy each logical channel to its designated physical channel
                for (int lc = 0; lc < logicalChannels && lc < selectors.Length; lc++)
                {
                    int physCh = selectors[lc];
                    if (physCh < physicalChannels)
                        physicalOutput[frame * physicalChannels + physCh] = logicalBuffer[frame * logicalChannels + lc];
                }
            }
        }

        /// <summary>
        /// Routes a physical device input buffer's selected channels into a logical ring buffer.
        /// </summary>
        private unsafe void RouteInputChannels(
            void* physicalInput,
            Span<float> logicalBuffer,
            int frameCount,
            int physicalChannels,
            int logicalChannels,
            int[] selectors)
        {
            float* pIn = (float*)physicalInput;
            for (int frame = 0; frame < frameCount; frame++)
            {
                for (int lc = 0; lc < logicalChannels && lc < selectors.Length; lc++)
                {
                    int physCh = selectors[lc];
                    logicalBuffer[frame * logicalChannels + lc] = (physCh < physicalChannels)
                        ? pIn[frame * physicalChannels + physCh]
                        : 0f;
                }
            }
        }

        #endregion

        #region Status Methods

        /// <summary>
        /// Gets the activation state of the audio engine.
        /// </summary>
        /// <returns>
        /// 1 = running (including DeviceDisconnected – data processing continues),
        /// 0 = idle / stopped,
        /// -1 = error.
        /// Use <see cref="Status"/> for detailed state including <see cref="EngineStatus.DeviceDisconnected"/>.
        /// </returns>
        public int OwnAudioEngineActivate()
        {
            return _isRunning;
        }

        /// <summary>
        /// Checks if the audio engine is stopped.
        /// </summary>
        /// <returns>1 if stopped, 0 if running.</returns>
        public int OwnAudioEngineStopped()
        {
            return _isRunning == 0 ? 1 : 0;
        }

        /// <summary>
        /// Gets the native stream or device handle.
        /// </summary>
        /// <returns>Pointer to PortAudio stream or MiniAudio device.</returns>
        public IntPtr GetStream()
        {
            return _backend == AudioEngineBackend.PortAudio ? _paStream : _maDevice;
        }

        #endregion

        #region Device Enumeration

        /// <summary>
        /// Gets a list of available output (playback) devices.
        /// </summary>
        /// <returns>List of output device information.</returns>
        public List<AudioDeviceInfo> GetOutputDevices()
        {
            return _backend == AudioEngineBackend.PortAudio
                ? GetPortAudioOutputDevices()
                : GetMiniAudioOutputDevices();
        }

        /// <summary>
        /// Gets a list of available input (recording) devices.
        /// </summary>
        /// <returns>List of input device information.</returns>
        public List<AudioDeviceInfo> GetInputDevices()
        {
            return _backend == AudioEngineBackend.PortAudio
                ? GetPortAudioInputDevices()
                : GetMiniAudioInputDevices();
        }

        #endregion

        #region Device Selection

        /// <summary>
        /// Sets the output device by name.
        /// </summary>
        /// <param name="deviceName">The name of the device to use.</param>
        /// <returns>0 on success, -1 if not implemented or error.</returns>
        public int SetOutputDeviceByName(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
                return -1;

            if (_isRunning == 1)
                return -2; // Cannot change device while running

            var devices = GetOutputDevices();
            foreach (var device in devices)
            {
                if (device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    // Store the selected device ID in config
                    _config.OutputDeviceId = device.DeviceId;

                    // Handle backend-specific reinitialization
                    if (_backend == AudioEngineBackend.PortAudio)
                    {
                        if (int.TryParse(device.DeviceId, out int deviceIndex))
                        {
                            _activeOutputDeviceIndex = deviceIndex;
                            return ReinitializePortAudioStream();
                        }
                    }
                    else // MiniAudio
                    {
                        return ReinitializeMiniAudioDevice();
                    }
                }
            }

            return -3; // Device not found
        }

        /// <summary>
        /// Sets the output device by index.
        /// </summary>
        /// <param name="deviceIndex">The index of the device to use.</param>
        /// <returns>0 on success, -1 if not implemented or error.</returns>
        public int SetOutputDeviceByIndex(int deviceIndex)
        {
            if (deviceIndex < 0)
                return -1;

            if (_isRunning == 1)
                return -2; // Cannot change device while running

            var devices = GetOutputDevices();
            if (deviceIndex >= devices.Count)
                return -3; // Index out of range

            // Store the selected device ID in config
            _config.OutputDeviceId = devices[deviceIndex].DeviceId;

            // Handle backend-specific reinitialization
            if (_backend == AudioEngineBackend.PortAudio)
            {
                if (int.TryParse(devices[deviceIndex].DeviceId, out int actualPaIndex))
                {
                    _activeOutputDeviceIndex = actualPaIndex;
                    return ReinitializePortAudioStream();
                }
                return -3; // Should not happen if DeviceId is valid
            }
            else // MiniAudio
            {
                return ReinitializeMiniAudioDevice();
            }
        }

        /// <summary>
        /// Sets the input device by name.
        /// </summary>
        /// <param name="deviceName">The name of the device to use.</param>
        /// <returns>0 on success, -1 if not implemented or error.</returns>
        public int SetInputDeviceByName(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
                return -1;

            if (_isRunning == 1)
                return -2; // Cannot change device while running

            var devices = GetInputDevices();
            foreach (var device in devices)
            {
                if (device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    // Store the selected device ID in config
                    _config.InputDeviceId = device.DeviceId;

                    // Handle backend-specific reinitialization
                    if (_backend == AudioEngineBackend.PortAudio)
                    {
                        if (int.TryParse(device.DeviceId, out int deviceIdx))
                        {
                            _activeInputDeviceIndex = deviceIdx;
                            return ReinitializePortAudioStream();
                        }
                    }
                    else // MiniAudio
                    {
                        return ReinitializeMiniAudioDevice();
                    }
                }
            }

            return -3; // Device not found
        }

        /// <summary>
        /// Sets the input device by index.
        /// </summary>
        /// <param name="deviceIndex">The index of the device to use.</param>
        /// <returns>0 on success, -1 if not implemented or error.</returns>
        public int SetInputDeviceByIndex(int deviceIndex)
        {
            if (deviceIndex < 0)
                return -1;

            if (_isRunning == 1)
                return -2; // Cannot change device while running

            var devices = GetInputDevices();
            if (deviceIndex >= devices.Count)
                return -3; // Index out of range

            // Store the selected device ID in config
            _config.InputDeviceId = devices[deviceIndex].DeviceId;

            // Handle backend-specific reinitialization
            if (_backend == AudioEngineBackend.PortAudio)
            {
                if (int.TryParse(devices[deviceIndex].DeviceId, out int actualPaIndex))
                {
                    _activeInputDeviceIndex = actualPaIndex;
                    return ReinitializePortAudioStream();
                }
                return -3; // Should not happen if DeviceId is valid
            }
            else // MiniAudio
            {
                return ReinitializeMiniAudioDevice();
            }
        }

        #endregion

        #region Dispose

        /// <summary>
        /// Disposes the audio engine and releases all resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            // Stop device monitoring first
            StopDeviceMonitor();

            Stop();

            if (_backend == AudioEngineBackend.PortAudio)
            {
                DisposePortAudio();
            }
            else if (_backend == AudioEngineBackend.MiniAudio)
            {
                DisposeMiniAudio();
            }

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        #endregion

        #region Backend Enum

        /// <summary>
        /// Specifies the audio engine backend type.
        /// </summary>
        private enum AudioEngineBackend
        {
            /// <summary>
            /// PortAudio backend.
            /// </summary>
            PortAudio,

            /// <summary>
            /// MiniAudio backend.
            /// </summary>
            MiniAudio
        }

        #endregion
    }
}
