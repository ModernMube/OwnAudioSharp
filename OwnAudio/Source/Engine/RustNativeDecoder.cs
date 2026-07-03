using System;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Decoders;
using Ownaudio.Safe;
using CoreStreamInfo = Ownaudio.AudioStreamInfo;

namespace OwnaudioNET.Engine;

/// <summary>
/// Adapts the Rust-backed <see cref="StreamingAudioDecoder"/> to the core
/// <see cref="IAudioDecoder"/> contract, so the pure-Rust Symphonia backend
/// (WAV, MP3, FLAC, OGG/Vorbis, AAC/M4A, AIFF) becomes the primary decoder used by
/// <see cref="Ownaudio.Decoders.AudioDecoderFactory"/>. FFmpeg is only used as a
/// fallback for formats the native backend cannot decode.
/// </summary>
internal sealed class RustNativeDecoder : IAudioDecoder
{
    #region Fields

    /// <summary>
    /// Upper bound on the number of 1 ms idle waits tolerated during a transient
    /// native prefetch underrun before a read is reported as end-of-stream. This
    /// prevents an unbounded hang if the prefetch thread stops making progress.
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
    /// Opens <paramref name="filePath"/> with the native Rust decoder.
    /// </summary>
    /// <param name="filePath">Path to the audio file.</param>
    /// <param name="targetSampleRate">Output sample rate in Hz (0 = source rate).</param>
    /// <param name="targetChannels">Output channel count (0 = source channels).</param>
    public RustNativeDecoder(string filePath, int targetSampleRate, int targetChannels)
    {
        _inner = new StreamingAudioDecoder(filePath, targetSampleRate, targetChannels);

        var info = _inner.StreamInfo;
        _channels = info.Channels > 0 ? info.Channels : 1;
        _sampleRate = info.SampleRate > 0 ? info.SampleRate : 48_000;
        StreamInfo = new CoreStreamInfo(info.Channels, info.SampleRate, info.Duration, info.BitDepth);
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

        Span<float> floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan());

        int capacityFrames = floats.Length / _channels;
        if (capacityFrames == 0)
            return AudioDecoderResult.CreateSuccess(0, _currentPts);

        Span<float> frameAligned = floats.Slice(0, capacityFrames * _channels);

        int written;
        int waits = 0;
        while (true)
        {
            try
            {
                written = _inner.Read(frameAligned);
            }
            catch (Exception ex)
            {
                return AudioDecoderResult.CreateError(ex.Message);
            }

            if (written > 0)
                break;

            if (_inner.IsEndOfStream)
                return AudioDecoderResult.CreateEOF();

            if (++waits > MaxUnderrunWaits)
                return AudioDecoderResult.CreateEOF();

            Thread.Sleep(1);
        }

        int framesRead = written / _channels;
        _currentPts += framesRead * 1000.0 / _sampleRate;
        return AudioDecoderResult.CreateSuccess(framesRead, _currentPts);
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
