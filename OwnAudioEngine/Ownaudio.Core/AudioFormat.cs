namespace Ownaudio.Decoders;

/// <summary>
/// Supported audio file formats for decoding.
/// </summary>
public enum AudioFormat
{
    /// <summary>
    /// Unknown or unsupported audio format.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// WAV audio format (PCM, IEEE Float, ADPCM).
    /// </summary>
    Wav = 1,

    /// <summary>
    /// MP3 audio format (MPEG-1/2 Layer III).
    /// </summary>
    Mp3 = 2,

    /// <summary>
    /// FLAC audio format (Free Lossless Audio Codec).
    /// </summary>
    Flac = 3,

    /// <summary>
    /// Format handled exclusively by the FFmpeg decoder (OGG, Opus, AAC, M4A, WMA, AIFF, etc.).
    /// Requires FFmpeg dynamic libraries to be present at runtime.
    /// </summary>
    FFmpeg = 4
}
