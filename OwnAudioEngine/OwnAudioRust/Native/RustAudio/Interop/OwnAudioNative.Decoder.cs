using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Structs;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// P/Invoke declarations for the native streaming audio file decoder API.
/// </summary>
internal static unsafe partial class OwnAudioNative
{
    #region Decoder lifecycle

    /// <summary>
    /// Opens an audio file for streaming decoding and writes its handle to <paramref name="outDecoder"/>.
    /// </summary>
    /// <param name="path">Null-terminated UTF-8 file path.</param>
    /// <param name="targetSampleRate">Desired output sample rate in Hz; 0 keeps the source rate.</param>
    /// <param name="targetChannels">Desired output channel count; 0 keeps the source channels.</param>
    /// <param name="prefetchSeconds">Prefetch buffer length in seconds; values ≤ 0 use the 2.0 s default.</param>
    /// <param name="outDecoder">Receives the new decoder handle on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    /// <remarks>
    /// Mirrors:
    /// <c>ownaudio_v1_decoder_open(path, target_sample_rate, target_channels, prefetch_seconds, out_decoder) → i32</c>
    /// </remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ownaudio_v1_decoder_open(
        string path,
        uint targetSampleRate,
        uint targetChannels,
        float prefetchSeconds,
        out IntPtr outDecoder);

    /// <summary>
    /// Destroys a decoder handle, stopping and joining its prefetch thread.
    /// </summary>
    /// <param name="decoder">Decoder handle to destroy; passing <see cref="IntPtr.Zero"/> is safe.</param>
    /// <remarks>Mirrors: <c>ownaudio_v1_decoder_destroy(OwnAudioDecoderHandle*) → void</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_decoder_destroy(IntPtr decoder);

    #endregion

    #region Decoding

    /// <summary>
    /// Reads up to <paramref name="bufferCount"/> decoded interleaved <c>f32</c> samples into
    /// <paramref name="buffer"/> and writes the number actually produced to <paramref name="outSamplesWritten"/>.
    /// </summary>
    /// <param name="decoder">Valid decoder handle.</param>
    /// <param name="buffer">Reference to the first element of the destination buffer.</param>
    /// <param name="bufferCount">Number of <c>f32</c> elements available in the buffer.</param>
    /// <param name="outSamplesWritten">Receives the number of samples written.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    /// <remarks>
    /// Real-time safe on the native side. Mirrors:
    /// <c>ownaudio_v1_decoder_read(decoder, buffer, buffer_count, out_samples_written) → i32</c>
    /// </remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_decoder_read(
        IntPtr decoder,
        ref float buffer,
        nuint bufferCount,
        out nuint outSamplesWritten);

    /// <summary>
    /// Requests a non-blocking seek to <paramref name="framePosition"/> (output sample frames).
    /// </summary>
    /// <param name="decoder">Valid decoder handle.</param>
    /// <param name="framePosition">Target output frame position.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    /// <remarks>Mirrors: <c>ownaudio_v1_decoder_seek(decoder, frame_position) → i32</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_decoder_seek(IntPtr decoder, ulong framePosition);

    #endregion

    #region Queries

    /// <summary>
    /// Writes the decoded output stream metadata to <paramref name="outInfo"/>.
    /// </summary>
    /// <param name="decoder">Valid decoder handle.</param>
    /// <param name="outInfo">Receives the stream metadata.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    /// <remarks>Mirrors: <c>ownaudio_v1_decoder_get_stream_info(decoder, out_info) → i32</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_decoder_get_stream_info(
        IntPtr decoder,
        out NativeAudioStreamInfo outInfo);

    /// <summary>
    /// Writes <see langword="true"/> to <paramref name="outIsEof"/> when the file has been fully
    /// decoded and the prefetch buffer is drained.
    /// </summary>
    /// <param name="decoder">Valid decoder handle.</param>
    /// <param name="outIsEof">Receives the end-of-file flag.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    /// <remarks>Mirrors: <c>ownaudio_v1_decoder_is_eof(decoder, out_is_eof) → i32</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_decoder_is_eof(
        IntPtr decoder,
        [MarshalAs(UnmanagedType.U1)] out bool outIsEof);

    #endregion
}
