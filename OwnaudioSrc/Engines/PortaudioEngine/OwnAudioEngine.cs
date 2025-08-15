using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Exceptions;
using Ownaudio.Utilities.Extensions;
using static Ownaudio.Bindings.PortAudio.PaBinding;

namespace Ownaudio.Engines;

/// <summary>
/// Provides interaction with audio input/output devices using the PortAudio library.
/// This class cannot be inherited.
/// </summary>
/// <remarks>
/// This class implements <see cref="IAudioEngine"/> and provides cross-platform audio processing
/// capabilities with support for both input and output operations. It uses buffer pooling
/// for efficient memory management and supports host-specific optimizations for WASAPI and ASIO.
/// </remarks>
public sealed partial class OwnAudioEngine : IAudioEngine
{
    #region Constants
    /// <summary>
    /// Default stream flags used for PortAudio stream configuration.
    /// </summary>
    private const PaStreamFlags StreamFlags = PaStreamFlags.paPrimeOutputBuffersUsingStreamCallback | PaStreamFlags.paClipOff;
    #endregion

    #region Fields
    // Configuration
    private readonly AudioEngineOutputOptions _outputOptions;
    private readonly AudioEngineInputOptions _inputOptions;

    // PortAudio stream and parameters
    private IntPtr _stream;
    private PaStreamParameters _inputParameters;
    private PaStreamParameters _outputParameters;
    private IntPtr _inputHostApiSpecific;
    private IntPtr _outputHostApiSpecific;

    // Callback management
    private PaStreamCallback? _paCallback;
    private GCHandle _callbackHandle;

    // Buffer management
    private readonly ConcurrentQueue<float[]> _inputBufferQueue = new ConcurrentQueue<float[]>();
    private readonly ConcurrentQueue<float[]> _outputBufferQueue = new ConcurrentQueue<float[]>();
    private readonly ConcurrentBag<float[]> _inputBufferPool = new ConcurrentBag<float[]>();
    private readonly ConcurrentBag<float[]> _outputBufferPool = new ConcurrentBag<float[]>();
    private float[]? _silenceBuffer;
    private int _maxQueueSize;
    private int _poolInitialSize;
    private int _expectedOutputSizeConst;
    private int _expectedInputSizeConst;
    private SpinLock _outputQueueSpinLock = new SpinLock();

    // State management
    private bool _disposed;
    private readonly Stopwatch _stopwatch = new Stopwatch();
    #endregion

    #region Properties
    /// <summary>
    /// Gets the number of frames processed per buffer.
    /// </summary>
    /// <value>
    /// The number of audio frames that are processed in each callback iteration.
    /// Higher values increase latency but may improve performance.
    /// </value>
    public int FramesPerBuffer { get; private set; } = 512;
    #endregion

    #region Constructors
    /// <summary>
    /// Initializes a new instance of the <see cref="OwnAudioEngine"/> class with output-only capabilities.
    /// </summary>
    /// <param name="outputOptions">The audio engine output configuration options. If null, default options are used.</param>
    /// <param name="framesPerBuffer">The number of frames per buffer for audio processing. Default is 512.</param>
    /// <exception cref="PortAudioException">
    /// Thrown when errors occur during PortAudio stream initialization.
    /// </exception>
    /// <remarks>
    /// This constructor creates an audio engine configured for output operations only.
    /// Input capabilities are disabled when using this constructor.
    /// </remarks>
    public OwnAudioEngine(AudioEngineOutputOptions? outputOptions = default, int framesPerBuffer = 512)
        : this(null, outputOptions, framesPerBuffer)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OwnAudioEngine"/> class with both input and output capabilities.
    /// </summary>
    /// <param name="inputOptions">The audio engine input configuration options. If null, default options are used.</param>
    /// <param name="outputOptions">The audio engine output configuration options. If null, default options are used.</param>
    /// <param name="framesPerBuffer">The number of frames per buffer for audio processing. Default is 512.</param>
    /// <exception cref="PortAudioException">
    /// Thrown when errors occur during PortAudio stream initialization.
    /// </exception>
    /// <remarks>
    /// This constructor creates an audio engine with full duplex capabilities, supporting both
    /// audio input and output operations. Host-specific optimizations for WASAPI and ASIO
    /// are automatically configured when available.
    /// </remarks>
    public OwnAudioEngine(AudioEngineInputOptions? inputOptions = default, AudioEngineOutputOptions? outputOptions = default, int framesPerBuffer = 512)
    {
        _inputOptions = inputOptions ?? new AudioEngineInputOptions();
        _outputOptions = outputOptions ?? new AudioEngineOutputOptions();
        FramesPerBuffer = framesPerBuffer;

        CalculateOptimalSizes();
        InitializeBufferPools();
        InitializePortAudioEngine();
    }
    #endregion

