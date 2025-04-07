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
    private readonly IntPtr _stream;
    private readonly PaStreamParameters inparameters;
    private readonly PaStreamParameters parameters;
    private readonly IntPtr inHostApiSpecific;
    private readonly IntPtr outHostApiSpecific;

    private bool _disposed;

    private readonly PaStreamCallback _paCallback;
    private readonly GCHandle _callbackHandle;
    private readonly ConcurrentQueue<float[]> _inputBufferQueue = new ConcurrentQueue<float[]>();
    private readonly ConcurrentQueue<float[]> _outputBufferQueue = new ConcurrentQueue<float[]>();
    private readonly int _maxQueueSize = 10;

    private long _totalSamplesProcessed = 0;
    private long _totalSamplesQueued = 0;
    private readonly object _positionLock = new object();

    // Időzítés mérését segítő mezők
    private Stopwatch _playbackTimer = new Stopwatch();
    private bool _isPlaying = false;

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
    {
        _inputoptions = new AudioEngineInputOptions();
        _outoptions = outoptions ?? new AudioEngineOutputOptions();
        FramesPerBuffer = framesPerBuffer;

        unsafe
        {
#nullable disable
            _paCallback =
            (void* inputBuffer, void* outputBuffer, long frameCount, IntPtr timeInfo, PaStreamCallbackFlags statusFlags, void* userData) =>
            {
                if (outputBuffer != null && _outoptions.Channels > 0)
                {
                    float[] outputData = null;

                    if (_outputBufferQueue.TryDequeue(out outputData))
                    {
                        if (outputData.Length != frameCount * (int)_outoptions.Channels)
                        {
                            Array.Resize(ref outputData, (int)(frameCount * (int)_outoptions.Channels));
                        }

                        Marshal.Copy(outputData, 0, (IntPtr)outputBuffer, outputData.Length);

                        lock (_positionLock)
                        {
                            _totalSamplesProcessed += frameCount;
                        }
                    }
                    else
                    {
                        var silence = new float[(int)(frameCount * (int)_outoptions.Channels)];
                        Marshal.Copy(silence, 0, (IntPtr)outputBuffer, silence.Length);

                        lock (_positionLock)
                        {
                            _totalSamplesProcessed += frameCount;
                        }
                    }
                }

                if (inputBuffer != null && _inputoptions.Channels > 0)
                {
                    var inputData = new float[(int)(frameCount * (int)_inputoptions.Channels)];
                    Marshal.Copy((IntPtr)inputBuffer, inputData, 0, inputData.Length);
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

            IntPtr stream;

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
                    new IntPtr(&stream),
                    inparametersPtr,
                    parametersPtr,
                    outoptions.SampleRate,
                    FramesPerBuffer,
                    StreamFlags,
                    _paCallback,
                    IntPtr.Zero).PaGuard();
#nullable restore
                Debug.WriteLine(codeIn.PaErrorToText());
            }

            _stream = stream;
        }
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

        unsafe
        {
#nullable disable
            _paCallback =
            (void* inputBuffer, void* outputBuffer, long frameCount, IntPtr timeInfo, PaStreamCallbackFlags statusFlags, void* userData) =>
            {
                if (outputBuffer != null && _outoptions.Channels > 0)
                {
                    float[] outputData = null;

                    if (_outputBufferQueue.TryDequeue(out outputData))
                    {
                        if (outputData.Length != frameCount * (int)_outoptions.Channels)
                        {
                            Array.Resize(ref outputData, (int)(frameCount * (int)_outoptions.Channels));
                        }

                        Marshal.Copy(outputData, 0, (IntPtr)outputBuffer, outputData.Length);

                        lock (_positionLock)
                        {
                            _totalSamplesProcessed += frameCount;
                        }
                    }
                    else
                    {
                        var silence = new float[(int)(frameCount * (int)_outoptions.Channels)];
                        Marshal.Copy(silence, 0, (IntPtr)outputBuffer, silence.Length);

                        lock (_positionLock)
                        {
                            _totalSamplesProcessed += frameCount;
                        }
                    }
                }

                if (inputBuffer != null && _inputoptions.Channels > 0)
                {
                    var inputData = new float[(int)(frameCount * (int)_inputoptions.Channels)];
                    Marshal.Copy((IntPtr)inputBuffer, inputData, 0, inputData.Length);

                    while (_inputBufferQueue.Count >= _maxQueueSize)
                    {
                        float[] dummy;
                        _inputBufferQueue.TryDequeue(out dummy);
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

            IntPtr stream;

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
                    new IntPtr(&stream),
                    inparametersPtr,
                    parametersPtr,
                    outoptions.SampleRate,
                    FramesPerBuffer,
                    StreamFlags,
                    _paCallback,
                    IntPtr.Zero).PaGuard();
#nullable restore
                Debug.WriteLine(codeIn.PaErrorToText());
            }

            _stream = stream;
        }
    }

    /// <summary>
    /// Engine Frames per Buffer
    /// </summary>
    public int FramesPerBuffer { get; private set; } = 512;

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
    /// Checks if the audio engine is currently active.
    /// </summary>
    /// <returns>
    /// 0 - the engine is not playing or recording
    /// 1 - the engine is playing or recording
    /// negative value if there is an error
    /// </returns>
    /// <remarks>
    /// This function checks the activity state of the audio engine.
    /// </remarks>
    public int OwnAudioEngineActivate() { return Pa_IsStreamActive(_stream); }

    /// <summary>
    /// Checks if the audio engine is currently stopped.
    /// </summary>
    /// <returns>
    /// 0 - the engine is running
    /// 1 - the engine is stopped
    /// negative value if there is an error
    /// </returns>
    /// <remarks>
    /// This function checks the stopped state of the audio engine.
    /// </remarks>
    public int OwnAudioEngineStopped() { return Pa_IsStreamStopped(_stream); }

    /// <summary>
    /// Gets the current playback position in samples.
    /// </summary>
    /// <returns>The current playback position expressed in samples.</returns>
    /// <remarks>
    /// This function returns the number of audio samples that have been processed
    /// since the start of playback or since the last position reset.
    /// </remarks>
    public long GetCurrentPosition()
    {
        lock (_positionLock)
        {
            return _totalSamplesProcessed;
        }
    }

    /// <summary>
    /// Gets the current playback position in seconds.
    /// </summary>
    /// <returns>The current playback position expressed in seconds.</returns>
    /// <remarks>
    /// This function returns the playback time in seconds by converting the
    /// sample position using the configured sample rate.
    /// </remarks>
    public double GetCurrentPositionInSeconds()
    {
        if (_isPlaying)
        {
            double elapsedTime = _playbackTimer.Elapsed.TotalSeconds;

            lock (_positionLock)
            {
                double portAudioTime = (double)_totalSamplesProcessed / _outoptions.SampleRate;
                return (elapsedTime + portAudioTime) / 2.0;
            }
        }
        else
        {
            lock (_positionLock)
            {
                return (double)_totalSamplesProcessed / _outoptions.SampleRate;
            }
        }
    }

    /// <summary>
    /// Gets the estimated latency in samples.
    /// </summary>
    /// <returns>The latency expressed in samples.</returns>
    /// <remarks>
    /// This function calculates the difference between the number of samples
    /// queued and the number of samples processed, which represents the
    /// current latency of the audio engine.
    /// </remarks>
    public long GetCurrentLatencyInSamples()
    {
        lock (_positionLock)
        {
            return _totalSamplesQueued - _totalSamplesProcessed;
        }
    }

    /// <summary>
    /// Resets the position counters of the audio engine.
    /// </summary>
    /// <remarks>
    /// This function resets both the processed samples counter and the
    /// queued samples counter to zero, effectively resetting the playback position.
    /// </remarks>
    public void ResetPosition()
    {
        lock (_positionLock)
        {
            _totalSamplesProcessed = 0;
            _totalSamplesQueued = 0;

            float[] dummy;
#nullable disable
            while (_outputBufferQueue.TryDequeue(out dummy)) { }
            while (_inputBufferQueue.TryDequeue(out dummy)) { }
#nullable restore
        }
    }

    /// <summary>
    /// Starts the audio engine.
    /// </summary>
    /// <returns>Error code from the PortAudio library (0 means success).</returns>
    /// <remarks>
    /// This function starts the audio stream and resets the position counters.
    /// It should be called before sending audio data to the engine.
    /// </remarks>
    public int Start()
    {
        lock (_positionLock)
        {
            _totalSamplesProcessed = 0;
            _totalSamplesQueued = 0;
        }

        _playbackTimer.Reset();
        _playbackTimer.Start();
        _isPlaying = true;

        return Pa_StartStream(_stream).PaGuard();
    }

    /// <summary>
    /// Stops the audio engine.
    /// </summary>
    /// <returns>Error code from the PortAudio library (0 means success).</returns>
    /// <remarks>
    /// This function stops the audio stream and clears all internal buffers.
    /// </remarks>
    public int Stop()
    {
#nullable disable
        _playbackTimer.Stop();
        _isPlaying = false;

        int result = Pa_StopStream(_stream).PaGuard();

        float[] dummy;
        while (_outputBufferQueue.TryDequeue(out dummy)) { }
        while (_inputBufferQueue.TryDequeue(out dummy)) { }

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
    /// audio data for all channels.
    /// </remarks>
    public void Send(Span<float> samples)
    {
        if (_outputBufferQueue.Count < _maxQueueSize)
        {
            var data = samples.ToArray();
            _outputBufferQueue.Enqueue(data);

            lock (_positionLock)
            {
                _totalSamplesQueued += samples.Length / (int)_outoptions.Channels;
            }
        }
        else
        {
            Thread.Sleep(5);

            if (_outputBufferQueue.Count < _maxQueueSize)
            {
                var data = samples.ToArray();
                _outputBufferQueue.Enqueue(data);

                lock (_positionLock)
                {
                    _totalSamplesQueued += samples.Length / (int)_outoptions.Channels;
                }
            }
        }
    }

    /// <summary>
    /// Receives audio data from the input device.
    /// </summary>
    /// <param name="samples">An output array that will be filled with the received audio samples.</param>
    /// <remarks>
    /// This function attempts to retrieve audio data from the input queue.
    /// If no data is immediately available, it will wait briefly for data
    /// to arrive. If no data arrives within the timeout period, it will
    /// return an empty buffer.
    /// </remarks>
    public void Receives(out float[] samples)
    {
#nullable disable
        if (_inputBufferQueue.TryDequeue(out samples))
        {
            return;
        }

        int waitCount = 0;
        int maxWait = 20; // Adjust as needed

        while (!_inputBufferQueue.TryDequeue(out samples) && waitCount < maxWait)
        {
            Thread.Sleep(5);
            waitCount++;
        }

        if (samples == null)
        {
            samples = new float[FramesPerBuffer * (int)_inputoptions.Channels];
        }
#nullable restore
    }

    /// <summary>
    /// Releases all resources used by the audio engine.
    /// </summary>
    /// <remarks>
    /// This function stops and closes the audio stream, frees any allocated
    /// memory for host-specific stream information, and releases the GC handle
    /// for the callback. It should be called when the audio engine is no longer needed.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed || _stream == IntPtr.Zero)
            return;

        Pa_AbortStream(_stream);
        Pa_CloseStream(_stream);

        if (inHostApiSpecific != IntPtr.Zero)
            Marshal.FreeHGlobal(inHostApiSpecific);

        if (outHostApiSpecific != IntPtr.Zero)
            Marshal.FreeHGlobal(outHostApiSpecific);

        if (_callbackHandle.IsAllocated)
            _callbackHandle.Free();

        _disposed = true;
    }
}
