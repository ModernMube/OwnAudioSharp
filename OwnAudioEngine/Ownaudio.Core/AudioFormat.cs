namespace Ownaudio.Decoders;

/// <summary>
/// Formats we can name. Only used as a hint — the native decoder sniffs the real thing.
/// </summary>
public enum AudioFormat
{
    /// <summary>
    /// Couldn't tell.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// WAV (PCM, IEEE float, ADPCM).
    /// </summary>
    Wav = 1,

    /// <summary>
    /// MP3, MPEG-1/2 Layer III.
    /// </summary>
    Mp3 = 2,

    /// <summary>
    /// FLAC.
    /// </summary>
    Flac = 3,

    /// <summary>
    /// Everything else the native decoder handles: OGG, Opus, AAC, M4A, WMA, AIFF.
    /// Name is a leftover from the FFmpeg days.
    /// </summary>
    FFmpeg = 4
}