    #region Initialization Methods
    /// <summary>
    /// Initializes the buffer pools used for efficient memory management during audio processing.
    /// </summary>
    /// <remarks>
    /// This method pre-allocates buffers for both input and output operations to minimize
    /// garbage collection during real-time audio processing. The silence buffer is also
    /// initialized for use when no output data is available.
    /// </remarks>
    private void InitializeBufferPools()
    {
        int outputBufferSize = FramesPerBuffer * (int)_outputOptions.Channels;
        int inputBufferSize = FramesPerBuffer * (int)_inputOptions.Channels;

        _silenceBuffer = new float[outputBufferSize];

        // Pre-allocate buffers with optimized pool size
        for (int i = 0; i < _poolInitialSize; i++)
        {
            if (_outputOptions.Channels > 0)
                _outputBufferPool.Add(new float[outputBufferSize]);

            if (_inputOptions.Channels > 0)
                _inputBufferPool.Add(new float[inputBufferSize]);
        }
    }

    /// <summary>
    /// Initializes the PortAudio stream and configures all audio parameters.
    /// </summary>
    /// <exception cref="PortAudioException">
    /// Thrown when PortAudio stream initialization fails.
    /// </exception>
    /// <remarks>
    /// This method performs the complete PortAudio initialization sequence including:
    /// callback setup, parameter configuration, host-specific optimizations,
    /// and stream creation.
    /// </remarks>
    private void InitializePortAudioEngine()
    {
        unsafe
        {
            // Calculate constants once
            _expectedOutputSizeConst = FramesPerBuffer * (int)_outputOptions.Channels;
            _expectedInputSizeConst = FramesPerBuffer * (int)_inputOptions.Channels;

            SetupAudioCallback();
            ConfigureStreamParameters();
            CreatePortAudioStream();
        }
    }

    /// <summary>
    /// Calculates optimal buffer pool and queue sizes based on latency requirements.
    /// </summary>
    private void CalculateOptimalSizes()
    {
        double latencyMs = Math.Max(_inputOptions.Latency, _outputOptions.Latency) * 1000;
        double bufferDurationMs = (FramesPerBuffer * 1000.0) / _outputOptions.SampleRate;
        
        _maxQueueSize = Math.Max(3, (int)(latencyMs / bufferDurationMs) + 2);
        _poolInitialSize = _maxQueueSize * 3; // 3x reserve for safety
    }

    /// <summary>
    /// Sets up the audio processing callback function.
    /// </summary>
    /// <remarks>
    /// The callback handles real-time audio processing, managing both input and output
    /// buffers with proper memory management to avoid allocations during processing.
    /// </remarks>
    private unsafe void SetupAudioCallback()
    {
        #nullable disable
        _paCallback = CreateAudioProcessingCallback();
        #nullable restore
        _callbackHandle = GCHandle.Alloc(_paCallback);
    }

    /// <summary>
    /// Creates the audio processing callback delegate.
    /// </summary>
    /// <returns>A configured PaStreamCallback delegate for audio processing.</returns>
    private unsafe PaStreamCallback CreateAudioProcessingCallback()
    {
        return (void* inputBuffer, void* outputBuffer, long frameCount, IntPtr timeInfo, PaStreamCallbackFlags statusFlags, void* userData) =>
        {
            // Use pre-calculated constants instead of runtime multiplication
            ProcessOutputBuffer(outputBuffer, _expectedOutputSizeConst);
            ProcessInputBuffer(inputBuffer, _expectedInputSizeConst);

            return PaStreamCallbackResult.paContinue;
        };
    }

