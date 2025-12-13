using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using Ownaudio.Windows.Interop;

namespace Ownaudio.Windows
{
    /// <summary>
    /// Windows WASAPI audio engine implementation.
    /// Zero-allocation, real-time safe audio processing with COM interop.
    /// </summary>
    public sealed class WasapiEngine : IAudioEngine
    {
        // COM interfaces - pinned for entire lifetime
        private IMMDeviceEnumerator _deviceEnumerator;
        private IMMDevice _outputDevice;
        private IMMDevice _inputDevice;
        private IAudioClient _audioClient;
        private IAudioClient _inputAudioClient;
        private IAudioRenderClient _renderClient;
        private IAudioCaptureClient _captureClient;

        // Device notification
        private WasapiDeviceNotificationClient _notificationClient;
        private IMMDeviceEnumeratorExtensions.IMMDeviceEnumeratorWithNotifications _deviceEnumeratorWithNotifications;

        // Pre-allocated buffers (pinned)
        private float[] _outputBuffer;
        private float[] _inputBuffer;
        private GCHandle _outputBufferHandle;
        private GCHandle _inputBufferHandle;
        private IntPtr _outputBufferPtr;
        private IntPtr _inputBufferPtr;

        // Ring buffers for thread-safe data exchange
        private LockFreeRingBuffer<float> _outputRing;
        private LockFreeRingBuffer<float> _inputRing;

        // Configuration
        private AudioConfig _config;
        private int _framesPerBuffer;
        private int _samplesPerBuffer;
        private IntPtr _audioClientPtr;

        // State management (atomic operations)
        private volatile int _isRunning; // 0 = stopped, 1 = running
        private volatile int _isActive;  // 0 = idle, 1 = active, -1 = error
        private volatile int _errorCode;

        // Debug/monitoring counters
        private volatile int _underrunCount;
        private volatile int _lastRingBufferLevel;

        // Threading
        private Thread _audioThread;
        private IntPtr _stopEvent;
        private readonly object _stateLock = new object();

        // Buffer pool for Receives()
        private AudioBufferPool _bufferPool;

        // Device enumeration
        private WasapiDeviceEnumerator _deviceEnumHelper;
        private string _currentOutputDeviceId;
        private string _currentInputDeviceId;

        // Constants
        private const int SAMPLES_TO_BYTES_MULTIPLIER = 4; // float = 4 bytes
        private const int MAX_RETRIES = 3;

        // Events
        /// <summary>
        /// Occurs when the default output device changes.
        /// </summary>
        public event EventHandler<AudioDeviceChangedEventArgs> OutputDeviceChanged;
        /// <summary>
        /// Occurs when the default input device changes.
        /// </summary>
        public event EventHandler<AudioDeviceChangedEventArgs> InputDeviceChanged;
        /// <summary>
        /// Occurs when an audio device's state changes (e.g., unplugged).
        /// </summary>
        public event EventHandler<AudioDeviceStateChangedEventArgs> DeviceStateChanged;

        /// <summary>
        /// Gets the number of audio frames per buffer provided by the WASAPI client.
        /// </summary>
        public int FramesPerBuffer => _framesPerBuffer;

        /// <summary>
        /// Creates a new WASAPI engine instance.
        /// </summary>
        public WasapiEngine()
        {
            _isRunning = 0;
            _isActive = 0;
            _errorCode = 0;
            _deviceEnumHelper = new WasapiDeviceEnumerator();
        }

