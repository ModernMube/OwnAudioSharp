using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Ownaudio.Native.Utils;

namespace Ownaudio.Native.PortAudio;

/// <summary>
/// Provides unmanaged bindings and wrapper methods for calling native
/// PortAudio shared library APIs on all supported platforms.
/// </summary>
internal static unsafe partial class PaBinding
{
    /// <summary>
    /// Initializes all PortAudio function pointers by loading their corresponding
    /// exported symbols from the provided library loader.
    /// </summary>
    /// <param name="loader">The library loader containing the loaded native library handle.</param>
    public static void InitializeBindings(LibraryLoader loader)
    {
        _initialize = (delegate* unmanaged[Cdecl]<int>)
            loader.GetExport("Pa_Initialize");

        _terminate = (delegate* unmanaged[Cdecl]<int>)
            loader.GetExport("Pa_Terminate");

        _getErrorText = (delegate* unmanaged[Cdecl]<int, IntPtr>)
            loader.GetExport("Pa_GetErrorText");

        _getDefaultOutputDevice = (delegate* unmanaged[Cdecl]<int>)
            loader.GetExport("Pa_GetDefaultOutputDevice");

        _getDeviceInfo = (delegate* unmanaged[Cdecl]<int, IntPtr>)
            loader.GetExport("Pa_GetDeviceInfo");

        _getDeviceCount = (delegate* unmanaged[Cdecl]<int>)
            loader.GetExport("Pa_GetDeviceCount");

        _openStream = (delegate* unmanaged[Cdecl]<out IntPtr, IntPtr, IntPtr, double, long, PaStreamFlags, IntPtr, IntPtr, int>)
            loader.GetExport("Pa_OpenStream");

        _writeStream = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, long, int>)
            loader.GetExport("Pa_WriteStream");

        _startStream = (delegate* unmanaged[Cdecl]<IntPtr, int>)
            loader.GetExport("Pa_StartStream");

        _stopStream = (delegate* unmanaged[Cdecl]<IntPtr, int>)
            loader.GetExport("Pa_StopStream");

        _abortStream = (delegate* unmanaged[Cdecl]<IntPtr, int>)
            loader.GetExport("Pa_AbortStream");

        _closeStream = (delegate* unmanaged[Cdecl]<IntPtr, int>)
            loader.GetExport("Pa_CloseStream");

        _isStreamActive = (delegate* unmanaged[Cdecl]<IntPtr, int>)
            loader.GetExport("Pa_IsStreamActive");

        _isStreamStopped = (delegate* unmanaged[Cdecl]<IntPtr, int>)
            loader.GetExport("Pa_IsStreamStopped");

        _hostApiTypeIdToHostApiIndex = (delegate* unmanaged[Cdecl]<PaHostApiTypeId, int>)
            loader.GetExport("Pa_HostApiTypeIdToHostApiIndex");

        _hostApiDeviceIndexToDeviceIndex = (delegate* unmanaged[Cdecl]<int, int, int>)
            loader.GetExport("Pa_HostApiDeviceIndexToDeviceIndex");

        _getHostApiInfo = (delegate* unmanaged[Cdecl]<int, IntPtr>)
            loader.GetExport("Pa_GetHostApiInfo");

        _getDefaultHostApi = (delegate* unmanaged[Cdecl]<int>)
            loader.GetExport("Pa_GetDefaultHostApi");

        _getDefaultInputDevice = (delegate* unmanaged[Cdecl]<int>)
            loader.GetExport("Pa_GetDefaultInputDevice");