    /// <summary>
    /// Processes the output audio buffer during the callback.
    /// </summary>
    /// <param name="outputBuffer">Pointer to the output buffer.</param>
    /// <param name="expectedOutputSize">Expected size of the output buffer.</param>
    private unsafe void ProcessOutputBuffer(void* outputBuffer, int expectedOutputSize)
    {
        if (outputBuffer == null || _outputOptions.Channels <= 0)
            return;

        if (_outputBufferQueue.TryDequeue(out float[]? outputData))
        {
            bool needsReturn = true;

            // Handle buffer size mismatch
            if (outputData.Length != expectedOutputSize)
            {
                outputData = ResizeOutputBuffer(outputData, expectedOutputSize);
                needsReturn = false;
            }

            // Optimized SIMD copy for large buffers
            if (outputData.Length >= Vector<float>.Count * 4)
            {
                OptimizedBufferCopy(outputData, (float*)outputBuffer, outputData.Length);
            }
            else
            {
                Marshal.Copy(outputData, 0, (IntPtr)outputBuffer, outputData.Length);
            }

            if (needsReturn)
                _outputBufferPool.Add(outputData);
        }
        else
        {
            // Use silence when no data is available
            if (expectedOutputSize >= Vector<float>.Count * 4)
            {
                OptimizedBufferCopy(_silenceBuffer!, (float*)outputBuffer, expectedOutputSize);
            }
            else
            {
                Marshal.Copy(_silenceBuffer!, 0, (IntPtr)outputBuffer, expectedOutputSize);
            }
        }
    }

    /// <summary>
    /// SIMD optimized buffer copy for better performance with large buffers.
    /// </summary>
    private unsafe void OptimizedBufferCopy(float[] source, float* dest, int length)
    {
        // Use built-in optimized copy which already uses SIMD internally
        var destSpan = new Span<float>(dest, length);
        source.AsSpan(0, length).CopyTo(destSpan);
    }

    /// <summary>
    /// Resizes an output buffer to match the expected size.
    /// </summary>
    /// <param name="originalBuffer">The original buffer to resize.</param>
    /// <param name="expectedSize">The expected buffer size.</param>
    /// <returns>A buffer with the correct size containing the original data.</returns>
    private float[] ResizeOutputBuffer(float[] originalBuffer, int expectedSize)
    {
        if (!_outputBufferPool.TryTake(out float[]? correctSizeBuffer))
            correctSizeBuffer = new float[expectedSize];

        int copyLength = Math.Min(originalBuffer.Length, expectedSize);
        Array.Copy(originalBuffer, correctSizeBuffer, copyLength);

        _outputBufferPool.Add(originalBuffer);

        return correctSizeBuffer;
    }

    /// <summary>
    /// Processes the input audio buffer during the callback.
    /// </summary>
    /// <param name="inputBuffer">Pointer to the input buffer.</param>
    /// <param name="expectedInputSize">Expected size of the input buffer.</param>
    private unsafe void ProcessInputBuffer(void* inputBuffer, int expectedInputSize)
    {
        if (inputBuffer == null || _inputOptions.Channels <= 0)
            return;

        // Get or create input buffer
        if (!_inputBufferPool.TryTake(out float[]? inputData) || inputData.Length != expectedInputSize)
        {
            if (inputData != null)
                _inputBufferPool.Add(inputData);

            inputData = new float[expectedInputSize];
        }

        Marshal.Copy((IntPtr)inputBuffer, inputData, 0, inputData.Length);

        // Manage queue size to prevent memory buildup
        while (_inputBufferQueue.Count >= _maxQueueSize)
        {
            if (_inputBufferQueue.TryDequeue(out float[]? dummy))
                _inputBufferPool.Add(dummy);
        }

        _inputBufferQueue.Enqueue(inputData);
    }

