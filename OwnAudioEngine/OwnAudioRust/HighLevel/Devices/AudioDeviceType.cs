namespace Ownaudio.Audio.Devices;

/// <summary>
/// Indicates whether an audio device is used for output (playback) or input (capture).
/// </summary>
public enum AudioDeviceType
{
    /// <summary>The device produces audio (speakers, headphones, virtual output).</summary>
    Playback,

    /// <summary>The device captures audio (microphone, line-in, virtual input).</summary>
    Capture,
}
