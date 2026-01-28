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

        #endregion

        #region Events

        /// <summary>
        /// Raised when the output device changes.
        /// </summary>
#pragma warning disable CS0067 // Event is never used
        public event EventHandler<AudioDeviceChangedEventArgs>? OutputDeviceChanged;

        /// <summary>
        /// Raised when the input device changes.
        /// </summary>
        public event EventHandler<AudioDeviceChangedEventArgs>? InputDeviceChanged;

        /// <summary>
        /// Raised when the device state changes.
        /// </summary>
        public event EventHandler<AudioDeviceStateChangedEventArgs>? DeviceStateChanged;
#pragma warning restore CS0067

        #endregion

        #region Device Monitoring Fields

        /// <summary>
        /// Device monitoring interval in milliseconds.
        /// </summary>
        private const int DeviceMonitorIntervalMs = 1000;

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

        #endregion

        #region Properties

        /// <summary>
        /// Gets the number of frames per audio buffer.
        /// </summary>
        public int FramesPerBuffer => _framesPerBuffer;

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
            // Set prebuffer threshold to 2x buffer size (in samples)
            _prebufferThreshold = config.BufferSize * config.Channels * 2;

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
        /// </summary>
        private async Task DeviceMonitorLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(DeviceMonitorIntervalMs, ct);
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
        /// Checks for device changes and handles device removal.
        /// Uses lightweight counting first to avoid expensive full enumeration.
        /// </summary>
        private void CheckForDeviceChanges()
        {
            try
            {
                // Only monitor if engine is running
                if (_isRunning == 0)
                    return;

                // Lightweight check: First check if the number of devices has changed
                int currentRawCount = GetRawDeviceCount();
                if (currentRawCount == _lastRawDeviceCount)
                {
                    // No change in device count, assume no changes to avoid expensive enumeration/probing
                    return;
                }

                // If count changed, update the last known count and proceed with full verification
                _lastRawDeviceCount = currentRawCount;
                Log.Info($"Device count changed to {currentRawCount}. Performing full device enumeration.");

                // Get current device lists
                var currentOutputDevices = GetOutputDevices();
                var currentInputDevices = _config.EnableInput ? GetInputDevices() : null;

                // Check if active output device was removed
                if (_lastActiveOutputDeviceId != null)
                {
                    var activeOutputExists = currentOutputDevices?.Any(d => d.DeviceId == _lastActiveOutputDeviceId) ?? false;

                    if (!activeOutputExists)
                    {
                        Log.Warning($"Active output device removed (ID: {_lastActiveOutputDeviceId})");
                        _ = HandleDeviceRemoved(true); // Fire and forget
                        return; // Exit early, HandleDeviceRemoved will reinitialize
                    }
                }

                // Check if active input device was removed
                if (_config.EnableInput && _lastActiveInputDeviceId != null)
                {
                    var activeInputExists = currentInputDevices?.Any(d => d.DeviceId == _lastActiveInputDeviceId) ?? false;

                    if (!activeInputExists)
                    {
                        Log.Warning($"Active input device removed (ID: {_lastActiveInputDeviceId})");
                        _ = HandleDeviceRemoved(false); // Fire and forget
                        return; // Exit early, HandleDeviceRemoved will reinitialize
                    }
                }

                // Check if default device changed (only if we're using default)
                if (_config.OutputDeviceId == null)
                {
                    var currentDefault = currentOutputDevices?.Find(d => d.IsDefault);
                    if (currentDefault != null && currentDefault.DeviceId != _lastActiveOutputDeviceId)
                    {
                        Log.Info($"Default output device changed to: {currentDefault.Name}");
                        _ = HandleDeviceRemoved(true); // Fire and forget - reinitialize with new default
                        return;
                    }
                }

                // Update last known device lists
                _lastKnownOutputDevices = currentOutputDevices;
                _lastKnownInputDevices = currentInputDevices;
            }
            catch (Exception ex)
            {
                Log.Error($"Error checking for device changes: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles device removal by reinitializing the engine with the default device.
        /// </summary>
        private async Task HandleDeviceRemoved(bool isOutputDevice)
        {
            try
            {
                Log.Warning("Device removed, switching to default device...");

                // Stop the engine safely
                Stop();

                // Wait a bit for the device to be fully released
                await Task.Delay(500);

                // Modify config to use default device
                if (isOutputDevice)
                {
                    _config.OutputDeviceId = null; // Use default
                    _activeOutputDeviceIndex = -1;
                }
                else
                {
                    _config.InputDeviceId = null; // Use default
                    _activeInputDeviceIndex = -1;
                }

                // Reinitialize the engine
                int result = _backend == AudioEngineBackend.PortAudio
                    ? InitializePortAudio()
                    : InitializeMiniAudio();

                if (result != 0)
                {
                    Log.Error($"Failed to reinitialize engine after device removal: {result}");
                    return;
                }

                // Restart the engine
                result = Start();
                if (result != 0)
                {
                    Log.Error($"Failed to restart engine after device removal: {result}");
                    return;
                }

                Log.Info("Successfully switched to default device");
            }
            catch (Exception ex)
            {
                Log.Error($"Error handling device removal: {ex.Message}");
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

            int result = _backend == AudioEngineBackend.PortAudio
                ? StartPortAudio()
                : StartMiniAudio();

            if (result != 0)
            {
                _isRunning = 0;
                _isBuffering = 0;
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
            const int timeoutMs = 1000; // Increased timeout to 1s to be safe

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
                    // Check for timeout (every ~15ms is enough resolution)
                    long currentTime = Environment.TickCount64;
                    if (currentTime - lastProgressTime > timeoutMs)
                    {
                        throw new AudioException($"Send timeout: No progress for {timeoutMs}ms. Audio thread may have stopped.");
                    }

                    // Buffer is full. Wait for the consumer (audio hardware) to play some samples.
                    // Using Thread.Sleep(1) yields the CPU and prevents thread starvation,
                    // which is crucial for the high-priority audio callback thread to run smoothly.
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

        #region Status Methods

        /// <summary>
        /// Gets the activation state of the audio engine.
        /// </summary>
        /// <returns>0 = idle, 1 = active, -1 = error.</returns>
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
