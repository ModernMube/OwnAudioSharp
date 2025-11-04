using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using Ownaudio.macOS.Interop;
using static Ownaudio.macOS.Interop.CoreAudioInterop;

namespace Ownaudio.macOS
{
    /// <summary>
    /// macOS Core Audio engine implementation using Audio Unit API.
    /// Zero-allocation, real-time safe audio processing with direct hardware access.
    /// Provides sample-accurate timing similar to Windows WASAPI.
    /// </summary>
    public sealed class CoreAudioEngine : IAudioEngine
    {
        // Audio Unit handles
        private IntPtr _outputAudioUnit;
        private IntPtr _inputAudioUnit;

        // Pre-allocated buffers (pinned)
        private float[] _outputBuffer;
        private float[] _inputBuffer;
        private GCHandle _outputBufferHandle;
        private GCHandle _inputBufferHandle;
        private IntPtr _outputBufferPtr;
        private IntPtr _inputBufferPtr;

        // AudioBufferList for input capture
        private IntPtr _inputBufferListPtr;
        private int _inputBufferListSize;

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

        // Callbacks (must be kept alive to prevent GC)
        private AURenderCallback _outputCallback;
        private AURenderCallback _inputCallback;

        // Threading
        private readonly object _stateLock = new object();
        //private Thread? _audioThread;
        private volatile int _stopRequested; // 0 = keep running, 1 = stop requested

        // Buffer pool for Receives()
        private AudioBufferPool _bufferPool;

        // Device management
        private CoreAudioDeviceEnumerator _deviceEnumHelper;
        private uint _currentOutputDeviceId;
        private uint _currentInputDeviceId;
        private AudioObjectPropertyListenerProc _deviceChangeListener;

        // Sample rate tracking - CRITICAL for correct playback speed
        private double _actualOutputSampleRate;
        private double _actualInputSampleRate;

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
        /// Creates a new Core Audio engine instance.
        /// </summary>
        public CoreAudioEngine()
        {
            _isRunning = 0;
            _isActive = 0;
            _errorCode = 0;
            _deviceEnumHelper = new CoreAudioDeviceEnumerator();
        }

        /// <summary>
        /// Initializes the Core Audio engine with the specified configuration.
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


                    // Initialize output device if enabled
                    if (config.EnableOutput)
                    {
                        int result = InitializeOutputDevice(config);
                        if (result != 0)
                        {
                            return result;
                        }
                    }

                    // Initialize input device if enabled
                    if (config.EnableInput)
                    {
                        int result = InitializeInputDevice(config);
                        if (result != 0)
                        {
                            return result;
                        }
                    }

                    // Allocate and pin buffers
                    _outputBuffer = new float[_samplesPerBuffer];
                    _inputBuffer = new float[_samplesPerBuffer];
                    _outputBufferHandle = GCHandle.Alloc(_outputBuffer, GCHandleType.Pinned);
                    _inputBufferHandle = GCHandle.Alloc(_inputBuffer, GCHandleType.Pinned);
                    _outputBufferPtr = _outputBufferHandle.AddrOfPinnedObject();
                    _inputBufferPtr = _inputBufferHandle.AddrOfPinnedObject();

                    // Create ring buffers (16x buffer size for better buffering)
                    int ringSize = _samplesPerBuffer * 16;
                    _outputRing = new LockFreeRingBuffer<float>(ringSize);
                    _inputRing = new LockFreeRingBuffer<float>(ringSize);

                    // Create buffer pool for input capture
                    _bufferPool = new AudioBufferPool(_samplesPerBuffer, initialPoolSize: 4, maxPoolSize: 16);

