using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Collections.Generic;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using Ownaudio.Linux.Interop;
using Logger;
using static Ownaudio.Linux.Interop.PulseAudioInterop;

namespace Ownaudio.Linux
{
    /// <summary>
    /// Linux PulseAudio engine implementation using async API.
    /// Zero-allocation, real-time safe audio processing with direct hardware access.
    /// Provides sample-accurate timing similar to Windows WASAPI and macOS Core Audio.
    /// </summary>
    public sealed class PulseAudioEngine : IAudioEngine
    {
        // PulseAudio handles
        private IntPtr _mainLoop;
        private IntPtr _context;
        private IntPtr _outputStream;
        private IntPtr _inputStream;

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
        private int _bufferSizeBytes;

        // State management (atomic operations)
        private volatile int _isRunning; // 0 = stopped, 1 = running
        private volatile int _isActive;  // 0 = idle, 1 = active, -1 = error
        private volatile int _errorCode;

        // Debug/monitoring counters
        private volatile int _underrunCount;
        private volatile int _lastRingBufferLevel;

        // Timing diagnostics
        private long _callbackCount;
        private long _totalSamplesWritten;
        private DateTime _startTime;
        private DateTime _lastCallbackTime;
        private readonly object _timingLock = new object();

        // Adaptive resampling for tempo correction (real-time safe)
        private AudioResampler _adaptiveResampler;
        private float[] _resampleBuffer;
        private double _currentTempoRatio;
        private int _adaptiveSampleRate;

        // Callbacks (must be kept alive to prevent GC)
        private pa_context_notify_cb _contextStateCallback;
        private pa_stream_request_cb _outputWriteCallback;
        private pa_stream_request_cb _inputReadCallback;
        private pa_stream_notify_cb _outputStateCallback;
        private pa_stream_notify_cb _inputStateCallback;

        // Threading
        private readonly object _stateLock = new object();
        private ManualResetEventSlim _contextReadyEvent;
        private ManualResetEventSlim _outputStreamReadyEvent;
        private ManualResetEventSlim _inputStreamReadyEvent;

        // Buffer pool for Receives()
        private AudioBufferPool _bufferPool;

        // Device management
        private PulseAudioDeviceEnumerator _deviceEnumHelper;
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
        /// Occurs when an audio device's state changes.
        /// </summary>
        public event EventHandler<AudioDeviceStateChangedEventArgs> DeviceStateChanged;

        /// <summary>
        /// Gets the number of audio frames per buffer.
        /// </summary>
        public int FramesPerBuffer => _framesPerBuffer;

        /// <summary>
        /// Creates a new PulseAudio engine instance.
        /// </summary>
#nullable disable
        public PulseAudioEngine()
        {
            _isRunning = 0;
            _isActive = 0;
            _errorCode = 0;
            _contextReadyEvent = new ManualResetEventSlim(false);
            _outputStreamReadyEvent = new ManualResetEventSlim(false);
            _inputStreamReadyEvent = new ManualResetEventSlim(false);
            _deviceEnumHelper = new PulseAudioDeviceEnumerator();
        }
#nullable restore

        /// <summary>
        /// Initializes the PulseAudio engine with the specified configuration.
        /// </summary>
        /// <param name="config">The audio configuration.</param>
        /// <returns>0 on success, negative error code on failure.</returns>
        public int Initialize(AudioConfig config)
        {
            if (config == null || !config.Validate())
                return -1;

            lock (_stateLock)
            {
                try
                {
                    _config = config;

                    // Calculate buffer parameters
                    _framesPerBuffer = config.BufferSize;
                    _samplesPerBuffer = _framesPerBuffer * config.Channels;
                    _bufferSizeBytes = _samplesPerBuffer * SAMPLES_TO_BYTES_MULTIPLIER;

                    // Allocate and pin buffers
                    // IMPORTANT: PulseAudio may request more data than our buffer size in callbacks
                    // With tlength=100ms and minreq=50ms, PulseAudio can request up to 100ms of audio
                    // At 44.1kHz stereo, 100ms = 44100 * 2 * 0.1 = 8820 samples
                    // Allocate 32x the buffer size to safely handle any callback request
                    int outputBufferSize = _samplesPerBuffer * 32;
                    _outputBuffer = new float[outputBufferSize];
                    _inputBuffer = new float[outputBufferSize];
                    _outputBufferHandle = GCHandle.Alloc(_outputBuffer, GCHandleType.Pinned);
                    _inputBufferHandle = GCHandle.Alloc(_inputBuffer, GCHandleType.Pinned);
                    _outputBufferPtr = _outputBufferHandle.AddrOfPinnedObject();
                    _inputBufferPtr = _inputBufferHandle.AddrOfPinnedObject();

                    // Create ring buffers (16x buffer size for smooth playback)
                    // Larger buffer prevents blocking in Send() and maintains steady tempo
                    int ringSize = _samplesPerBuffer * 16;
                    _outputRing = new LockFreeRingBuffer<float>(ringSize);
                    _inputRing = new LockFreeRingBuffer<float>(ringSize);

                    // Create buffer pool for input capture
                    _bufferPool = new AudioBufferPool(_samplesPerBuffer, initialPoolSize: 4, maxPoolSize: 16);

                    // Initialize PulseAudio
                    int result = InitializePulseAudio(config);
                    if (result != 0)
                        return result;

                    _isActive = 0; // Idle state
                    return 0;
                }
                catch (Exception ex)
                {
                    _errorCode = -1;
                    _isActive = -1;
                    return -1;
                }
            }
        }

