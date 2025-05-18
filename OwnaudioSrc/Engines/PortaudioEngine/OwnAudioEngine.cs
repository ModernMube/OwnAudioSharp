using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Exceptions;
using Ownaudio.Utilities.Extensions;
using static Ownaudio.Bindings.PortAudio.PaBinding;

namespace Ownaudio.Engines;

/// <summary>
/// Interact with output audio device by using PortAudio library.
/// This class cannot be inherited.
/// <para>Implements: <see cref="IAudioEngine"/>.</para>
/// </summary>
public sealed partial class OwnAudioEngine : IAudioEngine
{
    private const PaStreamFlags StreamFlags = PaStreamFlags.paPrimeOutputBuffersUsingStreamCallback | PaStreamFlags.paClipOff;

    private readonly AudioEngineOutputOptions _outoptions;
    private readonly AudioEngineInputOptions _inputoptions;
    private IntPtr _stream;
    private PaStreamParameters inparameters;
    private PaStreamParameters parameters;
    private IntPtr inHostApiSpecific;
    private IntPtr outHostApiSpecific;

    private bool _disposed;

    private PaStreamCallback? _paCallback;
    private GCHandle _callbackHandle;
    private ConcurrentQueue<float[]> _inputBufferQueue = new ConcurrentQueue<float[]>();
    private ConcurrentQueue<float[]> _outputBufferQueue = new ConcurrentQueue<float[]>();
    private readonly int _maxQueueSize = 10;

    private readonly ConcurrentBag<float[]> _inputBufferPool = new ConcurrentBag<float[]>();
    private readonly ConcurrentBag<float[]> _outputBufferPool = new ConcurrentBag<float[]>();
    private float[]? _silenceBuffer;

    private Stopwatch _stopwatch = new Stopwatch();

    /// <summary>
    /// Initializes a new instance of the OwnAudioEngine class with output options only.
    /// </summary>
    /// <param name="outoptions">Optional audio engine output options.</param>
    /// <param name="framesPerBuffer">Number of frames per buffer (default is 512).</param>
    /// <exception cref="PortAudioException">
    /// Thrown when errors occur during PortAudio stream initialization.
    /// </exception>
    /// <remarks>
    /// This constructor initializes the audio engine with output capabilities only.
    /// It sets up the PortAudio stream with the specified output device parameters.
    /// </remarks>
    public OwnAudioEngine(AudioEngineOutputOptions? outoptions = default, int framesPerBuffer = 512)
    : this(null, outoptions, framesPerBuffer)
    {
    }

    /// <summary>
    /// Initializes a new instance of the OwnAudioEngine class with both input and output options.
    /// </summary>
    /// <param name="inoptions">Optional audio engine input options.</param>
    /// <param name="outoptions">Optional audio engine output options.</param>
    /// <param name="framesPerBuffer">Number of frames per buffer (default is 512).</param>
    /// <exception cref="PortAudioException">
    /// Thrown when errors occur during PortAudio stream initialization.
    /// </exception>
    /// <remarks>
    /// This constructor initializes the audio engine with both input and output capabilities.
    /// It sets up the PortAudio stream with the specified input and output device parameters,
    /// and also configures host-specific settings for WASAPI or ASIO if detected.
    /// </remarks>
    public OwnAudioEngine(AudioEngineInputOptions? inoptions = default, AudioEngineOutputOptions? outoptions = default, int framesPerBuffer = 512)
    {
        _inputoptions = inoptions ?? new AudioEngineInputOptions();
        _outoptions = outoptions ?? new AudioEngineOutputOptions();
        FramesPerBuffer = framesPerBuffer;

        InitializeBufferPools();
        OwnAudioEngineInitialize();

    }