                    // Register device change notifications
                    RegisterDeviceNotifications();

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
        /// Initializes the output Audio Unit.
        /// </summary>
        private int InitializeOutputDevice(AudioConfig config)
        {
            try
            {
                // Get default output device ID
                if (!string.IsNullOrEmpty(config.OutputDeviceId))
                {
                    if (uint.TryParse(config.OutputDeviceId, out uint deviceId))
                    {
                        _currentOutputDeviceId = deviceId;
                    }
                }
                else
                {
                    // Use device enumerator to get default device
                    var outputDevices = _deviceEnumHelper.EnumerateOutputDevices();
                    var defaultDevice = outputDevices.Find(d => d.IsDefault);
                    if (defaultDevice != null && uint.TryParse(defaultDevice.DeviceId, out uint devId))
                    {
                        _currentOutputDeviceId = devId;
                    }
                    else if (outputDevices.Count > 0 && uint.TryParse(outputDevices[0].DeviceId, out uint firstDevId))
                    {
                        // Fallback to first device
                        _currentOutputDeviceId = firstDevId;
                    }
                    else
                    {
                        return -1;
                    }
                }

                // CRITICAL: Get and verify device sample rate
                double deviceSampleRate = GetDeviceSampleRate(_currentOutputDeviceId);

                // Try to set the device sample rate if it differs
                if (Math.Abs(deviceSampleRate - config.SampleRate) > 0.1)
                {
                    SetDeviceSampleRate(_currentOutputDeviceId, config.SampleRate);
                    deviceSampleRate = GetDeviceSampleRate(_currentOutputDeviceId);
                }

                _actualOutputSampleRate = deviceSampleRate;

                // Find the HAL Output Audio Unit component
                var desc = new AudioComponentDescription
                {
                    componentType = kAudioUnitType_Output,
                    componentSubType = kAudioUnitSubType_HALOutput,
                    componentManufacturer = kAudioUnitManufacturer_Apple,
                    componentFlags = 0,
                    componentFlagsMask = 0
                };

                IntPtr component = AudioComponentFindNext(IntPtr.Zero, ref desc);
                if (component == IntPtr.Zero)
                {
                    return -1;
                }

                // Create Audio Unit instance
                int status = AudioComponentInstanceNew(component, out _outputAudioUnit);
                if (!IsSuccess(status))
                {
                    return status;
                }

                // Set the current device
                unsafe
                {
                    uint deviceId = _currentOutputDeviceId;
                    status = AudioUnitSetProperty(
                        _outputAudioUnit,
                        kAudioOutputUnitProperty_CurrentDevice,
                        kAudioUnitScope_Global,
                        0,
                        new IntPtr(&deviceId),
                        sizeof(uint));

                    if (!IsSuccess(status))
                    {
                        return status;
                    }
                }

                // Configure stream format - INTERLEAVED for compatibility
                var format = AudioStreamBasicDescription.CreateFloat32InterleavedPCM(config.SampleRate, config.Channels);

                unsafe
                {
                    status = AudioUnitSetProperty(
                        _outputAudioUnit,
                        kAudioUnitProperty_StreamFormat,
                        kAudioUnitScope_Input, // Input to the output unit (our data)
                        0,
                        new IntPtr(&format),
                        (uint)sizeof(AudioStreamBasicDescription));

                    if (!IsSuccess(status))
                    {
                        return status;
                    }
                }

                // Set maximum frames per slice
                unsafe
                {
                    uint maxFrames = (uint)_framesPerBuffer;
                    status = AudioUnitSetProperty(
                        _outputAudioUnit,
                        kAudioUnitProperty_MaximumFramesPerSlice,
                        kAudioUnitScope_Global,
                        0,
                        new IntPtr(&maxFrames),
                        sizeof(uint));

                    // Not critical, continue even if it fails
                }

                // Set render callback
                // CRITICAL: Keep delegate alive to prevent GC collection
                _outputCallback = OutputRenderCallback;
                IntPtr callbackPtr = Marshal.GetFunctionPointerForDelegate(_outputCallback);

                var callbackStruct = new AURenderCallbackStruct
                {
                    inputProc = callbackPtr,
                    inputProcRefCon = IntPtr.Zero
                };

                unsafe
                {
                    status = AudioUnitSetProperty(
                        _outputAudioUnit,
                        kAudioUnitProperty_SetRenderCallback,
                        kAudioUnitScope_Input,
                        0,
                        new IntPtr(&callbackStruct),
                        (uint)sizeof(AURenderCallbackStruct));

                    if (!IsSuccess(status))
                    {
                        return status;
                    }
                }

                // Initialize the Audio Unit
                status = AudioUnitInitialize(_outputAudioUnit);
                if (!IsSuccess(status))
                {
                    return status;
                }


                return 0;
            }
            catch (Exception ex)
            {
                return -1;
            }
        }

