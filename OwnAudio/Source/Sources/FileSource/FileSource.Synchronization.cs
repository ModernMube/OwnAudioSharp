using OwnaudioNET.Synchronization;
using OwnaudioNET.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Sources;

/// <summary>
/// FileSource partial class containing MasterClock attachment and time-based reading.
/// </summary>
/// <remarks>
/// As of 4.0 (plan L) playback and its drift correction run entirely in the native engine, so the
/// managed real-time soft-sync / adaptive-tolerance machinery has been removed. The
/// <see cref="ISynchronizable"/> / <see cref="IMasterClockSource"/> surface is retained for API
/// compatibility; <see cref="ReadSamplesAtTime"/> now decodes on demand for analysis callers.
/// </remarks>
public partial class FileSource
{
    #region MasterClock Synchronization

    /// <inheritdoc/>
    public void AttachToClock(MasterClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        DetachFromClock();

        _masterClock = clock;

        double targetTrackPosition = _masterClock.CurrentTimestamp - _startOffset;

        if (targetTrackPosition > 0 && Seek(targetTrackPosition))
        {
            _trackLocalTime = targetTrackPosition;
        }
        else
        {
            Seek(0);
            _trackLocalTime = 0.0;
        }

        IsSynchronized = true;
    }

    /// <inheritdoc/>
    public void DetachFromClock()
    {
        if (_masterClock != null)
        {
            _masterClock = null;
            _trackLocalTime = 0.0;

            IsSynchronized = false;
        }
    }

    #endregion

    #region Time-Based Reading

    /// <inheritdoc/>
    /// <remarks>
    /// The managed real-time drift correction was removed with the legacy playback path (plan L); this
    /// now decodes on demand for analysis callers. Timestamps before the source's
    /// <see cref="StartOffset"/> yield silence; otherwise raw frames are decoded sequentially.
    /// </remarks>
    public bool ReadSamplesAtTime(double masterTimestamp, Span<float> buffer, int frameCount, out ReadResult result)
    {
        ThrowIfDisposed();

        double relativeTimestamp = masterTimestamp - _startOffset;

        if (relativeTimestamp < 0)
        {
            FillWithSilence(buffer, frameCount * _streamInfo.Channels);
            result = ReadResult.CreateSuccess(frameCount);
            return true;
        }

        int framesRead = DecodeSynchronously(buffer, frameCount);
        _trackLocalTime = relativeTimestamp + framesRead / (double)_streamInfo.SampleRate;

        result = ReadResult.CreateSuccess(framesRead);
        return true;
    }

    /// <summary>
    /// Reads samples when attached to a <see cref="MasterClock"/>, using the clock's current timestamp.
    /// </summary>
    /// <param name="buffer">Destination span for interleaved samples.</param>
    /// <param name="frameCount">Number of frames requested.</param>
    /// <returns>The number of frames decoded.</returns>
    private int ReadSamplesSynchronized(Span<float> buffer, int frameCount)
    {
        ReadSamplesAtTime(
            _masterClock!.CurrentTimestamp,
            buffer,
            frameCount,
            out ReadResult result);

        return result.FramesRead;
    }

    #endregion
}