        /// <summary>
        /// Initializes the WASAPI engine with the specified configuration.
        /// </summary>
        /// <param name="config">The audio configuration.</param>
        /// <returns>0 on success, or an HRESULT error code otherwise.</returns>
        public int Initialize(AudioConfig config)
        {
            if (config == null || !config.Validate())
                return -1;

            lock (_stateLock)
            {
                try
                {
                    _config = config;

                    // Initialize COM
                    int hr = Ole32.CoInitializeEx(IntPtr.Zero, Ole32.COINIT_MULTITHREADED);
                    if (hr != WasapiInterop.S_OK &&
                        hr != WasapiInterop.S_FALSE && // Already initialized on this thread
                        hr != WasapiInterop.RPC_E_CHANGED_MODE) // Different threading model (STA vs MTA)
                    {
                        return hr;
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
                        return hr;

                    _deviceEnumerator = (IMMDeviceEnumerator)enumeratorObj;

                    // Setup device notification client
                    try
                    {
                        _notificationClient = new WasapiDeviceNotificationClient();
                        _notificationClient.Initialize();

                        // Wire up events
                        _notificationClient.DefaultOutputDeviceChanged += OnDefaultOutputDeviceChanged;
                        _notificationClient.DefaultInputDeviceChanged += OnDefaultInputDeviceChanged;
                        _notificationClient.DeviceStateChanged += OnDeviceStateChanged;

                        // Register for notifications
                        _deviceEnumeratorWithNotifications = (IMMDeviceEnumeratorExtensions.IMMDeviceEnumeratorWithNotifications)enumeratorObj;
                        _deviceEnumeratorWithNotifications.RegisterEndpointNotificationCallback(_notificationClient);
                    }
                    catch
                    {
                        // Device notifications are optional, continue without them
                    }

                    // Initialize output device if enabled
                    if (config.EnableOutput)
                    {
                        hr = InitializeOutputDevice(config);
                        if (hr != WasapiInterop.S_OK)
                            return hr;
                    }

                    // Initialize input device if enabled
                    if (config.EnableInput)
                    {
                        hr = InitializeInputDevice(config);
                        if (hr != WasapiInterop.S_OK)
                            return hr;
                    }

                    // Use output audio client as primary for GetStream()
                    if (_audioClient != null)
                    {
                        _audioClientPtr = Marshal.GetIUnknownForObject(_audioClient);
                    }
                    else if (_inputAudioClient != null)
                    {
                        _audioClientPtr = Marshal.GetIUnknownForObject(_inputAudioClient);
                    }

                    // Get actual buffer size from primary audio client
                    IAudioClient primaryClient = _audioClient ?? _inputAudioClient;
                    if (primaryClient != null)
                    {
                        hr = primaryClient.GetBufferSize(out uint bufferFrameCount);
                        if (hr != WasapiInterop.S_OK)
                            return hr;

                        _framesPerBuffer = (int)bufferFrameCount;
                        _samplesPerBuffer = _framesPerBuffer * config.Channels;
                    }

                    // Allocate and pin buffers
                    _outputBuffer = new float[_samplesPerBuffer];
                    _inputBuffer = new float[_samplesPerBuffer];
                    _outputBufferHandle = GCHandle.Alloc(_outputBuffer, GCHandleType.Pinned);
                    _inputBufferHandle = GCHandle.Alloc(_inputBuffer, GCHandleType.Pinned);
                    _outputBufferPtr = _outputBufferHandle.AddrOfPinnedObject();
                    _inputBufferPtr = _inputBufferHandle.AddrOfPinnedObject();

                    // Create ring buffers (16x buffer size for better buffering and reduced underruns)
                    int ringSize = _samplesPerBuffer * 16;
                    _outputRing = new LockFreeRingBuffer<float>(ringSize);
                    _inputRing = new LockFreeRingBuffer<float>(ringSize);

                    // Create buffer pool for input capture
                    _bufferPool = new AudioBufferPool(_samplesPerBuffer, initialPoolSize: 4, maxPoolSize: 16);

                    // Create stop event
                    _stopEvent = Kernel32.CreateEvent(IntPtr.Zero, true, false, null!);
                    if (_stopEvent == IntPtr.Zero)
                        return -1;

                    _isActive = 0; // Idle state
                    return 0;
                }
                catch (Exception)
                {
                    _errorCode = -1;
                    _isActive = -1;
                    return -1;
                }
            }
        }

        /// <summary>
        /// Starts the WASAPI audio streaming thread.
        /// </summary>
        /// <returns>0 on success, or an HRESULT error code otherwise.</returns>
        public int Start()
        {
            lock (_stateLock)
            {
                if (_isRunning == 1)
                    return 0; // Already running (idempotent)

                if (_audioClient == null)
                    return -1;

                try
                {
                    // CRITICAL: Clear ring buffers to prevent old audio from playing after stop
                    // This ensures that when resuming playback, we don't hear stale audio data
                    _outputRing?.Clear();
                    _inputRing?.Clear();

                    // Start output audio client
                    if (_audioClient != null)
                    {
                        int hr = _audioClient.Start();
                        if (hr != WasapiInterop.S_OK)
                            return hr;
                    }

                    // Start input audio client
                    if (_inputAudioClient != null)
                    {
                        int hr = _inputAudioClient.Start();
                        if (hr != WasapiInterop.S_OK)
                            return hr;
                    }

                    // Reset stop event
                    Kernel32.ResetEvent(_stopEvent);

                    // Start audio thread
                    _isRunning = 1;
                    _isActive = 1;
                    _audioThread = new Thread(AudioThreadProc)
                    {
                        Priority = ThreadPriority.Highest,
                        IsBackground = false,
                        Name = "WASAPI Audio RT Thread"
                    };
                    _audioThread.Start();

                    return 0;
                }
                catch
                {
                    _isActive = -1;
                    return -1;
                }
            }
        }

        /// <summary>
        /// Stops the WASAPI audio streaming thread and resets the audio clients.
        /// </summary>
        /// <returns>0 on success, or an HRESULT error code otherwise.</returns>
        public int Stop()
        {
            lock (_stateLock)
            {
                if (_isRunning == 0)
                    return 0; // Already stopped (idempotent)

                try
                {
                    // Signal stop
                    _isRunning = 0;
                    Kernel32.SetEvent(_stopEvent);

                    // Wait for thread to finish (with timeout)
                    if (_audioThread != null && _audioThread.IsAlive)
                    {
                        if (!_audioThread.Join(2000))
                        {
                            System.Diagnostics.Debug.WriteLine("WASAPI: Audio thread did not stop within 2s");
                            // Note: Thread.Abort() is obsolete and not supported in .NET 5+
                            // The thread should terminate naturally when _isRunning is set to 0
                        }
                        _audioThread = null!;
                    }

                    // Stop output audio client
                    if (_audioClient != null)
                    {
                        _audioClient.Stop();
                        _audioClient.Reset();
                    }

                    // Stop input audio client
                    if (_inputAudioClient != null)
                    {
                        _inputAudioClient.Stop();
                        _inputAudioClient.Reset();
                    }

                    _isActive = 0;
                    return 0;
                }
                catch
                {
                    _isActive = -1;
                    return -1;
                }
            }
        }

        /// <summary>
        /// Audio thread procedure - ZERO ALLOCATION ZONE!
        /// Manages real-time audio processing for output and input.
        /// Uses MMCSS (Multimedia Class Scheduler Service) for guaranteed real-time priority.
        /// </summary>
        private void AudioThreadProc()
        {
            IntPtr mmcssHandle = IntPtr.Zero;
            uint taskIndex = 0;

            try
            {
                // CRITICAL: Set up MMCSS for real-time audio priority
                // This registers the thread with Windows' Multimedia Class Scheduler
                // which provides guaranteed CPU time and prevents priority inversion
                mmcssHandle = Kernel32.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);

                if (mmcssHandle != IntPtr.Zero)
                {
                    // Successfully registered with MMCSS, optionally boost to critical priority
                    Kernel32.AvSetMmThreadPriority(mmcssHandle, Kernel32.AvrtPriority.AVRT_PRIORITY_CRITICAL);
                }
                else
                {
                    // Fallback: Use kernel32 thread priority if MMCSS unavailable
                    IntPtr currentThread = Kernel32.GetCurrentThread();
                    Kernel32.SetThreadPriority(currentThread, Kernel32.ThreadPriorityLevel.THREAD_PRIORITY_TIME_CRITICAL);
                }

                int retryCount = 0;

                while (_isRunning == 1)
                {
                    try
                    {
                        // Check for stop signal (non-blocking check)
                        uint waitResult = Kernel32.WaitForSingleObject(_stopEvent, 0);
                        if (waitResult == Kernel32.WAIT_OBJECT_0)
                            break;

                        // Process output (rendering)
                        if (_config.EnableOutput && _renderClient != null)
                        {
                            ProcessOutput();
                        }

                        // Process input (capture)
                        if (_config.EnableInput && _captureClient != null)
                        {
                            ProcessInput();
                        }

                        // Small sleep to avoid busy-waiting
                        // Calculate sleep time based on buffer size
                        int sleepMs = Math.Max(1, _framesPerBuffer * 1000 / _config.SampleRate / 4);
                        Thread.Sleep(sleepMs);

                        retryCount = 0; // Reset retry counter on success
                    }
                    catch
                    {
                        retryCount++;
                        if (retryCount >= MAX_RETRIES)
                        {
                            _isActive = -1;
                            _errorCode = -1;
                            break;
                        }
                        Thread.Sleep(10); // Brief pause before retry
                    }
                }
            }
            finally
            {
                // CRITICAL: Revert MMCSS characteristics when thread exits
                if (mmcssHandle != IntPtr.Zero)
                {
                    Kernel32.AvRevertMmThreadCharacteristics(mmcssHandle);
                }
            }
        }