    /// <summary>
    /// Initializes the PortAudio stream and configures audio parameters.
    /// </summary>
    /// <remarks>
    /// This private method handles the core initialization of the PortAudio system.
    /// It sets up the callback function, configures input/output parameters,
    /// initializes host-specific settings for WASAPI or ASIO if detected,
    /// and opens the audio stream with the specified configuration.
    /// </remarks>
    private void OwnAudioEngineInitialize()
    {
        unsafe
        {
#nullable disable
            _paCallback =
            (void* inputBuffer, void* outputBuffer, long frameCount, IntPtr timeInfo, PaStreamCallbackFlags statusFlags, void* userData) =>
            {
                int expectedOutputSize = (int)(frameCount * (int)_outoptions.Channels);
                int expectedInputSize = (int)(frameCount * (int)_inputoptions.Channels);

                if (outputBuffer != null && _outoptions.Channels > 0)
                {
                    float[] outputData = null;
                    bool needsReturn = false;

                    if (_outputBufferQueue.TryDequeue(out outputData))
                    {
                        needsReturn = true;

                        if (outputData.Length != expectedOutputSize)
                        {
                            float[] correctSizeBuffer;
                            if (!_outputBufferPool.TryTake(out correctSizeBuffer))
                            {
                                correctSizeBuffer = new float[expectedOutputSize];
                            }

                            int copyLength = Math.Min(outputData.Length, expectedOutputSize);
                            Array.Copy(outputData, correctSizeBuffer, copyLength);

                            _outputBufferPool.Add(outputData);

                            outputData = correctSizeBuffer;
                            needsReturn = false;
                        }

                        Marshal.Copy(outputData, 0, (IntPtr)outputBuffer, outputData.Length);

                        if (needsReturn)
                            _outputBufferPool.Add(outputData);
                    }
                    else
                    {
                        Marshal.Copy(_silenceBuffer, 0, (IntPtr)outputBuffer, expectedOutputSize);
                    }
                }

                if (inputBuffer != null && _inputoptions.Channels > 0)
                {
                    float[] inputData;
                    if (!_inputBufferPool.TryTake(out inputData) || inputData.Length != expectedInputSize)
                    {
                        if (inputData != null)
                            _inputBufferPool.Add(inputData);

                        inputData = new float[expectedInputSize];
                    }

                    Marshal.Copy((IntPtr)inputBuffer, inputData, 0, inputData.Length);

                    while (_inputBufferQueue.Count >= _maxQueueSize)
                    {
                        float[] dummy;
                        if (_inputBufferQueue.TryDequeue(out dummy))
                            _inputBufferPool.Add(dummy);
                    }

                    _inputBufferQueue.Enqueue(inputData);
                }

                return PaStreamCallbackResult.paContinue;
            };
#nullable disable

            _callbackHandle = GCHandle.Alloc(_paCallback);

            inparameters = new PaStreamParameters
            {
                channelCount = _inputoptions.Channels,
                device = _inputoptions.Device.DeviceIndex,
                hostApiSpecificStreamInfo = IntPtr.Zero,
                sampleFormat = OwnAudio.Constants.PaSampleFormat,
                suggestedLatency = _inputoptions.Latency
            };

            if (OwnAudio.HostID.PaHostApiInfo().name.ToLower().Contains("wasapi")) //Wasapi host spcific
            {
                inHostApiSpecific = CreateWasapiStreamInfo(PaWasapiFlags.ThreadPriority);
                inparameters.hostApiSpecificStreamInfo = inHostApiSpecific;
            }

            if (OwnAudio.HostID.PaHostApiInfo().name.ToLower().Contains("asio")) //Asio host specific
            {
                inHostApiSpecific = CreateAsioStreamInfo(new int[] { 0 });
                inparameters.hostApiSpecificStreamInfo = inHostApiSpecific;
            }

            parameters = new PaStreamParameters
            {
                channelCount = _outoptions.Channels,
                device = _outoptions.Device.DeviceIndex,
                hostApiSpecificStreamInfo = IntPtr.Zero,
                sampleFormat = OwnAudio.Constants.PaSampleFormat,
                suggestedLatency = _outoptions.Latency
            };

            if (OwnAudio.HostID.PaHostApiInfo().name.ToLower().Contains("wasapi")) //Wasapi host spcific
            {
                outHostApiSpecific = CreateWasapiStreamInfo(PaWasapiFlags.ThreadPriority);
                parameters.hostApiSpecificStreamInfo = outHostApiSpecific;
            }

            if (OwnAudio.HostID.PaHostApiInfo().name.ToLower().Contains("asio")) //Asio host specific
            {
                outHostApiSpecific = CreateAsioStreamInfo(new int[] { 0, 1 });
                parameters.hostApiSpecificStreamInfo = outHostApiSpecific;
            }

            //IntPtr stream;

            unsafe
            {
                PaStreamParameters intempParameters;
                var inparametersPtr = new IntPtr(&intempParameters);
                Marshal.StructureToPtr(inparameters, inparametersPtr, false);

                PaStreamParameters tempParameters;
                var parametersPtr = new IntPtr(&tempParameters);
                Marshal.StructureToPtr(parameters, parametersPtr, false);

#nullable disable
                var codeIn = Pa_OpenStream(
                    out _stream,
                    inparametersPtr,
                    parametersPtr,
                    _outoptions.SampleRate,
                    FramesPerBuffer,
                    StreamFlags,
                    _paCallback,
                    IntPtr.Zero).PaGuard();
#nullable restore
                Debug.WriteLine(codeIn.PaErrorToText());
            }

            //_stream = stream;
            //Stopwatch.StartNew();
        }
    }

