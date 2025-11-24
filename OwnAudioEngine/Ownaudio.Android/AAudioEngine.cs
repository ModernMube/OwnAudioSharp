using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Android.Interop;
using Ownaudio.Core;
using Ownaudio.Core.Common;

namespace Ownaudio.Android
{
    /// <summary>
    /// Android audio engine implementation using the native AAudio API.
    /// Provides low-latency audio playback and recording for Android platform.
    /// </summary>
    /// <remarks>
    /// AAudio is a native C API introduced in Android 8.0 (API level 26) for high-performance audio.
    /// The system library (libaaudio.so) is automatically available on supported Android devices.
    /// No external dependencies or native libraries need to be packaged in the APK.
    ///
    /// For devices running Android 7.1 or earlier (API &lt; 26), consider using OpenSL ES instead.
    /// </remarks>
    public sealed class AAudioEngine : IAudioEngine
    {

        #region Private Fields

        // Stream handles
        private IntPtr _outputStream = IntPtr.Zero;
        private IntPtr _inputStream = IntPtr.Zero;

        // Configuration
        private AudioConfig? _config;
        private int _framesPerBuffer;
        private int _actualSampleRate;
        private int _actualChannels;
        private int _requestedSampleRate;  // Track what we originally requested
        private int _requestedChannels;

        // State management
        private volatile int _isRunning; // 0 = stopped, 1 = running
        private volatile int _isActive;  // 0 = idle, 1 = active, -1 = error
        private volatile bool _isDisposed;
        private readonly object _stateLock = new object();

        // Lock-free ring buffers for thread-safe communication
        private LockFreeRingBuffer<float>? _outputRingBuffer;
        private LockFreeRingBuffer<float>? _inputRingBuffer;

        // Buffer pool for zero-allocation Receives
        private AudioBufferPool? _inputBufferPool;

        // Pinned buffers for native interop (avoid GC allocation during audio callbacks)
        private GCHandle _outputBufferHandle;
        private GCHandle _inputBufferHandle;
        private float[]? _outputBuffer;
        private float[]? _inputBuffer;

        // Resamplers for handling sample rate mismatches
        private AudioResampler? _outputResampler;
        private AudioResampler? _inputResampler;
        private float[]? _resampleTempBuffer;  // Temporary buffer for output resampling
        private float[]? _inputResampleTempBuffer;  // Temporary buffer for input resampling

        // Callback delegates (must be kept alive to prevent GC collection)
        private AAudioInterop.AAudioStream_dataCallback? _outputDataCallback;
        private AAudioInterop.AAudioStream_dataCallback? _inputDataCallback;
        private AAudioInterop.AAudioStream_errorCallback? _outputErrorCallback;
        private AAudioInterop.AAudioStream_errorCallback? _inputErrorCallback;

        // Device management
        private int _outputDeviceId = AAudioInterop.AAUDIO_UNSPECIFIED;
        private int _inputDeviceId = AAudioInterop.AAUDIO_UNSPECIFIED;

        // Constants
        private const int DEFAULT_RING_BUFFER_MULTIPLIER = 8; // 8x buffer size for ring buffer
        private const int MIN_BUFFER_SIZE = 48;
        private const int MAX_BUFFER_SIZE = 8192;

        #endregion

        #region IAudioEngine Properties

        /// <summary>
        /// Gets the native AAudioStream handle for output.
        /// Returns IntPtr.Zero if output is not enabled.
        /// </summary>
        public IntPtr GetStream()
        {
            return _outputStream;
        }

        /// <summary>
        /// Gets the actual frames per buffer size negotiated with the device.
        /// </summary>
        public int FramesPerBuffer => _framesPerBuffer;

        /// <summary>
        /// Gets the actual audio configuration negotiated with the device.
        /// IMPORTANT: This returns the ACTUAL device configuration, which may differ from the requested configuration.
        /// Always use this property to get the correct sample rate and channel count for audio sources.
        /// </summary>
        public AudioConfig Config
        {
            get
            {
                if (_config == null)
                {
                    throw new InvalidOperationException("Engine not initialized. Call Initialize() first.");
                }

                // Return a config with ACTUAL device parameters (not requested)
                return new AudioConfig
                {
                    SampleRate = _actualSampleRate,  // Use actual device sample rate
                    Channels = _actualChannels,      // Use actual device channels
                    BufferSize = _framesPerBuffer,   // Use actual buffer size
                    EnableOutput = _config.EnableOutput,
                    EnableInput = _config.EnableInput
                };
            }
        }

        #endregion

        #region IAudioEngine Events

