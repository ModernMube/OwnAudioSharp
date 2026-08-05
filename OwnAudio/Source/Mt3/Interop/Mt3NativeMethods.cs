using System.Runtime.InteropServices;

namespace OwnaudioNET.Features.Extensions.Mt3Interop;

/// <summary>
/// Every ownaudio_mt3_ffi call lives here. Source-generated LibraryImport so it survives AOT.
/// Return value is an <see cref="Mt3ErrorCode"/> unless the call can't fail.
/// </summary>
internal static unsafe partial class Mt3NativeMethods
{
    private const string LibName = Mt3NativeLibraryLoader.LogicalName;

    static Mt3NativeMethods()
    {
        Mt3NativeLibraryLoader.EnsureRegistered();
    }

    /// <summary>
    /// Progress reporter the native side calls once per audio segment.
    /// </summary>
    internal delegate void ProgressCallback(double progress, IntPtr userData);

    /// <summary>
    /// Loads the encoder, both decoder graphs and the vocabulary. All four must come from the
    /// same export run.
    /// </summary>
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ownaudio_mt3_v1_create(
        string encoderPath,
        string decoderInitPath,
        string decoderStepPath,
        string vocabPath,
        in NativeMt3Options options,
        out IntPtr outTranscriber);

    /// <summary>
    /// Releases the transcriber and the ONNX sessions behind it.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial void ownaudio_mt3_v1_destroy(IntPtr transcriber);

    /// <summary>
    /// The rate the model runs at, so we can decode straight into it.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial uint ownaudio_mt3_v1_sample_rate(IntPtr transcriber);

    /// <summary>
    /// Runs the whole track. Minutes of work on a long song — never call it from a UI thread.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_mt3_v1_transcribe(
        IntPtr transcriber,
        float* samples,
        nuint sampleCount,
        uint sampleRate,
        ushort channels,
        IntPtr progress,
        IntPtr userData,
        out IntPtr outNotes,
        out nuint outCount);

    /// <summary>
    /// Gives back a note array from a transcribe call.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial void ownaudio_mt3_v1_free_notes(IntPtr notes, nuint count);

    /// <summary>
    /// Last error text on this thread. Pass a null buffer to just ask for the needed size.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial nuint ownaudio_mt3_v1_last_error(byte* buffer, nuint capacity);

    /// <summary>
    /// Pulls the last error out as a string, or a placeholder when there's nothing to tell.
    /// </summary>
    internal static string LastError()
    {
        nuint needed = ownaudio_mt3_v1_last_error(null, 0);
        if (needed <= 1) return "no further detail from the native transcriber";

        Span<byte> buffer = needed <= 512 ? stackalloc byte[(int)needed] : new byte[(int)needed];
        fixed (byte* p = buffer)
        {
            ownaudio_mt3_v1_last_error(p, needed);
        }

        int end = buffer.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(end < 0 ? buffer : buffer[..end]);
    }
}