        /// <summary>
        /// Process audio output (rendering) from the ring buffer to the WASAPI device.
        /// ZERO ALLOCATION!
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessOutput()
        {
            // Get current padding (how many frames are already in the buffer)
            int hr = _audioClient.GetCurrentPadding(out uint paddingFrames);
            if (hr != WasapiInterop.S_OK)
                return;

            // Calculate available frames
            uint availableFrames = (uint)_framesPerBuffer - paddingFrames;
            if (availableFrames == 0)
                return;

            // Get buffer from WASAPI
            hr = _renderClient.GetBuffer(availableFrames, out IntPtr dataPtr);
            if (hr != WasapiInterop.S_OK)
                return;

            int samplesToWrite = (int)availableFrames * _config.Channels;

            // Monitor ring buffer level
            int currentLevel = _outputRing.AvailableRead;
            _lastRingBufferLevel = currentLevel;

            // Read from ring buffer into our pre-allocated buffer
            int samplesRead = _outputRing.Read(_outputBuffer.AsSpan(0, samplesToWrite));

            // If not enough data, fill with silence
            if (samplesRead < samplesToWrite)
            {
                _underrunCount++;

                // Log underrun every 100 occurrences to avoid spam
                if (_underrunCount % 100 == 1)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[WASAPI] Buffer underrun #{_underrunCount}: needed {samplesToWrite}, got {samplesRead}, ring level: {currentLevel}");
                }

                Array.Clear(_outputBuffer, samplesRead, samplesToWrite - samplesRead);
            }

