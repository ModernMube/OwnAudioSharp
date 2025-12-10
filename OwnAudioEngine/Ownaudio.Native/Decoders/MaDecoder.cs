using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using Ownaudio.Decoders;
using Ownaudio.Native.Utils;
using Ownaudio.Native.MiniAudio;
using static Ownaudio.Native.MiniAudio.MaBinding;

namespace Ownaudio.Native.Decoders;

/// <summary>
/// MiniAudio-based cross-platform audio decoder supporting MP3, WAV, FLAC, and other formats.
/// This is the PRIMARY decoder for OwnAudioSharp 2.1.0+ when native libraries are available.
/// </summary>
/// <remarks>
/// Uses miniaudio native library for high-performance decoding with broad format support.
///
/// Supported formats:
/// - MP3 (MPEG-1/2/2.5 Layer III)
/// - WAV (PCM, IEEE Float, ADPCM, and more)
/// - FLAC (Free Lossless Audio Codec)
/// - OGG Vorbis
/// - Other formats supported by miniaudio
///
/// Output format: Always Float32, interleaved channels.
/// </remarks>
public sealed class MaDecoder : IAudioDecoder
{
    /// <summary>
    /// MiniAudio library loader instance.
    /// </summary>
    // private readonly LibraryLoader? _miniAudioLoader; // REMOVED: Managed by MaBinding

    /// <summary>
    /// Pointer to the MiniAudio decoder instance.
    /// </summary>
    private IntPtr _decoder;

    /// <summary>
    /// Pointer to the decoder configuration.
    /// </summary>
    private IntPtr _configPtr;

    /// <summary>
    /// Information about the audio stream.
    /// </summary>
    private AudioStreamInfo _streamInfo;

    /// <summary>
    /// Path to the audio file being decoded.
    /// </summary>
    private readonly string? _filePath;

    /// <summary>
    /// Stream containing audio data.
    /// </summary>
    private readonly Stream? _stream;

    /// <summary>
    /// Indicates whether this decoder owns and should dispose the stream.
    /// </summary>
    private readonly bool _ownsStream;

    /// <summary>
    /// Current presentation timestamp in milliseconds.
    /// </summary>
    private double _currentPts;

    /// <summary>
    /// Number of samples to decode per frame.
    /// </summary>
    private readonly int _samplesPerFrame;

    /// <summary>
    /// Indicates whether the decoder has been disposed.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Target number of channels for resampling.
    /// </summary>
    private readonly int _targetChannels;

    /// <summary>
    /// Target sample rate for resampling.
    /// </summary>
    private readonly int _targetSampleRate;

    /// <summary>
    /// Indicates whether the end of stream has been reached.
    /// </summary>
    private bool _endOfStreamReached;

    /// <summary>
    /// Audio format returned by the decoder.
    /// </summary>
    private MaFormat _decoderFormat;

    /// <summary>
    /// Number of audio channels in the decoded stream.
    /// </summary>
    private uint _decoderChannels;

    /// <summary>
    /// Sample rate of the decoded audio.
    /// </summary>
    private uint _decoderSampleRate;

    /// <summary>
    /// Total number of frames in the audio stream.
    /// </summary>
    private ulong _totalFrames;

    /// <summary>
    /// Temporary buffer for decoded float audio samples.
    /// </summary>
    private readonly float[] _tempDecodeBuffer;

    /// <summary>
    /// Temporary buffer for converting float samples to bytes.
    /// </summary>
    private readonly byte[] _tempByteBuffer;

    /// <summary>
    /// Callback delegate for reading data from the stream.
    /// </summary>
    private DecoderReadProc? _readCallback;

    /// <summary>
    /// Callback delegate for seeking within the stream.
    /// </summary>
    private DecoderSeekProc? _seekCallback;

    /// <summary>
    /// Pooled buffer for reading stream data in callbacks.
    /// </summary>
    private byte[] _readBuffer;