        _readStream = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, long, int>)
            loader.GetExport("Pa_ReadStream");

        _isFormatSupported = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, double, int>)
            loader.GetExport("Pa_IsFormatSupported");
    }

    /// <summary>
    /// Initializes the PortAudio library interface.
    /// This must be called before invoking any other PortAudio APIs.
    /// </summary>
    /// <returns>Zero on success, or a negative PortAudio error code.</returns>
    public static int Pa_Initialize()
    {
        return _initialize != null ? _initialize() : -1;
    }

    /// <summary>
    /// Terminates the PortAudio library interface.
    /// Releases all resources allocated by the library during its execution.
    /// </summary>
    /// <returns>Zero on success, or a negative PortAudio error code.</returns>
    public static int Pa_Terminate()
    {
        return _terminate != null ? _terminate() : 0;
    }

    /// <summary>
    /// Retrieves a pointer to a human-readable description string for a given error code.
    /// Useful for logging and diagnostic print statements.
    /// </summary>
    /// <param name="code">The error code returned by a PortAudio function.</param>
    /// <returns>An unmanaged pointer to a null-terminated UTF-8 string.</returns>
    public static IntPtr Pa_GetErrorText(int code)
    {
        return _getErrorText != null ? _getErrorText(code) : IntPtr.Zero;
    }

    /// <summary>
    /// Retrieves the index of the default output device.
    /// Returns a valid device index or a negative error value if none is available.
    /// </summary>
    /// <returns>The index of the default output device, or negative on failure.</returns>
    public static int Pa_GetDefaultOutputDevice()
    {
        return _getDefaultOutputDevice != null ? _getDefaultOutputDevice() : -1;
    }

    /// <summary>
    /// Retrieves a pointer to the device information structure for a specific index.
    /// The returned memory is managed by PortAudio and must not be freed by the caller.
    /// </summary>
    /// <param name="device">The index of the target device to query.</param>
    /// <returns>A pointer to a read-only device information structure.</returns>
    public static IntPtr Pa_GetDeviceInfo(int device)
    {
        return _getDeviceInfo != null ? _getDeviceInfo(device) : IntPtr.Zero;
    }

    /// <summary>
    /// Retrieves the total number of audio devices recognized by the system.
    /// Returns zero or a positive count, or a negative error code.
    /// </summary>
    /// <returns>The total number of available devices on the system.</returns>
    public static int Pa_GetDeviceCount()
    {
        return _getDeviceCount != null ? _getDeviceCount() : 0;
    }

    /// <summary>
    /// Opens an audio stream with the specified parameters and callback.
    /// Converts the managed delegate callback to an unmanaged function pointer.
    /// </summary>
    /// <param name="stream">Receives the opened stream pointer handle.</param>
    /// <param name="inputParameters">Configuration parameters for the input device, or zero.</param>
    /// <param name="outputParameters">Configuration parameters for the output device, or zero.</param>
    /// <param name="sampleRate">The sample rate in Hertz (e.g. 44100.0 or 48000.0).</param>
    /// <param name="framesPerBuffer">The number of frames passed to the callback function.</param>
    /// <param name="streamFlags">Flags controlling the latency and buffer priming behaviour.</param>
    /// <param name="streamCallback">The managed callback delegate to process audio buffers.</param>
    /// <param name="userData">User-defined context data pointer passed back in the callback.</param>
    /// <returns>Zero on success, or a negative PortAudio error code.</returns>
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
        stream = IntPtr.Zero;
        if (_openStream == null)
            return -1;

        IntPtr cb = streamCallback != null
            ? Marshal.GetFunctionPointerForDelegate(streamCallback)
            : IntPtr.Zero;

        return _openStream(
            out stream,
            inputParameters,
            outputParameters,
            sampleRate,
            framesPerBuffer,
            streamFlags,
            cb,
            userData
        );
    }

    /// <summary>
    /// Starts audio processing on the specified active stream.
    /// Triggers the registered stream callback repeatedly in a background thread.
    /// </summary>
    /// <param name="stream">The pointer handle of the stream to start.</param>
    /// <returns>Zero on success, or a negative PortAudio error code.</returns>
    public static int Pa_StartStream(IntPtr stream)
    {
        return _startStream != null ? _startStream(stream) : -1;
    }

    /// <summary>
    /// Stops audio processing on the specified active stream gracefully.
    /// Blocks until all queued buffers have completed playback.
    /// </summary>
    /// <param name="stream">The pointer handle of the stream to stop.</param>
    /// <returns>Zero on success, or a negative PortAudio error code.</returns>
    public static int Pa_StopStream(IntPtr stream)
    {
        return _stopStream != null ? _stopStream(stream) : -1;
    }

    /// <summary>
    /// Writes audio frames to the output stream in blocking mode.
    /// Should not be used when asynchronous callbacks are active.
    /// </summary>
    /// <param name="stream">The pointer handle of the stream to write to.</param>
    /// <param name="buffer">Pointer to the source buffer containing interleaved samples.</param>
    /// <param name="frames">The number of audio frames to write.</param>
    /// <returns>Zero on success, or a negative PortAudio error code.</returns>
    public static int Pa_WriteStream(IntPtr stream, IntPtr buffer, long frames)
    {
        return _writeStream != null ? _writeStream(stream, buffer, frames) : -1;
    }

    /// <summary>
    /// Aborts audio processing on the specified active stream immediately.
    /// Discards any remaining queued buffers without waiting for playback.
    /// </summary>
    /// <param name="stream">The pointer handle of the stream to abort.</param>
    /// <returns>Zero on success, or a negative PortAudio error code.</returns>
    public static int Pa_AbortStream(IntPtr stream)
    {
        return _abortStream != null ? _abortStream(stream) : -1;
    }

    /// <summary>
    /// Closes an open audio stream and releases all allocated stream resources.
    /// Stops the stream first if it is still running.
    /// </summary>
    /// <param name="stream">The pointer handle of the stream to close.</param>
    /// <returns>Zero on success, or a negative PortAudio error code.</returns>
    public static int Pa_CloseStream(IntPtr stream)
    {
        return _closeStream != null ? _closeStream(stream) : -1;
    }

    /// <summary>
    /// Retrieves the index of the default input device.
    /// Returns a valid device index or a negative error value if none is available.
    /// </summary>
    /// <returns>The index of the default input device, or negative on failure.</returns>
    public static int Pa_GetDefaultInputDevice()
    {
        return _getDefaultInputDevice != null ? _getDefaultInputDevice() : -1;
    }

    /// <summary>
    /// Reads audio frames from the input stream in blocking mode.
    /// Should not be used when asynchronous callbacks are active.
    /// </summary>
    /// <param name="stream">The pointer handle of the stream to read from.</param>
    /// <param name="buffer">Pointer to the destination buffer to store interleaved samples.</param>
    /// <param name="frames">The number of audio frames to read.</param>
    /// <returns>Zero on success, or a negative PortAudio error code.</returns>
    public static int Pa_ReadStream(IntPtr stream, IntPtr buffer, long frames)
    {
        return _readStream != null ? _readStream(stream, buffer, frames) : -1;
    }

    /// <summary>
    /// Determines whether the specified stream is active and running.
    /// Returns a positive value if active, zero if inactive, or negative error.
    /// </summary>
    /// <param name="stream">The pointer handle of the stream to inspect.</param>
    /// <returns>One if active, zero if inactive, or negative on failure.</returns>
    public static int Pa_IsStreamActive(IntPtr stream)
    {
        return _isStreamActive != null ? _isStreamActive(stream) : 0;
    }

    /// <summary>
    /// Determines whether the specified stream is currently stopped or inactive.
    /// Returns a positive value if stopped, zero if active, or negative error.
    /// </summary>
    /// <param name="stream">The pointer handle of the stream to inspect.</param>
    /// <returns>One if stopped, zero if running, or negative on failure.</returns>
    public static int Pa_IsStreamStopped(IntPtr stream)
    {
        return _isStreamStopped != null ? _isStreamStopped(stream) : 1;
    }

    /// <summary>
    /// Translates a host API type identifier to its corresponding API index.
    /// Returns a negative value if the host API is not supported on this platform.
    /// </summary>
    /// <param name="hostApiTypeId">The host API type identifier (e.g. WASAPI, CoreAudio).</param>
    /// <returns>The index of the host API, or negative if not available.</returns>
    public static int Pa_HostApiTypeIdToHostApiIndex(PaHostApiTypeId hostApiTypeId)
    {
        return _hostApiTypeIdToHostApiIndex != null ? _hostApiTypeIdToHostApiIndex(hostApiTypeId) : -1;
    }

    /// <summary>
    /// Translates a host API device index to a global PortAudio device index.
    /// Used to convert local indices back to system global indices.
    /// </summary>
    /// <param name="hostApiIndex">The index of the target host API.</param>
    /// <param name="device">The local device index under that host API.</param>
    /// <returns>The global PortAudio device index, or negative on error.</returns>
    public static int Pa_HostApiDeviceIndexToDeviceIndex(int hostApiIndex, int device)
    {
        return _hostApiDeviceIndexToDeviceIndex != null ? _hostApiDeviceIndexToDeviceIndex(hostApiIndex, device) : -1;
    }

    /// <summary>
    /// Retrieves a pointer to the host API information structure for a specific index.
    /// The returned memory is managed by PortAudio and must not be freed by the caller.
    /// </summary>
    /// <param name="ApiIndex">The index of the target host API to query.</param>
    /// <returns>A pointer to a read-only host API information structure.</returns>
    public static IntPtr Pa_GetHostApiInfo(int ApiIndex)
    {
        return _getHostApiInfo != null ? _getHostApiInfo(ApiIndex) : IntPtr.Zero;
    }

    /// <summary>
    /// Retrieves the index of the default host API on the current platform.
    /// Returns a zero-based index or a negative error value on failure.
    /// </summary>
    /// <returns>The index of the default host API, or negative on failure.</returns>
    public static int Pa_GetDefaultHostApi()
    {
        return _getDefaultHostApi != null ? _getDefaultHostApi() : -1;
    }

    /// <summary>
    /// Verifies whether the specified configuration parameters are supported by the system.
    /// Returns zero if supported, or a negative error code if unsupported.
    /// </summary>
    /// <param name="inputParameters">Configuration parameters for the input device, or zero.</param>
    /// <param name="outputParameters">Configuration parameters for the output device, or zero.</param>
    /// <param name="sampleRate">The target sample rate in Hertz.</param>
    /// <returns>Zero if supported, or negative error if unsupported.</returns>
    public static int Pa_IsFormatSupported(IntPtr inputParameters, IntPtr outputParameters, double sampleRate)
    {
        return _isFormatSupported != null ? _isFormatSupported(inputParameters, outputParameters, sampleRate) : -1;
    }
}