            // Copy to WASAPI buffer (unsafe fast copy)
            unsafe
            {
                Buffer.MemoryCopy(
                    _outputBufferPtr.ToPointer(),
                    dataPtr.ToPointer(),
                    samplesToWrite * SAMPLES_TO_BYTES_MULTIPLIER,
                    samplesToWrite * SAMPLES_TO_BYTES_MULTIPLIER);
            }

            // Release buffer
            _renderClient.ReleaseBuffer(availableFrames, 0);
        }

        /// <summary>
        /// Process audio input (capture) from the WASAPI device to the ring buffer.
        /// ZERO ALLOCATION!
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessInput()
        {
            if (_captureClient == null)
                return;

            // Get next packet size
            int hr = _captureClient.GetNextPacketSize(out uint packetSize);
            if (hr != WasapiInterop.S_OK || packetSize == 0)
                return;

            // Get buffer from WASAPI
            hr = _captureClient.GetBuffer(
                out IntPtr dataPtr,
                out uint numFramesToRead,
                out uint flags,
                out _,
                out _);

            if (hr != WasapiInterop.S_OK)
                return;

            int samplesToRead = (int)numFramesToRead * _config.Channels;

            // Copy from WASAPI buffer to our pre-allocated buffer
            if ((flags & 2) == 0) // Not silent
            {
                unsafe
                {
                    Buffer.MemoryCopy(
                        dataPtr.ToPointer(),
                        _inputBufferPtr.ToPointer(),
                        samplesToRead * SAMPLES_TO_BYTES_MULTIPLIER,
                        samplesToRead * SAMPLES_TO_BYTES_MULTIPLIER);
                }
            }
            else
            {
                // Silent packet - zero out buffer
                Array.Clear(_inputBuffer, 0, samplesToRead);
            }

            // Write to ring buffer
            _inputRing.Write(_inputBuffer.AsSpan(0, samplesToRead));

            // Release buffer
            _captureClient.ReleaseBuffer(numFramesToRead);
        }

        /// <summary>
        /// Sends audio samples for playback by writing them to the output ring buffer.
        /// This is a BLOCKING CALL that waits until space is available in the buffer.
        /// </summary>
        /// <param name="samples">The span of float samples to send.</param>
        /// <exception cref="AudioException">Thrown if the engine is not running.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Send(Span<float> samples)
        {
            if (_isRunning == 0)
                throw new AudioException("Engine not running");

            // Write to ring buffer, block until space available
            int written = 0;
            int retryCount = 0;
            while (written < samples.Length && _isRunning == 1)
            {
                int result = _outputRing.Write(samples.Slice(written));
                written += result;

                if (result == 0)
                {
                    // Buffer full, use hybrid wait strategy
                    retryCount++;
                    if (retryCount < 10)
                    {
                        // First try spinning for very short waits
                        Thread.SpinWait(1000);
                    }
                    else
                    {
                        // If buffer stays full, yield or sleep to prevent CPU spinning
                        Thread.Sleep(1);
                        retryCount = 0;
                    }
                }
                else
                {
                    retryCount = 0; // Reset on successful write
                }
            }
        }

