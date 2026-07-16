using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Structs;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// P/Invokes for the native streaming file decoder. 0 means ok, anything else is an error code.
/// </summary>
internal static unsafe partial class OwnAudioNative
{
    #region Decoder lifecycle

    /// <summary>
    /// Opens a file for streaming, handle comes back in outDecoder.
    /// </summary>
    /// <param name="path">utf8 path</param>
    /// <param name="targetSampleRate">0 keeps whatever the source has</param>
    /// <param name="targetChannels">0 keeps whatever the source has</param>
    /// <param name="prefetchSeconds">prefetch buffer length, 0 or less falls back to 2 sec</param>
    /// <param name="outDecoder"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ownaudio_v1_decoder_open(
        string path,
        uint targetSampleRate,
        uint targetChannels,
        float prefetchSeconds,
        out IntPtr outDecoder);

    /// <summary>
    /// Kills the decoder and joins its prefetch thread. Zero handle is fine.
    /// </summary>
    /// <param name="decoder"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_decoder_destroy(IntPtr decoder);

    #endregion

    #region Decoding

    /// <summary>
    /// Pulls decoded interleaved f32 samples into the buffer, rt safe on the native side.
    /// </summary>
    /// <param name="decoder"></param>
    /// <param name="buffer">first element of the destination</param>
    /// <param name="bufferCount">how many f32 elements fit in there</param>
    /// <param name="outSamplesWritten">what we really got</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_decoder_read(
        IntPtr decoder,
        ref float buffer,
        nuint bufferCount,
        out nuint outSamplesWritten);

    /// <summary>
    /// Asks for a seek, doesn't block. Position is in output frames.
    /// </summary>
    /// <param name="decoder"></param>
    /// <param name="framePosition"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_decoder_seek(IntPtr decoder, ulong framePosition);

    #endregion

    #region Queries

    /// <summary>
    /// Metadata of the decoded output stream.
    /// </summary>
    /// <param name="decoder"></param>
    /// <param name="outInfo"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_decoder_get_stream_info(
        IntPtr decoder,
        out NativeAudioStreamInfo outInfo);

    /// <summary>
    /// True only when the file is done and the prefetch buffer is empty too.
    /// </summary>
    /// <param name="decoder"></param>
    /// <param name="outIsEof"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_decoder_is_eof(
        IntPtr decoder,
        [MarshalAs(UnmanagedType.U1)] out bool outIsEof);

    #endregion
}