        public event EventHandler<AudioDeviceChangedEventArgs>? OutputDeviceChanged;
        public event EventHandler<AudioDeviceChangedEventArgs>? InputDeviceChanged;
        public event EventHandler<AudioDeviceStateChangedEventArgs>? DeviceStateChanged;

        #endregion

        #region IAudioEngine State Methods

        /// <summary>
        /// Gets the activation state of the audio engine.
        /// </summary>
        /// <returns>1 = active, 0 = idle, -1 = error</returns>
        public int OwnAudioEngineActivate()
        {
            return _isActive;
        }

        /// <summary>
        /// Gets the stopped state of the audio engine.
        /// </summary>
        /// <returns>1 = stopped, 0 = running</returns>
        public int OwnAudioEngineStopped()
        {
            return _isRunning == 0 ? 1 : 0;
        }

        #endregion

        #region IAudioEngine Core Methods

        /// <summary>
        /// Initializes the AAudio engine with the specified configuration.
        ///
        /// WARNING: This method BLOCKS for 50-500ms!
        /// DO NOT call from UI thread - use Task.Run() or InitializeAsync().
        /// </summary>
        public int Initialize(AudioConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            if (!config.Validate())
                throw new ArgumentException("Invalid audio configuration", nameof(config));

            lock (_stateLock)
            {
                if (_isRunning != 0)
                    return -1; // Cannot initialize while running

                try
                {
                    _config = config;
                    _requestedSampleRate = config.SampleRate;
                    _requestedChannels = config.Channels;
                    _actualSampleRate = config.SampleRate;  // Will be updated after stream creation
                    _actualChannels = config.Channels;      // Will be updated after stream creation

                    // Clamp buffer size to reasonable limits
                    _framesPerBuffer = Math.Clamp(config.BufferSize, MIN_BUFFER_SIZE, MAX_BUFFER_SIZE);

                    // Create output stream if enabled
                    if (config.EnableOutput)
                    {
                        int result = CreateOutputStream();
                        if (result < 0)
                        {
                            _isActive = -1;
                            return result;
                        }
                    }

                    // Create input stream if enabled
                    if (config.EnableInput)
                    {
                        int result = CreateInputStream();
                        if (result < 0)
                        {
                            _isActive = -1;
                            return result;
                        }
                    }

                    // Create ring buffers for lock-free communication
                    int ringBufferSize = _framesPerBuffer * _actualChannels * DEFAULT_RING_BUFFER_MULTIPLIER;

                    if (config.EnableOutput)
                    {
                        _outputRingBuffer = new LockFreeRingBuffer<float>(ringBufferSize);
                        _outputBuffer = new float[_framesPerBuffer * _actualChannels];
                        _outputBufferHandle = GCHandle.Alloc(_outputBuffer, GCHandleType.Pinned);
                    }

                    if (config.EnableInput)
                    {
                        _inputRingBuffer = new LockFreeRingBuffer<float>(ringBufferSize);
                        _inputBuffer = new float[_framesPerBuffer * _actualChannels];
                        _inputBufferHandle = GCHandle.Alloc(_inputBuffer, GCHandleType.Pinned);

                        // Create buffer pool for zero-allocation Receives
                        _inputBufferPool = new AudioBufferPool(
                            bufferSize: ringBufferSize,
                            initialPoolSize: 4,
                            maxPoolSize: 16);
                    }

                    _isActive = 0; // Idle state
                    return AAudioInterop.AAUDIO_OK;
                }
                catch (Exception ex)
                {
                    _isActive = -1;
                    throw new AudioException("Failed to initialize AAudio engine", ex);
                }
            }
        }

        /// <summary>
        /// Starts the audio streams. Thread-safe and idempotent.
        /// </summary>
        public int Start()
        {
            lock (_stateLock)
            {
                if (_isRunning != 0)
                    return AAudioInterop.AAUDIO_OK; // Already running

                if (_config == null)
                    return AAudioInterop.AAUDIO_ERROR_INVALID_STATE;

                try
                {
                    // CRITICAL: Clear ring buffers to prevent old audio from playing after stop
                    // This ensures that when resuming playback, we don't hear stale audio data
                    // This is the same approach used in WASAPI engine
                    _outputRingBuffer?.Clear();
                    _inputRingBuffer?.Clear();

                    // Start output stream
                    if (_config.EnableOutput && _outputStream != IntPtr.Zero)
                    {
                        int result = AAudioInterop.AAudioStream_requestStart(_outputStream);
                        if (result != AAudioInterop.AAUDIO_OK)
                        {
                            throw new AudioException(
                                $"Failed to start output stream: {AAudioInterop.GetErrorString(result)}");
                        }
                    }

                    // Start input stream
                    if (_config.EnableInput && _inputStream != IntPtr.Zero)
                    {
                        int result = AAudioInterop.AAudioStream_requestStart(_inputStream);
                        if (result != AAudioInterop.AAUDIO_OK)
                        {
                            throw new AudioException(
                                $"Failed to start input stream: {AAudioInterop.GetErrorString(result)}");
                        }
                    }

                    _isRunning = 1;
                    _isActive = 1; // Active state
                    return AAudioInterop.AAUDIO_OK;
                }
                catch (Exception ex)
                {
                    _isActive = -1;
                    throw new AudioException("Failed to start AAudio engine", ex);
                }
            }
        }

