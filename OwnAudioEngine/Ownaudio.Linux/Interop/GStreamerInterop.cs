using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Linux.Interop;

/// <summary>
/// GStreamer native library P/Invoke bindings for Linux MP3 decoding.
/// Minimal binding set focused on audio decoding pipeline.
/// </summary>
/// <remarks>
/// Required packages on Linux:
/// - libgstreamer1.0-0
/// - gstreamer1.0-plugins-base
/// - gstreamer1.0-plugins-good (for MP3 support)
/// - gstreamer1.0-plugins-ugly (for MP3 support)
///
/// Install on Ubuntu/Debian:
/// sudo apt-get install libgstreamer1.0-dev gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-plugins-ugly
/// </remarks>
internal static class GStreamerInterop
{
    private const string GStreamerLib = "libgstreamer-1.0.so.0";
    private const string GstAppLib = "libgstapp-1.0.so.0";
    private const string GLib = "libglib-2.0.so.0";

    #region GStreamer Core Functions

    /// <summary>
    /// Initialize GStreamer library.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void gst_init(IntPtr argc, IntPtr argv);

    /// <summary>
    /// Check if GStreamer is initialized.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool gst_is_initialized();

    /// <summary>
    /// Deinitialize GStreamer library.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void gst_deinit();

    /// <summary>
    /// Parse a pipeline description into a pipeline element.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr gst_parse_launch(
        [MarshalAs(UnmanagedType.LPStr)] string pipeline_description,
        out IntPtr error);

    /// <summary>
    /// Get an element from a bin by name.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr gst_bin_get_by_name(
        IntPtr bin,
        [MarshalAs(UnmanagedType.LPStr)] string name);

    /// <summary>
    /// Set the state of an element.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern GstStateChangeReturn gst_element_set_state(
        IntPtr element,
        GstState state);

    /// <summary>
    /// Get the state of an element.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern GstStateChangeReturn gst_element_get_state(
        IntPtr element,
        out GstState state,
        out GstState pending,
        ulong timeout);

    /// <summary>
    /// Query an element for stream information.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool gst_element_query_duration(
        IntPtr element,
        GstFormat format,
        out long duration);

    /// <summary>
    /// Query an element for current position.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool gst_element_query_position(
        IntPtr element,
        GstFormat format,
        out long position);

    /// <summary>
    /// Seek to a specific position in the stream.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool gst_element_seek_simple(
        IntPtr element,
        GstFormat format,
        GstSeekFlags seek_flags,
        long seek_pos);

    /// <summary>
    /// Get the bus from a pipeline.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr gst_element_get_bus(IntPtr element);

    /// <summary>
    /// Pop a message from the bus.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr gst_bus_pop_filtered(
        IntPtr bus,
        GstMessageType types);

    /// <summary>
    /// Get the type of a message.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern GstMessageType gst_message_type(IntPtr message);

    #endregion

    #region GstApp Functions (for pulling samples)

    /// <summary>
    /// Pull a sample from appsink.
    /// </summary>
    [DllImport(GstAppLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr gst_app_sink_pull_sample(IntPtr appsink);

    /// <summary>
    /// Try to pull a sample from appsink with timeout.
    /// </summary>
    [DllImport(GstAppLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr gst_app_sink_try_pull_sample(
        IntPtr appsink,
        ulong timeout);

    /// <summary>
    /// Check if appsink has reached EOS.
    /// </summary>
    [DllImport(GstAppLib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool gst_app_sink_is_eos(IntPtr appsink);

    /// <summary>
    /// Get the buffer from a sample.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr gst_sample_get_buffer(IntPtr sample);

    /// <summary>
    /// Get the caps from a sample.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr gst_sample_get_caps(IntPtr sample);

    /// <summary>
    /// Get size of a buffer.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint gst_buffer_get_size(IntPtr buffer);

    /// <summary>
    /// Extract data from buffer into memory.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint gst_buffer_extract(
        IntPtr buffer,
        nuint offset,
        IntPtr dest,
        nuint size);

    /// <summary>
    /// Get the PTS (presentation timestamp) from buffer.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong gst_buffer_get_pts(IntPtr buffer);

    #endregion

    #region GstCaps Functions (for format negotiation)

    /// <summary>
    /// Get structure from caps at index.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr gst_caps_get_structure(IntPtr caps, uint index);

    /// <summary>
    /// Get integer value from structure.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool gst_structure_get_int(
        IntPtr structure,
        [MarshalAs(UnmanagedType.LPStr)] string fieldname,
        out int value);

    /// <summary>
    /// Get name of structure.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr gst_structure_get_name(IntPtr structure);

    #endregion

    #region GObject/GLib Functions (for reference counting)

    /// <summary>
    /// Increase reference count.
    /// </summary>
    [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr g_object_ref(IntPtr obj);

    /// <summary>
    /// Decrease reference count.
    /// </summary>
    [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void g_object_unref(IntPtr obj);

    /// <summary>
    /// Free GStreamer sample.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void gst_sample_unref(IntPtr sample);

    /// <summary>
    /// Free GError structure.
    /// </summary>
    [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void g_error_free(IntPtr error);

    /// <summary>
    /// Free message.
    /// </summary>
    [DllImport(GStreamerLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void gst_message_unref(IntPtr message);

    #endregion

    #region Enums and Constants

    /// <summary>
    /// GStreamer element state.
    /// </summary>
    public enum GstState
    {
        VoidPending = 0,
        Null = 1,
        Ready = 2,
        Paused = 3,
        Playing = 4
    }

    /// <summary>
    /// Return value for state change operations.
    /// </summary>
    public enum GstStateChangeReturn
    {
        Failure = 0,
        Success = 1,
        Async = 2,
        NoPreroll = 3
    }

    /// <summary>
    /// Format for queries and seeks.
    /// </summary>
    public enum GstFormat
    {
        Undefined = 0,
        Default = 1,
        Bytes = 2,
        Time = 3,      // Time in nanoseconds
        Buffers = 4,
        Percent = 5
    }

    /// <summary>
    /// Seek flags.
    /// </summary>
    [Flags]
    public enum GstSeekFlags
    {
        None = 0,
        Flush = 1 << 0,
        Accurate = 1 << 1,
        KeyUnit = 1 << 2,
        Segment = 1 << 3,
        Skip = 1 << 4
    }

    /// <summary>
    /// Message types.
    /// </summary>
    [Flags]
    public enum GstMessageType : uint
    {
        Unknown = 0,
        Eos = 1 << 0,
        Error = 1 << 1,
        Warning = 1 << 2,
        Info = 1 << 3,
        Tag = 1 << 4,
        Buffering = 1 << 5,
        StateChanged = 1 << 6,
        Any = 0xFFFFFFFF
    }

    /// <summary>
    /// GError structure for error handling.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GError
    {
        public uint domain;
        public int code;
        public IntPtr message; // char*
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Convert GStreamer time (nanoseconds) to milliseconds.
    /// </summary>
    public static double GstTimeToMs(long gstTime)
    {
        return gstTime / 1_000_000.0;
    }

    /// <summary>
    /// Convert milliseconds to GStreamer time (nanoseconds).
    /// </summary>
    public static long MsToGstTime(double ms)
    {
        return (long)(ms * 1_000_000.0);
    }

    /// <summary>
    /// Constant representing invalid/unknown time.
    /// </summary>
    public const ulong GST_CLOCK_TIME_NONE = ulong.MaxValue;

    #endregion
}