        /// <summary>
        /// Receives captured audio samples from the input ring buffer.
        /// Uses a buffer pool to minimize allocation.
        /// </summary>
        /// <param name="samples">The allocated array of float samples containing the captured data.</param>
        /// <returns>The number of samples read, or -1 if the engine is not running.</returns>
        public int Receives(out float[] samples)
        {
            if (_isRunning == 0)
            {
                samples = null!;
                return -1;
            }

            // Get buffer from pool
            samples = _bufferPool.Get();

            // Read from ring buffer
            int samplesRead = _inputRing.Read(samples.AsSpan());

            // Clear unused portion
            if (samplesRead < samples.Length)
            {
                Array.Clear(samples, samplesRead, samples.Length - samplesRead);
            }

            return samplesRead;
        }

        /// <summary>
        /// Gets the raw COM pointer to the primary IAudioClient interface.
        /// </summary>
        /// <returns>An <see cref="IntPtr"/> to the IAudioClient COM object.</returns>
        public IntPtr GetStream() => _audioClientPtr;

        /// <summary>
        /// Checks the current activation state of the engine.
        /// </summary>
        /// <returns>1 if active, 0 if idle, -1 if in an error state.</returns>
        public int OwnAudioEngineActivate() => _isActive;

        /// <summary>
        /// Checks if the engine's main audio thread is stopped.
        /// </summary>
        /// <returns>1 if stopped, 0 if running.</returns>
        public int OwnAudioEngineStopped() => _isRunning == 0 ? 1 : 0;

        /// <summary>
        /// Initializes the output device with the specified configuration.
        /// </summary>
        /// <param name="config">Audio configuration.</param>
        /// <returns>HRESULT error code.</returns>
        private int InitializeOutputDevice(AudioConfig config)
        {
            int hr;

            // Get output device (specific device or default)
            if (!string.IsNullOrEmpty(config.OutputDeviceId))
            {
                // Get device by ID
                var enumeratorWithDevice = _deviceEnumerator as IMMDeviceEnumeratorExtensions.IMMDeviceEnumeratorWithNotifications;
                if (enumeratorWithDevice != null)
                {
                    hr = enumeratorWithDevice.GetDevice(config.OutputDeviceId, out _outputDevice);
                }
                else
                {
                    // Fallback to default
                    hr = _deviceEnumerator.GetDefaultAudioEndpoint(
                        WasapiInterop.EDataFlow.eRender,
                        WasapiInterop.ERole.eMultimedia,
                        out _outputDevice);
                }
            }
            else
            {
                hr = _deviceEnumerator.GetDefaultAudioEndpoint(
                    WasapiInterop.EDataFlow.eRender,
                    WasapiInterop.ERole.eMultimedia,
                    out _outputDevice);
            }

            if (hr != WasapiInterop.S_OK)
                return hr;

            // Activate audio client
            Guid audioClientIid = WasapiInterop.IID_IAudioClient;
            hr = _outputDevice.Activate(
                ref audioClientIid,
                Ole32.CLSCTX_ALL,
                IntPtr.Zero,
                out object audioClientObj);

            if (hr != WasapiInterop.S_OK)
                return hr;

            _audioClient = (IAudioClient)audioClientObj;

            // Setup wave format (Float32)
            var waveFormat = new WasapiInterop.WAVEFORMATEXTENSIBLE
            {
                Format = new WasapiInterop.WAVEFORMATEX
                {
                    wFormatTag = WasapiInterop.WAVE_FORMAT_EXTENSIBLE,
                    nChannels = (ushort)config.Channels,
                    nSamplesPerSec = (uint)config.SampleRate,
                    wBitsPerSample = 32, // Float32
                    nBlockAlign = (ushort)(config.Channels * 4),
                    nAvgBytesPerSec = (uint)(config.SampleRate * config.Channels * 4),
                    cbSize = 22
                },
                Samples = 32,
                dwChannelMask = config.Channels == 2 ? 3u : 1u, // SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT
                SubFormat = WasapiInterop.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
            };

            // Calculate buffer duration
            long bufferDuration = (long)config.BufferSize * WasapiInterop.REFTIMES_PER_SEC / config.SampleRate;

            // Initialize audio client
            Guid sessionGuid = Guid.Empty;
            hr = _audioClient.Initialize(
                WasapiInterop.AudioClientShareMode.AUDCLNT_SHAREMODE_SHARED,
                WasapiInterop.AudioClientStreamFlags.None,
                bufferDuration,
                0,
                ref waveFormat,
                ref sessionGuid);

            if (hr != WasapiInterop.S_OK)
                return hr;

            // Get render client
            Guid renderClientIid = WasapiInterop.IID_IAudioRenderClient;
            hr = _audioClient.GetService(ref renderClientIid, out object renderClientObj);
            if (hr != WasapiInterop.S_OK)
                return hr;

            _renderClient = (IAudioRenderClient)renderClientObj;
            return WasapiInterop.S_OK;
        }