        /// <summary>
        /// Stops the audio streams gracefully and closes them for proper restart capability.
        ///
        /// WARNING: This method BLOCKS for up to 2000ms!
        /// DO NOT call from UI thread - use Task.Run() or StopAsync().
        /// </summary>
        /// <remarks>
        /// AAudio streams cannot be restarted after being stopped - they must be recreated.
        /// This method closes the streams to allow Initialize() to be called again for restart.
        /// </remarks>
        public int Stop()
        {
            lock (_stateLock)
            {
                if (_isRunning == 0)
                    return AAudioInterop.AAUDIO_OK; // Already stopped

                try
                {
                    _isRunning = 0;
                    _isActive = 0; // Idle state

                    // Stop and close output stream
                    if (_outputStream != IntPtr.Zero)
                    {
                        int result = AAudioInterop.AAudioStream_requestStop(_outputStream);
                        if (result != AAudioInterop.AAUDIO_OK)
                        {
                            Console.WriteLine(
                                $"Warning: Failed to stop output stream: {AAudioInterop.GetErrorString(result)}");
                        }

                        // Wait for stream to stop (with timeout)
                        WaitForStreamState(_outputStream, AAudioInterop.AAUDIO_STREAM_STATE_STOPPED, 2000);

                        // CRITICAL: Close the stream to allow restart
                        // AAudio streams cannot be restarted after stop - they must be recreated
                        AAudioInterop.AAudioStream_close(_outputStream);
                        _outputStream = IntPtr.Zero;
                    }

                    // Stop and close input stream
                    if (_inputStream != IntPtr.Zero)
                    {
                        int result = AAudioInterop.AAudioStream_requestStop(_inputStream);
                        if (result != AAudioInterop.AAUDIO_OK)
                        {
                            Console.WriteLine(
                                $"Warning: Failed to stop input stream: {AAudioInterop.GetErrorString(result)}");
                        }

                        // Wait for stream to stop (with timeout)
                        WaitForStreamState(_inputStream, AAudioInterop.AAUDIO_STREAM_STATE_STOPPED, 2000);

                        // CRITICAL: Close the stream to allow restart
                        AAudioInterop.AAudioStream_close(_inputStream);
                        _inputStream = IntPtr.Zero;
                    }

                    // Clean up resources to prepare for potential restart
                    CleanupResources();

                    Console.WriteLine("AAudio streams stopped and closed. Call Initialize() to restart.");

                    return AAudioInterop.AAUDIO_OK;
                }
                catch (Exception ex)
                {
                    throw new AudioException("Failed to stop AAudio engine", ex);
                }
            }
        }

        /// <summary>
        /// Sends audio samples to the output device.
        /// Blocks the caller until all samples are written to the buffer.
        ///
        /// WARNING: This method BLOCKS until all samples are written!
        /// Use AudioEngineWrapper for non-blocking operation from UI thread.
        /// </summary>
        /// <remarks>
        /// This method uses a SpinWait strategy for efficient waiting:
        /// - Initially spins to minimize latency for quick writes
        /// - Yields to other threads when buffer is full to avoid CPU burn
        /// - Synchronizes caller's speed with audio playback rate automatically
        /// This ensures proper flow control - audio won't play too fast or drop samples.
        /// </remarks>
        public void Send(Span<float> samples)
        {
            if (_outputRingBuffer == null)
                throw new InvalidOperationException("Output is not enabled");

            if (_isRunning == 0)
                return; // Silently ignore if not running

            int written = 0;
            int totalSamples = samples.Length;

            // SpinWait structure for efficient waiting
            SpinWait spinWait = new SpinWait();

            while (written < totalSamples)
            {
                // Check if engine is still running to avoid infinite loop on shutdown
                if (_isRunning == 0 || _isActive < 0)
                    break;

                // Try to write remaining samples
                int count = _outputRingBuffer.Write(samples.Slice(written));
                written += count;

                if (written < totalSamples)
                {
                    // Buffer is full. Wait for callback to consume data.
                    // SpinWait intelligently switches between spinning and yielding.
                    spinWait.SpinOnce();

                    // Optional: If we've yielded many times, add a small sleep
                    // to avoid burning CPU, since audio callback only frees space every few ms.
                    if (spinWait.NextSpinWillYield)
                    {
                        Thread.Sleep(1);
                    }
                }
            }
        }

