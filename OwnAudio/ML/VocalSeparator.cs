using OwnAudio.ML.Interop;

namespace OwnAudio.ML;

/// <summary>Result of a vocal separation operation.</summary>
public sealed class SeparationResult
{
    /// <summary>Isolated vocal track samples (stereo interleaved).</summary>
    public float[] Vocals { get; }

    /// <summary>Instrumental (minus vocals) samples (stereo interleaved).</summary>
    public float[] Instrumental { get; }

    internal SeparationResult(float[] vocals, float[] instrumental)
    {
        Vocals = vocals;
        Instrumental = instrumental;
    }
}

/// <summary>
/// Separates vocals from instrumental audio using the ownaudio_ml native library
/// (HTDemucs model via ONNX Runtime C API).
/// </summary>
public static class VocalSeparator
{
    /// <summary>
    /// Separates <paramref name="audioData"/> into vocal and instrumental stems.
    /// </summary>
    /// <param name="audioData">Stereo interleaved float samples at <paramref name="sampleRate"/> Hz.</param>
    /// <param name="sampleRate">Sample rate of the input audio.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<SeparationResult> SeparateAsync(
        float[] audioData,
        int sampleRate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioData);

        return Task.Run(() =>
        {
            unsafe
            {
                fixed (float* inputPtr = audioData)
                {
                    NativeSeparationResult nativeResult;
                    int status = NativeMl.ownaudio_ml_separate_vocals(
                        inputPtr,
                        audioData.Length,
                        sampleRate,
                        &nativeResult);

                    if (status != 0)
                        throw new OwnAudioMlException($"Vocal separation failed with status {status}.");

                    try
                    {
                        var vocals = new float[nativeResult.SampleCount];
                        var instrumental = new float[nativeResult.SampleCount];

                        new ReadOnlySpan<float>(nativeResult.Vocals, nativeResult.SampleCount)
                            .CopyTo(vocals);
                        new ReadOnlySpan<float>(nativeResult.Instrumental, nativeResult.SampleCount)
                            .CopyTo(instrumental);

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
