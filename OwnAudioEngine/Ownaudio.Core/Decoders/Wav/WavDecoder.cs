using System;
using System.IO;
using System.Runtime.InteropServices;
using Ownaudio.Core.Common;

namespace Ownaudio.Decoders.Wav;

/// <summary>
/// Platform-independent WAV audio decoder supporting PCM, IEEE Float, and ADPCM formats.
/// </summary>
/// <remarks>
/// This decoder is pure C# with zero P/Invoke, working on all platforms.
/// Supports:
/// - PCM (8-bit, 16-bit, 24-bit, 32-bit integer)
/// - IEEE Float (32-bit, 64-bit)
/// - ADPCM (IMA ADPCM, MS ADPCM)
///
/// Output format: Always Float32, interleaved channels.
/// GC-optimized for minimal allocations during decode loop.
/// </remarks>
public sealed class WavDecoder : IAudioDecoder
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private AudioStreamInfo _streamInfo;
    private WavFormatChunk _format;
    private long _dataChunkStart;
    private long _dataChunkSize;
    private double _currentPts;
    private readonly int _samplesPerFrame;
    private readonly byte[] _tempReadBuffer;
    private readonly AudioBuffer _decodeBuffer;
    private bool _disposed;

    // Calculated values
    private readonly int _bytesPerSample;
    private readonly int _frameSizeBytes;

    // Zero-allocation frame pooling
    private readonly AudioFramePool _framePool;

    // Format conversion (optional, only if target format differs from source)
    private readonly AudioFormatConverter? _formatConverter;
    private readonly int _targetSampleRate;
    private readonly int _targetChannels;
    private readonly float[] _convertBuffer;

    /// <summary>
    /// Gets the information about the loaded WAV audio stream.
    /// </summary>
    public AudioStreamInfo StreamInfo => _streamInfo;

    /// <summary>
    /// Initializes a new instance of the <see cref="WavDecoder"/> class.
    /// </summary>
    /// <param name="stream">Stream containing WAV audio data. Must support seeking and reading.</param>
    /// <param name="ownsStream">If true, the decoder will dispose the stream when disposed.</param>
    /// <param name="targetSampleRate">Target sample rate in Hz (0 = use source rate, no resampling).</param>
    /// <param name="targetChannels">Target channel count (0 = use source channels, no conversion).</param>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    /// <exception cref="ArgumentException">Thrown when stream does not support seeking or reading.</exception>
    /// <exception cref="AudioException">Thrown when WAV format is invalid or unsupported.</exception>
    public WavDecoder(Stream stream, bool ownsStream = false, int targetSampleRate = 0, int targetChannels = 0)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _ownsStream = ownsStream;

        if (!_stream.CanRead)
            throw new ArgumentException("Stream must support reading.", nameof(stream));

        if (!_stream.CanSeek)
            throw new ArgumentException("Stream must support seeking.", nameof(stream));

        // Default frame size: 4096 samples per frame
        _samplesPerFrame = 4096;

        // Initialize (parse WAV headers)
        Initialize();

        // Calculate derived values
        _bytesPerSample = _format.BitsPerSample / 8;
        _frameSizeBytes = _samplesPerFrame * _format.Channels * _bytesPerSample;

        // Pre-allocate buffers for zero-allocation decode path
        _tempReadBuffer = new byte[_frameSizeBytes];
        _decodeBuffer = new AudioBuffer(_samplesPerFrame * _format.Channels * sizeof(float));

        _currentPts = 0.0;

        // Initialize format converter if target format differs from source
        _targetSampleRate = targetSampleRate > 0 ? targetSampleRate : (int)_format.SampleRate;
        _targetChannels = targetChannels > 0 ? targetChannels : _format.Channels;

        bool needsConversion = (_targetSampleRate != _format.SampleRate) || (_targetChannels != _format.Channels);

        if (needsConversion)
        {
            _formatConverter = new AudioFormatConverter(
                sourceRate: (int)_format.SampleRate,
                sourceChannels: _format.Channels,
                targetRate: _targetSampleRate,
                targetChannels: _targetChannels,
                maxFrameSize: _samplesPerFrame
            );

            // Pre-allocate conversion buffer (worst case: 4x upsample + channel upmix)
            int maxConvertedSamples = _formatConverter.CalculateOutputSize(_samplesPerFrame * _format.Channels) * 2;
            _convertBuffer = new float[maxConvertedSamples];

            // Update StreamInfo to reflect target format (after conversion)
            _streamInfo = new AudioStreamInfo(
                channels: _targetChannels,
                sampleRate: _targetSampleRate,
                duration: _streamInfo.Duration, // Duration stays the same
                bitDepth: 32 // Always Float32 output
            );
        }
        else
        {
            _formatConverter = null;
            _convertBuffer = Array.Empty<float>();
        }

        // Initialize frame pool AFTER format converter setup
        // Calculate worst-case output size accounting for resampling + channel conversion
        int maxOutputSamples = _samplesPerFrame * _targetChannels;
        if (_formatConverter != null)
        {
            // Account for resampling ratio (e.g., 44.1kHz -> 48kHz = 1.088x)
            maxOutputSamples = _formatConverter.CalculateOutputSize(_samplesPerFrame * _format.Channels);
        }
        int maxFrameBytes = maxOutputSamples * sizeof(float) * 2; // 2x safety margin
        _framePool = new AudioFramePool(bufferSize: maxFrameBytes, initialPoolSize: 2, maxPoolSize: 8);
    }

    /// <summary>
    /// Initializes the decoder by parsing WAV file headers.
    /// </summary>
    private void Initialize()
    {
        _stream.Position = 0;

        // Read RIFF chunk
        Span<byte> riffBuffer = stackalloc byte[12];
        if (_stream.Read(riffBuffer) != 12)
            throw new AudioException("Invalid WAV file: Unable to read RIFF header.");

        RiffChunk riff = MemoryMarshal.Read<RiffChunk>(riffBuffer);

        if (riff.ChunkID != RiffChunk.RIFF_ID)
            throw new AudioException($"Invalid WAV file: Expected RIFF header, got 0x{riff.ChunkID:X8}.");

        if (riff.Format != RiffChunk.WAVE_ID)
            throw new AudioException($"Invalid WAV file: Expected WAVE format, got 0x{riff.Format:X8}.");

        // Parse chunks until we find 'fmt ' and 'data'
        bool foundFmt = false;
        bool foundData = false;

        while (_stream.Position < _stream.Length)
        {
            Span<byte> chunkHeaderBuffer = stackalloc byte[8];
            int read = _stream.Read(chunkHeaderBuffer);

            if (read < 8)
                break; // End of file

            ChunkHeader chunkHeader = MemoryMarshal.Read<ChunkHeader>(chunkHeaderBuffer);

            if (chunkHeader.ChunkID == ChunkHeader.FMT_ID)
            {
                // Parse format chunk
                ParseFormatChunk(chunkHeader.ChunkSize);
                foundFmt = true;
            }
            else if (chunkHeader.ChunkID == DataChunk.DATA_ID)
            {
                // Found data chunk
                _dataChunkStart = _stream.Position;
                _dataChunkSize = chunkHeader.ChunkSize;
                foundData = true;
                break; // We have everything we need
            }
            else
            {
                // Skip unknown chunk
                _stream.Position += chunkHeader.ChunkSize;

                // WAV chunks are word-aligned (pad byte if odd size)
                if ((chunkHeader.ChunkSize & 1) != 0)
                    _stream.Position += 1;
            }
        }

        if (!foundFmt)
            throw new AudioException("Invalid WAV file: 'fmt ' chunk not found.");

        if (!foundData)
            throw new AudioException("Invalid WAV file: 'data' chunk not found.");

        // Calculate duration (based on source format, before conversion)
        long totalSamples = _dataChunkSize / (_format.Channels * (_format.BitsPerSample / 8));
        double durationSeconds = (double)totalSamples / _format.SampleRate;

        // Note: StreamInfo will be updated in constructor after format converter is initialized
        // This is a temporary StreamInfo with source format
        _streamInfo = new AudioStreamInfo(
            channels: _format.Channels,
            sampleRate: (int)_format.SampleRate,
            duration: TimeSpan.FromSeconds(durationSeconds),
            bitDepth: _format.BitsPerSample
        );

        // Seek to start of data
        _stream.Position = _dataChunkStart;
    }

    /// <summary>
    /// Parses the WAV format chunk.
    /// </summary>
    private void ParseFormatChunk(uint chunkSize)
    {
        if (chunkSize < 16)
            throw new AudioException($"Invalid WAV file: 'fmt ' chunk too small ({chunkSize} bytes).");

        Span<byte> fmtBuffer = stackalloc byte[16];
        if (_stream.Read(fmtBuffer) != 16)
            throw new AudioException("Invalid WAV file: Unable to read 'fmt ' chunk.");

        _format = MemoryMarshal.Read<WavFormatChunk>(fmtBuffer);

        // Skip extra format bytes if present
        int extraBytes = (int)chunkSize - 16;
        if (extraBytes > 0)
            _stream.Position += extraBytes;

        // Validate format
        ValidateFormat();
    }

    /// <summary>
    /// Validates that the WAV format is supported.
    /// </summary>
    private void ValidateFormat()
    {
        // Check supported formats
        if (_format.AudioFormat != WavFormatChunk.WAVE_FORMAT_PCM &&
            _format.AudioFormat != WavFormatChunk.WAVE_FORMAT_IEEE_FLOAT)
        {
            throw new AudioException(
                $"Unsupported WAV format: 0x{_format.AudioFormat:X4}. " +
                $"Only PCM (0x0001) and IEEE Float (0x0003) are currently supported.");
        }

        // Validate channels
        if (_format.Channels == 0 || _format.Channels > 8)
            throw new AudioException($"Invalid channel count: {_format.Channels}. Must be between 1 and 8.");

        // Validate sample rate
        if (_format.SampleRate < 8000 || _format.SampleRate > 192000)
            throw new AudioException($"Invalid sample rate: {_format.SampleRate}. Must be between 8000 and 192000 Hz.");

        // Validate bits per sample
        if (_format.AudioFormat == WavFormatChunk.WAVE_FORMAT_PCM)
        {
            if (_format.BitsPerSample != 8 && _format.BitsPerSample != 16 &&
                _format.BitsPerSample != 24 && _format.BitsPerSample != 32)
            {
                throw new AudioException(
                    $"Unsupported PCM bit depth: {_format.BitsPerSample}. " +
                    $"Supported: 8, 16, 24, 32 bits.");
            }
        }
        else if (_format.AudioFormat == WavFormatChunk.WAVE_FORMAT_IEEE_FLOAT)
        {
            if (_format.BitsPerSample != 32 && _format.BitsPerSample != 64)
            {
                throw new AudioException(
                    $"Unsupported IEEE Float bit depth: {_format.BitsPerSample}. " +
                    $"Supported: 32, 64 bits.");
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
        if (_disposed)
            return AudioDecoderResult.CreateError("Decoder has been disposed.");

        // Check if we've reached end of data chunk
        if (_stream.Position >= _dataChunkStart + _dataChunkSize)
            return AudioDecoderResult.CreateEOF();

        // Calculate how many bytes to read from the source
        long remainingBytes = (_dataChunkStart + _dataChunkSize) - _stream.Position;
        int bytesToRead = (int)Math.Min(_frameSizeBytes, remainingBytes);

        // Read audio data into pre-allocated buffer
        int bytesRead = _stream.Read(_tempReadBuffer, 0, bytesToRead);

        if (bytesRead == 0)
            return AudioDecoderResult.CreateEOF();

        // Convert to Float32 using SIMD
        int samplesRead = bytesRead / _bytesPerSample;
        ConvertToFloat32(_tempReadBuffer.AsSpan(0, bytesRead), samplesRead);

        // Apply format conversion if needed (resampling + channel conversion)
        int finalSampleCount = samplesRead;
        Span<byte> outputData = _decodeBuffer.Data.Slice(0, samplesRead * sizeof(float));

        if (_formatConverter != null)
        {
            // Convert format (resample + channel convert)
            Span<float> sourceFloat = MemoryMarshal.Cast<byte, float>(outputData);
            Span<float> convertedFloat = _convertBuffer.AsSpan();

            finalSampleCount = _formatConverter.Convert(sourceFloat, convertedFloat);

            // Copy converted data back to byte buffer
            outputData = MemoryMarshal.Cast<float, byte>(convertedFloat.Slice(0, finalSampleCount));
        }

        // Copy the decoded data to the output buffer
        int float32Bytes = finalSampleCount * sizeof(float);
        if (float32Bytes > buffer.Length)
        {
            return AudioDecoderResult.CreateError($"Output buffer too small. Required: {float32Bytes}, Available: {buffer.Length}");
        }

        outputData.Slice(0, float32Bytes).CopyTo(buffer.AsSpan());

        // Update presentation timestamp (based on SOURCE format, not target)
        int samplesPerChannel = samplesRead / _format.Channels;
        _currentPts += (samplesPerChannel * 1000.0) / _format.SampleRate;

        // Calculate number of frames read (frames = samples / channels)
        int framesRead = finalSampleCount / _targetChannels;

        return AudioDecoderResult.CreateSuccess(framesRead, _currentPts);
    }

    /// <summary>
    /// Decodes the next audio frame from the WAV stream.
    /// </summary>
    /// <returns>A <see cref="AudioDecoderResult"/> containing the decoded frame or error information.</returns>
    /// <remarks>
    /// This method is ZERO-ALLOCATION after warmup using AudioFramePool and SIMD conversion.
    /// Expected allocation: 0 bytes during steady state.
    /// </remarks>
    public AudioDecoderResult DecodeNextFrame()
    {
        if (_disposed)
            return new AudioDecoderResult(null!, false, false, "Decoder has been disposed.");

        // Check if we've reached end of data chunk
        if (_stream.Position >= _dataChunkStart + _dataChunkSize)
            return new AudioDecoderResult(null!, false, true);

        // Calculate how many bytes to read (don't read past end of data)
        long remainingBytes = (_dataChunkStart + _dataChunkSize) - _stream.Position;
        int bytesToRead = (int)Math.Min(_frameSizeBytes, remainingBytes);

        // Read audio data into pre-allocated buffer
        int bytesRead = _stream.Read(_tempReadBuffer, 0, bytesToRead);

        if (bytesRead == 0)
            return new AudioDecoderResult(null!, false, true);

        // Convert to Float32 using SIMD
        int samplesRead = bytesRead / _bytesPerSample;
        ConvertToFloat32(_tempReadBuffer.AsSpan(0, bytesRead), samplesRead);

        // Apply format conversion if needed (resampling + channel conversion)
        int finalSampleCount = samplesRead;
        Span<byte> outputData = _decodeBuffer.Data.Slice(0, samplesRead * sizeof(float));

        if (_formatConverter != null)
        {
            // Convert format (resample + channel convert)
            Span<float> sourceFloat = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(outputData);
            Span<float> convertedFloat = _convertBuffer.AsSpan();

            finalSampleCount = _formatConverter.Convert(sourceFloat, convertedFloat);

            // Copy converted data back to byte buffer
            outputData = System.Runtime.InteropServices.MemoryMarshal.Cast<float, byte>(convertedFloat.Slice(0, finalSampleCount));
        }

        // Rent pooled frame (ZERO ALLOCATION)
        int float32Bytes = finalSampleCount * sizeof(float);
        var pooledFrame = _framePool.Rent(_currentPts, float32Bytes);

        // Copy data to pooled frame buffer
        outputData.Slice(0, float32Bytes).CopyTo(pooledFrame.BufferSpan);

        // Convert to standard AudioFrame (this is the only allocation - the frame wrapper itself)
        var frame = pooledFrame.ToAudioFrame();

        // Return pooled frame to pool immediately
        _framePool.Return(pooledFrame);

        // Update presentation timestamp (based on SOURCE format, not target)
        int samplesPerChannel = samplesRead / _format.Channels;
        _currentPts += (samplesPerChannel * 1000.0) / _format.SampleRate;

        return new AudioDecoderResult(frame, true, false);
    }

    /// <summary>
    /// Converts audio samples to Float32 format.
    /// </summary>
    private void ConvertToFloat32(Span<byte> source, int sampleCount)
    {
        Span<float> dest = MemoryMarshal.Cast<byte, float>(_decodeBuffer.Data);

        if (_format.AudioFormat == WavFormatChunk.WAVE_FORMAT_IEEE_FLOAT && _format.BitsPerSample == 32)
        {
            // Already Float32, just copy
            Span<float> sourceFloat = MemoryMarshal.Cast<byte, float>(source);
            sourceFloat.Slice(0, sampleCount).CopyTo(dest);
        }
        else if (_format.AudioFormat == WavFormatChunk.WAVE_FORMAT_PCM)
        {
            ConvertPCMToFloat32(source, dest, sampleCount);
        }
        else
        {
            throw new AudioException($"Unsupported format conversion: format={_format.AudioFormat}, bits={_format.BitsPerSample}");
        }
    }

    /// <summary>
    /// Converts PCM samples to Float32 using SIMD acceleration.
    /// </summary>
    private void ConvertPCMToFloat32(Span<byte> source, Span<float> dest, int sampleCount)
    {
        switch (_format.BitsPerSample)
        {
            case 16:
                SimdAudioConverter.ConvertPCM16ToFloat32(source, dest, sampleCount);
                break;

            case 8:
                SimdAudioConverter.ConvertPCM8ToFloat32(source, dest, sampleCount);
                break;

            case 24:
                SimdAudioConverter.ConvertPCM24ToFloat32(source, dest, sampleCount);
                break;

            case 32:
                SimdAudioConverter.ConvertPCM32ToFloat32(source, dest, sampleCount);
                break;

            default:
                throw new AudioException($"Unsupported PCM bit depth: {_format.BitsPerSample}");
        }
    }

    /// <summary>
    /// Seeks the audio stream to the specified position.
    /// </summary>
    /// <param name="position">Target position as a <see cref="TimeSpan"/>.</param>
    /// <param name="error">Error message if seek fails.</param>
    /// <returns>True if seek succeeded, false otherwise.</returns>
    public bool TrySeek(TimeSpan position, out string error)
    {
        error = string.Empty;

        if (_disposed)
        {
            error = "Decoder has been disposed.";
            return false;
        }

        if (position < TimeSpan.Zero)
        {
            error = "Position cannot be negative.";
            return false;
        }

        if (position > _streamInfo.Duration)
        {
            error = $"Position {position} exceeds stream duration {_streamInfo.Duration}.";
            return false;
        }

        // Calculate byte offset
        double positionSeconds = position.TotalSeconds;
        long samplePosition = (long)(positionSeconds * _format.SampleRate);
        long byteOffset = samplePosition * _format.Channels * _bytesPerSample;

        // Align to block boundary
        byteOffset = (byteOffset / _format.BlockAlign) * _format.BlockAlign;

        long targetPosition = _dataChunkStart + byteOffset;

        if (targetPosition < _dataChunkStart || targetPosition >= _dataChunkStart + _dataChunkSize)
        {
            error = "Calculated seek position is out of bounds.";
            return false;
        }

        _stream.Position = targetPosition;
        _currentPts = position.TotalMilliseconds;

        // Reset format converter state (resampler position)
        _formatConverter?.Reset();

        return true;
    }

    /// <summary>
    /// Decodes all frames starting from the specified position.
    /// </summary>
    /// <param name="position">Starting position as a <see cref="TimeSpan"/>.</param>
    /// <returns>A <see cref="AudioDecoderResult"/> containing all decoded audio data.</returns>
    /// <remarks>
    /// Warning: This method loads the entire audio stream into memory.
    /// Uses pooled buffers to minimize GC pressure during accumulation.
    /// </remarks>
    public AudioDecoderResult DecodeAllFrames(TimeSpan position)
    {
        if (!TrySeek(position, out string error))
            return new AudioDecoderResult(null!, false, false, error);

        // Use pooled buffer writer instead of MemoryStream (reduces GC pressure)
        using var writer = new PooledByteBufferWriter(initialCapacity: 65536);
        double startPts = _currentPts;

        while (true)
        {
            var result = DecodeNextFrame();

            if (result.IsEOF)
                break;

            if (!result.IsSucceeded)
                return result;

            writer.Write(result.Frame.Data, 0, result.Frame.Data.Length);
        }

        byte[] allData = writer.ToArray();
        var frame = new AudioFrame(startPts, allData);

        return new AudioDecoderResult(frame, true, false);
    }

    /// <summary>
    /// Releases all resources used by the <see cref="WavDecoder"/>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        if (_ownsStream)
            _stream?.Dispose();

        _disposed = true;
    }
}