    /// <summary>
    /// Gets or sets the number of frames processed per buffer.
    /// </summary>
    /// <remarks>
    /// This property defines the size of audio chunks processed in each callback.
    /// Higher values increase latency but may improve performance.
    /// </remarks>
    public int FramesPerBuffer { get; private set; } = 512;

    /// <summary>
    /// Initializes the PortAudio stream and configures audio parameters.
    /// </summary>
    /// <remarks>
    /// This private method handles the core initialization of the PortAudio system.
    /// It sets up the callback function, configures input/output parameters,
    /// initializes host-specific settings for WASAPI or ASIO if detected,
    /// and opens the audio stream with the specified configuration.
    /// </remarks>   
    private void InitializeBufferPools()
    {
        int outputBufferSize = FramesPerBuffer * (int)_outoptions.Channels;
        int inputBufferSize = FramesPerBuffer * (int)_inputoptions.Channels;

        _silenceBuffer = new float[outputBufferSize];

        for (int i = 0; i < _maxQueueSize * 2; i++)
        {
            if (_outoptions.Channels > 0)
                _outputBufferPool.Add(new float[outputBufferSize]);

            if (_inputoptions.Channels > 0)
                _inputBufferPool.Add(new float[inputBufferSize]);
        }
    }

    /// <summary>
    /// Creates a WASAPI host API specific stream information structure.
    /// </summary>
    /// <param name="flags">Flags that define the WASAPI stream configuration.</param>
    /// <returns>A pointer to the allocated WASAPI stream information structure.</returns>
    /// <remarks>
    /// This function creates a WASAPI-specific stream information structure 
    /// necessary for using the WASAPI API. The function allocates the required
    /// memory and returns its address.
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
    /// This function creates an ASIO-specific stream information structure
    /// necessary for using the ASIO API. The function sets up the selected
    /// channels and allocates the required memory.
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

    /// <summary>
    /// Gets the PortAudio stream pointer.
    /// </summary>
    /// <returns>Returns the memory address of the PortAudio stream.</returns>
    /// <remarks>
    /// This function returns the internal PortAudio stream pointer,
    /// which may be useful for low-level operations.
    /// </remarks>
    public IntPtr GetStream() { return _stream; }

    /// <summary>
    /// Checks the active state of the audio engine.
    /// </summary>
    /// <returns>
    /// 0 - the engine is not playing or recording
    /// 1 - the engine is playing or recording
    /// negative value if there is an error
    /// </returns>
    /// <remarks>
    /// This method calls the PortAudio Pa_IsStreamActive function to determine
    /// if the audio stream is currently active (playing or recording).
    /// </remarks>
    public int OwnAudioEngineActivate() { return Pa_IsStreamActive(_stream); }

    /// <summary>
    /// Checks the stopped state of the audio engine.
    /// </summary>
    /// <returns>
    /// 0 - the engine is running
    /// 1 - the engine is stopped
    /// negative value if there is an error
    /// </returns>
    /// <remarks>
    /// This method calls the PortAudio Pa_IsStreamStopped function to determine
    /// if the audio stream is currently in a stopped state.
    /// </remarks>
    public int OwnAudioEngineStopped() { return Pa_IsStreamStopped(_stream); }

    /// <summary>
    /// Starts the audio engine.
    /// </summary>
    /// <returns>Error code from the PortAudio library (0 means success).</returns>
    /// <remarks>
    /// This function starts the audio stream and begins processing.
    /// It should be called before sending audio data to the engine.
    /// The return value should be checked to ensure the stream started successfully.
    /// </remarks>
    public int Start()
    {
        float[]? buffer;
        while (_outputBufferQueue.TryDequeue(out buffer))
            _outputBufferPool.Add(buffer);

        while (_inputBufferQueue.TryDequeue(out buffer))
            _inputBufferPool.Add(buffer);

        if (_silenceBuffer != null)
            Array.Clear(_silenceBuffer, 0, _silenceBuffer.Length);

        return Pa_StartStream(_stream).PaGuard();
    }

