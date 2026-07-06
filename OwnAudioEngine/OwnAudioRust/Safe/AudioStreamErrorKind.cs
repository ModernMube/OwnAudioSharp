namespace Ownaudio.Safe;

/// <summary>
/// Classification of an audio-stream error reported by the native backend and
/// polled through <see cref="AudioOutputStream.PollErrorState"/>.
/// </summary>
public enum AudioStreamErrorKind
{
    /// <summary>No error has been observed on the stream.</summary>
    None = 0,

    /// <summary>
    /// The audio device is no longer available (unplugged, disabled, or lost on
    /// sleep/wake or a sample-rate change). The stream has stopped and must be
    /// reopened to resume audio.
    /// </summary>
    DeviceNotAvailable = 1,

    /// <summary>A backend-specific error that is not a plain device removal.</summary>
    BackendSpecific = 2,
}
