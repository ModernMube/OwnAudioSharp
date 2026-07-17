using OwnaudioNET.Synchronization;
using OwnaudioNET.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Sources;

/// <summary>
/// MasterClock attachment and time based reading. Real drift correction lives in
/// the native engine, this surface stays for API compatibility.
/// </summary>
public partial class FileSource
{
    #region MasterClock Synchronization

    /// <inheritdoc/>
    public void AttachToClock(MasterClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        DetachFromClock();

        _masterClock = clock;

        double _target = _masterClock.CurrentTimestamp - _startOffset;

        if (_target > 0 && Seek(_target))
        {
            _trackLocalTime = _target;
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
        if (_masterClock == null) return;

        _masterClock = null;
        _trackLocalTime = 0.0;
        IsSynchronized = false;
    }

    #endregion

    #region Time-Based Reading

    /// <inheritdoc/>
    public bool ReadSamplesAtTime(double masterTimestamp, Span<float> buffer, int frameCount, out ReadResult result)
    {
        ThrowIfDisposed();

        double _relative = masterTimestamp - _startOffset;

        if (_relative < 0)
        {
            FillWithSilence(buffer, frameCount * _streamInfo.Channels);
            result = ReadResult.CreateSuccess(frameCount);
            return true;
        }

        int _framesRead = _decodeSynchronously(buffer, frameCount);
        _trackLocalTime = _relative + _framesRead / (double)_streamInfo.SampleRate;

        result = ReadResult.CreateSuccess(_framesRead);
        return true;
    }

    /// <summary>
    /// Reads using the attached clock's current timestamp.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="frameCount"></param>
    /// <returns></returns>
    private int _readSamplesSynchronized(Span<float> buffer, int frameCount)
    {
        ReadSamplesAtTime(_masterClock!.CurrentTimestamp, buffer, frameCount, out ReadResult _result);
        return _result.FramesRead;
    }

    #endregion
}