        /// <summary>
        /// Initializes PulseAudio mainloop and context.
        /// </summary>
        private int InitializePulseAudio(AudioConfig config)
        {
            try
            {
                // Create threaded mainloop
                _mainLoop = pa_threaded_mainloop_new();
                if (_mainLoop == IntPtr.Zero)
                    return -1;

                // Get mainloop API
                IntPtr api = pa_threaded_mainloop_get_api(_mainLoop);
                if (api == IntPtr.Zero)
                    return -2;

                // Create context
                _context = pa_context_new(api, "OwnAudio");
                if (_context == IntPtr.Zero)
                    return -3;

                // Setup context state callback
                _contextStateCallback = OnContextStateChanged;
                pa_context_set_state_callback(_context, _contextStateCallback, IntPtr.Zero);

                // Start mainloop
                if (pa_threaded_mainloop_start(_mainLoop) != 0)
                    return -4;

                // Connect to PulseAudio server
                pa_threaded_mainloop_lock(_mainLoop);
                int connectResult = pa_context_connect(_context, null, 0, IntPtr.Zero);
                pa_threaded_mainloop_unlock(_mainLoop);

                if (connectResult != 0)
                    return -5;

                // Wait for context to be ready
                if (!_contextReadyEvent.Wait(TimeSpan.FromSeconds(5)))
                    return -6;

                // Create output stream if enabled
                if (config.EnableOutput)
                {
                    int result = CreateOutputStream(config);
                    if (result != 0)
                        return result;
                }

                // Create input stream if enabled
                if (config.EnableInput)
                {
                    int result = CreateInputStream(config);
                    if (result != 0)
                        return result;
                }

                return 0;
            }
            catch (Exception ex)
            {
                return -100;
            }
        }

