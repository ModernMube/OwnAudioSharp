using System;
using Ownaudio.Native.Utils;

namespace Ownaudio.Native.PortAudio;

internal static partial class PaBinding
{
    public static void InitializeBindings(LibraryLoader loader)
    {
        _initialize = loader.LoadFunc<Initialize>(nameof(Pa_Initialize));
        _terminate = loader.LoadFunc<Terminate>(nameof(Pa_Terminate));

        _getErrorText = loader.LoadFunc<GetErrorText>(nameof(Pa_GetErrorText));

        _getDefaultOutputDevice = loader.LoadFunc<GetDefaultOutputDevice>(nameof(Pa_GetDefaultOutputDevice));
        _getDeviceInfo = loader.LoadFunc<GetDeviceInfo>(nameof(Pa_GetDeviceInfo));
        _getDeviceCount = loader.LoadFunc<GetDeviceCount>(nameof(Pa_GetDeviceCount));

        _openStream = loader.LoadFunc<OpenStream>(nameof(Pa_OpenStream));
        _writeStream = loader.LoadFunc<WriteStream>(nameof(Pa_WriteStream));
        _startStream = loader.LoadFunc<StartStream>(nameof(Pa_StartStream));
        _stopStream = loader.LoadFunc<StopStream>(nameof(Pa_StopStream));
        _abortStream = loader.LoadFunc<AbortStream>(nameof(Pa_AbortStream));
        _closeStream = loader.LoadFunc<CloseStream>(nameof(Pa_CloseStream));
        _isStreamActive = loader.LoadFunc<IsStreamActive>(nameof(Pa_IsStreamActive));
        _isStreamStopped = loader.LoadFunc<IsStreamStopped>(nameof(Pa_IsStreamStopped));

        _hostApiTypeIdToHostApiIndex = loader.LoadFunc<HostApiTypeIdToHostApiIndex>(nameof(Pa_HostApiTypeIdToHostApiIndex));
        _hostApiDeviceIndexToDeviceIndex = loader.LoadFunc<HostApiDeviceIndexToDeviceIndex>(nameof(Pa_HostApiDeviceIndexToDeviceIndex));
        _getHostApiInfo = loader.LoadFunc<GetHostApiInfo>(nameof(Pa_GetHostApiInfo));
        _getDefaultHostApi = loader.LoadFunc<GetDefaultHostApi>(nameof(Pa_GetDefaultHostApi));

        _getDefaultInputDevice = loader.LoadFunc<GetDefaultInputDevice>(nameof(Pa_GetDefaultInputDevice));
        _readStream = loader.LoadFunc<ReadStream>(nameof(Pa_ReadStream));
    }

#nullable disable
    public static int Pa_Initialize()
    {
        return _initialize();
    }

    public static int Pa_Terminate()
    {
        return _terminate();
    }

    public static IntPtr Pa_GetErrorText(int code)
    {
        return _getErrorText(code);
    }

    public static int Pa_GetDefaultOutputDevice()
    {
        return _getDefaultOutputDevice();
    }

    public static IntPtr Pa_GetDeviceInfo(int device)
    {
        return _getDeviceInfo(device);
    }

    public static int Pa_GetDeviceCount()
    {
        return _getDeviceCount();
    }

    public static int Pa_OpenStream(
        out IntPtr stream,
        IntPtr inputParameters,
        IntPtr outputParameters,
        double sampleRate,
        long framesPerBuffer,
        PaStreamFlags streamFlags,
        PaStreamCallback streamCallback,
        IntPtr userData)
    {
        return _openStream(
            out stream,
            inputParameters,
            outputParameters,
            sampleRate,
            framesPerBuffer,
            streamFlags,
            streamCallback,
            userData
        );
    }

    public static int Pa_StartStream(IntPtr stream)
    {
        return _startStream(stream);
    }

    public static int Pa_StopStream(IntPtr stream)
    {
        return _stopStream(stream);
    }

    public static int Pa_WriteStream(IntPtr stream, IntPtr buffer, long frames)
    {
        return _writeStream(stream, buffer, frames);
    }

    public static int Pa_AbortStream(IntPtr stream)
    {
        return _abortStream(stream);
    }

    public static int Pa_CloseStream(IntPtr stream)
    {
        return _closeStream(stream);
    }

    public static int Pa_GetDefaultInputDevice()
    {
        return _getDefaultInputDevice();
    }

    public static int Pa_ReadStream(IntPtr stream, IntPtr buffer, long frames)
    {
        return _readStream(stream, buffer, frames);
    }

    public static int Pa_IsStreamActive(IntPtr stream)
    {
        return _isStreamActive(stream);
    }

    public static int Pa_IsStreamStopped(IntPtr stream)
    {
        return _isStreamStopped(stream);
    }

    public static int Pa_HostApiTypeIdToHostApiIndex(PaHostApiTypeId hostApiTypeId)
    {
        return _hostApiTypeIdToHostApiIndex(hostApiTypeId);
    }

    public static int Pa_HostApiDeviceIndexToDeviceIndex(int hostApiIndex, int device)
    {
        return _hostApiDeviceIndexToDeviceIndex(hostApiIndex, device);
    }

    public static IntPtr Pa_GetHostApiInfo(int ApiIndex)
    {
        return _getHostApiInfo(ApiIndex);
    }

    public static int Pa_GetDefaultHostApi()
    {
        return _getDefaultHostApi();
    }
#nullable restore
}
