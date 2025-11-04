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
    Flac = 3
}
