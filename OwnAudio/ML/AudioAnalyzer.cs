using OwnAudio.ML.Interop;

namespace OwnAudio.ML;

/// <summary>Spectrum analysis result containing 30-band EQ data and loudness metrics.</summary>
public sealed class AudioSpectrum
{
    /// <summary>30-band frequency energy values (logarithmically spaced).</summary>
    public float[] FrequencyBands { get; }

    /// <summary>RMS level in dBFS.</summary>
    public float RmsLevel { get; }

    /// <summary>Peak level in dBFS.</summary>
    public float PeakLevel { get; }

    /// <summary>Integrated loudness in LUFS.</summary>
    public float Loudness { get; }

    /// <summary>Dynamic range in dB.</summary>
    public float DynamicRange { get; }

    internal unsafe AudioSpectrum(NativeAudioSpectrum* native)
    {
        FrequencyBands = new float[30];
        for (int i = 0; i < 30; i++)
            FrequencyBands[i] = native->FrequencyBands[i];

        RmsLevel = native->RmsLevel;
        PeakLevel = native->PeakLevel;
        Loudness = native->Loudness;
        DynamicRange = native->DynamicRange;
    }
}

/// <summary>
/// Analyses audio spectrum and computes EQ adjustments to match a target spectrum.
/// Used for the audio matchering / mastering feature.
/// </summary>
public static class AudioAnalyzer
{
    private const int BandCount = 30;

    /// <summary>Analyses the spectrum of <paramref name="audioData"/>.</summary>
    public static Task<AudioSpectrum> AnalyzeAsync(
        float[] audioData,
        int sampleRate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioData);

        return Task.Run(() =>
        {
            unsafe
            {
                NativeAudioSpectrum nativeResult;

                fixed (float* inputPtr = audioData)
                {
                    int status = NativeMl.ownaudio_ml_analyze_spectrum(
                        inputPtr,
                        audioData.Length,
                        sampleRate,
                        &nativeResult);

                    if (status != 0)
                        throw new OwnAudioMlException($"Spectrum analysis failed with status {status}.");
                }

                return new AudioSpectrum(&nativeResult);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Calculates per-band EQ adjustments to make <paramref name="source"/> match
    /// <paramref name="target"/>.
    /// </summary>
    /// <returns>Array of <see cref="BandCount"/> gain adjustments in dB.</returns>
    public static Task<float[]> CalculateEqAdjustmentsAsync(
        AudioSpectrum source,
        AudioSpectrum target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        return Task.Run(() =>
        {
            unsafe
            {
                float[] eqAdjustments = new float[BandCount];

                fixed (float* srcBands = source.FrequencyBands)
                fixed (float* tgtBands = target.FrequencyBands)
                fixed (float* outPtr = eqAdjustments)
                {
                    // Build native structs from managed spectrum objects
                    NativeAudioSpectrum nativeSrc = default;
                    NativeAudioSpectrum nativeTgt = default;

                    for (int i = 0; i < BandCount; i++)
                    {
                        nativeSrc.FrequencyBands[i] = source.FrequencyBands[i];
                        nativeTgt.FrequencyBands[i] = target.FrequencyBands[i];
                    }
                    nativeSrc.RmsLevel    = source.RmsLevel;
                    nativeSrc.PeakLevel   = source.PeakLevel;
                    nativeSrc.Loudness    = source.Loudness;
                    nativeSrc.DynamicRange = source.DynamicRange;
                    nativeTgt.RmsLevel    = target.RmsLevel;
                    nativeTgt.PeakLevel   = target.PeakLevel;
                    nativeTgt.Loudness    = target.Loudness;
                    nativeTgt.DynamicRange = target.DynamicRange;

                    int status = NativeMl.ownaudio_ml_calculate_eq_adjustments(
                        &nativeSrc, &nativeTgt, outPtr, BandCount);

                    if (status != 0)
                        throw new OwnAudioMlException($"EQ adjustment calculation failed with status {status}.");
                }

                return eqAdjustments;
            }
        }, cancellationToken);
    }
}
