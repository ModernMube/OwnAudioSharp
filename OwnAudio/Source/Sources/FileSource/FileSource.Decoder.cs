using System.Runtime.InteropServices;
using OwnaudioNET.Core;
using OwnaudioNET.Events;

namespace OwnaudioNET.Sources;

/// <summary>
/// FileSource partial class implementing on-demand synchronous decoding used by
/// <see cref="FileSource.ReadSamples"/>.
/// </summary>
/// <remarks>
/// As of 4.0 (plan L — legacy cut-over) the file is decoded and rendered entirely by the native Rust
/// engine on its own audio thread. The managed side keeps a decoder only so external callers can pull
/// raw samples for analysis: this class decodes on the caller's thread, on demand, with no background
/// thread, no circular buffer and no SoundTouch — so managed code never processes audio in real time
/// and the GC is never touched during playback.
/// </remarks>
public partial class FileSource
{
    #region Synchronous Decoding

    /// <summary>
    /// Decodes up to <paramref name="frameCount"/> frames on the calling thread directly from the
    /// managed decoder into <paramref name="destination"/> as raw interleaved PCM (no tempo/pitch
    /// processing), advancing the analysis read position and applying <see cref="BaseAudioSource.Volume"/>.
    /// </summary>
    /// <remarks>
    /// This is a sequential analysis cursor independent of native playback: it reads from the start of
    /// the file and advances only as samples are consumed here. Any shortfall at end of stream is
    /// padded with silence so the caller always receives <paramref name="frameCount"/> frames; when
    /// <see cref="BaseAudioSource.Loop"/> is set the decoder wraps to the start instead.
    /// </remarks>
    /// <param name="destination">Destination span for interleaved samples; must hold at least
    /// <paramref name="frameCount"/> × channel-count elements.</param>
    /// <param name="frameCount">Number of frames requested.</param>
    /// <returns>The number of decoded frames actually produced (excluding silence padding).</returns>
    private int DecodeSynchronously(Span<float> destination, int frameCount)
    {
        int channels = _streamInfo.Channels;
        int samplesRequested = frameCount * channels;
        int samplesFilled = 0;

        lock (_seekLock)
        {
            if (_analysisSeekTarget >= 0.0)
            {
                if (_decoder.TrySeek(TimeSpan.FromSeconds(_analysisSeekTarget), out _))
                {
                    ClearPending();
                    Interlocked.Exchange(ref _currentPosition, _analysisSeekTarget);
                    SetSamplePosition((long)(_analysisSeekTarget * _streamInfo.SampleRate));
                }
                _analysisSeekTarget = -1.0;
            }

            samplesFilled += DrainPending(destination.Slice(0, samplesRequested));

            while (samplesFilled < samplesRequested)
            {
                var readResult = _decoder.ReadFrames(_decodeBuffer);

                if (readResult.IsEOF || readResult.FramesRead == 0)
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

                if (!readResult.IsSucceeded)
                {
                    OnError(new AudioErrorEventArgs($"Decode error: {readResult.ErrorMessage}", null));
                    _isEndOfStream = true;
                    break;
                }

                int decodedSamples = readResult.FramesRead * channels;
                Span<float> decoded = MemoryMarshal.Cast<byte, float>(
                    _decodeBuffer.AsSpan(0, decodedSamples * sizeof(float)));

                int copy = Math.Min(decodedSamples, samplesRequested - samplesFilled);
                decoded.Slice(0, copy).CopyTo(destination.Slice(samplesFilled));
                samplesFilled += copy;

                int remainder = decodedSamples - copy;
                if (remainder > 0)
                {
                    decoded.Slice(copy, remainder).CopyTo(_pendingDecoded.AsSpan(0));
                    _pendingOffset = 0;
                    _pendingCount = remainder;
                }
            }
        }

        int framesFilled = samplesFilled / channels;

        if (framesFilled > 0)
        {
            UpdateSamplePosition(framesFilled);

            double frameDuration = 1.0 / _streamInfo.SampleRate;
            double newPosition = Interlocked.CompareExchange(ref _currentPosition, 0, 0) + framesFilled * frameDuration;
            Interlocked.Exchange(ref _currentPosition, newPosition);
        }

        if (samplesFilled < samplesRequested)
        {
            FillWithSilence(destination.Slice(samplesFilled), samplesRequested - samplesFilled);
        }

        if (framesFilled == 0 && _isEndOfStream && !Loop)
        {
            State = AudioState.EndOfStream;
        }

        ApplyVolume(destination, samplesRequested);
        return framesFilled;
    }

    /// <summary>
    /// Copies samples left over from a previous <see cref="DecodeSynchronously"/> call (decoder frame
    /// granularity rarely matches the requested frame count) into <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">Destination span sized to the current request.</param>
    /// <returns>The number of samples copied from the pending buffer.</returns>
    private int DrainPending(Span<float> destination)
    {
        if (_pendingCount == 0)
            return 0;

        int copy = Math.Min(_pendingCount, destination.Length);
        _pendingDecoded.AsSpan(_pendingOffset, copy).CopyTo(destination);
        _pendingOffset += copy;
        _pendingCount -= copy;
        return copy;
    }

    /// <summary>
    /// Discards any samples buffered from a previous decode call. Called on seek so the analysis cursor
    /// does not emit stale audio from the old position.
    /// </summary>
    private void ClearPending()
    {
        _pendingOffset = 0;
        _pendingCount = 0;
    }

    #endregion
}
