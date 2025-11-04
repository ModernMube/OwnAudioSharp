using System;
using System.IO;
using Ownaudio.Core.Common;

namespace Ownaudio.Decoders;

/// <summary>
/// Abstract base class for stream-based audio decoders.
/// Provides common stream lifecycle management, error handling, and seeking validation.
/// </summary>
/// <remarks>
/// <para><b>Thread Safety:</b> Not thread-safe. Decoder instances should not be shared across threads.</para>
/// <para><b>GC Behavior:</b> Zero-allocation during decode operations after initialization.</para>
/// <para><b>Platform Support:</b> All platforms (pure C#).</para>
/// </remarks>
public abstract class BaseStreamDecoder : IAudioDecoder
{
    /// <summary>
    /// The underlying stream containing audio data.
    /// </summary>
    protected Stream? _stream;

    /// <summary>
    /// Indicates whether the decoder owns the stream and should dispose it.
    /// </summary>
    protected bool _ownsStream;

    /// <summary>
    /// Information about the audio stream (format, duration, etc.).
    /// </summary>
    protected AudioStreamInfo _streamInfo;

    /// <summary>
    /// Current presentation timestamp in milliseconds.
    /// </summary>
    protected double _currentPts;

    /// <summary>
    /// Indicates whether the decoder has been disposed.
    /// </summary>
    protected bool _isDisposed;

    /// <summary>
    /// Gets the information about the loaded audio stream.
    /// </summary>
    public AudioStreamInfo StreamInfo => _streamInfo;

    /// <summary>
    /// Parses the stream to extract audio format information.
    /// Called during initialization to populate <see cref="_streamInfo"/>.
    /// </summary>
    /// <returns>Audio stream information.</returns>
    /// <exception cref="AudioException">Thrown when stream format is invalid or unsupported.</exception>
    protected abstract AudioStreamInfo ParseStreamInfo();

    /// <summary>
    /// Decodes the next audio frame from the stream.
    /// This is the platform/format-specific implementation.
    /// </summary>
    /// <returns>Decoded audio data and metadata.</returns>
    /// <exception cref="AudioException">Thrown when decoding fails.</exception>
    protected abstract AudioDecoderResult DecodeNextFrameCore();

    /// <summary>
    /// Seeks to the specified sample position in the stream.
    /// This is the platform/format-specific implementation.
    /// </summary>
    /// <param name="samplePosition">Sample position to seek to (zero-based).</param>
    /// <returns>True if seek succeeded, false otherwise.</returns>
    protected abstract bool SeekCore(long samplePosition);

    /// <summary>
    /// Decodes the next audio frame from the stream.
    /// Includes common validation and error handling.
    /// </summary>
    /// <returns>Decoded audio data and metadata.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when decoder has been disposed.</exception>
    public AudioDecoderResult DecodeNextFrame()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        try
        {
            return DecodeNextFrameCore();
        }
        catch (AudioException)
        {
            // Re-throw AudioException as-is
            throw;
        }
        catch (Exception ex)
        {
            // Wrap unexpected exceptions
            throw new AudioException(
                AudioErrorCategory.Decoding,
                $"Unexpected error during frame decode at position {_stream?.Position ?? -1}",
                ex)
            {
                StreamPosition = _stream?.Position ?? -1
            };
        }
    }

    /// <summary>
    /// Attempts to seek the audio stream to the specified position.
    /// </summary>
    /// <param name="position">Desired seek position as a time offset.</param>
    /// <param name="error">Error message if seek fails.</param>
    /// <returns>True if seek succeeded, false otherwise.</returns>
    public bool TrySeek(TimeSpan position, out string error)
    {
        if (_isDisposed)
        {
            error = "Decoder has been disposed";
            return false;
        }

        // Validate stream is seekable
        if (_stream == null || !_stream.CanSeek)
        {
            error = "Stream does not support seeking";
            return false;
        }

        // Validate position range
        if (position < TimeSpan.Zero)
        {
            error = $"Position {position} cannot be negative";
            return false;
        }

        if (position > _streamInfo.Duration)
        {
            error = $"Position {position} exceeds stream duration {_streamInfo.Duration}";
            return false;
        }

        // Calculate sample position
        long samplePosition = (long)(position.TotalSeconds * _streamInfo.SampleRate);

        // Call platform-specific implementation
        try
        {
            if (!SeekCore(samplePosition))
            {
                error = "Platform decoder seek failed";
                return false;
            }

            _currentPts = position.TotalMilliseconds;
            error = null!;
            return true;
        }
        catch (AudioException ex)
        {
            error = $"Seek error: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Unexpected seek error: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Decodes all audio frames from the specified position to the end of the stream.
    /// </summary>
    /// <param name="position">Starting position for decoding.</param>
    /// <returns>All decoded audio data concatenated.</returns>
    /// <exception cref="AudioException">Thrown when decoding fails.</exception>
    public virtual AudioDecoderResult DecodeAllFrames(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Seek to start position if not at zero
        if (position != TimeSpan.Zero)
        {
            if (!TrySeek(position, out string error))
            {
                throw new AudioException(
                    AudioErrorCategory.Seeking,
                    $"Failed to seek to position {position}: {error}");
            }
        }

        // Calculate total samples needed
        long totalSamples = (long)(_streamInfo.Duration.TotalSeconds * _streamInfo.SampleRate * _streamInfo.Channels);

        // Pre-allocate buffer for all samples
        float[] allSamples = new float[totalSamples];
        int offset = 0;

        // Decode all frames
        while (true)
        {
            var result = DecodeNextFrame();

            if (result.IsEOF)
                break;

            if (result.Frame != null && result.Frame.Data != null && result.Frame.Data.Length > 0)
            {
                // Convert byte[] to float[] (AudioFrame.Data is byte[], we need float[])
                int floatCount = result.Frame.Data.Length / sizeof(float);
                ReadOnlySpan<float> floatData = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(result.Frame.Data.AsSpan());

                // Ensure we don't overflow
                int copyLength = Math.Min(floatCount, allSamples.Length - offset);
                floatData.Slice(0, copyLength).CopyTo(allSamples.AsSpan(offset, copyLength));
                offset += copyLength;
            }
        }

        // Trim to actual size
        if (offset < allSamples.Length)
        {
            Array.Resize(ref allSamples, offset);
        }

        // Convert float[] to byte[] for AudioFrame
        byte[] byteData = new byte[allSamples.Length * sizeof(float)];
        Buffer.BlockCopy(allSamples, 0, byteData, 0, byteData.Length);

        var audioFrame = new AudioFrame(_currentPts, byteData);
        return new AudioDecoderResult(audioFrame, true, false);
    }

    /// <summary>
    /// Releases all resources used by the decoder.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases unmanaged and optionally managed resources.
    /// </summary>
    /// <param name="disposing">True to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed)
            return;

        if (disposing)
        {
            // Dispose stream if owned
            if (_ownsStream && _stream != null)
            {
                _stream.Dispose();
            }
        }

        _isDisposed = true;
    }
}
