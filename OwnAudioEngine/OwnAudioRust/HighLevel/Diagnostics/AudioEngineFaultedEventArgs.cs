using System;

namespace Ownaudio.Audio.Diagnostics;

/// <summary>
/// Provides data for the <see cref="AudioEngine.Faulted"/> event.
/// </summary>
/// <remarks>
/// This event fires when the native audio engine reports an asynchronous, unrecoverable error
/// (for example, the audio device was disconnected during playback).  The engine transitions
/// to <see cref="AudioEngineState.Faulted"/> and all child players and recorders become
/// inoperable.  Dispose the engine and create a new instance to recover.
/// </remarks>
public sealed class AudioEngineFaultedEventArgs : EventArgs
{
    #region Properties

    /// <summary>The exception that caused the engine to fault.</summary>
    public Exception Cause { get; }

    #endregion

    #region Construction

    /// <summary>
    /// Initializes a new <see cref="AudioEngineFaultedEventArgs"/>.
    /// </summary>
    /// <param name="cause">The exception that caused the fault.</param>
    public AudioEngineFaultedEventArgs(Exception cause)
    {
        Cause = cause;
    }

    #endregion
}
