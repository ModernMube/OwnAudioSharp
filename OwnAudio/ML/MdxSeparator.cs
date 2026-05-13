using OwnAudio.ML.Interop;

namespace OwnAudio.ML;

/// <summary>
/// Separates vocals from instrumental audio using MDX ONNX models
/// (best.onnx, default.onnx, karaoke.onnx or any compatible MDX model).
/// Supports single-model and multi-model ensemble separation.
/// </summary>
public static class MdxSeparator
{
    /// <summary>
    /// Separates <paramref name="audioData"/> into vocal and instrumental stems
    /// using one MDX model.
    /// </summary>
    /// <param name="audioData">Stereo interleaved float samples at <paramref name="sampleRate"/> Hz.</param>
    /// <param name="sampleRate">Sample rate of the input audio.</param>
    /// <param name="modelName">
    /// Logical name of the MDX model to use, e.g. <c>"best"</c>, <c>"default"</c>,
    /// <c>"karaoke"</c>.  The model must have been loaded via
    /// <see cref="ModelManager.LoadModel"/> or <see cref="ModelManager.Initialize"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<SeparationResult> SeparateAsync(
        float[] audioData,
        int sampleRate,
        string modelName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioData);
        ArgumentException.ThrowIfNullOrEmpty(modelName);

        return SeparateInternalAsync(audioData, sampleRate, modelName, cancellationToken);
    }

    /// <summary>
    /// Separates <paramref name="audioData"/> using multiple MDX models and
    /// averages their outputs (ensemble).  Higher model count generally improves
    /// quality at the cost of proportionally longer runtime.
    /// </summary>
    /// <param name="audioData">Stereo interleaved float samples at <paramref name="sampleRate"/> Hz.</param>
    /// <param name="sampleRate">Sample rate of the input audio.</param>
    /// <param name="modelNames">
    /// One or more logical model names to include in the ensemble,
    /// e.g. <c>["best", "default"]</c>.  Each must be loaded in advance.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<SeparationResult> SeparateEnsembleAsync(
        float[] audioData,
        int sampleRate,
        IEnumerable<string> modelNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioData);
        ArgumentNullException.ThrowIfNull(modelNames);

        string combined = string.Join(',', modelNames);
        if (string.IsNullOrWhiteSpace(combined))
            throw new ArgumentException("At least one model name is required.", nameof(modelNames));

        return SeparateInternalAsync(audioData, sampleRate, combined, cancellationToken);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static Task<SeparationResult> SeparateInternalAsync(
        float[] audioData,
        int sampleRate,
        string modelNamesCsv,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            unsafe
            {
                fixed (float* inputPtr = audioData)
                {
                    NativeSeparationResult nativeResult;
                    int status = NativeMl.ownaudio_ml_separate_mdx(
                        inputPtr,
                        audioData.Length,
                        sampleRate,
                        modelNamesCsv,
                        &nativeResult);

                    if (status != 0)
                        throw new OwnAudioMlException(
                            $"MDX separation failed with status {status}.");

                    try
                    {
                        var vocals       = new float[nativeResult.SampleCount];
                        var instrumental = new float[nativeResult.SampleCount];

                        new ReadOnlySpan<float>(nativeResult.Vocals,       nativeResult.SampleCount).CopyTo(vocals);
                        new ReadOnlySpan<float>(nativeResult.Instrumental, nativeResult.SampleCount).CopyTo(instrumental);

                        return new SeparationResult(vocals, instrumental);
                    }
                    finally
                    {
                        NativeMl.ownaudio_ml_free_separation_result(&nativeResult);
                    }
                }
            }
        }, cancellationToken);
    }
}