    /// <summary>
    /// Minimum size for the read buffer.
    /// </summary>
    private const int MIN_READ_BUFFER_SIZE = 4096;

    /// <summary>
    /// Maximum size for the read buffer.
    /// </summary>
    private const int MAX_READ_BUFFER_SIZE = 65536;

    /// <summary>
    /// Synchronization lock for thread-safe operations.
    /// </summary>
    private readonly object _syncLock = new object();

    /// <summary>
    /// Gets the information about the loaded audio stream.
    /// </summary>
    public AudioStreamInfo StreamInfo => _streamInfo;

    /// <summary>
    /// Initializes a new instance of the <see cref="MaDecoder"/> class from a file path.
    /// </summary>
    /// <param name="filePath">Path to the audio file to decode.</param>
    /// <param name="targetSampleRate">Target sample rate for resampling (0 for original rate).</param>
    /// <param name="targetChannels">Target number of channels (0 for original channels).</param>
    /// <exception cref="ArgumentNullException">Thrown when filePath is null or empty.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="AudioException">Thrown when the decoder fails to initialize.</exception>
    public MaDecoder(string filePath, int targetSampleRate = 0, int targetChannels = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentNullException(nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Audio file not found: {filePath}", filePath);

        _filePath = filePath;
        _stream = File.OpenRead(filePath);
        _ownsStream = true; // We created the stream, so we own it
        _samplesPerFrame = 4096;
        _targetSampleRate = targetSampleRate;
        _targetChannels = targetChannels;
        _readBuffer = ArrayPool<byte>.Shared.Rent(MIN_READ_BUFFER_SIZE);

        // Load miniaudio library
        try
        {
            MaBinding.EnsureInitialized();
        }
        catch (Exception ex)
        {
            _stream?.Dispose();
            ArrayPool<byte>.Shared.Return(_readBuffer);
            throw new AudioException($"Failed to load miniaudio library: {ex.Message}", ex);
        }

        // Initialize decoder from stream
        InitializeFromStream(_stream, targetSampleRate, targetChannels);

        // Pre-allocate buffers
        int maxOutputSamples = _samplesPerFrame * (int)_decoderChannels * 2;
        _tempDecodeBuffer = new float[maxOutputSamples];
        _tempByteBuffer = new byte[maxOutputSamples * sizeof(float)];
        _currentPts = 0.0;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MaDecoder"/> class from a stream.
    /// </summary>
    /// <param name="stream">Stream containing audio data to decode.</param>
    /// <param name="ownsStream">If true, the decoder will dispose the stream when disposed.</param>
    /// <param name="targetSampleRate">Target sample rate for resampling (0 for original rate).</param>
    /// <param name="targetChannels">Target number of channels (0 for original channels).</param>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    /// <exception cref="ArgumentException">Thrown when stream doesn't support reading or seeking.</exception>
    /// <exception cref="AudioException">Thrown when the decoder fails to initialize.</exception>
    public MaDecoder(Stream stream, bool ownsStream = false, int targetSampleRate = 0, int targetChannels = 0)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _ownsStream = ownsStream;
        _filePath = null;
        _samplesPerFrame = 4096;
        _targetSampleRate = targetSampleRate;
        _targetChannels = targetChannels;
        _readBuffer = ArrayPool<byte>.Shared.Rent(MIN_READ_BUFFER_SIZE);

        if (!_stream.CanRead)
            throw new ArgumentException("Stream must support reading.", nameof(stream));

        if (!_stream.CanSeek)
            throw new ArgumentException("Stream must support seeking.", nameof(stream));

        // Load miniaudio library
        try
        {
            MaBinding.EnsureInitialized();
        }
        catch (Exception ex)
        {
            ArrayPool<byte>.Shared.Return(_readBuffer);
            throw new AudioException($"Failed to load miniaudio library: {ex.Message}", ex);
        }

        // Initialize decoder from stream
        InitializeFromStream(stream, targetSampleRate, targetChannels);

        // Pre-allocate buffers
        int maxOutputSamples = _samplesPerFrame * (int)_decoderChannels * 2;
        _tempDecodeBuffer = new float[maxOutputSamples];
        _tempByteBuffer = new byte[maxOutputSamples * sizeof(float)];
        _currentPts = 0.0;
    }

    /// <summary>
    /// Initializes the decoder from a stream using callbacks.
    /// </summary>
    /// <param name="stream">Stream to decode from.</param>
    /// <param name="targetSampleRate">Target sample rate for resampling.</param>
    /// <param name="targetChannels">Target number of channels.</param>
    /// <exception cref="AudioException">Thrown when decoder initialization fails.</exception>
    private void InitializeFromStream(Stream stream, int targetSampleRate, int targetChannels)
    {
        try
        {
            // Try to allocate decoder config - use native version if available, fallback to managed
            try
            {
                _configPtr = sf_allocate_decoder_config(
                    MaFormat.F32,
                    (uint)(targetChannels > 0 ? targetChannels : 2),
                    (uint)(targetSampleRate > 0 ? targetSampleRate : 44100)
                );
            }
            catch (NotSupportedException)
            {
                // Native version not available, use managed fallback
                _configPtr = allocate_decoder_config(
                    MaFormat.F32,
                    (uint)(targetChannels > 0 ? targetChannels : 2),
                    (uint)(targetSampleRate > 0 ? targetSampleRate : 44100)
                );
            }

            if (_configPtr == IntPtr.Zero)
                throw new AudioException("Failed to allocate decoder config");

            // Try to allocate decoder - use native version if available, fallback to managed
            try
            {
                _decoder = sf_allocate_decoder();
            }
            catch (NotSupportedException)
            {
                // Native version not available, use managed fallback
                _decoder = allocate_decoder();
            }

            if (_decoder == IntPtr.Zero)
            {
                ma_free(_configPtr, IntPtr.Zero, "Failed to allocate decoder - freeing config");
                _configPtr = IntPtr.Zero;
                throw new AudioException("Failed to allocate decoder");
            }

            // Create callback delegates
            _readCallback = ReadCallback;
            _seekCallback = SeekCallback;

            // Initialize decoder with callbacks
            MaResult result = ma_decoder_init(
                _readCallback,
                _seekCallback,
                IntPtr.Zero, // user data
                _configPtr,
                _decoder
            );

            if (result != MaResult.Success)
            {
                ma_free(_decoder, IntPtr.Zero, $"Init failed ({result}) - freeing decoder");
                _decoder = IntPtr.Zero;
                ma_free(_configPtr, IntPtr.Zero, $"Init failed ({result}) - freeing config");
                _configPtr = IntPtr.Zero;
                throw new AudioException($"Failed to initialize decoder: {result}");
            }

            // Config can be freed after successful init
            if (_configPtr != IntPtr.Zero)
            {
                ma_free(_configPtr, IntPtr.Zero, "Freeing config after successful init");
                _configPtr = IntPtr.Zero;
            }

            // Get decoder format information
            _decoderFormat = MaFormat.F32;
            _decoderChannels = 0;
            _decoderSampleRate = 0;

            result = ma_decoder_get_data_format(
                _decoder,
                ref _decoderFormat,
                ref _decoderChannels,
                ref _decoderSampleRate,
                IntPtr.Zero,
                0
            );

            if (result != MaResult.Success)
                throw new AudioException($"Failed to get decoder format: {result}");

            // Get total length in frames
            result = ma_decoder_get_length_in_pcm_frames(_decoder, out _totalFrames);
            if (result != MaResult.Success)
                _totalFrames = 0; // Unknown length

            // Calculate duration
            TimeSpan duration = TimeSpan.Zero;
            if (_totalFrames > 0 && _decoderSampleRate > 0)
            {
                duration = TimeSpan.FromSeconds((double)_totalFrames / _decoderSampleRate);
            }

            // Create stream info
            _streamInfo = new AudioStreamInfo(
                channels: (int)_decoderChannels,
                sampleRate: (int)_decoderSampleRate,
                duration: duration,
                bitDepth: 32 // Float32
            );

            _endOfStreamReached = false;
        }
        catch
        {
            if (_decoder != IntPtr.Zero)
            {
                ma_free(_decoder, IntPtr.Zero, "Exception during init - freeing decoder");
                _decoder = IntPtr.Zero;
            }
            if (_configPtr != IntPtr.Zero)
            {
                ma_free(_configPtr, IntPtr.Zero, "Exception during init - freeing config");
                _configPtr = IntPtr.Zero;
            }
            throw;
        }
    }

    /// <summary>
    /// Callback for reading data from the stream.
    /// </summary>
    /// <param name="pDecoder">Pointer to the decoder instance.</param>
    /// <param name="pBufferOut">Pointer to the output buffer.</param>
    /// <param name="bytesToRead">Number of bytes to read.</param>
    /// <param name="pBytesRead">Number of bytes actually read.</param>
    /// <returns>Result indicating success or error.</returns>
    private MaResult ReadCallback(IntPtr pDecoder, IntPtr pBufferOut, ulong bytesToRead, out ulong pBytesRead)
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                pBytesRead = 0;
                return MaResult.Error;
            }

