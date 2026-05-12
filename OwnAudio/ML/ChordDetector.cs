using OwnAudio.ML.Interop;
using System.Runtime.InteropServices;

namespace OwnAudio.ML;

/// <summary>A single detected chord with timing information.</summary>
public sealed class DetectedChord
{
    /// <summary>Start time in seconds.</summary>
    public float StartTime { get; }

    /// <summary>End time in seconds.</summary>
    public float EndTime { get; }

    /// <summary>Chord name, e.g. "Cmaj", "Am", "G7".</summary>
    public string Name { get; }

    /// <summary>Detection confidence in [0, 1].</summary>
    public float Confidence { get; }

    internal DetectedChord(float startTime, float endTime, string name, float confidence)
    {
        StartTime = startTime;
        EndTime = endTime;
        Name = name;
        Confidence = confidence;
    }
}

/// <summary>
/// Detects chords in audio data using the ownaudio_ml native library.
/// </summary>
public static class ChordDetector
{
    private const int MaxResults = 4096;

    /// <summary>
    /// Detects chords in <paramref name="audioData"/>.
    /// </summary>
    /// <param name="audioData">Mono or stereo float samples at <paramref name="sampleRate"/> Hz.</param>
    /// <param name="sampleRate">Sample rate of the input audio.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<IReadOnlyList<DetectedChord>> DetectAsync(
        float[] audioData,
        int sampleRate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioData);

        return Task.Run(() =>
        {
            unsafe
            {
                var nativeResults = stackalloc NativeChordResult[MaxResults];
                int resultCount = 0;

                fixed (float* inputPtr = audioData)
                {
                    int status = NativeMl.ownaudio_ml_detect_chords(
                        inputPtr,
                        audioData.Length,
                        sampleRate,
                        nativeResults,
                        MaxResults,
                        &resultCount);

                    if (status != 0)
                        throw new OwnAudioMlException($"Chord detection failed with status {status}.");
                }

                var chords = new DetectedChord[resultCount];
                for (int i = 0; i < resultCount; i++)
                {
                    NativeChordResult* p = &nativeResults[i];
                    string name = Marshal.PtrToStringAnsi((nint)p->ChordName) ?? string.Empty;
                    chords[i] = new DetectedChord(p->StartTime, p->EndTime, name, p->Confidence);
                }

                return (IReadOnlyList<DetectedChord>)chords;
            }
        }, cancellationToken);
    }
}