        /// <summary>
        /// Initializes the input device with the specified configuration.
        /// </summary>
        /// <param name="config">Audio configuration.</param>
        /// <returns>HRESULT error code.</returns>
        private int InitializeInputDevice(AudioConfig config)
        {
            int hr;

            // Get input device (specific device or default)
            if (!string.IsNullOrEmpty(config.InputDeviceId))
            {
                // Get device by ID
                var enumeratorWithDevice = _deviceEnumerator as IMMDeviceEnumeratorExtensions.IMMDeviceEnumeratorWithNotifications;
                if (enumeratorWithDevice != null)
                {
                    hr = enumeratorWithDevice.GetDevice(config.InputDeviceId, out _inputDevice);
                }
                else
                {
                    // Fallback to default
                    hr = _deviceEnumerator.GetDefaultAudioEndpoint(
                        WasapiInterop.EDataFlow.eCapture,
                        WasapiInterop.ERole.eMultimedia,
                        out _inputDevice);
                }
            }
            else
            {
                hr = _deviceEnumerator.GetDefaultAudioEndpoint(
                    WasapiInterop.EDataFlow.eCapture,
                    WasapiInterop.ERole.eMultimedia,
                    out _inputDevice);
            }

            if (hr != WasapiInterop.S_OK)
                return hr;

            // Activate audio client
            Guid audioClientIid = WasapiInterop.IID_IAudioClient;
            hr = _inputDevice.Activate(
                ref audioClientIid,
                Ole32.CLSCTX_ALL,
                IntPtr.Zero,
                out object audioClientObj);

            if (hr != WasapiInterop.S_OK)
                return hr;

            _inputAudioClient = (IAudioClient)audioClientObj;

            // Setup wave format (Float32)
            var waveFormat = new WasapiInterop.WAVEFORMATEXTENSIBLE
            {
                Format = new WasapiInterop.WAVEFORMATEX
                {
                    wFormatTag = WasapiInterop.WAVE_FORMAT_EXTENSIBLE,
                    nChannels = (ushort)config.Channels,
                    nSamplesPerSec = (uint)config.SampleRate,
                    wBitsPerSample = 32, // Float32
                    nBlockAlign = (ushort)(config.Channels * 4),
                    nAvgBytesPerSec = (uint)(config.SampleRate * config.Channels * 4),
                    cbSize = 22
                },
                Samples = 32,
                dwChannelMask = config.Channels == 2 ? 3u : 1u, // SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT
                SubFormat = WasapiInterop.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
            };

            // Calculate buffer duration
            long bufferDuration = (long)config.BufferSize * WasapiInterop.REFTIMES_PER_SEC / config.SampleRate;

            // Initialize audio client
            Guid sessionGuid = Guid.Empty;
            hr = _inputAudioClient.Initialize(
                WasapiInterop.AudioClientShareMode.AUDCLNT_SHAREMODE_SHARED,
                WasapiInterop.AudioClientStreamFlags.None,
                bufferDuration,
                0,
                ref waveFormat,
                ref sessionGuid);

            if (hr != WasapiInterop.S_OK)
                return hr;

            // Get capture client
            Guid captureClientIid = WasapiInterop.IID_IAudioCaptureClient;
            hr = _inputAudioClient.GetService(ref captureClientIid, out object captureClientObj);
            if (hr != WasapiInterop.S_OK)
                return hr;

            _captureClient = (IAudioCaptureClient)captureClientObj;
            return WasapiInterop.S_OK;
        }

        /// <summary>
        /// Gets a list of all available output devices.
        /// </summary>
        /// <returns>A <see cref="System.Collections.Generic.List{AudioDeviceInfo}"/> of output devices.</returns>
        public System.Collections.Generic.List<AudioDeviceInfo> GetOutputDevices()
        {
            return _deviceEnumHelper.EnumerateOutputDevices();
        }

        /// <summary>
        /// Gets a list of all available input devices.
        /// </summary>
        /// <returns>A <see cref="System.Collections.Generic.List{AudioDeviceInfo}"/> of input devices.</returns>
        public System.Collections.Generic.List<AudioDeviceInfo> GetInputDevices()
        {
            return _deviceEnumHelper.EnumerateInputDevices();
        }

