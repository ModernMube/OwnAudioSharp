using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.PortAudio;

/// <summary>
/// Contains the function pointer fields and unmanaged delegate signatures
/// for binding to the native PortAudio shared library functions.
/// </summary>
internal static unsafe partial class PaBinding
{
    /// <summary>
    /// Function pointer to the native Pa_Initialize function.
    /// Used to initialize the PortAudio library internals.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<int> _initialize;

    /// <summary>
    /// Function pointer to the native Pa_Terminate function.
    /// Used to clean up and deallocate PortAudio library resources.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<int> _terminate;

    /// <summary>
    /// Function pointer to the native Pa_GetErrorText function.
    /// Retrieves a human-readable error message string for a given error code.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<int, IntPtr> _getErrorText;

    /// <summary>
    /// Function pointer to the native Pa_GetDefaultOutputDevice function.
    /// Retrieves the index of the default output device on the host system.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<int> _getDefaultOutputDevice;

    /// <summary>
    /// Function pointer to the native Pa_GetDeviceInfo function.
    /// Retrieves static configuration details for a specific device index.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<int, IntPtr> _getDeviceInfo;

    /// <summary>
    /// Function pointer to the native Pa_GetDeviceCount function.
    /// Retrieves the total number of audio devices available on the host system.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<int> _getDeviceCount;

    /// <summary>
    /// Function pointer to the native Pa_OpenStream function.
    /// Opens an audio stream with the specified parameters and callbacks.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<out IntPtr, IntPtr, IntPtr, double, long, PaStreamFlags, IntPtr, IntPtr, int> _openStream;

    /// <summary>
    /// Function pointer to the native Pa_StartStream function.
    /// Starts audio processing on the specified active stream.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _startStream;

    /// <summary>
    /// Function pointer to the native Pa_StopStream function.
    /// Stops audio processing on the specified stream gracefully.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _stopStream;

    /// <summary>
    /// Function pointer to the native Pa_WriteStream function.
    /// Writes audio frames to the output stream in blocking mode.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, long, int> _writeStream;

    /// <summary>
    /// Function pointer to the native Pa_AbortStream function.
    /// Aborts audio processing on the specified stream immediately.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _abortStream;

    /// <summary>
    /// Function pointer to the native Pa_CloseStream function.
    /// Closes an open audio stream and releases its associated resources.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _closeStream;

    /// <summary>
    /// Function pointer to the native Pa_GetDefaultInputDevice function.
    /// Retrieves the index of the default input device on the host system.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<int> _getDefaultInputDevice;

    /// <summary>
    /// Function pointer to the native Pa_ReadStream function.
    /// Reads audio frames from the input stream in blocking mode.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, long, int> _readStream;

    /// <summary>
    /// Function pointer to the native Pa_IsStreamActive function.
    /// Determines whether the specified stream is currently active and processing.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _isStreamActive;

    /// <summary>
    /// Function pointer to the native Pa_IsStreamStopped function.
    /// Determines whether the specified stream has been stopped or aborted.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _isStreamStopped;

    /// <summary>
    /// Function pointer to the native Pa_HostApiTypeIdToHostApiIndex function.
    /// Translates a host API type identifier to its corresponding API index.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<PaHostApiTypeId, int> _hostApiTypeIdToHostApiIndex;

    /// <summary>
    /// Function pointer to the native Pa_HostApiDeviceIndexToDeviceIndex function.
    /// Translates a host API device index to a global PortAudio device index.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<int, int, int> _hostApiDeviceIndexToDeviceIndex;

    /// <summary>
    /// Function pointer to the native Pa_GetHostApiInfo function.
    /// Retrieves static configuration details for a specific host API index.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<int, IntPtr> _getHostApiInfo;

    /// <summary>
    /// Function pointer to the native Pa_GetDefaultHostApi function.
    /// Retrieves the index of the default host API on the current platform.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<int> _getDefaultHostApi;

    /// <summary>
    /// Function pointer to the native Pa_IsFormatSupported function.
    /// Verifies whether the specified configuration parameters are supported.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, double, int> _isFormatSupported;

    /// <summary>
    /// Unmanaged callback delegate invoked by PortAudio to process input and output audio buffers.
    /// This is used internally for low-latency streaming configurations.
    /// </summary>
    /// <param name="input">Pointer to input audio buffer.</param>
    /// <param name="output">Pointer to output audio buffer.</param>
    /// <param name="frameCount">Number of frames to process.</param>
    /// <param name="timeInfo">Timing information structure pointer.</param>
    /// <param name="statusFlags">Stream callback status flags.</param>
    /// <param name="userData">User-defined data pointer passed to the stream.</param>
    /// <returns>Callback result indicating whether to continue, stop, or abort.</returns>
    public unsafe delegate PaStreamCallbackResult PaStreamCallback(
        void* input,
        void* output,
        long frameCount,
        IntPtr timeInfo,
        PaStreamCallbackFlags statusFlags,
        void* userData
    );
}