        /// <summary>
        /// Initializes the input Audio Unit.
        /// </summary>
        private int InitializeInputDevice(AudioConfig config)
        {
            try
            {
                // Get default input device ID
                if (!string.IsNullOrEmpty(config.InputDeviceId))
                {
                    if (uint.TryParse(config.InputDeviceId, out uint deviceId))
                    {
                        _currentInputDeviceId = deviceId;
                    }
                }
                else
                {
                    // Use device enumerator to get default device
                    var inputDevices = _deviceEnumHelper.EnumerateInputDevices();
                    var defaultDevice = inputDevices.Find(d => d.IsDefault);
                    if (defaultDevice != null && uint.TryParse(defaultDevice.DeviceId, out uint devId))
                    {
                        _currentInputDeviceId = devId;
                    }
                    else if (inputDevices.Count > 0 && uint.TryParse(inputDevices[0].DeviceId, out uint firstDevId))
                    {
                        // Fallback to first device
                        _currentInputDeviceId = firstDevId;
                    }
                    else
                    {
                        return -1;
                    }
                }

                // CRITICAL: Get and verify device sample rate
                double deviceSampleRate = GetDeviceSampleRate(_currentInputDeviceId);

                // Try to set the device sample rate if it differs
                if (Math.Abs(deviceSampleRate - config.SampleRate) > 0.1)
                {
                    SetDeviceSampleRate(_currentInputDeviceId, config.SampleRate);
                    deviceSampleRate = GetDeviceSampleRate(_currentInputDeviceId);
                }

                _actualInputSampleRate = deviceSampleRate;

                // Find the HAL Output Audio Unit component (yes, same component for input)
                var desc = new AudioComponentDescription
                {
                    componentType = kAudioUnitType_Output,
                    componentSubType = kAudioUnitSubType_HALOutput,
                    componentManufacturer = kAudioUnitManufacturer_Apple,
                    componentFlags = 0,
                    componentFlagsMask = 0
                };

                IntPtr component = AudioComponentFindNext(IntPtr.Zero, ref desc);
                if (component == IntPtr.Zero)
                {
                    return -1;
                }

                // Create Audio Unit instance
                int status = AudioComponentInstanceNew(component, out _inputAudioUnit);
                if (!IsSuccess(status))
                {
                    return status;
                }

                // Enable input on the Audio Unit
                unsafe
                {
                    uint enableIO = 1;
                    status = AudioUnitSetProperty(
                        _inputAudioUnit,
                        kAudioOutputUnitProperty_EnableIO,
                        kAudioUnitScope_Input, // Enable input
                        1, // Input element
                        new IntPtr(&enableIO),
                        sizeof(uint));

                    if (!IsSuccess(status))
                    {
                        return status;
                    }

                    // Disable output on the input unit
                    enableIO = 0;
                    status = AudioUnitSetProperty(
                        _inputAudioUnit,
                        kAudioOutputUnitProperty_EnableIO,
                        kAudioUnitScope_Output, // Disable output
                        0, // Output element
                        new IntPtr(&enableIO),
                        sizeof(uint));

                    if (!IsSuccess(status))
                    {
                        return status;
                    }
                }

                // Set the current device
                unsafe
                {
                    uint deviceId = _currentInputDeviceId;
                    status = AudioUnitSetProperty(
                        _inputAudioUnit,
                        kAudioOutputUnitProperty_CurrentDevice,
                        kAudioUnitScope_Global,
                        0,
                        new IntPtr(&deviceId),
                        sizeof(uint));

                    if (!IsSuccess(status))
                    {
                        return status;
                    }
                }

                // Configure stream format
                var format = AudioStreamBasicDescription.CreateFloat32InterleavedPCM(config.SampleRate, config.Channels);

                unsafe
                {
                    // Set format on output scope (data coming out of the unit to us)
                    status = AudioUnitSetProperty(
                        _inputAudioUnit,
                        kAudioUnitProperty_StreamFormat,
                        kAudioUnitScope_Output,
                        1, // Input element
                        new IntPtr(&format),
                        (uint)sizeof(AudioStreamBasicDescription));

                    if (!IsSuccess(status))
                    {
                        return status;
                    }
                }

                // Allocate AudioBufferList for input capture
                // AudioBufferList contains 1 buffer in the struct, we need space for additional buffers if channels > 1
                int additionalBuffers = Math.Max(0, config.Channels - 1);
                _inputBufferListSize = Marshal.SizeOf<AudioBufferList>() + (additionalBuffers * Marshal.SizeOf<CoreAudioBuffer>());
                _inputBufferListPtr = Marshal.AllocHGlobal(_inputBufferListSize);

                unsafe
                {
                    AudioBufferList* bufferList = (AudioBufferList*)_inputBufferListPtr;

                    // For INTERLEAVED audio, we only need 1 buffer regardless of channel count
                    bufferList->mNumberBuffers = 1;

                    // Set up the first (and only) buffer for interleaved audio
                    bufferList->mFirstBuffer.mNumberChannels = (uint)config.Channels;
                    bufferList->mFirstBuffer.mDataByteSize = (uint)_bufferSizeBytes;
                    bufferList->mFirstBuffer.mData = _inputBufferPtr;
                }

                // Set input callback
                _inputCallback = InputRenderCallback;
                IntPtr inputCallbackPtr = Marshal.GetFunctionPointerForDelegate(_inputCallback);
                var callbackStruct = new AURenderCallbackStruct
                {
                    inputProc = inputCallbackPtr,
                    inputProcRefCon = IntPtr.Zero
                };

                unsafe
                {
                    status = AudioUnitSetProperty(
                        _inputAudioUnit,
                        kAudioOutputUnitProperty_SetInputCallback,
                        kAudioUnitScope_Global,
                        1,
                        new IntPtr(&callbackStruct),
                        (uint)sizeof(AURenderCallbackStruct));

                    if (!IsSuccess(status))
                    {
                        return status;
                    }
                }

                // Initialize the Audio Unit
                status = AudioUnitInitialize(_inputAudioUnit);
                if (!IsSuccess(status))
                {
                    return status;
                }

                return 0;
            }
            catch (Exception ex)
            {
                return -1;
            }
        }

