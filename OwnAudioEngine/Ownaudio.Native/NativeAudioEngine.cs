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

        private volatile int _inputDiagLogged;   // one-time log flags for input diagnostics
        private volatile int _inputSilenceLogged;

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

        /// <summary>
        /// Indicates that the engine switched to a fallback (default) device after the
        /// configured device disconnected. The engine continues running on the fallback
        /// device while monitoring for the original device to return.
        /// 0 = not in fallback mode, 1 = using fallback device.
        /// </summary>
        private volatile int _isFallbackActive;

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

            _prebufferThreshold = config.BufferSize * _physicalOutputChannels * 2;
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

            // PortAudio can return negative error codes (e.g. -9998 paInvalidDevice) on macOS
            // when input and output are separate Core Audio devices (speaker vs microphone).
            // Fall back to MiniAudio which handles duplex on separate devices correctly.
            if (result < 0 && _backend == AudioEngineBackend.PortAudio)
            {
                Log.Warning($"PortAudio initialization returned error {result}. Falling back to MiniAudio...");
                DisposePortAudio();
                _portAudioLoader = null;
                _backend = AudioEngineBackend.MiniAudio;
                MaBinding.EnsureInitialized();
                result = InitializeMiniAudio();
            }

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
                Log.Warning($"PortAudio not found: {ex.Message}");
                Log.Warning("Falling back to MiniAudio (bundled with application)");
                _portAudioLoader?.Dispose();
                _portAudioLoader = null;
            }
            catch (Exception ex)
            {
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
                StopDeviceMonitor();

                _lastKnownOutputDevices = GetOutputDevices();
                _lastKnownInputDevices = _config.EnableInput ? GetInputDevices() : null;
                _lastRawDeviceCount = GetRawDeviceCount();

                if (_backend == AudioEngineBackend.PortAudio)
                {
                    var outputDevices = _lastKnownOutputDevices;
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
                    var defaultOutput = _lastKnownOutputDevices?.Find(d => d.IsDefault);
                    _lastActiveOutputDeviceId = defaultOutput?.DeviceId;

                    if (_config.EnableInput)
                    {
                        var defaultInput = _lastKnownInputDevices?.Find(d => d.IsDefault);
                        _lastActiveInputDeviceId = defaultInput?.DeviceId;
                    }
                }
                
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
                    int delay = _isDeviceDisconnected == 1
                        ? DeviceReconnectPollIntervalMs
                        : DeviceMonitorIntervalMs;

                    await Task.Delay(delay, ct);

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
        /// to reappear. If fallback mode is active it triggers a switch back to the
        /// original device as soon as it reappears in the device list.
        /// Uses lightweight counting first to avoid expensive full enumeration.
        /// </summary>
        private void CheckForDeviceChanges()
        {
            try
            {
                if (_isRunning == 0)
                    return;

                if (_isFallbackActive == 1)
                {
                    int currentCount = GetRawDeviceCount();
                    if (currentCount == _lastRawDeviceCount)
                        return;

                    _lastRawDeviceCount = currentCount;

                    if (_disconnectedOutputDeviceName != null)
                    {
                        var currentOutputDevices = GetOutputDevices();
                        var original = currentOutputDevices?.Find(d =>
                            d.Name.Equals(_disconnectedOutputDeviceName, StringComparison.OrdinalIgnoreCase));

                        if (original != null)
                        {
                            Log.Info($"Original output device '{_disconnectedOutputDeviceName}' returned. Switching back...");
                            _ = HandleSwitchBackToOriginal(true, original);
                            return;
                        }
                    }

                    if (_disconnectedInputDeviceName != null)
                    {
                        var currentInputDevices = GetInputDevices();
                        var original = currentInputDevices?.Find(d =>
                            d.Name.Equals(_disconnectedInputDeviceName, StringComparison.OrdinalIgnoreCase));

                        if (original != null)
                        {
                            Log.Info($"Original input device '{_disconnectedInputDeviceName}' returned. Switching back...");
                            _ = HandleSwitchBackToOriginal(false, original);
                            return;
                        }
                    }

                    return;
                }

                if (_isDeviceDisconnected == 1)
                {
                    var currentOutputDevices = GetOutputDevices();
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

                    _lastRawDeviceCount = GetRawDeviceCount();
                    return;
                }

                int currentRawCount = GetRawDeviceCount();
                if (currentRawCount == _lastRawDeviceCount)
                    return; // No change

                _lastRawDeviceCount = currentRawCount;
                Log.Info($"Device count changed to {currentRawCount}. Performing full device enumeration.");

                var outputDevices = GetOutputDevices();
                var inputDevices = _config.EnableInput ? GetInputDevices() : null;

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
        /// When <see cref="AudioConfig.FallbackToDefaultOnDisconnect"/> is true, the engine
        /// immediately switches to the current system default device and continues running.
        /// The original device is monitored for reconnection; when it reappears the engine
        /// switches back automatically.
        /// When <see cref="AudioConfig.FallbackToDefaultOnDisconnect"/> is false, the engine
        /// enters <see cref="EngineStatus.DeviceDisconnected"/> state and waits for the
        /// original device to return before resuming.
        /// </summary>
        /// <param name="isOutputDevice">
        /// True if the output (playback) device was removed; false for the input device.
        /// </param>
        private async Task HandleDeviceRemoved(bool isOutputDevice)
        {
            _isFallbackActive = 0;

            if (Interlocked.CompareExchange(ref _isDeviceDisconnected, 1, 0) != 0)
                return;

            try
            {
                string removedDeviceName;

                if (isOutputDevice)
                {
                    removedDeviceName = _lastKnownOutputDevices
                        ?.Find(d => d.DeviceId == _lastActiveOutputDeviceId)?.Name
                        ?? _lastActiveOutputDeviceId
                        ?? "Unknown output device";

                    _disconnectedOutputDeviceName = removedDeviceName;
                    _originalOutputDeviceId = _config.OutputDeviceId;

                    Log.Warning($"Output device '{removedDeviceName}' disconnected.");
                }
                else
                {
                    removedDeviceName = _lastKnownInputDevices
                        ?.Find(d => d.DeviceId == _lastActiveInputDeviceId)?.Name
                        ?? _lastActiveInputDeviceId
                        ?? "Unknown input device";

                    _disconnectedInputDeviceName = removedDeviceName;
                    _originalInputDeviceId = _config.InputDeviceId;

                    Log.Warning($"Input device '{removedDeviceName}' disconnected.");
                }

                if (_backend == AudioEngineBackend.PortAudio)
                    StopPortAudio();
                else
                    StopMiniAudio();

                await Task.Delay(300);

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

                if (_config.FallbackToDefaultOnDisconnect)
                {
                    var currentDevices = isOutputDevice ? GetOutputDevices() : GetInputDevices();
                    var defaultDevice = currentDevices?.Find(d => d.IsDefault);
                    string? activeId = isOutputDevice ? _lastActiveOutputDeviceId : _lastActiveInputDeviceId;

                    if (defaultDevice != null && defaultDevice.DeviceId != activeId)
                    {
                        Log.Info($"Attempting fallback to default device '{defaultDevice.Name}'...");
                        bool switched = await SwitchToFallbackDevice(isOutputDevice, defaultDevice);
                        if (switched)
                            return;
                    }
                }

                _engineStatusValue = (int)EngineStatus.DeviceDisconnected;
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

                await Task.Delay(500);
                
                if (isOutputDevice)
                {
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

                int result = _backend == AudioEngineBackend.PortAudio
                    ? ReinitializePortAudioStream()
                    : ReinitializeMiniAudioDevice();

                if (result != 0)
                {
                    Log.Error($"Failed to reinitialize hardware stream after reconnect: {result}");
                    _engineStatusValue = (int)EngineStatus.Error;
                    return;
                }

                result = _backend == AudioEngineBackend.PortAudio
                    ? StartPortAudio()
                    : StartMiniAudio();

                if (result != 0)
                {
                    Log.Error($"Failed to start hardware stream after reconnect: {result}");
                    _engineStatusValue = (int)EngineStatus.Error;
                    return;
                }

                _isBuffering = 1;

                _isDeviceDisconnected = 0;
                _disconnectedOutputDeviceName = null;
                _disconnectedInputDeviceName = null;
                _originalOutputDeviceId = null;
                _originalInputDeviceId = null;
                _engineStatusValue = (int)EngineStatus.Running;

                Log.Info($"Device '{reconnectedDevice.Name}' reconnected. Playback resumed.");

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

        /// <summary>
        /// Switches the audio stream to the specified fallback device after the configured
        /// device has been disconnected. The engine continues running without interruption.
        /// The original device name is preserved in <see cref="_disconnectedOutputDeviceName"/>
        /// or <see cref="_disconnectedInputDeviceName"/> so the monitoring loop can detect
        /// when it returns and trigger a switch back.
        /// </summary>
        /// <param name="isOutputDevice">
        /// True to switch the output (playback) stream; false for the input stream.
        /// </param>
        /// <param name="fallbackDevice">
        /// The device to switch to, typically the current system default.
        /// </param>
        /// <returns>
        /// True if the switch succeeded and the engine is running on the fallback device;
        /// false if the switch failed and the caller should enter DeviceDisconnected state.
        /// </returns>
        private async Task<bool> SwitchToFallbackDevice(bool isOutputDevice, AudioDeviceInfo fallbackDevice)
        {
            try
            {
                await Task.Delay(200);

                string? previousOutputId = _config.OutputDeviceId;
                string? previousInputId = _config.InputDeviceId;

                if (isOutputDevice)
                {
                    _config.OutputDeviceId = null;
                    if (_backend == AudioEngineBackend.PortAudio)
                        _activeOutputDeviceIndex = Pa_GetDefaultOutputDevice();
                }
                else
                {
                    _config.InputDeviceId = null;
                    if (_backend == AudioEngineBackend.PortAudio)
                        _activeInputDeviceIndex = Pa_GetDefaultInputDevice();
                }

                int result = _backend == AudioEngineBackend.PortAudio
                    ? ReinitializePortAudioStream()
                    : ReinitializeMiniAudioDevice();

                if (result != 0)
                {
                    Log.Error($"Fallback device init failed: {result}. Restoring original config.");
                    _config.OutputDeviceId = previousOutputId;
                    _config.InputDeviceId = previousInputId;
                    return false;
                }

                result = _backend == AudioEngineBackend.PortAudio
                    ? StartPortAudio()
                    : StartMiniAudio();

                if (result != 0)
                {
                    Log.Error($"Fallback device start failed: {result}. Restoring original config.");
                    _config.OutputDeviceId = previousOutputId;
                    _config.InputDeviceId = previousInputId;
                    return false;
                }

                _isBuffering = 1;
                _isDeviceDisconnected = 0;
                _isFallbackActive = 1;

                string fallbackId = fallbackDevice.DeviceId;
                string previousDeviceName;

                if (isOutputDevice)
                {
                    previousDeviceName = _disconnectedOutputDeviceName ?? "Unknown";
                    _lastActiveOutputDeviceId = fallbackId;
                }
                else
                {
                    previousDeviceName = _disconnectedInputDeviceName ?? "Unknown";
                    _lastActiveInputDeviceId = fallbackId;
                }

                _lastRawDeviceCount = GetRawDeviceCount();
                _engineStatusValue = (int)EngineStatus.Running;

                Log.Info($"Switched to fallback device '{fallbackDevice.Name}'. Monitoring for '{previousDeviceName}' to return.");

                bool originalDeviceStillPresent = isOutputDevice
                    ? (GetOutputDevices()?.Any(d => d.Name.Equals(_disconnectedOutputDeviceName, StringComparison.OrdinalIgnoreCase)) ?? false)
                    : (GetInputDevices()?.Any(d => d.Name.Equals(_disconnectedInputDeviceName, StringComparison.OrdinalIgnoreCase)) ?? false);

                if (originalDeviceStillPresent)
                {
                    if (isOutputDevice) _disconnectedOutputDeviceName = null;
                    else _disconnectedInputDeviceName = null;
                    _isFallbackActive = 0;
                }

                OutputDeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(
                    previousDeviceName,
                    fallbackDevice.DeviceId,
                    fallbackDevice));

                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Error switching to fallback device: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Handles the return of the original device while the engine is running on a fallback
        /// device. Reinitializes the hardware stream with the original device and resumes
        /// playback or recording on it. Clears the fallback state on success.
        /// </summary>
        /// <param name="isOutputDevice">
        /// True if the original output (playback) device returned; false for the input device.
        /// </param>
        /// <param name="originalDevice">
        /// Information about the original device that has reappeared.
        /// </param>
        private async Task HandleSwitchBackToOriginal(bool isOutputDevice, AudioDeviceInfo originalDevice)
        {
            if (Interlocked.CompareExchange(ref _isFallbackActive, 0, 1) != 1)
                return;

            try
            {
                Log.Info($"Original device '{originalDevice.Name}' returned. Switching back...");

                await Task.Delay(500);

                if (isOutputDevice)
                {
                    _config.OutputDeviceId = originalDevice.DeviceId;
                    if (_backend == AudioEngineBackend.PortAudio && int.TryParse(originalDevice.DeviceId, out int paIdx))
                        _activeOutputDeviceIndex = paIdx;
                    _lastActiveOutputDeviceId = originalDevice.DeviceId;
                }
                else
                {
                    _config.InputDeviceId = originalDevice.DeviceId;
                    if (_backend == AudioEngineBackend.PortAudio && int.TryParse(originalDevice.DeviceId, out int paIdx))
                        _activeInputDeviceIndex = paIdx;
                    _lastActiveInputDeviceId = originalDevice.DeviceId;
                }

                _lastRawDeviceCount = GetRawDeviceCount();

                int result = _backend == AudioEngineBackend.PortAudio
                    ? ReinitializePortAudioStream()
                    : ReinitializeMiniAudioDevice();

                if (result != 0)
                {
                    Log.Error($"Failed to reinitialize original device: {result}");
                    _isFallbackActive = 1;
                    return;
                }

                result = _backend == AudioEngineBackend.PortAudio
                    ? StartPortAudio()
                    : StartMiniAudio();

                if (result != 0)
                {
                    Log.Error($"Failed to start original device: {result}");
                    _isFallbackActive = 1;
                    return;
                }

                _isBuffering = 1;
                _disconnectedOutputDeviceName = null;
                _disconnectedInputDeviceName = null;
                _originalOutputDeviceId = null;
                _originalInputDeviceId = null;
                _engineStatusValue = (int)EngineStatus.Running;

                Log.Info($"Switched back to original device '{originalDevice.Name}'.");

                DeviceReconnected?.Invoke(this, new AudioDeviceReconnectedEventArgs(
                    originalDevice.DeviceId,
                    originalDevice.Name,
                    isOutputDevice,
                    originalDevice));
            }
            catch (Exception ex)
            {
                Log.Error($"Error switching back to original device: {ex.Message}");
                _isFallbackActive = 1;
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

            _isBuffering = 1;
            _inputDiagLogged = 0;
            _inputSilenceLogged = 0;

            // Flush any stale samples left in the ring buffers from the previous session.
            // The device is stopped at this point so no concurrent callback access occurs.
            _outputRing?.Clear();
            _inputRing?.Clear();

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
            _isFallbackActive = 0;
            _disconnectedOutputDeviceName = null;
            _disconnectedInputDeviceName = null;
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

            const long normalTimeoutMs = 1000;
            const long disconnectTimeoutMs = 30_000; // 30 s – enough for a USB device to reappear

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

                    Thread.Sleep(1);
                }
            }
        }

        /// <summary>
        /// Receives audio samples from the input buffer into a caller-provided span.
        /// Zero-allocation: reads directly into the destination without any heap allocation.
        /// </summary>
        /// <param name="destination">Caller-allocated span to write captured samples into.</param>
        /// <returns>Number of samples written on success, -1 on error or if input is not enabled.</returns>
        public int Receives(Span<float> destination)
        {
            if (_isRunning == 0 || !_config.EnableInput)
                return -1;

            return _inputRing.Read(destination);
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
                for (int pc = 0; pc < physicalChannels; pc++)
                    physicalOutput[frame * physicalChannels + pc] = 0f;

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
                    _config.OutputDeviceId = device.DeviceId;

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

            _config.OutputDeviceId = devices[deviceIndex].DeviceId;

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
                    _config.InputDeviceId = device.DeviceId;

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

            _config.InputDeviceId = devices[deviceIndex].DeviceId;

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
