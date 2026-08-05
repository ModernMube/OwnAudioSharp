namespace OwnaudioNET.Features.Extensions;

/// <summary>
/// Turns raw audio into notes. Two implementations live here: the embedded BasicPitch net and
/// the external MT3 model — see <see cref="BasicPitchTranscriber"/> and <c>Mt3Transcriber</c>.
/// </summary>
public interface INoteTranscriber
{
    /// <summary>
    /// Rate the samples handed to <see cref="Transcribe"/> should be at. Decoding straight into
    /// this saves the transcriber a resampling pass.
    /// </summary>
    int PreferredSampleRate { get; }

    /// <summary>
    /// Mono float PCM in, notes out. progress gets 0..1 as it goes and may be null.
    /// </summary>
    IReadOnlyList<Note> Transcribe(float[] samples, int sampleRate, Action<double>? progress = null);
}
