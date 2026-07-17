using Ownaudio;
using Ownaudio.Core;
using Ownaudio.Decoders;
using OwnaudioNET.BufferManagement;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using System;
using System.IO;

namespace OwnaudioNET.Sources;

/// <summary>
/// Stream based constructors for FileSource.
/// </summary>
public partial class FileSource
{
    #region Stream-Based Constructors

    /// <summary>
    /// Builds a source from a seekable stream, format given by the caller.
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="format"></param>
    /// <param name="bufferSizeInFrames"></param>
    /// <param name="targetSampleRate"></param>
    /// <param name="targetChannels"></param>
    public FileSource(
        Stream stream,
        AudioFormat format,
        int bufferSizeInFrames = 8192,
        int targetSampleRate = 48000,
        int targetChannels = 2)
        : this(_createDecoderFromStream(stream, format, targetSampleRate, targetChannels), bufferSizeInFrames)
    {
    }

    /// <summary>
    /// Builds a source around a ready decoder, handy for custom decoder implementations.
    /// </summary>
    /// <param name="decoder"></param>
    /// <param name="bufferSizeInFrames"></param>
    public FileSource(IAudioDecoder decoder, int bufferSizeInFrames = 8192)
    {
        if (decoder == null)
            throw new ArgumentNullException(nameof(decoder));

        _bufferSizeInFrames = bufferSizeInFrames;
        _decoder = decoder;
        _streamInfo = _decoder.StreamInfo;
        _filePath = "stream_source";

        _rustNative = OwnaudioNET.Engine.RustNativeChain.Enabled;

        _config = new AudioConfig
        {
            SampleRate = _streamInfo.SampleRate,
            Channels = _streamInfo.Channels,
            BufferSize = bufferSizeInFrames
        };

        _decodeBuffer = new byte[bufferSizeInFrames * _streamInfo.Channels * sizeof(float)];
        _pendingDecoded = new float[bufferSizeInFrames * _streamInfo.Channels];

        _currentPosition = 0.0;
        _isEndOfStream = false;
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Decoder from a stream, must be readable and seekable.
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="format"></param>
    /// <param name="targetSampleRate"></param>
    /// <param name="targetChannels"></param>
    /// <returns></returns>
    private static IAudioDecoder _createDecoderFromStream(Stream stream, AudioFormat format, int targetSampleRate, int targetChannels)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        if(!stream.CanSeek)
            throw new ArgumentException("Stream must support seeking for audio playback.", nameof(stream));

        if(!stream.CanRead)
            throw new ArgumentException("Stream must support reading.", nameof(stream));

        return AudioDecoderFactory.Create(stream, format, targetSampleRate, targetChannels);
    }

    #endregion
}