        /// <summary>
        /// Receives audio samples from the input device.
        /// Uses a buffer pool to avoid GC allocations.
        ///
        /// WARNING: This method may BLOCK 1-20ms when ring buffer is empty!
        /// Use AudioEngineWrapper for non-blocking operation from UI thread.
        /// </summary>
        /// <remarks>
        /// The returned buffer comes from a pool. The caller is responsible for
        /// returning it to the pool when done (not implemented for Android yet,
        /// but pool automatically manages recycling).
        /// </remarks>
        public int Receives(out float[] samples)
        {
            if (_inputRingBuffer == null || _inputBufferPool == null)
            {
                samples = Array.Empty<float>();
                return AAudioInterop.AAUDIO_ERROR_INVALID_STATE;
            }

            if (_isRunning == 0)
            {
                samples = Array.Empty<float>();
                return AAudioInterop.AAUDIO_ERROR_INVALID_STATE;
            }

            int available = _inputRingBuffer.Available;
            if (available == 0)
            {
                samples = Array.Empty<float>();
                return 0; // No samples available
            }

            // Get buffer from pool (zero-allocation)
            samples = _inputBufferPool.Get();

            // Ensure buffer is large enough
            if (samples.Length < available)
            {
                // Buffer too small, read only what fits
                int read = _inputRingBuffer.Read(samples.AsSpan());
                return read > 0 ? AAudioInterop.AAUDIO_OK : 0;
            }

            // Read available data
            int samplesRead = _inputRingBuffer.Read(samples.AsSpan(0, available));

            return samplesRead > 0 ? AAudioInterop.AAUDIO_OK : 0;
        }

        #endregion

        #region Device Management

        public List<AudioDeviceInfo> GetOutputDevices()
        {
            // Android AAudio doesn't provide comprehensive device enumeration
            // Return a simplified list with the default device
            var devices = new List<AudioDeviceInfo>
            {
                new AudioDeviceInfo(
                    deviceId: "0",
                    name: "Default Output Device",
                    isInput: false,
                    isOutput: true,
                    isDefault: true,
                    state: AudioDeviceState.Active)
            };

            return devices;
        }

        public List<AudioDeviceInfo> GetInputDevices()
        {
            // Android AAudio doesn't provide comprehensive device enumeration
            // Return a simplified list with the default device
            var devices = new List<AudioDeviceInfo>
            {
                new AudioDeviceInfo(
                    deviceId: "0",
                    name: "Default Input Device",
                    isInput: true,
                    isOutput: false,
                    isDefault: true,
                    state: AudioDeviceState.Active)
            };

            return devices;
        }

        public int SetOutputDeviceByName(string deviceName)
        {
            // Android AAudio device switching requires stream recreation
            // This is a simplified implementation
            return AAudioInterop.AAUDIO_ERROR_UNIMPLEMENTED;
        }

        public int SetOutputDeviceByIndex(int deviceIndex)
        {
            // Android AAudio device switching requires stream recreation
            return AAudioInterop.AAUDIO_ERROR_UNIMPLEMENTED;
        }

        public int SetInputDeviceByName(string deviceName)
        {
            // Android AAudio device switching requires stream recreation
            return AAudioInterop.AAUDIO_ERROR_UNIMPLEMENTED;
        }

