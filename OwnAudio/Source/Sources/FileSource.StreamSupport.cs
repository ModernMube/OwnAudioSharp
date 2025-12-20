using Ownaudio;
using Ownaudio.Core;
using Ownaudio.Decoders;
using OwnaudioNET.BufferManagement;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Processing;
using System;
using System.IO;

namespace OwnaudioNET.Sources;

/// <summary>
/// Partial class for FileSource - implements stream-based loading functionality.
/// Provides constructors for loading audio from streams instead of file paths.
/// </summary>
public partial class FileSource
{
    /// <summary>
    /// Initializes a new instance of the FileSource class from a stream.
    /// Automatically detects audio format from stream header.
    /// </summary>
    /// <param name="stream">Stream containing audio data. Must support seeking.</param>
    /// <param name="format">Audio format of the stream.</param>
    /// <param name="bufferSizeInFrames">Size of the internal buffer in frames (default: 8192).</param>
    /// <param name="targetSampleRate">Target sample rate (0 = use source rate, default: 48000).</param>
    /// <param name="targetChannels">Target channel count (0 = use source channels, default: 2).</param>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    /// <exception cref="ArgumentException">Thrown when stream doesn't support seeking.</exception>
    /// <exception cref="AudioException">Thrown when the stream cannot be decoded or format is unsupported.</exception>
    public FileSource(
        Stream stream,
        AudioFormat format,
        int bufferSizeInFrames = 8192,
        int targetSampleRate = 48000,
        int targetChannels = 2)
        : this(CreateDecoderFromStream(stream, format, targetSampleRate, targetChannels), bufferSizeInFrames)
    {
    }

    /// <summary>
    /// Creates a new FileSource with a custom audio decoder.
    /// Useful for dependency injection or using custom decoder implementations.
    /// </summary>
    /// <param name="decoder">Pre-configured audio decoder.</param>
    /// <param name="bufferSizeInFrames">Size of the internal buffer in frames.</param>
    public FileSource(IAudioDecoder decoder, int bufferSizeInFrames = 8192)
    {
        if (decoder == null)
            throw new ArgumentNullException(nameof(decoder));

        _bufferSizeInFrames = bufferSizeInFrames;
        _decoder = decoder;
        _streamInfo = _decoder.StreamInfo;
        // Default file path for stream-based sources
        _filePath = "stream_source";

        _config = new AudioConfig
        {
            SampleRate = _streamInfo.SampleRate,
            Channels = _streamInfo.Channels,
            BufferSize = bufferSizeInFrames
        };

        // Initialize circular buffer with 4x size for better buffering
        int bufferSizeInSamples = bufferSizeInFrames * _streamInfo.Channels * 4;
        _buffer = new CircularBuffer(bufferSizeInSamples);

        // Initialize SoundTouch processor
        _soundTouch = new SoundTouchProcessor(_streamInfo.SampleRate, _streamInfo.Channels);
        _soundTouchOutputBuffer = new float[bufferSizeInFrames * _streamInfo.Channels * 2];

        // Pre-allocate buffers
        _soundTouchInputBuffer = new float[bufferSizeInFrames * _streamInfo.Channels * 4];
        _soundTouchAccumulationBuffer = new float[bufferSizeInFrames * _streamInfo.Channels * 8];
        _soundTouchAccumulationCount = 0;
        
        // ZERO-ALLOC: Pre-allocate a reusable buffer for the decoder.
        _decodeBuffer = new byte[bufferSizeInFrames * _streamInfo.Channels * sizeof(float)];

        // Initialize synchronization primitives
        _pauseEvent = new ManualResetEventSlim(false);
        _shouldStop = false;
        _seekRequested = false;
        _currentPosition = 0.0;
        _isEndOfStream = false;

        // Create decoder thread
        _decoderThread = new Thread(DecoderThreadProc)
        {
            Name = $"FileSource-Decoder-{Id}",
            IsBackground = true,
            Priority = ThreadPriority.Normal
        };
    }

    /// <summary>
    /// Creates an audio decoder from a stream.
    /// </summary>
    /// <param name="stream">Stream containing audio data.</param>
    /// <param name="format">Audio format.</param>
    /// <param name="targetSampleRate">Target sample rate.</param>
    /// <param name="targetChannels">Target channel count.</param>
    /// <returns>Configured audio decoder.</returns>
    private static IAudioDecoder CreateDecoderFromStream(Stream stream, AudioFormat format, int targetSampleRate, int targetChannels)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        if (!stream.CanSeek)
            throw new ArgumentException("Stream must support seeking for audio playback.", nameof(stream));

        if (!stream.CanRead)
            throw new ArgumentException("Stream must support reading.", nameof(stream));

        return AudioDecoderFactory.Create(stream, format, targetSampleRate, targetChannels);
    }
}
