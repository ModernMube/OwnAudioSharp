using System.Runtime.InteropServices;

namespace OwnAudio.ML.Interop;

/// <summary>
/// Internal P/Invoke bindings for the ownaudio_ml native library.
/// The native library wraps ONNX Runtime via its C API, keeping the managed
/// layer fully AOT-compatible.
/// </summary>
internal static unsafe partial class NativeMl
{
    private const string LibName = "ownaudio_ml";

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>Initialise the native ML runtime and load models from <paramref name="modelDirectory"/>.</summary>
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ownaudio_ml_init(string modelDirectory);

    /// <summary>Shut down the native ML runtime and release all resources.</summary>
    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void ownaudio_ml_shutdown();

    // ── Vocal Separation (HTDemucs) ───────────────────────────────────────────

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ownaudio_ml_separate_vocals(
        float* input,
        int sampleCount,
        int sampleRate,
        NativeSeparationResult* result);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void ownaudio_ml_free_separation_result(NativeSeparationResult* result);

    // ── Chord Detection ───────────────────────────────────────────────────────

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ownaudio_ml_detect_chords(
        float* input,
        int sampleCount,
        int sampleRate,
        NativeChordResult* results,
        int maxResults,
        int* resultCount);

    // ── Audio Analysis (EQ Matching) ──────────────────────────────────────────

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ownaudio_ml_analyze_spectrum(
        float* input,
        int sampleCount,
        int sampleRate,
        NativeAudioSpectrum* result);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ownaudio_ml_calculate_eq_adjustments(
        NativeAudioSpectrum* source,
        NativeAudioSpectrum* target,
        float* eqAdjustmentsOut,
        int bandCount);

    // ── MDX Separation ────────────────────────────────────────────────────────

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ownaudio_ml_separate_mdx(
        float* input,
        int sampleCount,
        int sampleRate,
        string modelNames,
        NativeSeparationResult* result);

    // ── Model Management ──────────────────────────────────────────────────────

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ownaudio_ml_load_model(string modelName, string path);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int ownaudio_ml_is_model_loaded(string modelName);
}