        public int SetInputDeviceByIndex(int deviceIndex)
        {
            // Android AAudio device switching requires stream recreation
            return AAudioInterop.AAUDIO_ERROR_UNIMPLEMENTED;
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Creates and configures the output audio stream.
        /// </summary>
        private int CreateOutputStream()
        {
            IntPtr builder = IntPtr.Zero;

            try
            {
                // Create stream builder
                int result = AAudioInterop.AAudio_createStreamBuilder(out builder);
                if (result != AAudioInterop.AAUDIO_OK)
                {
                    throw new AudioException(
                        $"Failed to create output stream builder: {AAudioInterop.GetErrorString(result)}");
                }

                // Configure stream parameters
                AAudioInterop.AAudioStreamBuilder_setDirection(builder, AAudioInterop.AAUDIO_DIRECTION_OUTPUT);
                AAudioInterop.AAudioStreamBuilder_setDeviceId(builder, _outputDeviceId);
                AAudioInterop.AAudioStreamBuilder_setSampleRate(builder, _actualSampleRate);
                AAudioInterop.AAudioStreamBuilder_setChannelCount(builder, _actualChannels);
                AAudioInterop.AAudioStreamBuilder_setFormat(builder, AAudioInterop.AAUDIO_FORMAT_PCM_FLOAT);
                AAudioInterop.AAudioStreamBuilder_setSharingMode(builder, AAudioInterop.AAUDIO_SHARING_MODE_SHARED);
                AAudioInterop.AAudioStreamBuilder_setPerformanceMode(builder, AAudioInterop.AAUDIO_PERFORMANCE_MODE_LOW_LATENCY);
                AAudioInterop.AAudioStreamBuilder_setUsage(builder, AAudioInterop.AAUDIO_USAGE_MEDIA);
                AAudioInterop.AAudioStreamBuilder_setContentType(builder, AAudioInterop.AAUDIO_CONTENT_TYPE_MUSIC);
                AAudioInterop.AAudioStreamBuilder_setBufferCapacityInFrames(builder, _framesPerBuffer * 2);

                // Set callbacks (keep delegates alive)
                _outputDataCallback = OutputDataCallback;
                _outputErrorCallback = OutputErrorCallback;

                AAudioInterop.AAudioStreamBuilder_setDataCallback(builder, _outputDataCallback, IntPtr.Zero);
                AAudioInterop.AAudioStreamBuilder_setErrorCallback(builder, _outputErrorCallback, IntPtr.Zero);

                // Open the stream
                result = AAudioInterop.AAudioStreamBuilder_openStream(builder, out _outputStream);
                if (result != AAudioInterop.AAUDIO_OK)
                {
                    throw new AudioException(
                        $"Failed to open output stream: {AAudioInterop.GetErrorString(result)}");
                }

                // Query actual stream parameters
                int deviceSampleRate = AAudioInterop.AAudioStream_getSampleRate(_outputStream);
                int deviceChannels = AAudioInterop.AAudioStream_getChannelCount(_outputStream);
                int framesPerBurst = AAudioInterop.AAudioStream_getFramesPerBurst(_outputStream);

                // CRITICAL: Check if device uses different sample rate than requested
                if (deviceSampleRate != _requestedSampleRate)
                {
                    Console.WriteLine(
                        $"WARNING: AAudio sample rate mismatch! Requested: {_requestedSampleRate} Hz, " +
                        $"Device using: {deviceSampleRate} Hz. Enabling automatic resampling...");

                    // Create resampler to handle the mismatch
                    int maxFrames = _framesPerBuffer * 4; // Generous buffer for resampling
                    _outputResampler = new AudioResampler(
                        sourceRate: _requestedSampleRate,
                        targetRate: deviceSampleRate,
                        channels: _requestedChannels,
                        maxFrameSize: maxFrames);

                    // Allocate temp buffer for resampled data
                    int tempBufferSize = _outputResampler.CalculateOutputSize(maxFrames * _requestedChannels);
                    _resampleTempBuffer = new float[tempBufferSize];

                    Console.WriteLine($"Resampler enabled: {_requestedSampleRate}Hz -> {deviceSampleRate}Hz");
                }

                if (deviceChannels != _requestedChannels)
                {
                    Console.WriteLine(
                        $"WARNING: AAudio channel count mismatch. Requested: {_requestedChannels}, " +
                        $"Got: {deviceChannels}. Audio may sound incorrect.");
                }

                _actualSampleRate = deviceSampleRate;
                _actualChannels = deviceChannels;

                // Use frames per burst as the actual buffer size
                if (framesPerBurst > 0)
                {
                    _framesPerBuffer = framesPerBurst;
                }

                Console.WriteLine($"AAudio output stream created: {_actualSampleRate}Hz, {_actualChannels}ch, {_framesPerBuffer} frames/buffer");

                return AAudioInterop.AAUDIO_OK;
            }
            finally
            {
                // Clean up builder
                if (builder != IntPtr.Zero)
                {
                    AAudioInterop.AAudioStreamBuilder_delete(builder);
                }
            }
        }

        /// <summary>
        /// Creates and configures the input audio stream.
        /// </summary>
        private int CreateInputStream()
        {
            IntPtr builder = IntPtr.Zero;

            try
            {
                // Create stream builder
                int result = AAudioInterop.AAudio_createStreamBuilder(out builder);
                if (result != AAudioInterop.AAUDIO_OK)
                {
                    throw new AudioException(
                        $"Failed to create input stream builder: {AAudioInterop.GetErrorString(result)}");
                }

                // Configure stream parameters
                AAudioInterop.AAudioStreamBuilder_setDirection(builder, AAudioInterop.AAUDIO_DIRECTION_INPUT);
                AAudioInterop.AAudioStreamBuilder_setDeviceId(builder, _inputDeviceId);
                AAudioInterop.AAudioStreamBuilder_setSampleRate(builder, _actualSampleRate);
                AAudioInterop.AAudioStreamBuilder_setChannelCount(builder, _actualChannels);
                AAudioInterop.AAudioStreamBuilder_setFormat(builder, AAudioInterop.AAUDIO_FORMAT_PCM_FLOAT);
                AAudioInterop.AAudioStreamBuilder_setSharingMode(builder, AAudioInterop.AAUDIO_SHARING_MODE_SHARED);
                AAudioInterop.AAudioStreamBuilder_setPerformanceMode(builder, AAudioInterop.AAUDIO_PERFORMANCE_MODE_LOW_LATENCY);
                AAudioInterop.AAudioStreamBuilder_setBufferCapacityInFrames(builder, _framesPerBuffer * 2);

                // Set callbacks (keep delegates alive)
                _inputDataCallback = InputDataCallback;
                _inputErrorCallback = InputErrorCallback;

                AAudioInterop.AAudioStreamBuilder_setDataCallback(builder, _inputDataCallback, IntPtr.Zero);
                AAudioInterop.AAudioStreamBuilder_setErrorCallback(builder, _inputErrorCallback, IntPtr.Zero);

                // Open the stream
                result = AAudioInterop.AAudioStreamBuilder_openStream(builder, out _inputStream);
                if (result != AAudioInterop.AAUDIO_OK)
                {
                    throw new AudioException(
                        $"Failed to open input stream: {AAudioInterop.GetErrorString(result)}");
                }

                // Query actual stream parameters
                int deviceSampleRate = AAudioInterop.AAudioStream_getSampleRate(_inputStream);
                int deviceChannels = AAudioInterop.AAudioStream_getChannelCount(_inputStream);
                int framesPerBurst = AAudioInterop.AAudioStream_getFramesPerBurst(_inputStream);

                // CRITICAL: Check if device uses different sample rate than requested
                if (deviceSampleRate != _requestedSampleRate)
                {
                    Console.WriteLine(
                        $"WARNING: AAudio input sample rate mismatch! Requested: {_requestedSampleRate} Hz, " +
                        $"Device using: {deviceSampleRate} Hz. Enabling automatic resampling...");

                    // Create resampler to handle the mismatch
                    // For input, we resample FROM device rate TO requested rate (opposite of output)
                    int maxFrames = _framesPerBuffer * 4; // Generous buffer for resampling
                    _inputResampler = new AudioResampler(
                        sourceRate: deviceSampleRate,
                        targetRate: _requestedSampleRate,
                        channels: deviceChannels,
                        maxFrameSize: maxFrames);

                    // Allocate temp buffer for resampled data
                    int tempBufferSize = _inputResampler.CalculateOutputSize(maxFrames * deviceChannels);
                    _inputResampleTempBuffer = new float[tempBufferSize];

                    Console.WriteLine($"Input resampler enabled: {deviceSampleRate}Hz -> {_requestedSampleRate}Hz");
                }

                if (deviceChannels != _requestedChannels)
                {
                    Console.WriteLine(
                        $"WARNING: AAudio input channel count mismatch. Requested: {_requestedChannels}, " +
                        $"Got: {deviceChannels}. Audio may sound incorrect.");
                }

                // Update actual parameters if needed
                if (_actualSampleRate != deviceSampleRate)
                {
                    _actualSampleRate = deviceSampleRate;
                }
                if (_actualChannels != deviceChannels)
                {
                    _actualChannels = deviceChannels;
                }

                if (framesPerBurst > 0 && _framesPerBuffer != framesPerBurst)
                {
                    _framesPerBuffer = framesPerBurst;
                }

                Console.WriteLine($"AAudio input stream created: {deviceSampleRate}Hz, {deviceChannels}ch, {_framesPerBuffer} frames/buffer");

                return AAudioInterop.AAUDIO_OK;
            }
            finally
            {
                // Clean up builder
                if (builder != IntPtr.Zero)
                {
                    AAudioInterop.AAudioStreamBuilder_delete(builder);
                }
            }
        }

        /// <summary>
        /// Output stream data callback - called by AAudio when audio data is needed.
        /// This runs on a real-time audio thread with highest priority.
        /// MUST be zero-allocation and lock-free!
        /// </summary>
        private int OutputDataCallback(IntPtr stream, IntPtr userData, IntPtr audioData, int numFrames)
        {
            try
            {
                // Validate inputs
                if (_outputRingBuffer == null || _outputBuffer == null || audioData == IntPtr.Zero || numFrames <= 0)
                    return AAudioInterop.AAUDIO_CALLBACK_RESULT_STOP;

                int samplesNeeded = numFrames * _actualChannels;

                // Safety check - prevent buffer overflow
                if (samplesNeeded > _outputBuffer.Length)
                    return AAudioInterop.AAUDIO_CALLBACK_RESULT_STOP;

                Span<float> outputSpan = new Span<float>(_outputBuffer, 0, samplesNeeded);

                // Check if resampling is needed
                if (_outputResampler != null && _outputResampler.IsResamplingNeeded && _resampleTempBuffer != null)
                {
                    // Calculate how many input frames we need (at requested/source rate)
                    // CRITICAL: Correct understanding of the resampling direction
                    // - Device requests: numFrames @ _actualSampleRate (e.g., 512 @ 44100 Hz)
                    // - Source data is @ _requestedSampleRate (e.g., 48000 Hz)
                    // - We're DOWNSAMPLING: 48000 Hz → 44100 Hz
                    // - Input needed = outputFrames * (sourceRate / targetRate)
                    // - Example: 512 * (48000/44100) = 557 input frames @ 48000 Hz
                    //
                    // NOTE: The original calculation was CORRECT for downsampling!
                    // The issue must be elsewhere (likely in the resampler logic or decoder timing)
                    int inputFramesNeeded = (int)Math.Ceiling(numFrames * (double)_requestedSampleRate / _actualSampleRate);
                    int inputSamplesNeeded = inputFramesNeeded * _requestedChannels;

                    // Read from ring buffer at requested sample rate
                    // Use _resampleTempBuffer.Length as the maximum read size to prevent buffer overrun
                    Span<float> inputSpan = new Span<float>(_resampleTempBuffer, 0, Math.Min(inputSamplesNeeded, _resampleTempBuffer.Length));
                    int samplesRead = _outputRingBuffer.Read(inputSpan);

                    if (samplesRead > 0)
                    {
                        // Resample to device sample rate
                        int resampledCount = _outputResampler.Resample(inputSpan.Slice(0, samplesRead), outputSpan);

                        // Fill remaining with silence if needed
                        if (resampledCount < samplesNeeded)
                        {
                            outputSpan.Slice(resampledCount).Clear();
                        }
                    }
                    else
                    {
                        // No data available - output silence
                        outputSpan.Clear();
                    }
                }
                else
                {
                    // No resampling - direct read from ring buffer
                    int samplesRead = _outputRingBuffer.Read(outputSpan);

                    // If we didn't get enough samples, fill with silence
                    if (samplesRead < samplesNeeded)
                    {
                        outputSpan.Slice(samplesRead).Clear();
                    }
                }

                // Copy to native buffer
                unsafe
                {
                    fixed (float* srcPtr = _outputBuffer)
                    {
                        Buffer.MemoryCopy(srcPtr, audioData.ToPointer(),
                            samplesNeeded * sizeof(float),
                            samplesNeeded * sizeof(float));
                    }
                }

                return AAudioInterop.AAUDIO_CALLBACK_RESULT_CONTINUE;
            }
            catch
            {
                // NEVER throw exceptions from audio callback!
                // Stop the stream on error to prevent further crashes
                return AAudioInterop.AAUDIO_CALLBACK_RESULT_STOP;
            }
        }

        /// <summary>
        /// Input stream data callback - called by AAudio when audio data is available.
        /// This runs on a real-time audio thread with highest priority.
        /// MUST be zero-allocation and lock-free!
        /// </summary>
        private int InputDataCallback(IntPtr stream, IntPtr userData, IntPtr audioData, int numFrames)
        {
            try
            {
                // Validate inputs
                if (_inputRingBuffer == null || _inputBuffer == null || audioData == IntPtr.Zero || numFrames <= 0)
                    return AAudioInterop.AAUDIO_CALLBACK_RESULT_STOP;

                int samplesAvailable = numFrames * _actualChannels;

                // Safety check - prevent buffer overflow
                if (samplesAvailable > _inputBuffer.Length)
                    return AAudioInterop.AAUDIO_CALLBACK_RESULT_STOP;

                // Copy from native buffer
                unsafe
                {
                    fixed (float* dstPtr = _inputBuffer)
                    {
                        Buffer.MemoryCopy(audioData.ToPointer(), dstPtr,
                            samplesAvailable * sizeof(float),
                            samplesAvailable * sizeof(float));
                    }
                }

                // Check if resampling is needed for input
                if (_inputResampler != null && _inputResampler.IsResamplingNeeded && _inputResampleTempBuffer != null)
                {
                    // Resample from device rate to requested rate
                    ReadOnlySpan<float> deviceSpan = new ReadOnlySpan<float>(_inputBuffer, 0, samplesAvailable);
                    Span<float> resampledSpan = new Span<float>(_inputResampleTempBuffer, 0, _inputResampleTempBuffer.Length);

                    int resampledCount = _inputResampler.Resample(
                        MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(deviceSpan), deviceSpan.Length),
                        resampledSpan);

                    // Write resampled data to ring buffer
                    if (resampledCount > 0)
                    {
                        _inputRingBuffer.Write(resampledSpan.Slice(0, resampledCount));
                    }
                }
                else
                {
                    // No resampling - direct write to ring buffer
                    ReadOnlySpan<float> inputSpan = new ReadOnlySpan<float>(_inputBuffer, 0, samplesAvailable);
                    _inputRingBuffer.Write(inputSpan);
                }

                return AAudioInterop.AAUDIO_CALLBACK_RESULT_CONTINUE;
            }
            catch
            {
                // NEVER throw exceptions from audio callback!
                // Stop the stream on error to prevent further crashes
                return AAudioInterop.AAUDIO_CALLBACK_RESULT_STOP;
            }
        }