    /// <summary>
    /// Configures the PortAudio stream parameters for both input and output.
    /// </summary>
    /// <remarks>
    /// This method sets up the stream parameters and applies host-specific optimizations
    /// for WASAPI and ASIO when available.
    /// </remarks>
    private void ConfigureStreamParameters()
    {
        ConfigureInputParameters();
        ConfigureOutputParameters();
    }

    /// <summary>
    /// Configures the input stream parameters.
    /// </summary>
    private void ConfigureInputParameters()
    {
        _inputParameters = new PaStreamParameters
        {
            channelCount = _inputOptions.Channels,
            device = _inputOptions.Device.DeviceIndex,
            hostApiSpecificStreamInfo = IntPtr.Zero,
            sampleFormat = OwnAudio.Constants.PaSampleFormat,
            suggestedLatency = _inputOptions.Latency
        };

        ConfigureHostSpecificSettings(ref _inputParameters, ref _inputHostApiSpecific, true);
    }

    /// <summary>
    /// Configures the output stream parameters.
    /// </summary>
    private void ConfigureOutputParameters()
    {
        _outputParameters = new PaStreamParameters
        {
            channelCount = _outputOptions.Channels,
            device = _outputOptions.Device.DeviceIndex,
            hostApiSpecificStreamInfo = IntPtr.Zero,
            sampleFormat = OwnAudio.Constants.PaSampleFormat,
            suggestedLatency = _outputOptions.Latency
        };

        ConfigureHostSpecificSettings(ref _outputParameters, ref _outputHostApiSpecific, false);
    }

    /// <summary>
    /// Configures host-specific settings for WASAPI and ASIO.
    /// </summary>
    /// <param name="parameters">The stream parameters to configure.</param>
    /// <param name="hostApiSpecific">Reference to the host-specific information pointer.</param>
    /// <param name="isInput">True if configuring input parameters, false for output.</param>
    private void ConfigureHostSpecificSettings(ref PaStreamParameters parameters, ref IntPtr hostApiSpecific, bool isInput)
    {
        string hostApiName = OwnAudio.HostID.PaHostApiInfo().name.ToLower();

        if (hostApiName.Contains("wasapi"))
        {
            hostApiSpecific = CreateWasapiStreamInfo(PaWasapiFlags.ThreadPriority);
            parameters.hostApiSpecificStreamInfo = hostApiSpecific;
        }
        else if (hostApiName.Contains("asio"))
        {
            int[] channelNumbers = isInput ? new int[] { 0 } : new int[] { 0, 1 };
            hostApiSpecific = CreateAsioStreamInfo(channelNumbers);
            parameters.hostApiSpecificStreamInfo = hostApiSpecific;
        }
    }

    /// <summary>
    /// Creates the PortAudio stream with the configured parameters.
    /// </summary>
    /// <exception cref="PortAudioException">
    /// Thrown when stream creation fails.
    /// </exception>
    private unsafe void CreatePortAudioStream()
    {
        PaStreamParameters tempInputParameters;
        var inputParametersPtr = new IntPtr(&tempInputParameters);
        Marshal.StructureToPtr(_inputParameters, inputParametersPtr, false);

        PaStreamParameters tempOutputParameters;
        var outputParametersPtr = new IntPtr(&tempOutputParameters);
        Marshal.StructureToPtr(_outputParameters, outputParametersPtr, false);

        #nullable disable
        var result = Pa_OpenStream(
            out _stream,
            inputParametersPtr,
            outputParametersPtr,
            _outputOptions.SampleRate,
            FramesPerBuffer,
            StreamFlags,
            _paCallback,
            IntPtr.Zero).PaGuard();
        #nullable restore

        Debug.WriteLine(result.PaErrorToText());
    }
    #endregion

