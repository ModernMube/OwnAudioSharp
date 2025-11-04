using System;
using System.IO;
using Ownaudio.Decoders;
using Ownaudio.Decoders.Mp3;

namespace Ownaudio.Windows.Decoders;

/// <summary>
/// Windows Media Foundation MP3 decoder adapter.
/// Implements IPlatformMp3Decoder interface for Mp3Decoder wrapper.
/// </summary>
/// <remarks>
/// This is an adapter that wraps the existing MFMp3Decoder to provide
/// the IPlatformMp3Decoder interface for the platform-independent Mp3Decoder.
/// </remarks>
public sealed class WindowsMFMp3Decoder : IPlatformMp3Decoder
{
    private MFMp3Decoder? _decoder;
    private bool _disposed;

    /// <summary>
    /// Default constructor (required for reflection-based creation).
    /// </summary>
    public WindowsMFMp3Decoder()
    {
    }

    /// <inheritdoc/>
    public void InitializeFromFile(string filePath, int targetSampleRate, int targetChannels)
    {
        _decoder = new MFMp3Decoder(filePath, targetSampleRate, targetChannels);
    }

    /// <inheritdoc/>
    public void InitializeFromStream(Stream stream, int targetSampleRate, int targetChannels)
    {
        _decoder = new MFMp3Decoder(stream, ownsStream: false, targetSampleRate, targetChannels);
    }

    /// <inheritdoc/>
    public AudioStreamInfo GetStreamInfo()
    {
        if (_decoder == null)
            throw new InvalidOperationException("Decoder not initialized");

        return _decoder.StreamInfo;
    }

    /// <inheritdoc/>
    public int DecodeFrame(Span<byte> outputBuffer, out double pts)
    {
        if (_decoder == null)
            throw new InvalidOperationException("Decoder not initialized");

        var result = _decoder.DecodeNextFrame();

        if (result.IsEOF)
        {
            pts = 0.0;
            return 0; // EOF
        }

        if (!result.IsSucceeded)
        {
            pts = 0.0;
            return -1; // Error
        }

        if (result.Frame == null || result.Frame.Data == null)
        {
            pts = 0.0;
            return -1; // Error
        }

        // Copy frame data to output buffer
        int bytesToCopy = Math.Min(result.Frame.Data.Length, outputBuffer.Length);
        result.Frame.Data.AsSpan(0, bytesToCopy).CopyTo(outputBuffer);

        pts = result.Frame.PresentationTime;
        return bytesToCopy;
    }

    /// <inheritdoc/>
    public bool Seek(long samplePosition)
    {
        if (_decoder == null)
            throw new InvalidOperationException("Decoder not initialized");

        // Convert sample position to TimeSpan
        var streamInfo = _decoder.StreamInfo;
        double positionSeconds = (double)samplePosition / streamInfo.SampleRate;
        var position = TimeSpan.FromSeconds(positionSeconds);

        return _decoder.TrySeek(position, out _);
    }

    /// <inheritdoc/>
    public double CurrentPts
    {
        get
        {
            if (_decoder == null)
                return 0.0;

            // The current PTS is managed internally by MFMp3Decoder
            // We can't directly access it, but it's returned in DecodeFrame
            return 0.0;
        }
    }

    /// <inheritdoc/>
    public bool IsEOF
    {
        get
        {
            if (_decoder == null)
                return true;

            // We detect EOF through DecodeFrame returning 0 bytes
            // This property is informational only
            return false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _decoder?.Dispose();
        _decoder = null;

        _disposed = true;
    }
}
