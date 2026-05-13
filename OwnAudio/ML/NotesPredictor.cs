using OwnAudio.ML.Interop;

namespace OwnAudio.ML;

/// <summary>Managed result of a BasicPitch note-prediction pass.</summary>
public sealed class NotesPredictionResult
{
    /// <summary>Contour activations: [FrameCount × FreqBins] row-major.</summary>
    public float[] Contours { get; }

    /// <summary>Note activations: [FrameCount × 88] row-major.</summary>
    public float[] Notes { get; }

    /// <summary>Onset activations: [FrameCount × 88] row-major.</summary>
    public float[] Onsets { get; }

    /// <summary>Number of output frames.</summary>
    public int FrameCount { get; }

    /// <summary>Number of contour frequency bins (typically 264 = 88 × 3).</summary>
    public int FreqBins { get; }

    internal NotesPredictionResult(float[] contours, float[] notes, float[] onsets, int frameCount, int freqBins)
    {
        Contours   = contours;
        Notes      = notes;
        Onsets     = onsets;
        FrameCount = frameCount;
        FreqBins   = freqBins;
    }
}

/// <summary>
/// Predicts musical notes from audio using the BasicPitch model
/// (ownaudio_ml native library, ONNX Runtime C API).
/// </summary>
public static class NotesPredictor
{
    /// <summary>
    /// Runs the BasicPitch model on <paramref name="audioData"/> and returns
    /// contour, note, and onset activation tensors.
    /// </summary>
    /// <param name="audioData">Mono float samples at <paramref name="sampleRate"/> Hz.</param>
    /// <param name="sampleRate">Sample rate (the native library resamples to 22050 Hz internally).</param>
    public static NotesPredictionResult Predict(float[] audioData, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(audioData);

        unsafe
        {
            fixed (float* inputPtr = audioData)
            {
                NativeNotesPredictionResult native;
                int status = NativeMl.ownaudio_ml_predict_notes(
                    inputPtr, audioData.Length, sampleRate, &native);

                if (status != 0)
                    throw new OwnAudioMlException($"BasicPitch prediction failed with status {status}.");

                try
                {
                    int fc = native.FrameCount;
                    int fb = native.FreqBins;

                    var contours = new float[fc * fb];
                    var notes    = new float[fc * 88];
                    var onsets   = new float[fc * 88];

                    new ReadOnlySpan<float>(native.Contours, fc * fb).CopyTo(contours);
                    new ReadOnlySpan<float>(native.Notes,    fc * 88).CopyTo(notes);
                    new ReadOnlySpan<float>(native.Onsets,   fc * 88).CopyTo(onsets);

                    return new NotesPredictionResult(contours, notes, onsets, fc, fb);
                }
                finally
                {
                    NativeMl.ownaudio_ml_free_notes_result(&native);
                }
            }
        }
    }

    /// <summary>
    /// Loads the BasicPitch ONNX model from <paramref name="path"/>.
    /// Call this before <see cref="Predict"/> if the model was not loaded via
    /// <see cref="ModelManager.Initialize"/>.
    /// </summary>
    public static void LoadModel(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        int status = NativeMl.ownaudio_ml_load_basic_pitch_model(path);
        if (status != 0)
            throw new OwnAudioMlException($"Failed to load BasicPitch model '{path}' (status {status}).");
    }
}
