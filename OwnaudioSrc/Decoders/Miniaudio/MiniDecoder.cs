using System;
using System.Buffers;
using System.IO;
using System.Diagnostics;
using Ownaudio.MiniAudio;
using Ownaudio.Decoders.FFmpeg;

namespace Ownaudio.Decoders.MiniAudio
{
    /// <summary>
    /// A class that uses MiniAudio for decoding and demuxing specified audio source.
    /// This class cannot be inherited.
    /// </summary>
    /// <remarks>
    /// Implements: <see cref="IAudioDecoder"/>.
    /// </remarks>
    public sealed class MiniDecoder : IAudioDecoder
    {
        /// <summary>
        /// The internal MiniAudio decoder instance.
        /// </summary>
        private MiniAudioDecoder? _decoder;

        /// <summary>
        /// Synchronization lock object for thread-safe operations.
        /// </summary>
        private readonly object _syncLock = new object();

        /// <summary>
        /// Input stream that needs to be disposed when the decoder is disposed.
        /// </summary>
        private Stream? _inputStreamToDispose;

        /// <summary>
        /// Flag indicating whether the decoder has been disposed.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Number of audio channels.
        /// </summary>
        private readonly int _channels;

        /// <summary>
        /// Audio sample rate in Hz.
        /// </summary>
        private readonly int _sampleRate;

        /// <summary>
        /// Buffer used for decoding audio data.
        /// </summary>
        private float[]? _decodingBuffer;

        /// <summary>
        /// Shared array pool for efficient buffer management.
        /// </summary>
        private readonly ArrayPool<float> _bufferPool = ArrayPool<float>.Shared;

        /// <summary>
        /// Size of the decoding buffer.
        /// </summary>
        private readonly int _bufferSize = 4096;

        /// <summary>
        /// Number of bytes per audio sample.
        /// </summary>
        private readonly int _bytesPerSample = sizeof(float);

        /// <summary>
        /// Flag indicating whether the end of the audio stream has been reached.
        /// </summary>
        private bool _endOfStreamReached = false;

