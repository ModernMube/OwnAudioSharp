using System;
using System.Buffers;
using System.IO;
using System.Diagnostics;
using Ownaudio.MiniAudio;
using Ownaudio.Decoders.FFmpeg;

namespace Ownaudio.Decoders.MiniAudio;

/// <summary>
/// A class that uses MiniAudio for decoding and demuxing specified audio source.
/// This class cannot be inherited.
/// Process description:
/// 
/// <para>Implements: <see cref="IAudioDecoder"/>.</para>
/// </summary>
public sealed class MiniDecoder : IAudioDecoder
{
    private MiniAudioDecoder? _decoder;
    private readonly object _syncLock = new object();
    private readonly Stream _inputStream;
    private bool _disposed;
    private readonly int _channels;
    private readonly int _sampleRate;
    private AudioFrame? _lastFrame;
    private float[]? _decodingBuffer;
    private readonly ArrayPool<float> _bufferPool = ArrayPool<float>.Shared;
    private readonly int _bufferSize = 8192;
    private readonly int _bytesPerSample = sizeof(float);
    private bool _endOfStreamReached = false;

    /// <summary>
    /// Audio stream info <see cref="AudioStreamInfo"/>
    /// </summary>
    public AudioStreamInfo StreamInfo { get; private set; }

    /// <summary>
    /// Initializes <see cref="MiniDecoder"/> by providing audio URL.
    /// The audio URL can be URL or path to local audio file.
    /// </summary>
    /// <param name="url">Audio URL or audio file path to decode.</param>
    /// <param name="options">An optional FFmpeg decoder options.</param>
    /// <exception cref="ArgumentNullException">Thrown when the given url is <c>null</c>.</exception>
    /// <exception cref="Exception">Thrown when errors occurred during setups.</exception>
    public MiniDecoder(string url, FFmpegDecoderOptions? options = default)
    {
        if (string.IsNullOrEmpty(url))
            throw new ArgumentNullException(nameof(url));

        options ??= new FFmpegDecoderOptions(2, OwnAudio.DefaultOutputDevice.DefaultSampleRate);
        _channels = options.Channels;
        _sampleRate = options.SampleRate;

        try
        {
            _inputStream = File.OpenRead(url);
            InitializeDecoder(options);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to initialize MiniAudio decoder: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Initializes <see cref="MiniDecoder"/> by providing source audio stream.
    /// </summary>
    /// <param name="stream">Source of audio stream to decode.</param>
    /// <param name="options">An optional FFmpeg decoder options.</param>
    /// <exception cref="ArgumentNullException">Thrown when the given stream is <c>null</c>.</exception>
    /// <exception cref="Exception">Thrown when errors occurred during setups.</exception>
    public MiniDecoder(Stream stream, FFmpegDecoderOptions? options = default)
    {
        _inputStream = stream ?? throw new ArgumentNullException(nameof(stream));

        options ??= new FFmpegDecoderOptions(2, OwnAudio.DefaultOutputDevice.DefaultSampleRate);
        _channels = options.Channels;
        _sampleRate = options.SampleRate;

        InitializeDecoder(options);
    }

    private void InitializeDecoder(FFmpegDecoderOptions options)
    {
        try
        {
            _decoder = new MiniAudioDecoder(
                _inputStream,
                EngineAudioFormat.F32,
                options.Channels,
                options.SampleRate);

            _decoder.EndOfStreamReached += (sender, e) =>
            {
                _endOfStreamReached = true;
            };

            _decodingBuffer = _bufferPool.Rent(_bufferSize);

            double totalSeconds = (_decoder.Length / (double)options.Channels) / options.SampleRate;
            StreamInfo = new AudioStreamInfo(
                options.Channels,
                options.SampleRate,
                TimeSpan.FromSeconds(totalSeconds));
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to initialize MiniAudio decoder: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Decodes the next available audio frame.
    /// </summary>
    /// <returns>An <see cref="AudioDecoderResult"/> containing the decoded frame or error information.</returns>
    public AudioDecoderResult DecodeNextFrame()
    {
#nullable disable
        lock (_syncLock)
        {
            if (_endOfStreamReached)
            {
                return new AudioDecoderResult(null, false, true, "End of stream reached (already at EOF)");
            }

            if (_disposed)
            {
                return new AudioDecoderResult(null, false, true, "Decoder disposed");
            }

            try
            {
                int framesToRead = 2048;
                int samplesToRead = framesToRead * _channels;

                if (samplesToRead > _decodingBuffer?.Length)
                {
                    _bufferPool.Return(_decodingBuffer);
                    _decodingBuffer = _bufferPool.Rent(samplesToRead);
                }

                long framesRead = _decoder.Decode(_decodingBuffer, 0, framesToRead);

                if (framesRead <= 0)
                {
                    _endOfStreamReached = true;
                    return new AudioDecoderResult(null, false, true, "End of stream reached (no frames read)");
                }

                int samplesRead = (int)(framesRead * _channels);
                int dataSize = samplesRead * _bytesPerSample;

                if (dataSize <= 0)
                {
                    _endOfStreamReached = true;
                    return new AudioDecoderResult(null, false, true, "End of stream reached (no data)");
                }

                var data = new byte[dataSize];
                Buffer.BlockCopy(_decodingBuffer, 0, data, 0, dataSize);

                double presentationTime = (double)framesRead / _sampleRate * 1000;
                _lastFrame = new AudioFrame(presentationTime, data);

                return new AudioDecoderResult(_lastFrame, true, false);
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("At end") || ex.Message.Contains("end of stream") ||
                    ex.Message.Contains("EOF") || ex.Message.Contains("No data available"))
                {
                    Debug.WriteLine($"EOF detected from exception: {ex.Message}");
                    _endOfStreamReached = true;
                    return new AudioDecoderResult(null, false, true, $"End of stream reached (exception: {ex.Message})");
                }

                return new AudioDecoderResult(null, false, false, ex.Message);
            }
        }
#nullable restore
    }

    /// <summary>
    /// Try to seeks audio stream to the specified position and returns <c>true</c> if successfully seeks,
    /// otherwise, <c>false</c>.
    /// </summary>
    /// <param name="position">Desired seek position.</param>
    /// <param name="error">An error message while seeking audio stream.</param>
    /// <returns><c>true</c> if successfully seeks, otherwise, <c>false</c>.</returns>
    public bool TrySeek(TimeSpan position, out string error)
    {
#nullable disable
        lock (_syncLock)
        {
            error = null;

            if (_disposed)
            {
                error = "Decoder is disposed";
                return false;
            }

            try
            {
                _endOfStreamReached = false;

                bool result = _decoder.Seek(0, _channels);

                if (result)
                {
                    int samplePosition = (int)(position.TotalSeconds * _sampleRate) * _channels;

                    result = _decoder.Seek(samplePosition, _channels);
                }

                return result;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Debug.WriteLine($"Seek exception: {error}");
                return false;
            }
        }
        #nullable restore
    }

    /// <summary>
    /// It processes all the frames. And returns them in an AudioDecoderResult
    /// </summary>
    /// <param name="position">Current position from which to continue</param>
    /// <returns>Audio decoder result</returns>
    public AudioDecoderResult DecodeAllFrames(TimeSpan position = default)
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                return new AudioDecoderResult(null, false, true, "Decoder disposed");
            }

            try
            {
                using var accumulatedData = new MemoryStream();
                double lastPresentationTime = 0;

                if (position != default)
                {
                    if (!TrySeek(position, out var seekError))
                    {
                        return new AudioDecoderResult(null, false, false, seekError);
                    }
                }
                else
                {
                    if (!TrySeek(TimeSpan.Zero, out var seekError))
                    {
                        return new AudioDecoderResult(null, false, false, seekError);
                    }
                }

                bool anyFrameProcessed = false;
                int maxIterations = 100000; 
                int iteration = 0;

                while (iteration < maxIterations)
                {
                    iteration++;

                    if (_endOfStreamReached)
                    {
                        break;
                    }

                    AudioDecoderResult frameResult = DecodeNextFrame();

                    if (frameResult.IsSucceeded && frameResult.Frame != null)
                    {
                        accumulatedData.Write(frameResult.Frame.Data, 0, frameResult.Frame.Data.Length);
                        lastPresentationTime = frameResult.Frame.PresentationTime;
                        anyFrameProcessed = true;
                    }
                    else if (frameResult.IsEOF)
                    {
                        _endOfStreamReached = true;
                        break;
                    }
                    else
                    {
                        Debug.WriteLine($"Decoder error: {frameResult.ErrorMessage ?? "Unknown error"}");
                        return new AudioDecoderResult(null, false, false,
                            $"Decoder error: {frameResult.ErrorMessage ?? "Unknown error"}");
                    }
                }

                if (!anyFrameProcessed || accumulatedData.Length == 0)
                {
                    return new AudioDecoderResult(null, false, false, "No frames were processed or no audio data available");
                }

                if (iteration >= maxIterations)
                {
                    return new AudioDecoderResult(
                        new AudioFrame(lastPresentationTime, accumulatedData.ToArray()),
                        true,
                        false,
                        "Warning: Maximum iteration count reached - possible infinite loop prevented");
                }

                TrySeek(position, out _);

                var frameData = accumulatedData.ToArray();

                return new AudioDecoderResult(
                    new AudioFrame(lastPresentationTime, frameData),
                    true,
                    false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DecodeAllFrames exception: {ex.Message}");
                return new AudioDecoderResult(null, false, false, ex.Message);
            }
        }
    }

    /// <summary>
    /// Disposes of resources used by the decoder.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_syncLock)
        {
            _decoder?.Dispose();

            if (_decodingBuffer != null)
            {
                _bufferPool.Return(_decodingBuffer);
                _decodingBuffer = null;
            }

            _disposed = true;
        }
    }
}
