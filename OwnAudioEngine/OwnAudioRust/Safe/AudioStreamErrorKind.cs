namespace Ownaudio.Safe;

/// <summary>
/// What the backend reported on a stream, polled via AudioOutputStream.PollErrorState.
/// </summary>
public enum AudioStreamErrorKind
{
    /// Nothing happened so far.
    None = 0,

    /// <summary>
    /// Device is gone: unplugged, disabled, or lost on sleep/wake or a rate change.
    /// The stream stopped, it has to be reopened.
    /// </summary>
    DeviceNotAvailable = 1,

    /// Some backend specific failure, not a plain device removal.
    BackendSpecific = 2,
}