        /// <summary>
        /// Gets the audio stream information.
        /// </summary>
        /// <value>
        /// An <see cref="AudioStreamInfo"/> object containing stream details.
        /// </value>
        public AudioStreamInfo StreamInfo { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="MiniDecoder"/> class using an audio URL.
        /// </summary>
        /// <param name="url">The audio URL, which can be a URL or path to a local audio file.</param>
        /// <param name="options">Optional decoder configuration options. If null, default options will be used.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="url"/> is null or empty.</exception>
        /// <exception cref="Exception">Thrown when the decoder initialization fails.</exception>
        public MiniDecoder(string url, FFmpegDecoderOptions? options = default)
        {
            if (string.IsNullOrEmpty(url))
                throw new ArgumentNullException(nameof(url));

            options ??= new FFmpegDecoderOptions(2, OwnAudio.DefaultOutputDevice.DefaultSampleRate);
            _channels = options.Channels;
            _sampleRate = options.SampleRate;

            Stream? localInputStream = null;
            try
            {
                localInputStream = File.OpenRead(url);
                _inputStreamToDispose = localInputStream;
                InitializeDecoder(localInputStream, options);
            }
            catch (Exception ex)
            {
                localInputStream?.Dispose();
                throw new Exception($"Failed to initialize MiniAudio decoder from URL: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MiniDecoder"/> class using a source audio stream.
        /// </summary>
        /// <param name="stream">The source audio stream to decode.</param>
        /// <param name="options">Optional decoder configuration options. If null, default options will be used.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
        public MiniDecoder(Stream stream, FFmpegDecoderOptions? options = default)
        {
            var inputStream = stream ?? throw new ArgumentNullException(nameof(stream));
            _inputStreamToDispose = null;

            options ??= new FFmpegDecoderOptions(2, OwnAudio.DefaultOutputDevice.DefaultSampleRate);
            _channels = options.Channels;
            _sampleRate = options.SampleRate;

            InitializeDecoder(inputStream, options);
        }

        /// <summary>
        /// Initializes the internal decoder with the provided stream and options.
        /// </summary>
        /// <param name="streamToDecode">The stream containing audio data to decode.</param>
        /// <param name="options">The decoder configuration options.</param>
        /// <exception cref="Exception">Thrown when the internal MiniAudioDecoder initialization fails.</exception>
        private void InitializeDecoder(Stream streamToDecode, FFmpegDecoderOptions options)
        {
            try
            {
                _decoder = new MiniAudioDecoder(
                    streamToDecode,
                    Ownaudio.MiniAudio.EngineAudioFormat.F32,
                    options.Channels,
                    options.SampleRate);

                _decoder.EndOfStreamReached += OnDecoderEndOfStreamReached;

                _decodingBuffer = _bufferPool.Rent(_bufferSize);

                double totalSeconds = (_decoder.Length / (double)options.Channels) / options.SampleRate;
                StreamInfo = new AudioStreamInfo(
                    options.Channels,
                    options.SampleRate,
                    TimeSpan.FromSeconds(totalSeconds));
            }
            catch (Exception ex)
            {
                _decoder?.Dispose();
                throw new Exception($"Failed to initialize internal MiniAudioDecoder: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Handles the end of stream event from the internal decoder.
        /// </summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnDecoderEndOfStreamReached(object? sender, EventArgs e)
        {
            _endOfStreamReached = true;
        }

        /// <summary>
        /// Decodes the next available audio frame.
        /// </summary>
        /// <returns>
        /// An <see cref="AudioDecoderResult"/> containing the decoded frame data and operation status.
        /// </returns>
        public AudioDecoderResult DecodeNextFrame()
        {
            lock (_syncLock)
            {
                if (_endOfStreamReached)
                {
                    return new AudioDecoderResult(null, false, true, "End of stream reached (already at EOF)");
                }

                if (_disposed || _decoder == null || _decoder.IsDisposed)
                {
                    return new AudioDecoderResult(null, false, true, "Decoder disposed");
                }

                try
                {
                    int framesToRead = _bufferSize / _channels;
                    int samplesToRead = framesToRead * _channels;

                    if (_decodingBuffer == null || samplesToRead > _decodingBuffer.Length)
                    {
                        if (_decodingBuffer != null)
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
                    var lastFrame = new AudioFrame(presentationTime, data);

                    return new AudioDecoderResult(lastFrame, true, false);
                }
                catch (ObjectDisposedException)
                {
                    _disposed = true;
                    _endOfStreamReached = true;
                    return new AudioDecoderResult(null, false, true, "Decoder was disposed during operation.");
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
                    Debug.WriteLine($"DecodeNextFrame exception: {ex.Message}");
                    return new AudioDecoderResult(null, false, false, ex.Message);
                }
            }
        }

        /// <summary>
        /// Attempts to seek the audio stream to the specified position.
        /// </summary>
        /// <param name="position">The position to seek to in the audio stream.</param>
        /// <param name="error">When this method returns, contains the error message if the seek operation failed, or an empty string if it succeeded.</param>
        /// <returns>
        /// <c>true</c> if the seek operation was successful; otherwise, <c>false</c>.
        /// </returns>
        public bool TrySeek(TimeSpan position, out string error)
        {
            lock (_syncLock)
            {
                error = string.Empty;

                if (_disposed || _decoder == null || _decoder.IsDisposed)
                {
                    error = "Decoder is disposed";
                    return false;
                }

                try
                {
                    int samplePosition = (int)(position.TotalSeconds * _sampleRate) * _channels;
                    bool result = _decoder.Seek(samplePosition, _channels);

                    if (result)
                    {
                        _endOfStreamReached = false;
                    }
                    else
                    {
                        error = "Seek operation failed.";
                    }
                    return result;
                }
                catch (ObjectDisposedException)
                {
                    error = "Decoder was disposed during seek.";
                    _disposed = true;
                    _endOfStreamReached = true;
                    return false;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Debug.WriteLine($"Seek exception: {error}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Processes and decodes all frames from the audio stream and returns them in a single result.
        /// </summary>
        /// <param name="position">The starting position in the audio stream. If default, starts from the current position or beginning.</param>
        /// <returns>
        /// An <see cref="AudioDecoderResult"/> containing all decoded frame data combined into a single frame.
        /// </returns>
        public AudioDecoderResult DecodeAllFrames(TimeSpan position = default)
        {
            lock (_syncLock)
            {
                if (_disposed || _decoder == null || _decoder.IsDisposed)
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
                            return new AudioDecoderResult(null, false, false, seekError ?? "Seek failed");
                        }
                    }
                    else
                    {
                        if (!TrySeek(TimeSpan.Zero, out var seekError))
                        {
                            return new AudioDecoderResult(null, false, false, seekError ?? "Seek to zero failed");
                        }
                    }

                    bool anyFrameProcessed = false;
                    int maxIterations = 100000;
                    int iteration = 0;

                    while (iteration < maxIterations)
                    {
                        iteration++;

                        if (_disposed || _decoder.IsDisposed || _endOfStreamReached)
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
                            Debug.WriteLine($"Decoder error during DecodeAllFrames: {frameResult.ErrorMessage ?? "Unknown error"}");
                            return new AudioDecoderResult(null, false, false,
                                $"Decoder error: {frameResult.ErrorMessage ?? "Unknown error"}");
                        }
                    }

                    if (!anyFrameProcessed || accumulatedData.Length == 0)
                    {
                        return new AudioDecoderResult(null, false, true, "No frames were processed or no audio data available (EOF or error).");
                    }

                    if (iteration >= maxIterations)
                    {
                        return new AudioDecoderResult(
                            new AudioFrame(lastPresentationTime, accumulatedData.ToArray()),
                            true,
                            false,
                            "Warning: Maximum iteration count reached - possible infinite loop prevented");
                    }

                    var frameData = accumulatedData.ToArray();

                    return new AudioDecoderResult(
                        new AudioFrame(lastPresentationTime, frameData),
                        true,
                        _endOfStreamReached);
                }
                catch (ObjectDisposedException)
                {
                    _disposed = true;
                    _endOfStreamReached = true;
                    return new AudioDecoderResult(null, false, true, "Decoder was disposed during DecodeAllFrames.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DecodeAllFrames exception: {ex.Message}");
                    return new AudioDecoderResult(null, false, false, ex.Message);
                }
            }
        }

        /// <summary>
        /// Releases all resources used by the <see cref="MiniDecoder"/>.
        /// </summary>
        /// <remarks>
        /// This method disposes of the internal decoder, returns buffers to the pool, 
        /// and disposes of any streams that were created by this instance.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            lock (_syncLock)
            {
                if (_disposed)
                    return;

                if (_decoder != null)
                {
                    _decoder.EndOfStreamReached -= OnDecoderEndOfStreamReached;
                    _decoder.Dispose();
                    _decoder = null;
                }

                if (_decodingBuffer != null)
                {
                    _bufferPool.Return(_decodingBuffer, clearArray: false);
                    _decodingBuffer = null;
                }

                _inputStreamToDispose?.Dispose();
                _inputStreamToDispose = null;

                _disposed = true;
            }
        }
    }
}