        /// <summary>
        /// Creates the output stream for playback.
        /// </summary>
        private int CreateOutputStream(AudioConfig config)
        {
            try
            {
                // Sample specification
                var spec = new pa_sample_spec
                {
                    format = pa_sample_format_t.PA_SAMPLE_FLOAT32LE,
                    rate = (uint)config.SampleRate,
                    channels = (byte)config.Channels
                };

                pa_threaded_mainloop_lock(_mainLoop);

                // Create stream
                _outputStream = pa_stream_new(_context, "OwnAudio Output", ref spec, IntPtr.Zero);
                if (_outputStream == IntPtr.Zero)
                {
                    pa_threaded_mainloop_unlock(_mainLoop);
                    return -10;
                }

                // Setup buffer attributes for LOW LATENCY and PRECISE TIMING
                // CRITICAL: Match macOS/Windows behavior - request data at our buffer size intervals
                // tlength = our buffer size (NOT 2x) - this sets the target playback latency
                // minreq = half our buffer - triggers callback when buffer is half empty
                // This ensures ~11ms callbacks matching our 512 frame buffer @ 44.1kHz
                uint bufferBytes = (uint)(_samplesPerBuffer * sizeof(float));
                var bufferAttr = new pa_buffer_attr
                {
                    maxlength = unchecked((uint)-1),              // Let PulseAudio decide max
                    tlength = bufferBytes,                        // Target latency = our buffer size
                    prebuf = 0,                                    // No prebuffering - start immediately
                    minreq = bufferBytes / 2,                     // Request when half empty (triggers frequent callbacks)
                    fragsize = unchecked((uint)-1)                // Not used for playback
                };

                // Setup callbacks
                _outputWriteCallback = OnOutputStreamWrite;
                _outputStateCallback = OnOutputStreamStateChanged;
                pa_stream_set_write_callback(_outputStream, _outputWriteCallback, IntPtr.Zero);
                pa_stream_set_state_callback(_outputStream, _outputStateCallback, IntPtr.Zero);

                // Connect to device
                string? device = config.OutputDeviceId;
                // Stream flags - MUST START CORKED like macOS AudioOutputUnitStart pattern
                // This prevents callbacks from being invoked before Start() is called
                // CRITICAL: DO NOT use PA_STREAM_ADJUST_LATENCY - it overrides our precise buffer settings!
                // This flag causes PulseAudio to ignore tlength/minreq and use default ~200ms latency
                uint flags = PA_STREAM_START_CORKED |
                             PA_STREAM_AUTO_TIMING_UPDATE |
                             PA_STREAM_NO_REMIX_CHANNELS |
                             PA_STREAM_NO_REMAP_CHANNELS;

                int connectResult = pa_stream_connect_playback(
                    _outputStream,
                    device,
                    ref bufferAttr,
                    flags,
                    IntPtr.Zero,
                    IntPtr.Zero);

                pa_threaded_mainloop_unlock(_mainLoop);

                if (connectResult != 0)
                    return -11;

                // Wait for stream to be ready
                if (!_outputStreamReadyEvent.Wait(TimeSpan.FromSeconds(5)))
                    return -12;

                // Verify the actual stream sample rate and buffer attributes
                pa_threaded_mainloop_lock(_mainLoop);
                IntPtr specPtr = pa_stream_get_sample_spec(_outputStream);
                if (specPtr != IntPtr.Zero)
                {
                    unsafe
                    {
                        pa_sample_spec* actualSpec = (pa_sample_spec*)specPtr;
                        // Log.Info($"[PulseAudio] Requested sample rate: {config.SampleRate} Hz");
                        // Log.Info($"[PulseAudio] Actual stream sample rate: {actualSpec->rate} Hz");
                        // if (actualSpec->rate != config.SampleRate)
                        // {
                        //     Log.Warning($"[PulseAudio] WARNING: Sample rate mismatch! This will cause tempo issues.");
                        // }
                    }
                }

                // Query negotiated buffer attributes
                IntPtr bufferAttrPtr = pa_stream_get_buffer_attr(_outputStream);
                if (bufferAttrPtr != IntPtr.Zero)
                {
                    unsafe
                    {
                        pa_buffer_attr* actualAttr = (pa_buffer_attr*)bufferAttrPtr;
                        Log.Info($"[PulseAudio] Requested buffer attributes:");
                        Log.Info($"  - tlength (target latency): {bufferBytes} bytes ({bufferBytes / sizeof(float)} samples)");
                        Log.Info($"  - minreq (minimum request): {bufferBytes / 2} bytes ({bufferBytes / (2 * sizeof(float))} samples)");
                        Log.Info($"[PulseAudio] Negotiated buffer attributes:");
                        Log.Info($"  - maxlength: {actualAttr->maxlength} bytes ({actualAttr->maxlength / sizeof(float)} samples)");
                        Log.Info($"  - tlength: {actualAttr->tlength} bytes ({actualAttr->tlength / sizeof(float)} samples)");
                        Log.Info($"  - prebuf: {actualAttr->prebuf} bytes ({actualAttr->prebuf / sizeof(float)} samples)");
                        Log.Info($"  - minreq: {actualAttr->minreq} bytes ({actualAttr->minreq / sizeof(float)} samples)");
                        Log.Info($"  - fragsize: {actualAttr->fragsize} bytes ({actualAttr->fragsize / sizeof(float)} samples)");
                    }
                }
                pa_threaded_mainloop_unlock(_mainLoop);

                _currentOutputDeviceId = device;
                return 0;
            }
            catch (Exception ex)
            {
                return -13;
            }
        }

        /// <summary>
        /// Creates the input stream for recording.
        /// </summary>
        private int CreateInputStream(AudioConfig config)
        {
            try
            {
                // Sample specification
                var spec = new pa_sample_spec
                {
                    format = pa_sample_format_t.PA_SAMPLE_FLOAT32LE,
                    rate = (uint)config.SampleRate,
                    channels = (byte)config.Channels
                };

                pa_threaded_mainloop_lock(_mainLoop);

                // Create stream
                _inputStream = pa_stream_new(_context, "OwnAudio Input", ref spec, IntPtr.Zero);
                if (_inputStream == IntPtr.Zero)
                {
                    pa_threaded_mainloop_unlock(_mainLoop);
                    return -20;
                }

                // Setup buffer attributes
                var bufferAttr = new pa_buffer_attr
                {
                    maxlength = uint.MaxValue,
                    tlength = uint.MaxValue,
                    prebuf = uint.MaxValue,
                    minreq = uint.MaxValue,
                    fragsize = (uint)_bufferSizeBytes
                };

                // Setup callbacks
                _inputReadCallback = OnInputStreamRead;
                _inputStateCallback = OnInputStreamStateChanged;
                pa_stream_set_read_callback(_inputStream, _inputReadCallback, IntPtr.Zero);
                pa_stream_set_state_callback(_inputStream, _inputStateCallback, IntPtr.Zero);

                // Connect to device
                string? device = config.InputDeviceId;
                // Stream flags - MUST START CORKED
                uint flags = PA_STREAM_START_CORKED |
                             PA_STREAM_AUTO_TIMING_UPDATE |
                             PA_STREAM_NO_REMIX_CHANNELS |
                             PA_STREAM_NO_REMAP_CHANNELS;

                int connectResult = pa_stream_connect_record(
                    _inputStream,
                    device,
                    ref bufferAttr,
                    flags);

                pa_threaded_mainloop_unlock(_mainLoop);

                if (connectResult != 0)
                    return -21;

                // Wait for stream to be ready
                if (!_inputStreamReadyEvent.Wait(TimeSpan.FromSeconds(5)))
                    return -22;

                _currentInputDeviceId = device;
                return 0;
            }
            catch (Exception ex)
            {
                return -23;
            }
        }