            var size = (int)Math.Min(bytesToRead, MAX_READ_BUFFER_SIZE);

            // Resize buffer if needed
            if (_readBuffer == null || _readBuffer.Length < size)
            {
                var newSize = Math.Max(size, MIN_READ_BUFFER_SIZE);
                if (_readBuffer != null)
                    ArrayPool<byte>.Shared.Return(_readBuffer, false);
                _readBuffer = ArrayPool<byte>.Shared.Rent(newSize);
            }

            var read = 0;
            try
            {
                read = _stream!.Read(_readBuffer, 0, size);
            }
            catch (ObjectDisposedException)
            {
                _endOfStreamReached = true;
                pBytesRead = 0;
                return MaResult.Error;
            }

            if (read == 0 && !_endOfStreamReached)
            {
                _endOfStreamReached = true;
            }

            if (read > 0)
            {
                unsafe
                {
                    fixed (byte* pReadBuffer = _readBuffer)
                    {
                        Buffer.MemoryCopy(pReadBuffer, (void*)pBufferOut, bytesToRead, (ulong)read);
                    }
                }
            }

            pBytesRead = (ulong)read;
            return MaResult.Success;
        }
    }

    /// <summary>
    /// Callback for seeking in the stream.
    /// </summary>
    /// <param name="pDecoder">Pointer to the decoder instance.</param>
    /// <param name="byteOffset">Byte offset to seek to.</param>
    /// <param name="origin">Seek origin (beginning or current position).</param>
    /// <returns>Result indicating success or error.</returns>
    private MaResult SeekCallback(IntPtr pDecoder, long byteOffset, SeekPoint origin)
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                return MaResult.Error;
            }

            if (!_stream!.CanSeek)
                return MaResult.FormatNotSupported;

            try
            {
                SeekOrigin seekOrigin = origin switch
                {
                    SeekPoint.FromCurrent => SeekOrigin.Current,
                    _ => SeekOrigin.Begin
                };

                _stream.Seek(byteOffset, seekOrigin);
                _endOfStreamReached = false;
            }
            catch (IOException)
            {
                return MaResult.IoError;
            }
            catch (ArgumentOutOfRangeException)
            {
                return MaResult.InvalidArgs;
            }
            catch (ObjectDisposedException)
            {
                return MaResult.Error;
            }

            return MaResult.Success;
        }
    }

    /// <summary>
    /// Decodes the next audio frame from the stream.
    /// </summary>
    /// <returns>Result containing the decoded audio frame or error information.</returns>
    [Obsolete("This method allocates a new AudioFrame on each call. Use ReadFrames instead.", true)]
    public AudioDecoderResult DecodeNextFrame()
    {
        lock (_syncLock)
        {
            if (_endOfStreamReached)
            {
                return new AudioDecoderResult(
                    frame: null!,
                    succeeded: true,
                    eof: true,
                    errorMessage: "End of stream reached"
                );
            }

            if (_disposed || _decoder == IntPtr.Zero)
            {
                return new AudioDecoderResult(
                    frame: null!,
                    succeeded: false,
                    eof: true,
                    errorMessage: "Decoder disposed"
                );
            }

            try
            {
                int framesToRead = _samplesPerFrame / (int)_decoderChannels;
                int samplesToRead = framesToRead * (int)_decoderChannels;

                unsafe
                {
                    fixed (float* pBuffer = _tempDecodeBuffer)
                    {
                        MaResult result = ma_decoder_read_pcm_frames(
                            _decoder,
                            (IntPtr)pBuffer,
                            (ulong)framesToRead,
                            out ulong framesRead
                        );

                        if (framesRead == 0 || result == MaResult.AtEnd)
                        {
                            _endOfStreamReached = true;
                            return new AudioDecoderResult(
                                frame: null!,
                                succeeded: true,
                                eof: true,
                                errorMessage: "End of stream"
                            );
                        }

                        if (result != MaResult.Success)
                        {
                            return new AudioDecoderResult(
                                frame: null!,
                                succeeded: false,
                                eof: false,
                                errorMessage: $"Decoder read error: {result}"
                            );
                        }

                        int samplesRead = (int)framesRead * (int)_decoderChannels;
                        int dataSize = samplesRead * sizeof(float);

                        if (dataSize <= 0)
                        {
                            _endOfStreamReached = true;
                            return new AudioDecoderResult(
                                frame: null!,
                                succeeded: true,
                                eof: true,
                                errorMessage: null
                            );
                        }

                        // Copy float samples to byte array
                        var sourceSpan = new ReadOnlySpan<float>(_tempDecodeBuffer, 0, samplesRead);
                        var targetSpan = new Span<byte>(_tempByteBuffer, 0, dataSize);
                        MemoryMarshal.AsBytes(sourceSpan).CopyTo(targetSpan);

                        // Create result array
                        var data = new byte[dataSize];
                        targetSpan.Slice(0, dataSize).CopyTo(data);

                        double presentationTime = _currentPts;
                        var frame = new AudioFrame(presentationTime, data);

                        // Update presentation time
                        _currentPts += (double)framesRead / _decoderSampleRate * 1000.0;

                        return new AudioDecoderResult(
                            frame: frame,
                            succeeded: true,
                            eof: false,
                            errorMessage: null
                        );
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                _disposed = true;
                _endOfStreamReached = true;
                return new AudioDecoderResult(
                    frame: null!,
                    succeeded: false,
                    eof: true,
                    errorMessage: "Decoder was disposed during operation"
                );
            }
            catch (Exception ex)
            {
                return new AudioDecoderResult(
                    frame: null!,
                    succeeded: false,
                    eof: false,
                    errorMessage: $"Decoder exception: {ex.Message}"
                );
            }
        }
    }

    /// <summary>
    /// Reads the next block of audio frames into the provided buffer.
    /// This is the recommended zero-allocation method for reading audio data.
    /// </summary>
    /// <param name="buffer">The buffer to write the decoded audio data into. The data is in 32-bit floating point format.</param>
    /// <returns>An <see cref="AudioDecoderResult"/> indicating the number of frames read.</returns>
    public AudioDecoderResult ReadFrames(byte[] buffer)
    {
        lock (_syncLock)
        {
            if (_endOfStreamReached)
            {
                return new AudioDecoderResult(0, true, true, "End of stream reached");
            }

            if (_disposed || _decoder == IntPtr.Zero)
            {
                return new AudioDecoderResult(0, false, true, "Decoder disposed");
            }

            try
            {
                int maxFramesToRead = buffer.Length / ((int)_decoderChannels * sizeof(float));

                unsafe
                {
                    fixed (byte* pBuffer = buffer)
                    {
                        // The native function expects a float*, so we cast our byte*
                        float* pFloatBuffer = (float*)pBuffer;

                        MaResult result = ma_decoder_read_pcm_frames(
                            _decoder,
                            (IntPtr)pFloatBuffer,
                            (ulong)maxFramesToRead,
                            out ulong framesRead
                        );

                        if (framesRead == 0 || result == MaResult.AtEnd)
                        {
                            _endOfStreamReached = true;
                            return new AudioDecoderResult((int)framesRead, true, true, "End of stream");
                        }

                        if (result != MaResult.Success)
                        {
                            return new AudioDecoderResult(0, false, false, $"Decoder read error: {result}");
                        }

                        // Update presentation time
                        _currentPts += (double)framesRead / _decoderSampleRate * 1000.0;

                        return new AudioDecoderResult((int)framesRead, true, false, null);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                _disposed = true;
                _endOfStreamReached = true;
                return new AudioDecoderResult(0, false, true, "Decoder was disposed during operation");
            }
            catch (Exception ex)
            {
                return new AudioDecoderResult(0, false, false, $"Decoder exception: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Seeks to the specified position in the audio stream.
    /// </summary>
    /// <param name="position">Position to seek to.</param>
    /// <param name="error">Error message if seek fails.</param>
    /// <returns>True if seek succeeded, false otherwise.</returns>
    public bool TrySeek(TimeSpan position, out string error)
    {
        lock (_syncLock)
        {
            error = string.Empty;

            if (_disposed || _decoder == IntPtr.Zero)
            {
                error = "Decoder is disposed";
                return false;
            }

            try
            {
                // Convert time to frame index
                ulong frameIndex = (ulong)(position.TotalSeconds * _decoderSampleRate);

                MaResult result = ma_decoder_seek_to_pcm_frame(_decoder, frameIndex);

                if (result != MaResult.Success)
                {
                    error = $"Seek failed: {result}";
                    return false;
                }

                // Update presentation time and reset EOF flag
                _currentPts = position.TotalMilliseconds;
                _endOfStreamReached = false;

                return true;
            }
            catch (ObjectDisposedException)
            {
                error = "Decoder was disposed during seek";
                _disposed = true;
                _endOfStreamReached = true;
                return false;
            }
            catch (Exception ex)
            {
                error = $"Seek exception: {ex.Message}";
                return false;
            }
        }
    }

    /// <summary>
    /// Disposes the decoder and releases all resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_syncLock)
        {
            if (_disposed)
                return;

            // Keep callbacks alive until after uninit
            GC.KeepAlive(_readCallback);
            GC.KeepAlive(_seekCallback);

            if (_decoder != IntPtr.Zero)
            {
                ma_decoder_uninit(_decoder);
                ma_free(_decoder, IntPtr.Zero, "Dispose - freeing decoder");
                _decoder = IntPtr.Zero;
            }

            if (_configPtr != IntPtr.Zero)
            {
                ma_free(_configPtr, IntPtr.Zero, "Dispose - freeing config");
                _configPtr = IntPtr.Zero;
            }

            if (_readBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_readBuffer, clearArray: false);
                _readBuffer = null!;
            }

            if (_ownsStream && _stream != null)
            {
                _stream.Dispose();
            }

            // _miniAudioLoader?.Dispose(); // REMOVED: Managed by MaBinding

            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Finalizer to ensure resources are released.
    /// </summary>
    ~MaDecoder()
    {
        Dispose();
    }
}
