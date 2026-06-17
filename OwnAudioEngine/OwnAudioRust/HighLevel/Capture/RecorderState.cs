namespace Ownaudio.Audio.Capture;

/// <summary>
/// Represents the current capture state of an <see cref="AudioRecorder"/>.
/// </summary>
public enum RecorderState
{
    /// <summary>The recorder is idle; no capture stream is open.</summary>
    Stopped,

    /// <summary>The recorder is actively capturing audio.</summary>
    Recording,

    /// <summary>Capture is paused; the stream remains open but the callback is not firing.</summary>
    Paused,
}