        /// <summary>
        /// Starts the PulseAudio engine.
        /// </summary>
        /// <returns>0 on success, negative error code on failure.</returns>
        public int Start()
        {
            lock (_stateLock)
            {
                if (_isRunning == 1)
                    return 0; // Already running (idempotent)

                if (_context == IntPtr.Zero)
                    return -1;

                try
                {
                    // Clear ring buffers
                    _outputRing?.Clear();
                    _inputRing?.Clear();

                    // Reset timing diagnostics
                    _callbackCount = 0;
                    _totalSamplesWritten = 0;
                    _startTime = DateTime.Now;
                    _lastCallbackTime = DateTime.Now;

                    // Initialize adaptive resampling for tempo correction
                    // Start with nominal sample rate, will adjust dynamically based on tempo
                    _currentTempoRatio = 1.0;
                    _adaptiveSampleRate = _config.SampleRate;
                    _adaptiveResampler = new AudioResampler(
                        _config.SampleRate,        // Source: nominal rate (44100 Hz)
                        _adaptiveSampleRate,       // Target: will be adjusted dynamically
                        _config.Channels,
                        _samplesPerBuffer * 2      // Max frame size
                    );
                    _resampleBuffer = new float[_samplesPerBuffer * 4]; // Extra space for resampling

                    // Pre-fill output ring buffer with 2x buffer size for better Linux startup
                    // CRITICAL: PulseAudio/PipeWire needs more initial data to start playback smoothly
                    if (_config.EnableOutput && _outputRing != null)
                    {
                        int preRollSamples = _samplesPerBuffer * 2;  // 2x for Linux stability
                        float[] silenceBuffer = new float[preRollSamples];
                        Array.Clear(silenceBuffer, 0, preRollSamples);
                        _outputRing.Write(silenceBuffer.AsSpan());
                    }

                    _isRunning = 1;
                    _isActive = 1;

                    // CRITICAL: Uncork streams NOW (exactly like macOS AudioOutputUnitStart)
                    // This must happen AFTER _isRunning=1 so callbacks can process data
                    pa_threaded_mainloop_lock(_mainLoop);

                    if (_outputStream != IntPtr.Zero)
                    {
                        // Uncork the output stream to start playback
                        pa_stream_success_cb uncorkCallback = (stream, success, userdata) => { };
                        IntPtr operationPtr = pa_stream_cork(_outputStream, 0, uncorkCallback, IntPtr.Zero);
                        if (operationPtr != IntPtr.Zero)
                        {
                            pa_operation_unref(operationPtr);
                        }

                        // Trigger callback flow - necessary to start PulseAudio write callbacks
                        pa_stream_success_cb triggerCallback = (stream, success, userdata) => { };
                        IntPtr triggerOp = pa_stream_trigger(_outputStream, triggerCallback, IntPtr.Zero);
                        if (triggerOp != IntPtr.Zero)
                        {
                            pa_operation_unref(triggerOp);
                        }
                    }

                    if (_inputStream != IntPtr.Zero)
                    {
                        pa_stream_success_cb inputUncorkCallback = (stream, success, userdata) => { };
                        IntPtr inputOp = pa_stream_cork(_inputStream, 0, inputUncorkCallback, IntPtr.Zero);
                        if (inputOp != IntPtr.Zero)
                        {
                            pa_operation_unref(inputOp);
                        }
                    }

                    pa_threaded_mainloop_unlock(_mainLoop);

                    return 0;
                }
                catch (Exception ex)
                {
                    _isActive = -1;
                    return -2;
                }
            }
        }

