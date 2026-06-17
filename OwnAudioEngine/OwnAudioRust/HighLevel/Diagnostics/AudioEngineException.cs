using System;

namespace Ownaudio.Audio.Diagnostics;

/// <summary>
/// Base exception for all errors raised by the high-level <c>Ownaudio.Audio</c> API.
/// </summary>
/// <remarks>
/// Wraps errors originating from the native audio engine.  The raw numeric
/// <see cref="NativeErrorCode"/> is preserved for diagnostic purposes but
/// should not be used for control flow — catch the specific subclasses
/// (<see cref="AudioDeviceException"/>, <see cref="AudioStreamException"/>) instead.
/// </remarks>
public class AudioEngineException : Exception
{
    #region Properties

    /// <summary>
    /// The raw integer error code returned by the native engine.
    /// Maps to <c>Ownaudio.Native.RustAudio.Interop.NativeErrorCode</c> internally.
    /// </summary>
    public int NativeErrorCode { get; }

    #endregion

    #region Construction

    /// <summary>
    /// Initializes a new <see cref="AudioEngineException"/>.
    /// </summary>
    /// <param name="nativeErrorCode">The integer error code from the native layer.</param>
    /// <param name="message">Human-readable error description.</param>
    /// <param name="inner">Optional inner exception.</param>
    public AudioEngineException(
        int nativeErrorCode,
        string message,
        Exception? inner = null)
        : base(message, inner)
    {
        NativeErrorCode = nativeErrorCode;
    }

    #endregion
}

/// <summary>
/// Thrown when an audio device enumeration or open operation fails.
/// </summary>
public sealed class AudioDeviceException : AudioEngineException
{
    /// <summary>
    /// Initializes a new <see cref="AudioDeviceException"/>.
    /// </summary>
    /// <param name="nativeErrorCode">The integer error code from the native layer.</param>
    /// <param name="message">Human-readable error description.</param>
    /// <param name="inner">Optional inner exception.</param>
    public AudioDeviceException(
        int nativeErrorCode,
        string message,
        Exception? inner = null)
        : base(nativeErrorCode, message, inner) { }
}

/// <summary>
/// Thrown when a stream lifecycle operation (open, start, stop, configure) fails.
/// </summary>
public sealed class AudioStreamException : AudioEngineException
{
    /// <summary>
    /// Initializes a new <see cref="AudioStreamException"/>.
    /// </summary>
    /// <param name="nativeErrorCode">The integer error code from the native layer.</param>
    /// <param name="message">Human-readable error description.</param>
    /// <param name="inner">Optional inner exception.</param>
    public AudioStreamException(
        int nativeErrorCode,
        string message,
        Exception? inner = null)
        : base(nativeErrorCode, message, inner) { }
}
