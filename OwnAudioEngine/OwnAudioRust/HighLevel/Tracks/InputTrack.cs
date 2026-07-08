using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// An input-capture-backed <see cref="AudioTrack"/>: a native device input stream captures on its
/// own audio thread and writes straight into the track's ring buffer, read by the mixer audio
/// thread — with no managed callback and no audio data crossing into managed code.
/// </summary>
/// <remarks>
/// <para>
/// This is the live-capture counterpart of <see cref="FileTrack"/> / <see cref="MemoryTrack"/> for
/// <c>InputSource</c>. The managed side is only a controller: it starts/stops capture
/// (<see cref="Play"/>/<see cref="Pause"/>) and reads metering (<see cref="GetInputPeaks"/>); the
/// audio path is entirely native, so the GC can never stall it.
/// </para>
/// <para>
/// Capture starts paused; call <see cref="Play"/> to begin. Being fed does not by itself make the
/// track audible — drive the track's transport through the owning <see cref="MultiTrackSession"/>.
/// </para>
/// </remarks>
public sealed class InputTrack : IDisposable
{
    #region Fields

    private readonly AudioTrack _track;
    private readonly InputSourceHandle _sourceHandle;
    private readonly object _sync = new();
    private bool _disposed;

    #endregion

    #region Construction

    /// <summary>
    /// Wraps a native input source that was opened on <paramref name="track"/>.
    /// </summary>
    /// <param name="track">The track the input source feeds.</param>
    /// <param name="sourceHandle">The native input-source control handle.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="track"/> or <paramref name="sourceHandle"/> is null.
    /// </exception>
    internal InputTrack(AudioTrack track, InputSourceHandle sourceHandle)
    {
        _track = track ?? throw new ArgumentNullException(nameof(track));
        _sourceHandle = sourceHandle ?? throw new ArgumentNullException(nameof(sourceHandle));
    }

    #endregion

    #region Properties

    /// <summary>Gets the track this input source feeds.</summary>
    public AudioTrack Track => _track;

    #endregion

    #region Capture control

    /// <summary>Starts (or resumes) device capture feeding the track.</summary>
    /// <exception cref="Ownaudio.Safe.Exceptions.OwnAudioException">
    /// Thrown when capture cannot be started.
    /// </exception>
    public void Play()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            int code = OwnAudioNative.ownaudio_v1_input_source_play(_sourceHandle.DangerousGetHandle());
            ErrorCodeMapper.ThrowIfError(code, nameof(Play));
        }
    }

    /// <summary>Pauses device capture. Samples already buffered in the ring keep playing out.</summary>
    public void Pause()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            int code = OwnAudioNative.ownaudio_v1_input_source_pause(_sourceHandle.DangerousGetHandle());
            ErrorCodeMapper.ThrowIfError(code, nameof(Pause));
        }
    }

    /// <summary>
    /// Gets the most recent capture peak levels (0.0..), measured natively in the capture callback.
    /// Returns <c>(0, 0)</c> when disposed.
    /// </summary>
    /// <returns>The left and right capture peak levels.</returns>
    public (float Left, float Right) GetInputPeaks()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return (0f, 0f);
            }

            int code = OwnAudioNative.ownaudio_v1_input_source_get_peaks(
                _sourceHandle.DangerousGetHandle(),
                out float left,
                out float right);
            ErrorCodeMapper.ThrowIfError(code, nameof(GetInputPeaks));
            return (left, right);
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Stops capture and releases the native input-source control handle. The track is left intact
    /// (it is owned by the session).
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _sourceHandle.Dispose();
    }

    #endregion
}