        /// <summary>
        /// Changes the output device by device name.
        /// Requires the engine to be stopped.
        /// </summary>
        /// <param name="deviceName">The name of the desired output device.</param>
        /// <returns>0 on success, -1 if the name is null/empty, -2 if the engine is running, -3 if the device is not found.</returns>
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
                    return ReinitializeOutputDevice();
                }
            }

            return -3; // Device not found
        }

        /// <summary>
        /// Changes the output device by index.
        /// Requires the engine to be stopped.
        /// </summary>
        /// <param name="deviceIndex">The index of the desired output device in the list returned by <see cref="GetOutputDevices"/>.</param>
        /// <returns>0 on success, -1 if the index is negative, -2 if the engine is running, -3 if the index is out of range.</returns>
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
            return ReinitializeOutputDevice();
        }

        /// <summary>
        /// Changes the input device by device name.
        /// Requires the engine to be stopped.
        /// </summary>
        /// <param name="deviceName">The name of the desired input device.</param>
        /// <returns>0 on success, -1 if the name is null/empty, -2 if the engine is running, -3 if the device is not found.</returns>
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
                    return ReinitializeInputDevice();
                }
            }

            return -3; // Device not found
        }

        /// <summary>
        /// Changes the input device by index.
        /// Requires the engine to be stopped.
        /// </summary>
        /// <param name="deviceIndex">The index of the desired input device in the list returned by <see cref="GetInputDevices"/>.</param>
        /// <returns>0 on success, -1 if the index is negative, -2 if the engine is running, -3 if the index is out of range.</returns>
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
            return ReinitializeInputDevice();
        }

        /// <summary>
        /// Releases existing output resources and reinitializes the output device with the current configuration.
        /// </summary>
        /// <returns>0 on success, or an HRESULT error code otherwise.</returns>
        private int ReinitializeOutputDevice()
        {
            lock (_stateLock)
            {
                // Release existing output resources
                if (_renderClient != null)
                {
                    Marshal.ReleaseComObject(_renderClient);
                    _renderClient = null!;
                }

                if (_audioClient != null)
                {
                    Marshal.ReleaseComObject(_audioClient);
                    _audioClient = null!;
                }

                if (_outputDevice != null)
                {
                    Marshal.ReleaseComObject(_outputDevice);
                    _outputDevice = null!;
                }

                // Reinitialize output device
                return InitializeOutputDevice(_config);
            }
        }

        /// <summary>
        /// Releases existing input resources and reinitializes the input device with the current configuration.
        /// </summary>
        /// <returns>0 on success, or an HRESULT error code otherwise.</returns>
        private int ReinitializeInputDevice()
        {
            lock (_stateLock)
            {
                // Release existing input resources
                if (_captureClient != null)
                {
                    Marshal.ReleaseComObject(_captureClient);
                    _captureClient = null!;
                }

                if (_inputAudioClient != null)
                {
                    Marshal.ReleaseComObject(_inputAudioClient);
                    _inputAudioClient = null!;
                }

                if (_inputDevice != null)
                {
                    Marshal.ReleaseComObject(_inputDevice);
                    _inputDevice = null!;
                }

                // Reinitialize input device
                return InitializeInputDevice(_config);
            }
        }

        /// <summary>
        /// Event handler for default output device changes.
        /// Auto-reinitializes and restarts the engine if it was using the default device.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The event arguments containing the new device ID.</param>
        private void OnDefaultOutputDeviceChanged(object sender, AudioDeviceChangedEventArgs e)
        {
            _currentOutputDeviceId = e.NewDeviceId;

            // Forward event to consumers
            OutputDeviceChanged?.Invoke(this, e);

            // Auto-reinitialize if currently using default device and running
            if (string.IsNullOrEmpty(_config.OutputDeviceId) && _isRunning == 1 && _config.EnableOutput)
            {
                // Stop, reinitialize, and restart
                System.Threading.Tasks.Task.Run(() =>
                {
                    Stop();
                    ReinitializeOutputDevice();
                    Start();
                });
            }
        }

        /// <summary>
        /// Event handler for default input device changes.
        /// Auto-reinitializes and restarts the engine if it was using the default device.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The event arguments containing the new device ID.</param>
        private void OnDefaultInputDeviceChanged(object sender, AudioDeviceChangedEventArgs e)
        {
            _currentInputDeviceId = e.NewDeviceId;

            // Forward event to consumers
            InputDeviceChanged?.Invoke(this, e);

            // Auto-reinitialize if currently using default device and running
            if (string.IsNullOrEmpty(_config.InputDeviceId) && _isRunning == 1 && _config.EnableInput)
            {
                // Stop, reinitialize, and restart
                System.Threading.Tasks.Task.Run(() =>
                {
                    Stop();
                    ReinitializeInputDevice();
                    Start();
                });
            }
        }

        /// <summary>
        /// Event handler for device state changes.
        /// Attempts recovery if the currently used device becomes unavailable.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The event arguments containing the device ID and new state.</param>
        private void OnDeviceStateChanged(object sender, AudioDeviceStateChangedEventArgs e)
        {
            // Forward event to consumers
            DeviceStateChanged?.Invoke(this, e);

            // Check if our current device was affected
            bool needsReinit = false;
            if (_config.EnableOutput && !string.IsNullOrEmpty(_config.OutputDeviceId) &&
                e.DeviceId == _config.OutputDeviceId && e.NewState != Core.AudioDeviceState.Active)
            {
                needsReinit = true;
            }

            if (_config.EnableInput && !string.IsNullOrEmpty(_config.InputDeviceId) &&
                e.DeviceId == _config.InputDeviceId && e.NewState != Core.AudioDeviceState.Active)
            {
                needsReinit = true;
            }

            if (needsReinit && _isRunning == 1)
            {
                // Device became unavailable - stop and attempt recovery
                System.Threading.Tasks.Task.Run(() =>
                {
                    Stop();
                    System.Threading.Thread.Sleep(500); // Brief delay for device to stabilize

                    // Try to reinitialize with default device
                    _config.OutputDeviceId = null;
                    _config.InputDeviceId = null;

                    if (ReinitializeOutputDevice() == 0 || ReinitializeInputDevice() == 0)
                    {
                        Start();
                    }
                });
            }
        }

        /// <summary>
        /// Cleans up all managed and unmanaged resources, releases COM objects, and stops the audio thread.
        /// </summary>
        public void Dispose()
        {
            Stop();

            // Unregister device notifications
            if (_deviceEnumeratorWithNotifications != null && _notificationClient != null)
            {
                try
                {
                    _deviceEnumeratorWithNotifications.UnregisterEndpointNotificationCallback(_notificationClient);
                }
                catch
                {
                    // Ignore errors during cleanup
                }
            }

            // Unhook events
            if (_notificationClient != null)
            {
                _notificationClient.DefaultOutputDeviceChanged -= OnDefaultOutputDeviceChanged;
                _notificationClient.DefaultInputDeviceChanged -= OnDefaultInputDeviceChanged;
                _notificationClient.DeviceStateChanged -= OnDeviceStateChanged;
            }

            // Unpin buffers
            if (_outputBufferHandle.IsAllocated)
                _outputBufferHandle.Free();
            if (_inputBufferHandle.IsAllocated)
                _inputBufferHandle.Free();

            // Close event
            if (_stopEvent != IntPtr.Zero)
            {
                Kernel32.CloseHandle(_stopEvent);
                _stopEvent = IntPtr.Zero;
            }

            // Release COM interfaces
            if (_renderClient != null)
            {
                Marshal.ReleaseComObject(_renderClient);
                _renderClient = null!;
            }

            if (_captureClient != null)
            {
                Marshal.ReleaseComObject(_captureClient);
                _captureClient = null!;
            }

            if (_audioClient != null)
            {
                Marshal.ReleaseComObject(_audioClient);
                _audioClient = null!;
            }

            if (_audioClientPtr != IntPtr.Zero)
            {
                Marshal.Release(_audioClientPtr);
                _audioClientPtr = IntPtr.Zero;
            }

            if (_outputDevice != null)
            {
                Marshal.ReleaseComObject(_outputDevice);
                _outputDevice = null!;
            }

            if (_inputDevice != null)
            {
                Marshal.ReleaseComObject(_inputDevice);
                _inputDevice = null!;
            }

            if (_inputAudioClient != null)
            {
                Marshal.ReleaseComObject(_inputAudioClient);
                _inputAudioClient = null!;
            }

            if (_deviceEnumerator != null)
            {
                Marshal.ReleaseComObject(_deviceEnumerator);
                _deviceEnumerator = null!;
            }

            // Uninitialize COM
            Ole32.CoUninitialize();

            // Clear buffer pool
            _bufferPool?.Clear();
        }
    }
}
