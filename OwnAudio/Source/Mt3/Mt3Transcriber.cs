using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OwnaudioNET.Exceptions;
using OwnaudioNET.Features.Extensions.Mt3Interop;

namespace OwnaudioNET.Features.Extensions;

/// <summary>
/// Where the four exported MT3 files live. They all come out of the same export run — pairing a
/// vocabulary with a decoder it wasn't dumped from gives you confident nonsense.
/// </summary>
public sealed record Mt3ModelPaths(string Encoder, string DecoderInit, string DecoderStep, string Vocab)
{
    /// <summary>
    /// Picks the four files out of a folder that holds an export as the script writes it:
    /// mt3_encoder.onnx, mt3_decoder_init.onnx, mt3_decoder_step.onnx, vocab.json.
    /// </summary>
    public static Mt3ModelPaths FromDirectory(string directory)
    {
        return new Mt3ModelPaths(
            Path.Combine(directory, "mt3_encoder.onnx"),
            Path.Combine(directory, "mt3_decoder_init.onnx"),
            Path.Combine(directory, "mt3_decoder_step.onnx"),
            Path.Combine(directory, "vocab.json"));
    }
}

/// <summary>
/// MT3 transcription — the transformer that separates instruments instead of smearing them
/// together. Everything below this class is Rust: the ONNX sessions, the decode loop and the
/// token-to-note state machine all live in ownaudio_mt3_ffi.
///
/// The weights aren't in the package; point <see cref="Mt3ModelPaths"/> at your own export.
/// </summary>
public sealed unsafe class Mt3Transcriber : INoteTranscriber, IDisposable
{
    private readonly object _lock = new object();
    private IntPtr _handle;
    private int _sampleRate;

    /// <summary>
    /// Loads an export. Throws if any of the four files is missing or the graphs don't match.
    /// threads is the ONNX Runtime intra-op count; 0 lets the runtime decide. skipDrums drops
    /// percussion natively, which is what chord analysis wants.
    /// </summary>
    public Mt3Transcriber(Mt3ModelPaths paths, int threads = 0, bool skipDrums = true)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var options = new NativeMt3Options { Threads = threads, SkipDrums = skipDrums ? 1 : 0 };
        int code = Mt3NativeMethods.ownaudio_mt3_v1_create(
            paths.Encoder, paths.DecoderInit, paths.DecoderStep, paths.Vocab,
            in options, out _handle);

        if (code != (int)Mt3ErrorCode.Success || _handle == IntPtr.Zero)
            throw new AudioException($"MT3 model could not be loaded ({(Mt3ErrorCode)code}): {Mt3NativeMethods.LastError()}");

        _sampleRate = (int)Mt3NativeMethods.ownaudio_mt3_v1_sample_rate(_handle);
    }

    /// <summary>
    /// What the loaded checkpoint was trained at, usually 16000.
    /// </summary>
    public int PreferredSampleRate => _sampleRate;

    /// <summary>
    /// Runs the whole track through the model. This is minutes of CPU work on a full song, so
    /// keep it off the UI thread; progress ticks once per two-second segment.
    /// </summary>
    public IReadOnlyList<Note> Transcribe(float[] samples, int sampleRate, Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        _throwIfDisposed();

        lock (_lock)
        {
            GCHandle callbackHandle = default;
            IntPtr callbackPtr = IntPtr.Zero;

            if (progress != null)
            {
                callbackHandle = GCHandle.Alloc(progress);
                callbackPtr = (IntPtr)(delegate* unmanaged[Cdecl]<double, IntPtr, void>)&_onProgress;
            }

            try
            {
                int code;
                IntPtr notes;
                nuint count;

                fixed (float* p = samples)
                {
                    code = Mt3NativeMethods.ownaudio_mt3_v1_transcribe(
                        _handle, p, (nuint)samples.Length, (uint)sampleRate, 1,
                        callbackPtr,
                        callbackHandle.IsAllocated ? GCHandle.ToIntPtr(callbackHandle) : IntPtr.Zero,
                        out notes, out count);
                }

                if (code != (int)Mt3ErrorCode.Success)
                    throw new AudioException($"MT3 transcription failed ({(Mt3ErrorCode)code}): {Mt3NativeMethods.LastError()}");

                return _copyOut(notes, count);
            }
            finally
            {
                if (callbackHandle.IsAllocated) callbackHandle.Free();
            }
        }
    }

    /// <summary>
    /// Releases the native model. Safe to call twice.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;

            Mt3NativeMethods.ownaudio_mt3_v1_destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private static List<Note> _copyOut(IntPtr notes, nuint count)
    {
        var result = new List<Note>((int)count);
        if (notes == IntPtr.Zero || count == 0) return result;

        try
        {
            var span = new ReadOnlySpan<NativeMt3Note>((void*)notes, (int)count);
            foreach (ref readonly var n in span)
            {
                result.Add(new Note(
                    n.StartTime,
                    n.EndTime,
                    n.Pitch,
                    n.Velocity / 127f,
                    null,
                    n.Program,
                    n.IsDrum != 0));
            }
        }
        finally
        {
            Mt3NativeMethods.ownaudio_mt3_v1_free_notes(notes, count);
        }

        return result;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void _onProgress(double progress, IntPtr userData)
    {
        if (userData == IntPtr.Zero) return;

        //A throwing callback would unwind into Rust, so it stops here.
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is Action<double> handler) handler(progress);
        }
        catch { }
    }

    private void _throwIfDisposed()
    {
        if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(Mt3Transcriber));
    }
}