        /// <summary>
        /// Starts the Core Audio engine with a dedicated high-priority audio thread.
        /// CRITICAL: Pre-fills the output ring buffer before starting the Audio Unit
        /// to prevent the Core Audio callback from consuming data too quickly.
        /// </summary>
        /// <returns>0 on success, negative error code on failure.</returns>
        public int Start()
        {
            lock (_stateLock)
            {
                if (_isRunning == 1)
                    return 0; // Already running (idempotent)

                if (_outputAudioUnit == IntPtr.Zero && _inputAudioUnit == IntPtr.Zero)
                    return -1;

                try
                {
                    // Clear ring buffers
                    _outputRing?.Clear();
                    _inputRing?.Clear();

                    // Reset stop flag
                    _stopRequested = 0;

                    // CRITICAL FIX: Pre-fill output ring buffer before starting Audio Unit
                    // This prevents the Core Audio callback from immediately consuming all data
                    // at maximum speed when the Audio Unit starts.
                    if (_config.EnableOutput && _outputRing != null)
                    {
                        // Calculate pre-roll buffer size: 3x the hardware buffer size
                        // This ensures stable playback timing from the start
                        int preRollSamples = _samplesPerBuffer * 3;

                        // Create silence buffer for pre-roll
                        float[] silenceBuffer = new float[preRollSamples];
                        Array.Clear(silenceBuffer, 0, preRollSamples);

                        // Fill the ring buffer with initial silence
                        // The application will replace this with actual audio data
                        _outputRing.Write(silenceBuffer.AsSpan());
                    }

                    // Start the dedicated audio thread
                    // This thread will handle the Audio Unit operations independently
                    _isRunning = 1;
                    _isActive = 1;

                    // _audioThread = new Thread(AudioThreadProc)
                    // {
                    //     Priority = ThreadPriority.Highest,
                    //     IsBackground = false,
                    //     Name = "CoreAudio RT Thread"
                    // };
                    // _audioThread.Start();
                    CoreAudioInterop.ThrowIfError(
                        AudioOutputUnitStart(_outputAudioUnit),
                        "AudioOutputUnitStart");

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
        /// Stops the Core Audio engine and the dedicated audio thread.
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
                    // Signal stop to the audio thread
                    _isRunning = 0;
                    _stopRequested = 1;

                    // Wait for the audio thread to finish (with timeout)
                    // if (_audioThread != null && _audioThread.IsAlive)
                    // {
                    //     if (!_audioThread.Join(5000))
                    //     {
                    //         // Force abort if thread doesn't finish in time
                    //         _audioThread.Interrupt();
                    //     }
                    //     _audioThread = null;
                    // }
                    CoreAudioInterop.ThrowIfError(
                        AudioOutputUnitStop(_outputAudioUnit),
                        "AudioOutputUnitStop");

                    // Stop output unit
                    if (_outputAudioUnit != IntPtr.Zero)
                    {
                        AudioOutputUnitStop(_outputAudioUnit);
                    }

                    // Stop input unit
                    if (_inputAudioUnit != IntPtr.Zero)
                    {
                        AudioOutputUnitStop(_inputAudioUnit);
                    }

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
        /// Audio thread procedure - manages Audio Unit lifecycle on a dedicated high-priority thread.
        /// This ensures audio processing is not interrupted by other application activities.
        /// </summary>
        private void AudioThreadProc()
        {
            try
            {
                // CRITICAL: Set MACH real-time priority for this thread
                // This is THE CORRECT way to create real-time audio threads on macOS
                // Apple's Core Audio uses this internally for all audio callbacks
                bool rtSuccess = MachThreadInterop.SetThreadToAudioRealTimePriority(
                    _config.SampleRate,
                    _framesPerBuffer);

                if (!rtSuccess)
                {
                    // Log warning but continue - thread will use standard priority
                    System.Diagnostics.Debug.WriteLine(
                        "[CoreAudio] Warning: Failed to set MACH real-time priority. " +
                        "Audio may experience glitches under load.");
                }

                // Start output unit on the dedicated thread
                if (_config.EnableOutput && _outputAudioUnit != IntPtr.Zero)
                {
                    int status = AudioOutputUnitStart(_outputAudioUnit);
                    if (!IsSuccess(status))
                    {
                        _isActive = -1;
                        _errorCode = status;
                        return;
                    }
                }

                // Start input unit on the dedicated thread
                if (_config.EnableInput && _inputAudioUnit != IntPtr.Zero)
                {
                    int status = AudioOutputUnitStart(_inputAudioUnit);
                    if (!IsSuccess(status))
                    {
                        _isActive = -1;
                        _errorCode = status;
                        return;
                    }
                }

                // Keep the thread alive while audio is running
                // The actual audio processing happens in the Core Audio callbacks
                // This thread just needs to stay alive to maintain the audio context
                while (_stopRequested == 0 && _isRunning == 1)
                {
                    // Sleep for a short period to avoid busy-waiting
                    // Calculate sleep time based on buffer size
                    int sleepMs = Math.Max(1, _framesPerBuffer * 1000 / _config.SampleRate / 4);
                    Thread.Sleep(sleepMs);
                }
            }
            catch (Exception ex)
            {
                _isActive = -1;
                _errorCode = -1;
            }
        }

        /// <summary>
        /// Output render callback - ZERO ALLOCATION ZONE!
        /// Called by Core Audio when it needs audio data.
        /// This is similar to WASAPI's render process.
        /// </summary>
        private int OutputRenderCallback(
            IntPtr inRefCon,
            ref uint ioActionFlags,
            ref AudioTimeStamp inTimeStamp,
            uint inBusNumber,
            uint inNumberFrames,
            IntPtr ioData)
        {
            if (_isRunning == 0)
            {
                // Output silence if not running
                ioActionFlags |= 1; // kAudioUnitRenderAction_OutputIsSilence
                return noErr;
            }

            try
            {
                // Safety checks
                if (_outputRing == null || _outputBuffer == null || _config == null)
                {
                    ioActionFlags |= 1;
                    return noErr;
                }

                unsafe
                {
                    // Get the AudioBufferList
                    AudioBufferList* bufferList = (AudioBufferList*)ioData;

                    // For interleaved audio, there's only one buffer
                    if (bufferList->mNumberBuffers < 1)
                    {
                        return noErr;
                    }

                    // CRITICAL FIX: The first buffer is PART OF the AudioBufferList struct!
                    // Access it directly via mFirstBuffer field, not by pointer arithmetic
                    CoreAudioBuffer* buffer = &bufferList->mFirstBuffer;

                    int samplesToWrite = (int)inNumberFrames * _config.Channels;

                    // Safety check buffer size
                    if (samplesToWrite > _outputBuffer.Length)
                    {
                        ioActionFlags |= 1;
                        return noErr;
                    }

                    // Monitor ring buffer level
                    int currentLevel = _outputRing.AvailableRead;
                    System.Threading.Interlocked.Exchange(ref _lastRingBufferLevel, currentLevel);

                    // Read from ring buffer
                    int samplesRead = _outputRing.Read(_outputBuffer.AsSpan(0, samplesToWrite));

                    // Get destination pointer
                    float* destPtr = (float*)buffer->mData.ToPointer();

                    // Handle underrun
                    if (samplesRead < samplesToWrite)
                    {
                        System.Threading.Interlocked.Increment(ref _underrunCount);

                        // NOTE: NEVER use Debug.WriteLine or Console.Write in real-time audio callbacks!
                        // On macOS, these can throw InvalidOperationException and disrupt audio processing.
                        // Use volatile counters instead and read them from another thread if needed.

                        // Fill remaining with silence using RT-safe zero memory
                        fixed (float* bufPtr = &_outputBuffer[samplesRead])
                        {
                            ZeroMemoryRealTimeSafe(bufPtr, samplesToWrite - samplesRead);
                        }
                        ioActionFlags |= 1; // kAudioUnitRenderAction_OutputIsSilence
                    }

                    // Copy to Audio Unit buffer using optimized memory copy
                    fixed (float* srcPtr = _outputBuffer)
                    {
                        uint byteCount = (uint)(samplesToWrite * sizeof(float));
                        Buffer.MemoryCopy(srcPtr, destPtr, buffer->mDataByteSize, byteCount);
                    }
                }

                return noErr;
            }
            catch
            {
                // Avoid exceptions in callback
                ioActionFlags |= 1;
                return noErr;
            }
        }

        /// <summary>
        /// Real-time safe zero memory operation.
        /// CRITICAL: No allocations, no locks, safe for audio callbacks.
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
        /// Input render callback - ZERO ALLOCATION ZONE!
        /// Called by Core Audio to capture audio data.
        /// </summary>
        private int InputRenderCallback(
            IntPtr inRefCon,
            ref uint ioActionFlags,
            ref AudioTimeStamp inTimeStamp,
            uint inBusNumber,
            uint inNumberFrames,
            IntPtr ioData)
        {
            if (_isRunning == 0)
                return noErr;

            try
            {
                // Render the input audio
                int status = AudioUnitRender(
                    _inputAudioUnit,
                    ref ioActionFlags,
                    ref inTimeStamp,
                    1, // Input element
                    inNumberFrames,
                    _inputBufferListPtr);

                if (!IsSuccess(status))
                    return status;

                // Copy captured data to ring buffer
                int samplesToRead = (int)inNumberFrames * _config.Channels;
                _inputRing.Write(_inputBuffer.AsSpan(0, samplesToRead));

                return noErr;
            }
            catch
            {
                return noErr;
            }
        }

        /// <summary>
        /// Sends audio samples to the output device.
        /// This is a BLOCKING CALL that waits until space is available in the buffer.
        /// CRITICAL: Must block to prevent pump thread from filling buffer too quickly!
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

            // CRITICAL FIX: Block until all samples are written
            // This prevents the pump thread from sending data faster than Core Audio can consume it
            ReadOnlySpan<float> readOnlyData = samples;
            int written = 0;
            int retryCount = 0;

            while (written < readOnlyData.Length && _isRunning == 1)
            {
                int result = _outputRing.Write(readOnlyData.Slice(written));
                written += result;

                if (result == 0)
                {
                    // Buffer full, use hybrid wait strategy (same as Windows WASAPI)
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
            return _outputAudioUnit != IntPtr.Zero ? _outputAudioUnit : _inputAudioUnit;
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

        #region Device Management

        /// <summary>
        /// Gets all output devices.
        /// </summary>
        public System.Collections.Generic.List<AudioDeviceInfo> GetOutputDevices()
        {
            return _deviceEnumHelper.EnumerateOutputDevices();
        }

        /// <summary>
        /// Gets all input devices.
        /// </summary>
        public System.Collections.Generic.List<AudioDeviceInfo> GetInputDevices()
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

            return SetOutputDevice(device.DeviceId);
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

            return SetOutputDevice(devices[deviceIndex].DeviceId);
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

            return SetInputDevice(device.DeviceId);
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

            return SetInputDevice(devices[deviceIndex].DeviceId);
        }

        private int SetOutputDevice(string deviceId)
        {
            if (!uint.TryParse(deviceId, out uint audioDeviceId))
                return -4; // Invalid device ID format

            if (_outputAudioUnit == IntPtr.Zero)
                return -5; // Output not initialized

            unsafe
            {
                int status = AudioUnitSetProperty(
                    _outputAudioUnit,
                    kAudioOutputUnitProperty_CurrentDevice,
                    kAudioUnitScope_Global,
                    0,
                    new IntPtr(&audioDeviceId),
                    sizeof(uint));

                if (IsSuccess(status))
                {
                    _currentOutputDeviceId = audioDeviceId;
                    return 0;
                }

                return status;
            }
        }

        private int SetInputDevice(string deviceId)
        {
            if (!uint.TryParse(deviceId, out uint audioDeviceId))
                return -4;

            if (_inputAudioUnit == IntPtr.Zero)
                return -5;

            unsafe
            {
                int status = AudioUnitSetProperty(
                    _inputAudioUnit,
                    kAudioOutputUnitProperty_CurrentDevice,
                    kAudioUnitScope_Global,
                    0,
                    new IntPtr(&audioDeviceId),
                    sizeof(uint));

                if (IsSuccess(status))
                {
                    _currentInputDeviceId = audioDeviceId;
                    return 0;
                }

                return status;
            }
        }

        private uint GetDefaultOutputDeviceId()
        {
            var address = new AudioObjectPropertyAddress(
                kAudioHardwarePropertyDefaultOutputDevice,
                kAudioObjectPropertyScopeGlobal,
                kAudioObjectPropertyElementMaster);

            uint deviceId = 0;
            uint dataSize = sizeof(uint);

            unsafe
            {
                int status = AudioObjectGetPropertyData(
                    kAudioObjectSystemObject,
                    ref address,
                    0,
                    IntPtr.Zero,
                    ref dataSize,
                    new IntPtr(&deviceId));

                if (!IsSuccess(status))
                {
                    return 0;
                }

                return deviceId;
            }
        }

        private uint GetDefaultInputDeviceId()
        {
            var address = new AudioObjectPropertyAddress(
                kAudioHardwarePropertyDefaultInputDevice,
                kAudioObjectPropertyScopeGlobal,
                kAudioObjectPropertyElementMaster);

            uint deviceId = 0;
            uint dataSize = sizeof(uint);

            unsafe
            {
                int status = AudioObjectGetPropertyData(
                    kAudioObjectSystemObject,
                    ref address,
                    0,
                    IntPtr.Zero,
                    ref dataSize,
                    new IntPtr(&deviceId));

                return IsSuccess(status) ? deviceId : 0;
            }
        }

        /// <summary>
        /// Gets the current nominal sample rate for a device.
        /// </summary>
        private double GetDeviceSampleRate(uint deviceId)
        {
            if (deviceId == 0)
                return 0;

            try
            {
                var address = new AudioObjectPropertyAddress(
                    kAudioDevicePropertyNominalSampleRate,
                    kAudioObjectPropertyScopeGlobal,
                    kAudioObjectPropertyElementMaster);

                double rate = 0;
                uint dataSize = sizeof(double);

                unsafe
                {
                    int status = AudioObjectGetPropertyData(
                        deviceId,
                        ref address,
                        0,
                        IntPtr.Zero,
                        ref dataSize,
                        new IntPtr(&rate));

                    return IsSuccess(status) ? rate : 0;
                }
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Sets the nominal sample rate for a device.
        /// This is crucial to prevent sample rate mismatch issues.
        /// </summary>
        private bool SetDeviceSampleRate(uint deviceId, int desiredSampleRate)
        {
            if (deviceId == 0)
                return false;

            try
            {
                var address = new AudioObjectPropertyAddress(
                    kAudioDevicePropertyNominalSampleRate,
                    kAudioObjectPropertyScopeGlobal,
                    kAudioObjectPropertyElementMaster);

                unsafe
                {
                    double newRate = desiredSampleRate;
                    int status = AudioObjectSetPropertyData(
                        deviceId,
                        ref address,
                        0,
                        IntPtr.Zero,
                        sizeof(double),
                        new IntPtr(&newRate));

                    return IsSuccess(status);
                }
            }
            catch
            {
                return false;
            }
        }

        private void RegisterDeviceNotifications()
        {
            try
            {
                _deviceChangeListener = OnDevicePropertyChanged;

                // Register for default output device changes
                var outputAddress = new AudioObjectPropertyAddress(
                    kAudioHardwarePropertyDefaultOutputDevice,
                    kAudioObjectPropertyScopeGlobal,
                    kAudioObjectPropertyElementMaster);

                AudioObjectAddPropertyListener(
                    kAudioObjectSystemObject,
                    ref outputAddress,
                    _deviceChangeListener,
                    IntPtr.Zero);

                // Register for default input device changes
                var inputAddress = new AudioObjectPropertyAddress(
                    kAudioHardwarePropertyDefaultInputDevice,
                    kAudioObjectPropertyScopeGlobal,
                    kAudioObjectPropertyElementMaster);

                AudioObjectAddPropertyListener(
                    kAudioObjectSystemObject,
                    ref inputAddress,
                    _deviceChangeListener,
                    IntPtr.Zero);
            }
            catch
            {
                // Device notifications are optional
            }
        }

        private int OnDevicePropertyChanged(uint inObjectID, uint inNumberAddresses, IntPtr inAddresses, IntPtr inClientData)
        {
            try
            {
                unsafe
                {
                    AudioObjectPropertyAddress* addresses = (AudioObjectPropertyAddress*)inAddresses;
                    for (int i = 0; i < inNumberAddresses; i++)
                    {
                        var address = addresses[i];

                        if (address.mSelector == kAudioHardwarePropertyDefaultOutputDevice)
                        {
                            // CRITICAL: Do NOT call AudioObjectGetPropertyData on HAL notification thread!
                            // Apple documentation: "Property listener callbacks must not block."
                            // Queue the entire handler to ThreadPool to avoid blocking Core Audio HAL thread
                            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try
                                {
                                    var newDeviceId = GetDefaultOutputDeviceId();
                                    var oldDeviceId = _currentOutputDeviceId;

                                    OutputDeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(
                                        oldDeviceId.ToString(),
                                        newDeviceId.ToString(),
                                        null));
                                }
                                catch
                                {
                                    // Suppress exceptions in background thread to prevent app crash
                                }
                            });
                        }
                        else if (address.mSelector == kAudioHardwarePropertyDefaultInputDevice)
                        {
                            // CRITICAL: Do NOT call AudioObjectGetPropertyData on HAL notification thread!
                            // Apple documentation: "Property listener callbacks must not block."
                            // Queue the entire handler to ThreadPool to avoid blocking Core Audio HAL thread
                            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try
                                {
                                    var newDeviceId = GetDefaultInputDeviceId();
                                    var oldDeviceId = _currentInputDeviceId;

                                    InputDeviceChanged?.Invoke(this, new AudioDeviceChangedEventArgs(
                                        oldDeviceId.ToString(),
                                        newDeviceId.ToString(),
                                        null));
                                }
                                catch
                                {
                                    // Suppress exceptions in background thread to prevent app crash
                                }
                            });
                        }
                    }
                }

                return noErr;
            }
            catch
            {
                return -1;
            }
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

                // Unregister device notifications
                if (_deviceChangeListener != null)
                {
                    try
                    {
                        var outputAddress = new AudioObjectPropertyAddress(
                            kAudioHardwarePropertyDefaultOutputDevice,
                            kAudioObjectPropertyScopeGlobal,
                            kAudioObjectPropertyElementMaster);

                        AudioObjectRemovePropertyListener(
                            kAudioObjectSystemObject,
                            ref outputAddress,
                            _deviceChangeListener,
                            IntPtr.Zero);

                        var inputAddress = new AudioObjectPropertyAddress(
                            kAudioHardwarePropertyDefaultInputDevice,
                            kAudioObjectPropertyScopeGlobal,
                            kAudioObjectPropertyElementMaster);

                        AudioObjectRemovePropertyListener(
                            kAudioObjectSystemObject,
                            ref inputAddress,
                            _deviceChangeListener,
                            IntPtr.Zero);
                    }
                    catch { }
                }

                // Uninitialize and dispose output unit
                if (_outputAudioUnit != IntPtr.Zero)
                {
                    AudioUnitUninitialize(_outputAudioUnit);
                    AudioComponentInstanceDispose(_outputAudioUnit);
                    _outputAudioUnit = IntPtr.Zero;
                }

                // Uninitialize and dispose input unit
                if (_inputAudioUnit != IntPtr.Zero)
                {
                    AudioUnitUninitialize(_inputAudioUnit);
                    AudioComponentInstanceDispose(_inputAudioUnit);
                    _inputAudioUnit = IntPtr.Zero;
                }

                // Free input buffer list
                if (_inputBufferListPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_inputBufferListPtr);
                    _inputBufferListPtr = IntPtr.Zero;
                }

                // Unpin buffers
                if (_outputBufferHandle.IsAllocated)
                    _outputBufferHandle.Free();

                if (_inputBufferHandle.IsAllocated)
                    _inputBufferHandle.Free();
            }
        }

        #endregion

        #region Helper Struct

        [StructLayout(LayoutKind.Sequential)]
        private struct AURenderCallbackStruct
        {
            public IntPtr inputProc;  // Function pointer, not delegate
            public IntPtr inputProcRefCon;
        }

        #endregion
    }
}