        /// <summary>
        /// Output stream error callback.
        /// </summary>
        private void OutputErrorCallback(IntPtr stream, IntPtr userData, int error)
        {
            Console.WriteLine($"AAudio output stream error: {AAudioInterop.GetErrorString(error)}");
            _isActive = -1;

            // Notify listeners about device state change
            var deviceInfo = new AudioDeviceInfo(
                deviceId: "0",
                name: "Default Output Device",
                isInput: false,
                isOutput: true,
                isDefault: true,
                state: AudioDeviceState.Disabled);

            DeviceStateChanged?.Invoke(this, new AudioDeviceStateChangedEventArgs(
                "0", AudioDeviceState.Disabled, deviceInfo));
        }

        /// <summary>
        /// Input stream error callback.
        /// </summary>
        private void InputErrorCallback(IntPtr stream, IntPtr userData, int error)
        {
            Console.WriteLine($"AAudio input stream error: {AAudioInterop.GetErrorString(error)}");
            _isActive = -1;

            // Notify listeners about device state change
            var deviceInfo = new AudioDeviceInfo(
                deviceId: "0",
                name: "Default Input Device",
                isInput: true,
                isOutput: false,
                isDefault: true,
                state: AudioDeviceState.Disabled);

            DeviceStateChanged?.Invoke(this, new AudioDeviceStateChangedEventArgs(
                "0", AudioDeviceState.Disabled, deviceInfo));
        }

