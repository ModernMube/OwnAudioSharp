using System;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Decoders;
using Ownaudio.Safe;
using CoreStreamInfo = Ownaudio.AudioStreamInfo;

namespace OwnaudioNET.Engine;

/// <summary>
/// Puts the Rust StreamingAudioDecoder behind IAudioDecoder, so the Symphonia backend (WAV, MP3, FLAC,
/// OGG, AAC/M4A, AIFF) is the primary decoder. FFmpeg only steps in for what it can't handle.
/// </summary>
internal sealed class RustNativeDecoder : IAudioDecoder
{
    #region Fields

    /// <summary>
    /// Max 1 ms idle waits we tolerate on a prefetch underrun before calling it EOF, so a stalled
    /// prefetch thread can't hang us forever.
    /// </summary>
    private const int MaxUnderrunWaits = 5000;

    private readonly StreamingAudioDecoder _inner;
    private readonly int _channels;
    private readonly int _sampleRate;
    private double _currentPts;
    private bool _disposed;

    #endregion

    #region Construction

    /// <summary>
    /// Opens the file with the native decoder. Zero target rate or channels means keep the source values.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="targetSampleRate"></param>
    /// <param name="targetChannels"></param>
    public RustNativeDecoder(string filePath, int targetSampleRate, int targetChannels)
    {
        _inner = new StreamingAudioDecoder(filePath, targetSampleRate, targetChannels);

        var _info = _inner.StreamInfo;
        _channels = _info.Channels > 0 ? _info.Channels : 1;
        _sampleRate = _info.SampleRate > 0 ? _info.SampleRate : 48_000;
        StreamInfo = new CoreStreamInfo(_info.Channels, _info.SampleRate, _info.Duration, _info.BitDepth);
    }

    #endregion

    #region IAudioDecoder

    /// <inheritdoc />
    public CoreStreamInfo StreamInfo { get; }

    /// <inheritdoc />
    public AudioDecoderResult ReadFrames(byte[] buffer)
    {
        if (_disposed)
            return AudioDecoderResult.CreateError("Decoder has been disposed.");

        if (buffer is null)
            return AudioDecoderResult.CreateError("Output buffer is null.");

        Span<float> _floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan());

        int _capacityFrames = _floats.Length / _channels;
        if (_capacityFrames == 0)
            return AudioDecoderResult.CreateSuccess(0, _currentPts);

        Span<float> _aligned = _floats.Slice(0, _capacityFrames * _channels);

        int _written;
        int _waits = 0;
        while (true)
        {
            try
            {
                _written = _inner.Read(_aligned);
            }
            catch (Exception ex)
            {
                return AudioDecoderResult.CreateError(ex.Message);
            }

            if (_written > 0) break;

            if (_inner.IsEndOfStream) return AudioDecoderResult.CreateEOF();
            if (++_waits > MaxUnderrunWaits) return AudioDecoderResult.CreateEOF();

            Thread.Sleep(1);
        }

        int _frames = _written / _channels;
        _currentPts += _frames * 1000.0 / _sampleRate;
        return AudioDecoderResult.CreateSuccess(_frames, _currentPts);
    }

    /// <inheritdoc />
    public bool TrySeek(TimeSpan position, out string error)
    {
        if (_disposed)
        {
            error = "Decoder has been disposed.";
            return false;
        }

        try
        {
            _inner.Seek(position);
            _currentPts = position.TotalMilliseconds;
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _inner.Dispose();
    }

    #endregion
}
