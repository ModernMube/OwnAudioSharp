namespace Ownaudio.Audio;

/// <summary>
/// Represents the lifecycle state of an <see cref="AudioEngine"/> instance.
/// </summary>
public enum AudioEngineState
{
    /// <summary>The engine has been created but <c>Create</c> has not yet completed.</summary>
    Uninitialized,

    /// <summary>The engine is active and ready to create players and recorders.</summary>
    Running,

    /// <summary>The engine has been disposed and can no longer be used.</summary>
    Stopped,

    /// <summary>
    /// The engine has encountered an unrecoverable error.
    /// Check the <see cref="AudioEngine.Faulted"/> event for details.
    /// </summary>
    Faulted,
}