        /// <summary>
        /// Stops the PulseAudio engine.
        /// </summary>
        /// <returns>0 on success, negative error code on failure.</returns>
        public int Stop()
        {
            lock (_stateLock)
            {
                if (_isRunning == 0)
                    return 0; // Already stopped (idempotent)

                try
                {
                    _isRunning = 0;

                    pa_threaded_mainloop_lock(_mainLoop);

                    // Cork (pause) streams to stop playback
                    if (_outputStream != IntPtr.Zero)
                    {
                        pa_stream_success_cb corkCallback = (stream, success, userdata) => { };
                        IntPtr op = pa_stream_cork(_outputStream, 1, corkCallback, IntPtr.Zero);
                        if (op != IntPtr.Zero)
                            pa_operation_unref(op);
                    }

                    if (_inputStream != IntPtr.Zero)
                    {
                        pa_stream_success_cb corkCallback = (stream, success, userdata) => { };
                        IntPtr op = pa_stream_cork(_inputStream, 1, corkCallback, IntPtr.Zero);
                        if (op != IntPtr.Zero)
                            pa_operation_unref(op);
                    }

                    pa_threaded_mainloop_unlock(_mainLoop);

                    _isActive = 0;
                    return 0;
                }
                catch (Exception ex)
                {
                    _isActive = -1;
                    return -1;
                }
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
            else if (state == pa_context_state_t.PA_CONTEXT_FAILED ||
                     state == pa_context_state_t.PA_CONTEXT_TERMINATED)
            {
                _isActive = -1;
                _errorCode = pa_context_errno(context);
            }
        }

        /// <summary>
        /// Output stream state changed callback.
        /// </summary>
        private void OnOutputStreamStateChanged(IntPtr stream, IntPtr userdata)
        {
            var state = pa_stream_get_state(stream);

            if (state == pa_stream_state_t.PA_STREAM_READY)
            {
                _outputStreamReadyEvent.Set();
            }
            else if (state == pa_stream_state_t.PA_STREAM_FAILED ||
                     state == pa_stream_state_t.PA_STREAM_TERMINATED)
            {
                _isActive = -1;
            }
        }

        /// <summary>
        /// Input stream state changed callback.
        /// </summary>
        private void OnInputStreamStateChanged(IntPtr stream, IntPtr userdata)
        {
            var state = pa_stream_get_state(stream);

            if (state == pa_stream_state_t.PA_STREAM_READY)
            {
                _inputStreamReadyEvent.Set();
            }
            else if (state == pa_stream_state_t.PA_STREAM_FAILED ||
                     state == pa_stream_state_t.PA_STREAM_TERMINATED)
            {
                _isActive = -1;
            }
        }

        /// <summary>
        /// Output stream write callback - ZERO ALLOCATION ZONE!
        /// Called by PulseAudio when it needs audio data.
        /// This is similar to macOS Core Audio render callback.
        /// EXACTLY MATCHES macOS OutputRenderCallback behavior.
        /// </summary>
        private void OnOutputStreamWrite(IntPtr stream, nuint bytes, IntPtr userdata)
        {
            // Early exit if buffers not initialized (should not happen in normal operation)
            if (_outputRing == null || _outputBuffer == null || _config == null)
                return;

            try
            {
                unsafe
                {
                    IntPtr buffer;
                    nuint size = bytes;

                    // Begin write operation
                    if (pa_stream_begin_write(stream, out buffer, ref size) < 0)
                        return;

                    int samplesToWrite = (int)(size / sizeof(float));

                    // Timing diagnostics (non-blocking, minimal overhead)
                    DateTime now = DateTime.Now;
                    long currentCallback = System.Threading.Interlocked.Increment(ref _callbackCount);
                    System.Threading.Interlocked.Add(ref _totalSamplesWritten, samplesToWrite);

                    // Calculate interval since last callback (every 10th callback to reduce overhead)
                    if (currentCallback % 10 == 0)
                    {
                        lock (_timingLock)
                        {
                            double intervalMs = (now - _lastCallbackTime).TotalMilliseconds;
                            _lastCallbackTime = now;

                            // Expected interval based on buffer size
                            double expectedIntervalMs = (_samplesPerBuffer * 1000.0) / (_config.SampleRate * _config.Channels);
                            double drift = intervalMs - expectedIntervalMs;

                            // Log timing every 100th callback only (reduced logging)
                            // if (currentCallback % 100 == 0)
                            // {
                            //     Log.Info($"[PA-Timing #{currentCallback}] Interval: {intervalMs:F2}ms (expected: {expectedIntervalMs:F2}ms, drift: {drift:F2}ms) | Requested: {samplesToWrite} samples | Ring: {_outputRing.AvailableRead}/{_outputRing.Capacity}");
                            // }
                        }
                    }

                    // Safety check buffer size
                    if (samplesToWrite > _outputBuffer.Length)
                    {
                        // Buffer too small - write silence to prevent audio glitches
                        System.Runtime.CompilerServices.Unsafe.InitBlock((void*)buffer, 0, (uint)size);
                        pa_stream_write(stream, buffer, size, IntPtr.Zero, 0, PA_SEEK_RELATIVE);
                        return;
                    }

                    // Monitor ring buffer level
                    int currentLevel = _outputRing.AvailableRead;
                    System.Threading.Interlocked.Exchange(ref _lastRingBufferLevel, currentLevel);

                    // Read from ring buffer - EXACTLY like macOS
                    int samplesRead = _outputRing.Read(_outputBuffer.AsSpan(0, samplesToWrite));

                    // Handle underrun - EXACTLY like macOS
                    if (samplesRead < samplesToWrite)
                    {
                        System.Threading.Interlocked.Increment(ref _underrunCount);

                        // NOTE: NEVER use Debug.WriteLine or Console.Write in real-time audio callbacks!
                        // On Linux, these can throw exceptions and disrupt audio processing.
                        // Use volatile counters instead and read them from another thread if needed.

                        // Fill remaining with silence using RT-safe zero memory
                        // EXACTLY like macOS ZeroMemoryRealTimeSafe
                        fixed (float* bufPtr = &_outputBuffer[samplesRead])
                        {
                            ZeroMemoryRealTimeSafe(bufPtr, samplesToWrite - samplesRead);
                        }
                    }

                    // Copy to PulseAudio buffer using optimized memory copy
                    // EXACTLY like macOS Buffer.MemoryCopy
                    nuint bytesToWrite = (nuint)(samplesToWrite * sizeof(float));
                    fixed (float* srcPtr = _outputBuffer)
                    {
                        Buffer.MemoryCopy(srcPtr, (void*)buffer, (long)size, (long)bytesToWrite);
                    }

                    // Commit the write - use ACTUAL bytes written, not requested size
                    pa_stream_write(stream, buffer, bytesToWrite, IntPtr.Zero, 0, PA_SEEK_RELATIVE);
                }
            }
            catch
            {
                // Avoid exceptions in callback - NEVER log here (not RT-safe)!
            }
        }

        /// <summary>
        /// Real-time safe zero memory operation.
        /// CRITICAL: No allocations, no locks, safe for audio callbacks.
        /// EXACTLY MATCHES macOS ZeroMemoryRealTimeSafe method.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void ZeroMemoryRealTimeSafe(float* ptr, int count)
        {
            if (count <= 0) return;

#if NET6_0_OR_GREATER
            // Modern .NET: Use intrinsic for optimal performance
            System.Runtime.CompilerServices.Unsafe.InitBlock(
                ptr, 0, (uint)(count * sizeof(float)));
#else
            // Legacy .NET/Mono: Manual loop (still RT-safe)
            float* end = ptr + count;
            for (float* p = ptr; p < end; p++)
                *p = 0f;
#endif
        }

        /// <summary>
        /// Input stream read callback - ZERO ALLOCATION ZONE!
        /// Called by PulseAudio when audio data is available.
        /// </summary>
        private void OnInputStreamRead(IntPtr stream, nuint bytes, IntPtr userdata)
        {
            if (_isRunning == 0 || _inputRing == null)
                return;

            try
            {
                unsafe
                {
                    IntPtr data;
                    nuint size;

                    // Peek at the data
                    if (pa_stream_peek(stream, out data, out size) < 0)
                        return;

                    if (data == IntPtr.Zero || size == 0)
                        return;

                    // Copy to ring buffer
                    int sampleCount = (int)(size / sizeof(float));
                    sampleCount = Math.Min(sampleCount, _inputBuffer.Length);

                    float* srcPtr = (float*)data.ToPointer();
                    fixed (float* destPtr = _inputBuffer)
                    {
                        Buffer.MemoryCopy(srcPtr, destPtr, (long)(_inputBuffer.Length * sizeof(float)), (long)size);
                    }

                    _inputRing.Write(_inputBuffer.AsSpan(0, sampleCount));

                    // Drop the data
                    pa_stream_drop(stream);
                }
            }
            catch
            {
                // Avoid exceptions in callback
            }
        }

        /// <summary>
        /// Sends audio samples to the output device.
        /// This is a BLOCKING CALL that waits until space is available in the buffer.
        /// CRITICAL: Must block to prevent pump thread from filling buffer too quickly!
        /// INCLUDES ADAPTIVE RESAMPLING to maintain accurate tempo on PulseAudio.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Send(Span<float> samples)
        {
            if (_isRunning == 0)
            {
                throw new AudioException("Engine not running");
            }

            if (_outputRing == null)
            {
                throw new AudioException("Output ring buffer not initialized");
            }

            // ADAPTIVE RESAMPLING FOR TEMPO CORRECTION (REAL-TIME SAFE)
            // Calculate tempo ratio and adjust sample rate dynamically
            Span<float> dataToSend = samples;

            if (_callbackCount > 10 && _adaptiveResampler != null)
            {
                double elapsedRealMs = (DateTime.Now - _startTime).TotalMilliseconds;
                double elapsedAudioMs = (_totalSamplesWritten * 1000.0) / (_config.SampleRate * _config.Channels);
                _currentTempoRatio = elapsedAudioMs / elapsedRealMs;

                // Calculate corrected sample rate
                // _currentTempoRatio = audio_written_to_callbacks / real_time_elapsed
                // If ratio < 1.0: PulseAudio is requesting data SLOWLY (consuming slowly)
                // If ratio > 1.0: PulseAudio is requesting data FAST (consuming fast)
                //
                // To match PulseAudio's consumption rate, we resample:
                // target_rate = source_rate * ratio
                // Example: ratio = 0.94 → 44100 * 0.94 = 41454 Hz
                // This produces fewer samples per input buffer, matching slow consumption
                int correctedSampleRate = (int)(_config.SampleRate * _currentTempoRatio);

                // Clamp to reasonable range (±10% max correction)
                int minRate = (int)(_config.SampleRate * 0.90);
                int maxRate = (int)(_config.SampleRate * 1.10);
                correctedSampleRate = Math.Clamp(correctedSampleRate, minRate, maxRate);

                // Only recreate resampler if rate changed significantly (>0.1%)
                if (Math.Abs(correctedSampleRate - _adaptiveSampleRate) > (_config.SampleRate * 0.001))
                {
                    _adaptiveSampleRate = correctedSampleRate;
                    _adaptiveResampler = new AudioResampler(
                        _config.SampleRate,        // Source: nominal rate
                        _adaptiveSampleRate,       // Target: corrected rate
                        _config.Channels,
                        _samplesPerBuffer * 2
                    );
                }

                // Resample if needed (zero-allocation)
                if (_adaptiveResampler.IsResamplingNeeded)
                {
                    Span<float> resampleOutput = _resampleBuffer.AsSpan();
                    int resampledCount = _adaptiveResampler.Resample(samples, resampleOutput);
                    dataToSend = resampleOutput.Slice(0, resampledCount);
                }
            }

            // CRITICAL FIX: Block until all samples are written
            // This prevents the pump thread from sending data faster than PulseAudio can consume it
            ReadOnlySpan<float> readOnlyData = dataToSend;
            int written = 0;
            int retryCount = 0;

            while (written < readOnlyData.Length && _isRunning == 1)
            {
                int result = _outputRing.Write(readOnlyData.Slice(written));
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
        /// Receives audio samples from the input device.
        /// </summary>
        public int Receives(out float[] samples)
        {
            samples = null!;

            if (_isRunning == 0 || _inputRing == null)
                return -1;

            int available = _inputRing.AvailableRead;
            if (available < _samplesPerBuffer)
                return -2; // Not enough data yet

            // Get buffer from pool
            samples = _bufferPool.Get();

            // Read from ring buffer
            int samplesRead = _inputRing.Read(samples.AsSpan(0, _samplesPerBuffer));

            return samplesRead > 0 ? 0 : -3;
        }

        /// <summary>
        /// Gets the native audio stream handle.
        /// </summary>
        public IntPtr GetStream()
        {
            return _outputStream != IntPtr.Zero ? _outputStream : _inputStream;
        }

        /// <summary>
        /// Gets the activation state.
        /// </summary>
        public int OwnAudioEngineActivate()
        {
            return _isActive;
        }

        /// <summary>
        /// Gets the stopped state.
        /// </summary>
        public int OwnAudioEngineStopped()
        {
            return _isRunning == 0 ? 1 : 0;
        }

        /// <summary>
        /// Gets timing statistics for diagnostics.
        /// </summary>
        public void PrintTimingStatistics()
        {
            lock (_timingLock)
            {
                if (_callbackCount == 0)
                {
                    //Log.Info("[PA-Stats] No callbacks received yet");
                    return;
                }

                double elapsedSec = (DateTime.Now - _startTime).TotalSeconds;
                double audioTimeSec = _totalSamplesWritten / (double)(_config.SampleRate * _config.Channels);
                double tempoRatio = audioTimeSec / elapsedSec;
                double tempoError = (tempoRatio - 1.0) * 100.0;

                Console.WriteLine("\n========== PulseAudio Timing Statistics ==========");
                Console.WriteLine($"Total callbacks: {_callbackCount}");
                Console.WriteLine($"Total samples written: {_totalSamplesWritten}");
                Console.WriteLine($"Real-time elapsed: {elapsedSec:F2} seconds");
                Console.WriteLine($"Audio time written: {audioTimeSec:F2} seconds");
                Console.WriteLine($"Tempo ratio (before correction): {tempoRatio:F6} (1.000000 = perfect)");
                Console.WriteLine($"Tempo error (before correction): {tempoError:+0.00;-0.00}%");
                Console.WriteLine($"Average samples per callback: {_totalSamplesWritten / _callbackCount}");
                Console.WriteLine($"Buffer underruns: {_underrunCount}");
                Console.WriteLine();
                Console.WriteLine("--- Adaptive Resampling ---");
                Console.WriteLine($"Nominal sample rate: {_config.SampleRate} Hz");
                Console.WriteLine($"Adaptive sample rate: {_adaptiveSampleRate} Hz");
                Console.WriteLine($"Correction factor: {_config.SampleRate / (double)_adaptiveSampleRate:F6}");
                Console.WriteLine($"Expected tempo after resampling: {1.0 / _currentTempoRatio:F6}");
                Console.WriteLine("==================================================\n");
            }
        }

        #region Device Management

        /// <summary>
        /// Gets all output devices.
        /// </summary>
        public List<AudioDeviceInfo> GetOutputDevices()
        {
            return _deviceEnumHelper.EnumerateOutputDevices();
        }

        /// <summary>
        /// Gets all input devices.
        /// </summary>
        public List<AudioDeviceInfo> GetInputDevices()
        {
            return _deviceEnumHelper.EnumerateInputDevices();
        }

        /// <summary>
        /// Sets the output device by name.
        /// </summary>
        public int SetOutputDeviceByName(string deviceName)
        {
            if (_isRunning == 1)
                return -1; // Must stop before changing devices

            var devices = GetOutputDevices();
            var device = devices.Find(d => d.Name == deviceName);
            if (device == null)
                return -3; // Device not found

            _config.OutputDeviceId = device.DeviceId;
            return 0;
        }

        /// <summary>
        /// Sets the output device by index.
        /// </summary>
        public int SetOutputDeviceByIndex(int deviceIndex)
        {
            if (_isRunning == 1)
                return -1;

            var devices = GetOutputDevices();
            if (deviceIndex < 0 || deviceIndex >= devices.Count)
                return -2; // Invalid index

            _config.OutputDeviceId = devices[deviceIndex].DeviceId;
            return 0;
        }

        /// <summary>
        /// Sets the input device by name.
        /// </summary>
        public int SetInputDeviceByName(string deviceName)
        {
            if (_isRunning == 1)
                return -1;

            var devices = GetInputDevices();
            var device = devices.Find(d => d.Name == deviceName);
            if (device == null)
                return -3;

            _config.InputDeviceId = device.DeviceId;
            return 0;
        }

        /// <summary>
        /// Sets the input device by index.
        /// </summary>
        public int SetInputDeviceByIndex(int deviceIndex)
        {
            if (_isRunning == 1)
                return -1;

            var devices = GetInputDevices();
            if (deviceIndex < 0 || deviceIndex >= devices.Count)
                return -2;

            _config.InputDeviceId = devices[deviceIndex].DeviceId;
            return 0;
        }

        /// <inheritdoc/>
        public void PauseDeviceMonitoring()
        {
            // PulseAudio implementation: no background device monitoring
        }

        /// <inheritdoc/>
        public void ResumeDeviceMonitoring()
        {
            // PulseAudio implementation: no background device monitoring
        }

        #endregion

        #region Disposal

        /// <summary>
        /// Disposes of all resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            lock (_stateLock)
            {
                if (_isRunning == 1)
                    Stop();

                if (_mainLoop != IntPtr.Zero)
                {
                    pa_threaded_mainloop_lock(_mainLoop);

                    // Disconnect and cleanup streams
                    if (_outputStream != IntPtr.Zero)
                    {
                        pa_stream_disconnect(_outputStream);
                        pa_stream_unref(_outputStream);
                        _outputStream = IntPtr.Zero;
                    }

                    if (_inputStream != IntPtr.Zero)
                    {
                        pa_stream_disconnect(_inputStream);
                        pa_stream_unref(_inputStream);
                        _inputStream = IntPtr.Zero;
                    }

                    // Disconnect context
                    if (_context != IntPtr.Zero)
                    {
                        pa_context_disconnect(_context);
                        pa_context_unref(_context);
                        _context = IntPtr.Zero;
                    }

                    pa_threaded_mainloop_unlock(_mainLoop);

                    // Stop and free mainloop
                    pa_threaded_mainloop_stop(_mainLoop);
                    pa_threaded_mainloop_free(_mainLoop);
                    _mainLoop = IntPtr.Zero;
                }

                // Unpin buffers
                if (_outputBufferHandle.IsAllocated)
                    _outputBufferHandle.Free();

                if (_inputBufferHandle.IsAllocated)
                    _inputBufferHandle.Free();

                // Dispose events
                _contextReadyEvent?.Dispose();
                _outputStreamReadyEvent?.Dispose();
                _inputStreamReadyEvent?.Dispose();
            }
        }

        #endregion
    }
}