    /// <summary>
    /// Stops the audio engine.
    /// </summary>
    /// <returns>Error code from the PortAudio library (0 means success).</returns>
    /// <remarks>
    /// This function stops the audio stream and clears all internal buffers.
    /// Any pending audio data in the queues is returned to the buffer pools.
    /// The return value should be checked to ensure the stream stopped successfully.
    /// </remarks>
    public int Stop()
    {
        int result = Pa_StopStream(_stream).PaGuard();

        float[]? buffer;
        while (_outputBufferQueue.TryDequeue(out buffer))
            _outputBufferPool.Add(buffer);

        while (_inputBufferQueue.TryDequeue(out buffer))
            _inputBufferPool.Add(buffer);

        return result;
#nullable restore
    }

    /// <summary>
    /// Sends audio data to the output device.
    /// </summary>
    /// <param name="samples">A span containing the audio samples to be played.</param>
    /// <remarks>
    /// This function queues audio data for playback. If the output queue is full,
    /// it will wait briefly and retry. Each sample array should contain interleaved
    /// audio data for all channels according to the configured format.
    /// The samples span should contain exactly FramesPerBuffer * Channels elements.
    /// </remarks>
    public void Send(Span<float> samples)
    {
        if (_outputBufferQueue.Count < _maxQueueSize)
        {
            float[]? buffer;
            if (!_outputBufferPool.TryTake(out buffer) || buffer.Length != samples.Length)
            {
                if (buffer != null)
                    _outputBufferPool.Add(buffer);

                buffer = new float[samples.Length];
            }

            samples.CopyTo(buffer);
            _outputBufferQueue.Enqueue(buffer);
        }
        else
        {
            Thread.Sleep(5);

            if (_outputBufferQueue.Count < _maxQueueSize)
            {
                float[]? buffer;
                if (!_outputBufferPool.TryTake(out buffer) || buffer.Length != samples.Length)
                {
                    if (buffer != null)
                        _outputBufferPool.Add(buffer);

                    buffer = new float[samples.Length];
                }

                samples.CopyTo(buffer);
                _outputBufferQueue.Enqueue(buffer);
            }
        }
        _stopwatch.Restart();
    }

    /// <summary>
    /// Receives audio data from the input device.
    /// </summary>
    /// <param name="samples">An output array that will be filled with the received audio samples.</param>
    /// <remarks>
    /// This function attempts to retrieve audio data from the input queue.
    /// If no data is immediately available, it will wait briefly for data
    /// to arrive. If no data arrives within the timeout period, it will
    /// return an empty buffer. The samples array will contain interleaved
    /// audio data for all input channels.
    /// </remarks>
    public void Receives(out float[] samples)
    {
#nullable disable
        if (_inputBufferQueue.TryDequeue(out samples))
        {
            return;
        }

        int waitCount = 0;
        int maxWait = 20;

        while (!_inputBufferQueue.TryDequeue(out samples) && waitCount < maxWait)
        {
            Thread.Sleep(5);
            waitCount++;
        }

        if (samples == null)
        {
            if (!_inputBufferPool.TryTake(out samples) || samples.Length != FramesPerBuffer * (int)_inputoptions.Channels)
            {
                if (samples != null)
                    _inputBufferPool.Add(samples);

                samples = new float[FramesPerBuffer * (int)_inputoptions.Channels];
            }

            Array.Clear(samples, 0, samples.Length);
        }
#nullable restore
    }

    /// <summary>
    /// Releases all resources used by the audio engine.
    /// </summary>
    /// <remarks>
    /// This function stops and closes the audio stream, frees any allocated
    /// memory for host-specific stream information, and releases the GC handle
    /// for the callback. It clears all buffer pools and marks the instance as disposed.
    /// This method is called automatically when the object is garbage collected.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed || _stream == IntPtr.Zero)
            return;

        if (_stream != IntPtr.Zero)
        {
            try
            {
                Pa_AbortStream(_stream);
                Pa_CloseStream(_stream);
            }
            catch (PortAudioException pae)
            {
                Debug.WriteLine($"Error closing PortAudio stream: {pae.Message})");
            }
            _stream = IntPtr.Zero;
        }

        float[]? buffer;
        while (_outputBufferQueue.TryDequeue(out buffer))
            _outputBufferPool.Add(buffer);

        while (_inputBufferQueue.TryDequeue(out buffer))
            _inputBufferPool.Add(buffer);

        if (inHostApiSpecific != IntPtr.Zero)
            Marshal.FreeHGlobal(inHostApiSpecific);

        if (outHostApiSpecific != IntPtr.Zero)
            Marshal.FreeHGlobal(outHostApiSpecific);

        if (_callbackHandle.IsAllocated)
            _callbackHandle.Free();

        _inputBufferPool.Clear();
        _outputBufferPool.Clear();
        _silenceBuffer = null;

        _disposed = true;
    }
}
