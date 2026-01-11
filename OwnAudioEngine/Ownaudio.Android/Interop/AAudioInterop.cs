using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Android.Interop
{
    /// <summary>
    /// P/Invoke definitions for AAudio (Android native audio API)
    /// </summary>
    /// <remarks>
    /// AAudio is a native Android audio API available since Android 8.0 (API 26).
    /// The API is provided by the system library "libaaudio.so".
    /// No external dependencies (like Oboe) are required - AAudio is part of Android OS.
    /// </remarks>
    internal static class AAudioInterop
    {
        private const string LibraryName = "aaudio";

        #region AAudio Constants

        // Result codes
        public const int AAUDIO_OK = 0;
        public const int AAUDIO_ERROR_BASE = -900;
        public const int AAUDIO_ERROR_DISCONNECTED = AAUDIO_ERROR_BASE - 1;
        public const int AAUDIO_ERROR_ILLEGAL_ARGUMENT = AAUDIO_ERROR_BASE - 2;
        public const int AAUDIO_ERROR_INTERNAL = AAUDIO_ERROR_BASE - 5;
        public const int AAUDIO_ERROR_INVALID_STATE = AAUDIO_ERROR_BASE - 3;
        public const int AAUDIO_ERROR_INVALID_HANDLE = AAUDIO_ERROR_BASE - 7;
        public const int AAUDIO_ERROR_UNIMPLEMENTED = AAUDIO_ERROR_BASE - 8;
        public const int AAUDIO_ERROR_UNAVAILABLE = AAUDIO_ERROR_BASE - 9;
        public const int AAUDIO_ERROR_NO_FREE_HANDLES = AAUDIO_ERROR_BASE - 10;
        public const int AAUDIO_ERROR_NO_MEMORY = AAUDIO_ERROR_BASE - 11;
        public const int AAUDIO_ERROR_NULL = AAUDIO_ERROR_BASE - 12;
        public const int AAUDIO_ERROR_TIMEOUT = AAUDIO_ERROR_BASE - 13;
        public const int AAUDIO_ERROR_WOULD_BLOCK = AAUDIO_ERROR_BASE - 14;
        public const int AAUDIO_ERROR_INVALID_FORMAT = AAUDIO_ERROR_BASE - 15;
        public const int AAUDIO_ERROR_OUT_OF_RANGE = AAUDIO_ERROR_BASE - 16;
        public const int AAUDIO_ERROR_NO_SERVICE = AAUDIO_ERROR_BASE - 17;

        // Stream states
        public const int AAUDIO_STREAM_STATE_UNINITIALIZED = 0;
        public const int AAUDIO_STREAM_STATE_UNKNOWN = 1;
        public const int AAUDIO_STREAM_STATE_OPEN = 2;
        public const int AAUDIO_STREAM_STATE_STARTING = 3;
        public const int AAUDIO_STREAM_STATE_STARTED = 4;
        public const int AAUDIO_STREAM_STATE_PAUSING = 5;
        public const int AAUDIO_STREAM_STATE_PAUSED = 6;
        public const int AAUDIO_STREAM_STATE_FLUSHING = 7;
        public const int AAUDIO_STREAM_STATE_FLUSHED = 8;
        public const int AAUDIO_STREAM_STATE_STOPPING = 9;
        public const int AAUDIO_STREAM_STATE_STOPPED = 10;
        public const int AAUDIO_STREAM_STATE_CLOSING = 11;
        public const int AAUDIO_STREAM_STATE_CLOSED = 12;
        public const int AAUDIO_STREAM_STATE_DISCONNECTED = 13;

        // Direction
        public const int AAUDIO_DIRECTION_OUTPUT = 0;
        public const int AAUDIO_DIRECTION_INPUT = 1;

        // Format
        public const int AAUDIO_FORMAT_INVALID = -1;
        public const int AAUDIO_FORMAT_UNSPECIFIED = 0;
        public const int AAUDIO_FORMAT_PCM_I16 = 1;
        public const int AAUDIO_FORMAT_PCM_FLOAT = 2;

        // Sharing mode
        public const int AAUDIO_SHARING_MODE_EXCLUSIVE = 0;
        public const int AAUDIO_SHARING_MODE_SHARED = 1;

        // Performance mode
        public const int AAUDIO_PERFORMANCE_MODE_NONE = 10;
        public const int AAUDIO_PERFORMANCE_MODE_POWER_SAVING = 11;
        public const int AAUDIO_PERFORMANCE_MODE_LOW_LATENCY = 12;

        // Usage
        public const int AAUDIO_USAGE_MEDIA = 1;
        public const int AAUDIO_USAGE_VOICE_COMMUNICATION = 2;
        public const int AAUDIO_USAGE_VOICE_COMMUNICATION_SIGNALLING = 3;
        public const int AAUDIO_USAGE_ALARM = 4;
        public const int AAUDIO_USAGE_NOTIFICATION = 5;
        public const int AAUDIO_USAGE_NOTIFICATION_RINGTONE = 6;
        public const int AAUDIO_USAGE_NOTIFICATION_EVENT = 10;
        public const int AAUDIO_USAGE_ASSISTANCE_ACCESSIBILITY = 11;
        public const int AAUDIO_USAGE_ASSISTANCE_NAVIGATION_GUIDANCE = 12;
        public const int AAUDIO_USAGE_ASSISTANCE_SONIFICATION = 13;
        public const int AAUDIO_USAGE_GAME = 14;
        public const int AAUDIO_USAGE_ASSISTANT = 16;

        // Content type
        public const int AAUDIO_CONTENT_TYPE_SPEECH = 1;
        public const int AAUDIO_CONTENT_TYPE_MUSIC = 2;
        public const int AAUDIO_CONTENT_TYPE_MOVIE = 3;
        public const int AAUDIO_CONTENT_TYPE_SONIFICATION = 4;

        // Unspecified value
        public const int AAUDIO_UNSPECIFIED = 0;

        #endregion

        #region Delegates for Callbacks

        /// <summary>
        /// Callback function for audio data processing.
        /// </summary>
        /// <param name="stream">AAudio stream handle</param>
        /// <param name="userData">User-provided data pointer</param>
        /// <param name="audioData">Audio buffer pointer (void*)</param>
        /// <param name="numFrames">Number of frames to process (int32_t)</param>
        /// <returns>AAUDIO_CALLBACK_RESULT_CONTINUE (0) or AAUDIO_CALLBACK_RESULT_STOP (1)</returns>
        /// <remarks>
        /// This callback is called from a real-time audio thread with highest priority.
        /// MUST be lock-free and allocation-free to avoid audio glitches.
        /// Use CallingConvention.Cdecl for native C function compatibility.
        /// </remarks>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = false)]
        public delegate int AAudioStream_dataCallback(
            IntPtr stream,
            IntPtr userData,
            IntPtr audioData,
            int numFrames);

        /// <summary>
        /// Callback function for stream error handling.
        /// </summary>
        /// <param name="stream">AAudio stream handle</param>
        /// <param name="userData">User-provided data pointer</param>
        /// <param name="error">Error code</param>
        /// <remarks>
        /// This callback is called when an audio error occurs.
        /// It runs on a separate thread, not the real-time audio thread.
        /// </remarks>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = false)]
        public delegate void AAudioStream_errorCallback(
            IntPtr stream,
            IntPtr userData,
            int error);

        // Callback result codes
        public const int AAUDIO_CALLBACK_RESULT_CONTINUE = 0;
        public const int AAUDIO_CALLBACK_RESULT_STOP = 1;

        #endregion

        #region AAudioStreamBuilder Functions

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudio_createStreamBuilder(out IntPtr builder);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AAudioStreamBuilder_setDeviceId(IntPtr builder, int deviceId);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AAudioStreamBuilder_setSampleRate(IntPtr builder, int sampleRate);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AAudioStreamBuilder_setChannelCount(IntPtr builder, int channelCount);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AAudioStreamBuilder_setFormat(IntPtr builder, int format);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AAudioStreamBuilder_setSharingMode(IntPtr builder, int sharingMode);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AAudioStreamBuilder_setDirection(IntPtr builder, int direction);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AAudioStreamBuilder_setBufferCapacityInFrames(IntPtr builder, int numFrames);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AAudioStreamBuilder_setPerformanceMode(IntPtr builder, int mode);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AAudioStreamBuilder_setUsage(IntPtr builder, int usage);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AAudioStreamBuilder_setContentType(IntPtr builder, int contentType);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AAudioStreamBuilder_setDataCallback(
            IntPtr builder,
            AAudioStream_dataCallback callback,
            IntPtr userData);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AAudioStreamBuilder_setErrorCallback(
            IntPtr builder,
            AAudioStream_errorCallback callback,
            IntPtr userData);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStreamBuilder_openStream(IntPtr builder, out IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStreamBuilder_delete(IntPtr builder);

        #endregion

        #region AAudioStream Functions

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_close(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_requestStart(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_requestPause(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_requestFlush(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_requestStop(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_getState(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_waitForStateChange(
            IntPtr stream,
            int inputState,
            out int nextState,
            long timeoutNanoseconds);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_read(
            IntPtr stream,
            IntPtr buffer,
            int numFrames,
            long timeoutNanoseconds);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_write(
            IntPtr stream,
            IntPtr buffer,
            int numFrames,
            long timeoutNanoseconds);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_setBufferSizeInFrames(IntPtr stream, int numFrames);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_getBufferSizeInFrames(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_getFramesPerBurst(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_getBufferCapacityInFrames(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_getXRunCount(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_getSampleRate(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_getChannelCount(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_getFormat(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_getSharingMode(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_getPerformanceMode(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAudioStream_getDeviceId(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern long AAudioStream_getFramesWritten(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern long AAudioStream_getFramesRead(IntPtr stream);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern long AAudioStream_getTimestamp(
            IntPtr stream,
            int clockid,
            out long framePosition,
            out long timeNanoseconds);

        #endregion

        #region Helper Functions

        /// <summary>
        /// Converts AAudio error code to string description.
        /// </summary>
        public static string GetErrorString(int error)
        {
            return error switch
            {
                AAUDIO_OK => "OK",
                AAUDIO_ERROR_DISCONNECTED => "Stream disconnected",
                AAUDIO_ERROR_ILLEGAL_ARGUMENT => "Illegal argument",
                AAUDIO_ERROR_INTERNAL => "Internal error",
                AAUDIO_ERROR_INVALID_STATE => "Invalid state",
                AAUDIO_ERROR_INVALID_HANDLE => "Invalid handle",
                AAUDIO_ERROR_UNIMPLEMENTED => "Unimplemented",
                AAUDIO_ERROR_UNAVAILABLE => "Unavailable",
                AAUDIO_ERROR_NO_FREE_HANDLES => "No free handles",
                AAUDIO_ERROR_NO_MEMORY => "No memory",
                AAUDIO_ERROR_NULL => "Null pointer",
                AAUDIO_ERROR_TIMEOUT => "Timeout",
                AAUDIO_ERROR_WOULD_BLOCK => "Would block",
                AAUDIO_ERROR_INVALID_FORMAT => "Invalid format",
                AAUDIO_ERROR_OUT_OF_RANGE => "Out of range",
                AAUDIO_ERROR_NO_SERVICE => "No service",
                _ => $"Unknown error ({error})"
            };
        }

        /// <summary>
        /// Converts AAudio stream state to string description.
        /// </summary>
        public static string GetStreamStateString(int state)
        {
            return state switch
            {
                AAUDIO_STREAM_STATE_UNINITIALIZED => "Uninitialized",
                AAUDIO_STREAM_STATE_UNKNOWN => "Unknown",
                AAUDIO_STREAM_STATE_OPEN => "Open",
                AAUDIO_STREAM_STATE_STARTING => "Starting",
                AAUDIO_STREAM_STATE_STARTED => "Started",
                AAUDIO_STREAM_STATE_PAUSING => "Pausing",
                AAUDIO_STREAM_STATE_PAUSED => "Paused",
                AAUDIO_STREAM_STATE_FLUSHING => "Flushing",
                AAUDIO_STREAM_STATE_FLUSHED => "Flushed",
                AAUDIO_STREAM_STATE_STOPPING => "Stopping",
                AAUDIO_STREAM_STATE_STOPPED => "Stopped",
                AAUDIO_STREAM_STATE_CLOSING => "Closing",
                AAUDIO_STREAM_STATE_CLOSED => "Closed",
                AAUDIO_STREAM_STATE_DISCONNECTED => "Disconnected",
                _ => $"Unknown state ({state})"
            };
        }

        #endregion
    }
}
