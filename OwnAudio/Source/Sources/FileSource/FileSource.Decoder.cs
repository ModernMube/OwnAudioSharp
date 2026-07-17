using System.Runtime.InteropServices;
using OwnaudioNET.Core;
using OwnaudioNET.Events;

namespace OwnaudioNET.Sources;

/// <summary>
/// On demand synchronous decoding for analysis callers. The native engine plays the
/// file, so no background thread and no allocation here.
/// </summary>
public partial class FileSource
{
    #region Synchronous Decoding

    /// <summary>
    /// Decodes raw PCM on the caller's thread into destination, pads with silence at
    /// EOF or wraps when Loop is set. Returns the decoded frame count.
    /// </summary>
    /// <param name="destination"></param>
    /// <param name="frameCount"></param>
    /// <returns></returns>
    private int _decodeSynchronously(Span<float> destination, int frameCount)
    {
        int _channels = _streamInfo.Channels;
        int _samplesRequested = frameCount * _channels;
        int _samplesFilled = 0;

        lock (_seekLock)
        {
            if (_analysisSeekTarget >= 0.0)
            {
                if (_decoder.TrySeek(TimeSpan.FromSeconds(_analysisSeekTarget), out _))
                {
                    _clearPending();
                    Interlocked.Exchange(ref _currentPosition, _analysisSeekTarget);
                    SetSamplePosition((long)(_analysisSeekTarget * _streamInfo.SampleRate));
                }
                _analysisSeekTarget = -1.0;
            }

            _samplesFilled += _drainPending(destination.Slice(0, _samplesRequested));

            while (_samplesFilled < _samplesRequested)
            {
                var _readResult = _decoder.ReadFrames(_decodeBuffer);

                if (_readResult.IsEOF || _readResult.FramesRead == 0)
                {
                    _isEndOfStream = true;

                    if (Loop && _decoder.TrySeek(TimeSpan.Zero, out _))
                    {
                        _isEndOfStream = false;
                        Interlocked.Exchange(ref _currentPosition, 0.0);
                        SetSamplePosition(0);
                        continue;
                    }

                    break;
                }

                if (!_readResult.IsSucceeded)
                {
                    OnError(new AudioErrorEventArgs($"Decode error: {_readResult.ErrorMessage}", null));
                    _isEndOfStream = true;
                    break;
                }

                int _decodedSamples = _readResult.FramesRead * _channels;
                Span<float> _decoded = MemoryMarshal.Cast<byte, float>(
                    _decodeBuffer.AsSpan(0, _decodedSamples * sizeof(float)));

                int _copy = Math.Min(_decodedSamples, _samplesRequested - _samplesFilled);
                _decoded.Slice(0, _copy).CopyTo(destination.Slice(_samplesFilled));
                _samplesFilled += _copy;

                int _remainder = _decodedSamples - _copy;
                if (_remainder > 0)
                {
                    _decoded.Slice(_copy, _remainder).CopyTo(_pendingDecoded.AsSpan(0));
                    _pendingOffset = 0;
                    _pendingCount = _remainder;
                }
            }
        }

        int _framesFilled = _samplesFilled / _channels;

        if (_framesFilled > 0)
        {
            UpdateSamplePosition(_framesFilled);

            double _newPosition = Interlocked.CompareExchange(ref _currentPosition, 0, 0)
                + _framesFilled / (double)_streamInfo.SampleRate;
            Interlocked.Exchange(ref _currentPosition, _newPosition);
        }

        if (_samplesFilled < _samplesRequested)
            FillWithSilence(destination.Slice(_samplesFilled), _samplesRequested - _samplesFilled);

        if (_framesFilled == 0 && _isEndOfStream && !Loop)
            State = AudioState.EndOfStream;

        ApplyVolume(destination, _samplesRequested);
        return _framesFilled;
    }

    /// <summary>
    /// Copies leftover samples from the previous decode call into destination.
    /// </summary>
    /// <param name="destination"></param>
    /// <returns></returns>
    private int _drainPending(Span<float> destination)
    {
        if (_pendingCount == 0) return 0;

        int _copy = Math.Min(_pendingCount, destination.Length);
        _pendingDecoded.AsSpan(_pendingOffset, _copy).CopyTo(destination);
        _pendingOffset += _copy;
        _pendingCount -= _copy;
        return _copy;
    }

    /// <summary>
    /// Drops the pending samples, called on seek so no stale audio leaks through.
    /// </summary>
    private void _clearPending()
    {
        _pendingOffset = 0;
        _pendingCount = 0;
    }

    #endregion
}