    #region Host-Specific Configuration Methods
    /// <summary>
    /// Creates a WASAPI host API specific stream information structure.
    /// </summary>
    /// <param name="flags">Flags that define the WASAPI stream configuration.</param>
    /// <returns>A pointer to the allocated WASAPI stream information structure.</returns>
    /// <remarks>
    /// This method creates a WASAPI-specific stream information structure
    /// necessary for using the WASAPI API with optimized settings.
    /// The caller is responsible for freeing the allocated memory.
    /// </remarks>
    private IntPtr CreateWasapiStreamInfo(PaWasapiFlags flags)
    {
        var wasapiStreamInfo = new PaWasapiStreamInfo
        {
            size = (uint)Marshal.SizeOf<PaWasapiStreamInfo>(),
            hostApiType = (int)PaHostApiTypeId.paWASAPI,
            version = 1,
            flags = (uint)flags,
            channelMask = 0x00000001
        };

        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<PaWasapiStreamInfo>());
        Marshal.StructureToPtr(wasapiStreamInfo, ptr, true);

        Debug.WriteLine($"Created WasapiStreamInfo pointer: {ptr}");
        return ptr;
    }

    /// <summary>
    /// Creates an ASIO host API specific stream information structure.
    /// </summary>
    /// <param name="channelNumbers">An array containing the ASIO channel indices to use.</param>
    /// <returns>A pointer to the allocated ASIO stream information structure.</returns>
    /// <remarks>
    /// This method creates an ASIO-specific stream information structure
    /// necessary for using the ASIO API with the specified channel configuration.
    /// The caller is responsible for freeing the allocated memory.
    /// </remarks>
    private IntPtr CreateAsioStreamInfo(int[] channelNumbers)
    {
        var asioStreamInfo = new PaAsioStreamInfo
        {
            size = (uint)Marshal.SizeOf<PaAsioStreamInfo>(),
            hostApiType = PaHostApiTypeId.paASIO,
            version = 1,
            flags = 0x01 // paAsioUseChannelSelectors
        };

        asioStreamInfo.SetChannelSelectors(channelNumbers);

        IntPtr streamInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(asioStreamInfo));
        Marshal.StructureToPtr(asioStreamInfo, streamInfoPtr, false);

        return streamInfoPtr;
    }
    #endregion

    #region Public Methods
    /// <summary>
    /// Gets the PortAudio stream pointer for low-level operations.
    /// </summary>
    /// <returns>The memory address of the PortAudio stream.</returns>
    /// <remarks>
    /// This method provides access to the internal PortAudio stream pointer,
    /// which may be useful for advanced low-level operations or debugging.
    /// </remarks>
    public IntPtr GetStream()
    {
        return _stream;
    }

    /// <summary>
    /// Checks whether the audio engine is currently active.
    /// </summary>
    /// <returns>
    /// A value indicating the active state:
    /// 0 - the engine is not playing or recording,
    /// 1 - the engine is playing or recording,
    /// negative value if there is an error.
    /// </returns>
    /// <remarks>
    /// This method queries the PortAudio library to determine if the audio stream
    /// is currently processing audio data.
    /// </remarks>
    public int OwnAudioEngineActivate()
    {
        return Pa_IsStreamActive(_stream);
    }

    /// <summary>
    /// Checks whether the audio engine is currently stopped.
    /// </summary>
    /// <returns>
    /// A value indicating the stopped state:
    /// 0 - the engine is running,
    /// 1 - the engine is stopped,
    /// negative value if there is an error.
    /// </returns>
    /// <remarks>
    /// This method queries the PortAudio library to determine if the audio stream
    /// is in a stopped state.
    /// </remarks>
    public int OwnAudioEngineStopped()
    {
        return Pa_IsStreamStopped(_stream);
    }

    /// <summary>
    /// Starts the audio engine and begins processing audio data.
    /// </summary>
    /// <returns>
    /// An error code from the PortAudio library where 0 indicates success.
    /// </returns>
    /// <remarks>
    /// This method starts the audio stream and clears any existing buffers.
    /// The stream must be started before audio data can be sent or received.
    /// Always check the return value to ensure the stream started successfully.
    /// </remarks>
    public int Start()
    {
        ClearBufferQueues();
        ClearSilenceBuffer();

        return Pa_StartStream(_stream).PaGuard();
    }

    /// <summary>
    /// Stops the audio engine and ceases all audio processing.
    /// </summary>
    /// <returns>
    /// An error code from the PortAudio library where 0 indicates success.
    /// </returns>
    /// <remarks>
    /// This method stops the audio stream and returns all queued buffers to their
    /// respective pools for reuse. Always check the return value to ensure
    /// the stream stopped successfully.
    /// </remarks>
    public int Stop()
    {
        int result = Pa_StopStream(_stream).PaGuard();
        ClearBufferQueues();
        return result;
    }

    /// <summary>
    /// Sends audio data to the output device for playback.
    /// </summary>
    /// <param name="samples">A span containing the audio samples to be played.</param>
    /// <remarks>
    /// This method queues audio data for playback. The samples should contain
    /// interleaved audio data for all output channels. If the output queue is full,
    /// the method will wait briefly and retry once. The samples span should contain
    /// exactly FramesPerBuffer * OutputChannels elements.
    /// </remarks>
    public void Send(Span<float> samples)
    {
        if (TryEnqueueOutputBuffer(samples))
        {
            _stopwatch.Restart();
            return;
        }

        // Wait briefly and retry once if queue was full
        Thread.Sleep(5);
        
        if (TryEnqueueOutputBuffer(samples))
        {
            _stopwatch.Restart();
        }
    }

    /// <summary>
    /// Receives audio data from the input device.
    /// </summary>
    /// <param name="samples">An output array that will be filled with the received audio samples.</param>
    /// <remarks>
    /// This method attempts to retrieve audio data from the input queue.
    /// If no data is immediately available, it will wait up to 100ms for data to arrive.
    /// If no data is received within the timeout period, an empty buffer is returned.
    /// The samples array will contain interleaved audio data for all input channels.
    /// </remarks>
    public void Receives(out float[] samples)
    {
        #nullable disable
        if (_inputBufferQueue.TryDequeue(out samples))
        {
            return;
        }

        // Wait for input data with timeout
        if (WaitForInputData(out samples))
        {
            return;
        }

        // Return empty buffer if no data received
        samples = GetEmptyInputBuffer();
        #nullable restore
    }
    #endregion

    #region Private Helper Methods
    /// <summary>
    /// Clears all buffer queues and returns buffers to their pools.
    /// </summary>
    private void ClearBufferQueues()
    {
        while (_outputBufferQueue.TryDequeue(out float[]? buffer))
            _outputBufferPool.Add(buffer);

        while (_inputBufferQueue.TryDequeue(out float []? buffer))
            _inputBufferPool.Add(buffer);
    }

    /// <summary>
    /// Clears the silence buffer by filling it with zeros.
    /// </summary>
    private void ClearSilenceBuffer()
    {
        if (_silenceBuffer != null)
            Array.Clear(_silenceBuffer, 0, _silenceBuffer.Length);
    }

    /// <summary>
    /// Attempts to enqueue an output buffer for playback.
    /// </summary>
    /// <param name="samples">The audio samples to enqueue.</param>
    /// <returns>True if the buffer was successfully enqueued, false if the queue is full.</returns>
    private bool TryEnqueueOutputBuffer(Span<float> samples)
    {
        bool lockTaken = false;
        try
        {
            // Try to acquire lock with minimal wait
            _outputQueueSpinLock.TryEnter(100, ref lockTaken); // 100 microseconds timeout
            
            if (!lockTaken || _outputBufferQueue.Count >= _maxQueueSize)
                return false;

            if (!_outputBufferPool.TryTake(out float[]? buffer) || buffer.Length != samples.Length)
            {
                if (buffer != null)
                    _outputBufferPool.Add(buffer);

                buffer = new float[samples.Length];
            }

            // Use SIMD copy for larger buffers
            if (samples.Length >= Vector<float>.Count * 2)
            {
                CopyWithSIMD(samples, buffer);
            }
            else
            {
                samples.CopyTo(buffer);
            }

            _outputBufferQueue.Enqueue(buffer);
            return true;
        }
        finally
        {
            if (lockTaken)
                _outputQueueSpinLock.Exit();
        }
    }

    /// <summary>
    /// SIMD optimized span to array copy.
    /// </summary>
    private void CopyWithSIMD(Span<float> source, float[] destination)
    {
        // Modern .NET already optimizes Span.CopyTo with SIMD
        source.CopyTo(destination.AsSpan());
    }

    /// <summary>
    /// Waits for input data to become available with a timeout.
    /// </summary>
    /// <param name="samples">The received samples if successful.</param>
    /// <returns>True if data was received, false if timeout occurred.</returns>
    private bool WaitForInputData(out float[]? samples)
    {
        const int maxWaitIterations = 20;
        const int waitIntervalMs = 5;

        for (int waitCount = 0; waitCount < maxWaitIterations; waitCount++)
        {
            if (_inputBufferQueue.TryDequeue(out samples))
                return true;

            Thread.Sleep(waitIntervalMs);
        }

        samples = null!;
        return false;
    }

    /// <summary>
    /// Gets an empty input buffer from the pool or creates a new one.
    /// </summary>
    /// <returns>An empty input buffer with the correct size.</returns>
    private float[] GetEmptyInputBuffer()
    {
        int expectedSize = FramesPerBuffer * (int)_inputOptions.Channels;

        if (!_inputBufferPool.TryTake(out float[]? buffer) || buffer.Length != expectedSize)
        {
            if (buffer != null)
                _inputBufferPool.Add(buffer);

            buffer = new float[expectedSize];
        }

        Array.Clear(buffer, 0, buffer.Length);
        return buffer;
    }
    #endregion

    #region IDisposable Implementation
    /// <summary>
    /// Releases all resources used by the audio engine.
    /// </summary>
    /// <remarks>
    /// This method stops and closes the audio stream, frees any allocated
    /// memory for host-specific stream information, releases the GC handle
    /// for the callback, and clears all buffer pools. This method is automatically
    /// called when the object is garbage collected.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed || _stream == IntPtr.Zero)
            return;

        DisposePortAudioStream();
        DisposeHostSpecificResources();
        DisposeCallbackHandle();
        DisposeSpinLocks();
        DisposeBufferResources();

        _disposed = true;
    }

    /// <summary>
    /// Disposes of the PortAudio stream resources.
    /// </summary>
    private void DisposePortAudioStream()
    {
        if (_stream == IntPtr.Zero)
            return;

        try
        {
            Pa_AbortStream(_stream);
            Pa_CloseStream(_stream);
        }
        catch (PortAudioException pae)
        {
            Debug.WriteLine($"Error closing PortAudio stream: {pae.Message}");
        }
        finally
        {
            _stream = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Disposes of host-specific stream information resources.
    /// </summary>
    private void DisposeHostSpecificResources()
    {
        if (_inputHostApiSpecific != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_inputHostApiSpecific);
            _inputHostApiSpecific = IntPtr.Zero;
        }

        if (_outputHostApiSpecific != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_outputHostApiSpecific);
            _outputHostApiSpecific = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Disposes of the callback GC handle.
    /// </summary>
    // private void DisposeCallbackHandle()
    // {
    //     if (_callbackHandle.IsAllocated)
    //         _callbackHandle.Free();
    // }
    private void DisposeCallbackHandle()
    {
        if (_callbackHandle.IsAllocated)
            _callbackHandle.Free();
    }

    /// <summary>
    /// Disposes SpinLock resources safely.
    /// </summary>
    private void DisposeSpinLocks()
    {
        // SpinLock doesn't require explicit disposal, but ensure no threads are waiting
        bool lockTaken = false;
        try
        {
            _outputQueueSpinLock.TryEnter(0, ref lockTaken);
        }
        finally
        {
            if (lockTaken)
                _outputQueueSpinLock.Exit();
        }
    }

    /// <summary>
    /// Disposes of buffer resources and clears buffer pools.
    /// </summary>
    private void DisposeBufferResources()
    {
        ClearBufferQueues();
        _inputBufferPool.Clear();
        _outputBufferPool.Clear();
        _silenceBuffer = null;
    }

    #endregion
}