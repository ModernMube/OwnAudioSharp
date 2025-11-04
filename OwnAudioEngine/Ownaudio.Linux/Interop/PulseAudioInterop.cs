using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Linux.Interop
{
    /// <summary>
    /// P/Invoke declarations for PulseAudio asynchronous API.
    /// This provides low-level, zero-resampling audio I/O for Linux similar to WASAPI and Core Audio.
    /// </summary>
    internal static class PulseAudioInterop
    {
        private const string LibPulse = "libpulse.so.0";

        #region Constants

        /// <summary>Invalid index constant</summary>
        internal const uint PA_INVALID_INDEX = 0xFFFFFFFF;

        /// <summary>Success return code</summary>
        internal const int PA_OK = 0;

        // Stream flags
        internal const uint PA_STREAM_START_CORKED = 0x0001U;
        internal const uint PA_STREAM_INTERPOLATE_TIMING = 0x0002U;
        internal const uint PA_STREAM_NOT_MONOTONIC = 0x0004U;
        internal const uint PA_STREAM_AUTO_TIMING_UPDATE = 0x0008U;
        internal const uint PA_STREAM_NO_REMAP_CHANNELS = 0x0010U;
        internal const uint PA_STREAM_NO_REMIX_CHANNELS = 0x0020U;
        internal const uint PA_STREAM_FIX_FORMAT = 0x0040U;
        internal const uint PA_STREAM_FIX_RATE = 0x0080U;
        internal const uint PA_STREAM_FIX_CHANNELS = 0x0100U;
        internal const uint PA_STREAM_DONT_MOVE = 0x0200U;
        internal const uint PA_STREAM_VARIABLE_RATE = 0x0400U;
        internal const uint PA_STREAM_PEAK_DETECT = 0x0800U;
        internal const uint PA_STREAM_START_MUTED = 0x1000U;
        internal const uint PA_STREAM_ADJUST_LATENCY = 0x2000U;
        internal const uint PA_STREAM_EARLY_REQUESTS = 0x4000U;
        internal const uint PA_STREAM_DONT_INHIBIT_AUTO_SUSPEND = 0x8000U;
        internal const uint PA_STREAM_START_UNMUTED = 0x10000U;
        internal const uint PA_STREAM_FAIL_ON_SUSPEND = 0x20000U;
        internal const uint PA_STREAM_RELATIVE_VOLUME = 0x40000U;
        internal const uint PA_STREAM_PASSTHROUGH = 0x80000U;

        // Subscription masks
        internal const uint PA_SUBSCRIPTION_MASK_SINK = 0x0001U;
        internal const uint PA_SUBSCRIPTION_MASK_SOURCE = 0x0002U;
        internal const uint PA_SUBSCRIPTION_MASK_SINK_INPUT = 0x0004U;
        internal const uint PA_SUBSCRIPTION_MASK_SOURCE_OUTPUT = 0x0008U;
        internal const uint PA_SUBSCRIPTION_MASK_MODULE = 0x0010U;
        internal const uint PA_SUBSCRIPTION_MASK_CLIENT = 0x0020U;
        internal const uint PA_SUBSCRIPTION_MASK_SAMPLE_CACHE = 0x0040U;
        internal const uint PA_SUBSCRIPTION_MASK_SERVER = 0x0080U;
        internal const uint PA_SUBSCRIPTION_MASK_CARD = 0x0100U;
        internal const uint PA_SUBSCRIPTION_MASK_ALL = 0x01FFU;

        // Subscription event types
        internal const uint PA_SUBSCRIPTION_EVENT_SINK = 0x0000U;
        internal const uint PA_SUBSCRIPTION_EVENT_SOURCE = 0x0001U;
        internal const uint PA_SUBSCRIPTION_EVENT_SINK_INPUT = 0x0002U;
        internal const uint PA_SUBSCRIPTION_EVENT_SOURCE_OUTPUT = 0x0003U;
        internal const uint PA_SUBSCRIPTION_EVENT_MODULE = 0x0004U;
        internal const uint PA_SUBSCRIPTION_EVENT_CLIENT = 0x0005U;
        internal const uint PA_SUBSCRIPTION_EVENT_SAMPLE_CACHE = 0x0006U;
        internal const uint PA_SUBSCRIPTION_EVENT_SERVER = 0x0007U;
        internal const uint PA_SUBSCRIPTION_EVENT_CARD = 0x0008U;
        internal const uint PA_SUBSCRIPTION_EVENT_FACILITY_MASK = 0x000FU;

        internal const uint PA_SUBSCRIPTION_EVENT_NEW = 0x0000U;
        internal const uint PA_SUBSCRIPTION_EVENT_CHANGE = 0x0010U;
        internal const uint PA_SUBSCRIPTION_EVENT_REMOVE = 0x0020U;
        internal const uint PA_SUBSCRIPTION_EVENT_TYPE_MASK = 0x0030U;

        // Seek modes
        internal const int PA_SEEK_RELATIVE = 0;
        internal const int PA_SEEK_ABSOLUTE = 1;
        internal const int PA_SEEK_RELATIVE_ON_READ = 2;
        internal const int PA_SEEK_RELATIVE_END = 3;

        #endregion

        #region Enums

        /// <summary>
        /// Sample format enumeration.
        /// </summary>
        internal enum pa_sample_format_t
        {
            PA_SAMPLE_U8,
            PA_SAMPLE_ALAW,
            PA_SAMPLE_ULAW,
            PA_SAMPLE_S16LE,
            PA_SAMPLE_S16BE,
            PA_SAMPLE_FLOAT32LE,
            PA_SAMPLE_FLOAT32BE,
            PA_SAMPLE_S32LE,
            PA_SAMPLE_S32BE,
            PA_SAMPLE_S24LE,
            PA_SAMPLE_S24BE,
            PA_SAMPLE_S24_32LE,
            PA_SAMPLE_S24_32BE,
            PA_SAMPLE_MAX,
            PA_SAMPLE_INVALID = -1
        }

        /// <summary>
        /// Context state enumeration.
        /// </summary>
        internal enum pa_context_state_t
        {
            PA_CONTEXT_UNCONNECTED,
            PA_CONTEXT_CONNECTING,
            PA_CONTEXT_AUTHORIZING,
            PA_CONTEXT_SETTING_NAME,
            PA_CONTEXT_READY,
            PA_CONTEXT_FAILED,
            PA_CONTEXT_TERMINATED
        }

        /// <summary>
        /// Stream state enumeration.
        /// </summary>
        internal enum pa_stream_state_t
        {
            PA_STREAM_UNCONNECTED,
            PA_STREAM_CREATING,
            PA_STREAM_READY,
            PA_STREAM_FAILED,
            PA_STREAM_TERMINATED
        }

        /// <summary>
        /// Operation state enumeration.
        /// </summary>
        internal enum pa_operation_state_t
        {
            PA_OPERATION_RUNNING,
            PA_OPERATION_DONE,
            PA_OPERATION_CANCELLED
        }

        #endregion

        #region Structures

        /// <summary>
        /// Sample specification structure.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct pa_sample_spec
        {
            public pa_sample_format_t format;
            public uint rate;
            public byte channels;
        }

        /// <summary>
        /// Buffer attributes structure.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct pa_buffer_attr
        {
            public uint maxlength;
            public uint tlength;
            public uint prebuf;
            public uint minreq;
            public uint fragsize;
        }

        /// <summary>
        /// Channel map structure.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct pa_channel_map
        {
            public byte channels;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public int[] map;
        }

        /// <summary>
        /// Sink info structure.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct pa_sink_info
        {
            public IntPtr name;
            public uint index;
            public IntPtr description;
            public pa_sample_spec sample_spec;
            public pa_channel_map channel_map;
            public uint owner_module;
            public IntPtr volume;  // pa_cvolume*
            public int mute;
            public uint monitor_source;
            public IntPtr monitor_source_name;
            public ulong latency;
            public IntPtr driver;
            public uint flags;
            public IntPtr proplist;
            public ulong configured_latency;
            public uint base_volume;
            public uint state;
            public uint n_volume_steps;
            public uint card;
            public uint n_ports;
            public IntPtr ports;
            public IntPtr active_port;
            public byte n_formats;
            public IntPtr formats;
        }

        /// <summary>
        /// Source info structure.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct pa_source_info
        {
            public IntPtr name;
            public uint index;
            public IntPtr description;
            public pa_sample_spec sample_spec;
            public pa_channel_map channel_map;
            public uint owner_module;
            public IntPtr volume;  // pa_cvolume*
            public int mute;
            public uint monitor_of_sink;
            public IntPtr monitor_of_sink_name;
            public ulong latency;
            public IntPtr driver;
            public uint flags;
            public IntPtr proplist;
            public ulong configured_latency;
            public uint base_volume;
            public uint state;
            public uint n_volume_steps;
            public uint card;
            public uint n_ports;
            public IntPtr ports;
            public IntPtr active_port;
            public byte n_formats;
            public IntPtr formats;
        }

        /// <summary>
        /// Server info structure.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct pa_server_info
        {
            public IntPtr user_name;
            public IntPtr host_name;
            public IntPtr server_version;
            public IntPtr server_name;
            public pa_sample_spec sample_spec;
            public IntPtr default_sink_name;
            public IntPtr default_source_name;
            public uint cookie;
            public pa_channel_map channel_map;
        }

        #endregion

        #region Delegates (Callbacks)

        /// <summary>
        /// Context state callback.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void pa_context_notify_cb(IntPtr context, IntPtr userdata);

        /// <summary>
        /// Stream state callback.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void pa_stream_notify_cb(IntPtr stream, IntPtr userdata);

        /// <summary>
        /// Stream request callback (for read/write events).
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void pa_stream_request_cb(IntPtr stream, nuint bytes, IntPtr userdata);

        /// <summary>
        /// Callback for async operations that indicate success/failure.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void pa_stream_success_cb(IntPtr stream, int success, IntPtr userdata);

        /// <summary>
        /// Sink info callback.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void pa_sink_info_cb(IntPtr context, IntPtr info, int eol, IntPtr userdata);

        /// <summary>
        /// Source info callback.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void pa_source_info_cb(IntPtr context, IntPtr info, int eol, IntPtr userdata);

        /// <summary>
        /// Server info callback.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void pa_server_info_cb(IntPtr context, IntPtr info, IntPtr userdata);

        /// <summary>
        /// Subscription callback.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void pa_context_subscribe_cb(IntPtr context, uint eventType, uint index, IntPtr userdata);

        /// <summary>
        /// Success callback.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void pa_context_success_cb(IntPtr context, int success, IntPtr userdata);

        #endregion

        #region Threaded Mainloop API

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pa_threaded_mainloop_new();

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pa_threaded_mainloop_free(IntPtr mainloop);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pa_threaded_mainloop_start(IntPtr mainloop);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pa_threaded_mainloop_stop(IntPtr mainloop);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pa_threaded_mainloop_lock(IntPtr mainloop);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pa_threaded_mainloop_unlock(IntPtr mainloop);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pa_threaded_mainloop_wait(IntPtr mainloop);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pa_threaded_mainloop_signal(IntPtr mainloop, int wait_for_accept);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pa_threaded_mainloop_get_api(IntPtr mainloop);

        #endregion

        #region Context API

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern IntPtr pa_context_new(IntPtr mainloop_api, string name);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pa_context_unref(IntPtr context);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pa_context_ref(IntPtr context);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int pa_context_connect(IntPtr context, string server, uint flags, IntPtr spawn_api);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pa_context_disconnect(IntPtr context);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern pa_context_state_t pa_context_get_state(IntPtr context);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pa_context_set_state_callback(IntPtr context, pa_context_notify_cb cb, IntPtr userdata);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pa_context_errno(IntPtr context);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pa_context_get_sink_info_list(IntPtr context, pa_sink_info_cb cb, IntPtr userdata);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pa_context_get_source_info_list(IntPtr context, pa_source_info_cb cb, IntPtr userdata);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pa_context_get_server_info(IntPtr context, pa_server_info_cb cb, IntPtr userdata);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pa_context_subscribe(IntPtr context, uint mask, pa_context_success_cb cb, IntPtr userdata);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pa_context_set_subscribe_callback(IntPtr context, pa_context_subscribe_cb cb, IntPtr userdata);

        #endregion

        #region Stream API

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern IntPtr pa_stream_new(IntPtr context, string name, ref pa_sample_spec spec, IntPtr channel_map);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pa_stream_unref(IntPtr stream);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pa_stream_ref(IntPtr stream);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int pa_stream_connect_playback(
            IntPtr stream,
            string device,
            ref pa_buffer_attr attr,
            uint flags,
            IntPtr volume,
            IntPtr sync_stream);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int pa_stream_connect_record(
            IntPtr stream,
            string device,
            ref pa_buffer_attr attr,
            uint flags);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pa_stream_disconnect(IntPtr stream);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern pa_stream_state_t pa_stream_get_state(IntPtr stream);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pa_stream_set_state_callback(IntPtr stream, pa_stream_notify_cb cb, IntPtr userdata);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pa_stream_set_write_callback(IntPtr stream, pa_stream_request_cb cb, IntPtr userdata);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pa_stream_set_read_callback(IntPtr stream, pa_stream_request_cb cb, IntPtr userdata);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pa_stream_begin_write(IntPtr stream, out IntPtr data, ref nuint nbytes);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pa_stream_write(
            IntPtr stream,
            IntPtr data,
            nuint bytes,
            IntPtr free_cb,
            long offset,
            int seek_mode);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pa_stream_peek(IntPtr stream, out IntPtr data, out nuint nbytes);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pa_stream_drop(IntPtr stream);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint pa_stream_writable_size(IntPtr stream);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint pa_stream_readable_size(IntPtr stream);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pa_stream_cork(IntPtr stream, int b, pa_stream_success_cb cb, IntPtr userdata);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pa_stream_trigger(IntPtr stream, pa_stream_success_cb cb, IntPtr userdata);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pa_stream_get_sample_spec(IntPtr stream);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pa_stream_get_buffer_attr(IntPtr stream);

        #endregion

        #region Operation API

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pa_operation_unref(IntPtr operation);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pa_operation_ref(IntPtr operation);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pa_operation_cancel(IntPtr operation);

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern pa_operation_state_t pa_operation_get_state(IntPtr operation);

        #endregion

        #region Error Handling

        [DllImport(LibPulse, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pa_strerror(int error);

        #endregion

        #region Helper Methods

        /// <summary>
        /// Creates a standard stereo channel map.
        /// </summary>
        internal static pa_channel_map CreateStereoChannelMap()
        {
            return new pa_channel_map
            {
                channels = 2,
                map = new int[32]
            };
        }

        /// <summary>
        /// Gets error message from error code.
        /// </summary>
        internal static string GetErrorMessage(int errorCode)
        {
            IntPtr ptr = pa_strerror(errorCode);
            return ptr != IntPtr.Zero ? Marshal.PtrToStringAnsi(ptr) ?? "Unknown error" : "Unknown error";
        }

        #endregion
    }
}
