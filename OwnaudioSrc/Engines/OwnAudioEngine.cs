using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

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
    private const   PaStreamFlags StreamFlags = PaStreamFlags.paNoFlag;
    private readonly AudioEngineOutputOptions _outoptions;
    private readonly AudioEngineInputOptions _inputoptions;
    private readonly IntPtr _stream;
    private readonly PaStreamParameters inparameters;
    private readonly PaStreamParameters parameters;
    private readonly IntPtr inHostApiSpecific;
    private readonly IntPtr outHostApiSpecific;

    private bool _disposed;

    private readonly object _lock = new object();
    private ConcurrentQueue<float[]> _sendData = new ConcurrentQueue<float[]>();

    /// <summary>
    /// Initializes <see cref="OwnAudioEngine"/> object.
    /// </summary>
    /// <param name="outoptions">Optional audio engine output options.</param>
    /// <param name="framesPerBuffer"></param>
    /// <exception cref="PortAudioException">
    /// Might be thrown when errors occured during PortAudio stream initialization.
    /// </exception>
    public OwnAudioEngine(AudioEngineOutputOptions? outoptions = default, int framesPerBuffer = 512)
    {
        _outoptions = outoptions ?? new AudioEngineOutputOptions();
        _inputoptions = new AudioEngineInputOptions();
        FramesPerBuffer = framesPerBuffer;

        var parameters = new PaStreamParameters
        {
            channelCount = _outoptions.Channels,
            device = _outoptions.Device.DeviceIndex,
            hostApiSpecificStreamInfo = IntPtr.Zero,
            sampleFormat = OwnAudio.Constants.PaSampleFormat,
            suggestedLatency = _outoptions.Latency
        };

        IntPtr stream;
       
        unsafe
        {
            PaStreamParameters tempParameters;
            var parametersPtr = new IntPtr(&tempParameters);
            Marshal.StructureToPtr(parameters, parametersPtr, false);

#nullable disable
            var code = Pa_OpenStream(
                new IntPtr(&stream),
                IntPtr.Zero,
                parametersPtr,
                _outoptions.SampleRate,
                FramesPerBuffer,
                StreamFlags,
                null,
                IntPtr.Zero).PaGuard();
#nullable restore

            Debug.WriteLine(code.PaErrorToText());

        }
        
        _stream = stream;

    }

    /// <summary>
    /// Initializes <see cref="OwnAudioEngine"/> object.
    /// </summary>
    /// <param name="inoptions">Optional audio engine input options.</param>
    /// <param name="outoptions">Optional audio engine output options.</param>
    /// <param name="framesPerBuffer"></param>
    /// <exception cref="PortAudioException">
    /// Might be thrown when errors occured during PortAudio stream initialization.
    /// </exception>
    public OwnAudioEngine(AudioEngineInputOptions? inoptions = default, AudioEngineOutputOptions? outoptions = default, int framesPerBuffer = 512)
    {
        _inputoptions = inoptions ?? new AudioEngineInputOptions();
        _outoptions = outoptions ?? new AudioEngineOutputOptions();
        FramesPerBuffer = framesPerBuffer;

        inparameters = new PaStreamParameters
        {
            channelCount = _inputoptions.Channels,
            device = _inputoptions.Device.DeviceIndex,
            hostApiSpecificStreamInfo = IntPtr.Zero,
            sampleFormat = OwnAudio.Constants.PaSampleFormat,
            suggestedLatency = _inputoptions.Latency
        };

        if(OwnAudio.HostID.PaHostApiInfo().name.ToLower().Contains("wasapi")) //Wasapi host spcific
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
                null,
                IntPtr.Zero).PaGuard();
#nullable restore

            Debug.WriteLine(codeIn.PaErrorToText());
        }

        _stream = stream;
    }

    /// <summary>
    /// Engine Frames per Buffer
    /// </summary>
    public int FramesPerBuffer { get; private set; } = 512;
    
    /// <summary>
    /// Wasapi Host api specific stream info
    /// </summary>
    /// <param name="flags"></param>
    /// <returns></returns>
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
        Marshal.StructureToPtr(wasapiStreamInfo, ptr, true); // *true*, hogy a régi adatokat törölje

        Debug.WriteLine($"Created WasapiStreamInfo pointer: {ptr}");
        return ptr;
    }

    /// <summary>
    /// Asio Host api specific stream info
    /// </summary>
    /// <param name="channelNumbers"></param>
    /// <returns></returns>
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
    /// Stream output pointer
    /// </summary>
    public IntPtr GetStream() { return _stream; }

    /// <summary>
    /// Returns a numeric value about the activity of the audio engine
    /// </summary>
    /// <returns>
    /// 0 - the engine not playing or recording
    /// 1 - the engine plays or records
    /// negative value if there is an error
    /// </returns>
    public int OwnAudioEngineActivate() { return Pa_IsStreamActive(_stream); }

    /// <summary>
    /// It returns a value whether the motor is stopped or running
    /// </summary>
    /// <returns>
    /// 0 - the engine running
    /// 1 - the engine stopped
    /// negative value if there is an error
    /// </returns>
    public int OwnAudioEngineStopped() {  return Pa_IsStreamStopped(_stream); }

    /// <summary>
    /// Audio engine start
    /// </summary>
    /// <returns>Error code</returns>
    public int Start() 
    {
        return Pa_StartStream(_stream).PaGuard();
    }

    /// <summary>
    /// Audio engine stop
    /// </summary>
    /// <returns>Error code</returns>
    public int Stop() 
    {
        return Pa_StopStream(_stream).PaGuard(); 
    }

    /// <summary>
    /// Sends audio data to the output.
    /// </summary>
    /// <param name="samples">An array of data</param>
    public void Send(Span<float> samples)
    {
        lock (_lock)
        {
            unsafe
            {
                fixed (float* buffer = samples)
                {
                    var frames = samples.Length / (int)_outoptions.Channels;
                    Pa_WriteStream(_stream, (IntPtr)buffer, frames);
                }
            }
        }
    }

    /// <summary>
    /// Receives audio data from the input
    /// </summary>
    /// <param name="samples"></param>
    public void Receives(out float[] samples)
    {
        lock (_lock)
        {
            unsafe
            {
                samples = new float[FramesPerBuffer * (int)_inputoptions.Channels];
                fixed (float* bufferPtr = samples)
                {
                    Pa_ReadStream(_stream, (IntPtr)bufferPtr, FramesPerBuffer).PaGuard();
                }
            }
        }
    }

    /// <summary>
    /// Dispose audio engine
    /// </summary>
    public void Dispose()
    {
        if (_disposed || _stream == IntPtr.Zero)
            return;

        Pa_AbortStream(_stream);
        Pa_CloseStream(_stream);

        Marshal.FreeHGlobal(inHostApiSpecific);
        Marshal.FreeHGlobal(outHostApiSpecific);

        _disposed = true;
    }
}