        /// <summary>
        /// Waits for a stream to reach the specified state.
        /// </summary>
        private void WaitForStreamState(IntPtr stream, int targetState, int timeoutMs)
        {
            if (stream == IntPtr.Zero)
                return;

            long timeoutNanos = (long)timeoutMs * 1_000_000;
            int currentState = AAudioInterop.AAudioStream_getState(stream);

            AAudioInterop.AAudioStream_waitForStateChange(
                stream, currentState, out _, timeoutNanos);
        }

        /// <summary>
        /// Cleans up resources after stopping to prepare for potential restart.
        /// </summary>
        private void CleanupResources()
        {
            // Free pinned buffers
            if (_outputBufferHandle.IsAllocated)
            {
                _outputBufferHandle.Free();
            }

            if (_inputBufferHandle.IsAllocated)
            {
                _inputBufferHandle.Free();
            }

            // Clear ring buffers (but don't null them out - they can be reused)
            // Note: LockFreeRingBuffer doesn't have a Clear method, so we'll just let them be recreated

            // Reset resamplers if they exist
            _outputResampler?.Reset();
            _inputResampler?.Reset();

            // Clear buffer pool
            _inputBufferPool?.Clear();

            // Clear buffer references
            _outputBuffer = null;
            _inputBuffer = null;
            _outputRingBuffer = null;
            _inputRingBuffer = null;
            _inputBufferPool = null;
            _outputResampler = null;
            _inputResampler = null;
            _resampleTempBuffer = null;
            _inputResampleTempBuffer = null;
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            if (_isDisposed)
                return;

            lock (_stateLock)
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;

                // Stop streams if running (this also closes streams and cleans up resources)
                if (_isRunning != 0)
                {
                    Stop();
                }
                else
                {
                    // If not running, still close any open streams
                    if (_outputStream != IntPtr.Zero)
                    {
                        AAudioInterop.AAudioStream_close(_outputStream);
                        _outputStream = IntPtr.Zero;
                    }

                    if (_inputStream != IntPtr.Zero)
                    {
                        AAudioInterop.AAudioStream_close(_inputStream);
                        _inputStream = IntPtr.Zero;
                    }

                    // Clean up resources
                    CleanupResources();
                }

                // Clear config
                _config = null;

                // Clear callback delegates
                _outputDataCallback = null;
                _outputErrorCallback = null;
                _inputDataCallback = null;
                _inputErrorCallback = null;
            }
        }

        #endregion
    }
}
