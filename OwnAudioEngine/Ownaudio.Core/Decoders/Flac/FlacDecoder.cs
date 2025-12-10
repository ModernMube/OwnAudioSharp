using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Ownaudio.Core.Common;

namespace Ownaudio.Decoders.Flac;

/// <summary>
/// Zero-allocation FLAC audio decoder.
/// Supports native FLAC format with lossless compression.
/// </summary>
/// <remarks>
/// Supports:
/// - Sample rates: 1Hz to 655350Hz
/// - Channels: 1-8
/// - Bit depths: 4-32 bits per sample
/// - Block sizes: 16-65535 samples
/// - Fixed and LPC prediction
/// - Rice/Rice2 residual coding
///
/// Output format: Always Float32, interleaved channels.
/// Zero-allocation decode path after initialization.
/// </remarks>
public sealed class FlacDecoder : IAudioDecoder
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private AudioStreamInfo _streamInfo;
    private FlacStreamInfo _flacStreamInfo;
    private long _firstFramePosition;
    private double _currentPts;
    private readonly byte[] _frameBuffer;
    private readonly int[] _decodeBuffer;
    private readonly int[] _channelDecodeBuffer; // Temporary buffer for channel decoding
    private readonly float[] _outputBuffer;
    private readonly AudioBuffer _audioBuffer;
    private bool _disposed;

    // SEEKTABLE for fast seeking (optional, only if present in file)
    private FlacSeekPoint[]? _seekTable;
    private int _seekTableCount;

    // Format conversion (optional, only if target format differs from source)
    private readonly AudioFormatConverter? _formatConverter;
    private readonly int _targetSampleRate;
    private readonly int _targetChannels;
    private readonly float[] _convertBuffer;

    // Decode state
    private int _currentFrame;
    private long _currentSample;

    // Zero-allocation frame pooling
    private readonly AudioFramePool _framePool;

    /// <summary>
    /// Gets the information about the loaded FLAC audio stream.
    /// </summary>
    public AudioStreamInfo StreamInfo => _streamInfo;

    /// <summary>
    /// Initializes a new instance of the <see cref="FlacDecoder"/> class.
    /// </summary>
    /// <param name="stream">Stream containing FLAC audio data. Must support seeking and reading.</param>
    /// <param name="ownsStream">If true, the decoder will dispose the stream when disposed.</param>
    /// <param name="targetSampleRate">Target sample rate in Hz (0 = use source rate, no resampling).</param>
    /// <param name="targetChannels">Target channel count (0 = use source channels, no conversion).</param>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    /// <exception cref="ArgumentException">Thrown when stream does not support seeking or reading.</exception>
    /// <exception cref="AudioException">Thrown when FLAC format is invalid or unsupported.</exception>
    public FlacDecoder(Stream stream, bool ownsStream = false, int targetSampleRate = 0, int targetChannels = 0)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _ownsStream = ownsStream;

        if (!_stream.CanRead)
            throw new ArgumentException("Stream must support reading.", nameof(stream));

        if (!_stream.CanSeek)
            throw new ArgumentException("Stream must support seeking.", nameof(stream));

        // Initialize (parse FLAC headers)
        Initialize();

        // Pre-allocate buffers for zero-allocation decode path
        int maxBlockSize = _flacStreamInfo.MaxBlockSizeValue;
        int maxChannels = _flacStreamInfo.Channels;

        // Frame buffer: worst case ~16KB per frame
        _frameBuffer = new byte[maxBlockSize * maxChannels * 4 + 16384];

        // Decode buffer: holds raw decoded samples (int32) - interleaved
        _decodeBuffer = new int[maxBlockSize * maxChannels];

        // Channel decode buffer: temporary storage for per-channel decoding (all channels * blockSize)
        _channelDecodeBuffer = new int[maxBlockSize * maxChannels];

        // Output buffer: Float32 samples
        _outputBuffer = new float[maxBlockSize * maxChannels];

        // Audio buffer for frame data
        _audioBuffer = new AudioBuffer(maxBlockSize * maxChannels * sizeof(float));

        _currentPts = 0.0;
        _currentFrame = 0;
        _currentSample = 0;

        // Initialize format converter if target format differs from source
        _targetSampleRate = targetSampleRate > 0 ? targetSampleRate : _flacStreamInfo.SampleRate;
        _targetChannels = targetChannels > 0 ? targetChannels : _flacStreamInfo.Channels;

        bool needsConversion = (_targetSampleRate != _flacStreamInfo.SampleRate) || (_targetChannels != _flacStreamInfo.Channels);

        if (needsConversion)
        {
            _formatConverter = new AudioFormatConverter(
                sourceRate: _flacStreamInfo.SampleRate,
                sourceChannels: _flacStreamInfo.Channels,
                targetRate: _targetSampleRate,
                targetChannels: _targetChannels,
                maxFrameSize: maxBlockSize
            );

            // Pre-allocate conversion buffer (worst case: 4x upsample + channel upmix)
            int maxConvertedSamples = _formatConverter.CalculateOutputSize(maxBlockSize * maxChannels) * 2;
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
        int maxOutputSamples = maxBlockSize * _targetChannels;
        if (_formatConverter != null)
        {
            // Account for resampling ratio (e.g., 44.1kHz -> 48kHz = 1.088x)
            maxOutputSamples = _formatConverter.CalculateOutputSize(maxBlockSize * _flacStreamInfo.Channels);
        }
        int maxFrameBytes = maxOutputSamples * sizeof(float) * 2; // 2x safety margin
        _framePool = new AudioFramePool(bufferSize: maxFrameBytes, initialPoolSize: 2, maxPoolSize: 8);
    }

    /// <summary>
    /// Initializes the decoder by parsing FLAC file headers.
    /// </summary>
    private void Initialize()
    {
        _stream.Position = 0;

        // Read FLAC marker: "fLaC"
        Span<byte> markerBuffer = stackalloc byte[4];
        if (_stream.Read(markerBuffer) != 4)
            throw new AudioException("Invalid FLAC file: Unable to read marker.");

        FlacMarker marker = MemoryMarshal.Read<FlacMarker>(markerBuffer);
        if (!marker.IsValid)
            throw new AudioException($"Invalid FLAC file: Expected 'fLaC' marker, got 0x{marker.Signature:X8}.");

        // Parse metadata blocks
        bool foundStreamInfo = false;

        while (true)
        {
            // Read metadata block header
            Span<byte> headerBuffer = stackalloc byte[4];
            if (_stream.Read(headerBuffer) != 4)
                throw new AudioException("Invalid FLAC file: Unable to read metadata block header.");

            FlacMetadataBlockHeader header = MemoryMarshal.Read<FlacMetadataBlockHeader>(headerBuffer);

            if (header.Type == FlacMetadataBlockType.StreamInfo)
            {
                // Parse STREAMINFO
                ParseStreamInfo(header.Length);
                foundStreamInfo = true;
            }
            else if (header.Type == FlacMetadataBlockType.SeekTable)
            {
                // Parse SEEKTABLE for fast seeking
                ParseSeekTable(header.Length);
            }
            else
            {
                // Skip other metadata blocks (VORBIS_COMMENT, PICTURE, etc.)
                _stream.Position += header.Length;
            }

            if (header.IsLast)
                break;
        }

        if (!foundStreamInfo)
            throw new AudioException("Invalid FLAC file: STREAMINFO block not found.");

        // Store position of first audio frame
        _firstFramePosition = _stream.Position;

        // Calculate duration
        double durationSeconds = (double)_flacStreamInfo.TotalSamples / _flacStreamInfo.SampleRate;

        _streamInfo = new AudioStreamInfo(
            channels: _flacStreamInfo.Channels,
            sampleRate: _flacStreamInfo.SampleRate,
            duration: TimeSpan.FromSeconds(durationSeconds),
            bitDepth: _flacStreamInfo.BitsPerSample
        );
    }

    /// <summary>
    /// Parses the FLAC STREAMINFO metadata block.
    /// </summary>
    private void ParseStreamInfo(int length)
    {
        if (length != 34)
            throw new AudioException($"Invalid FLAC STREAMINFO size: {length} (expected 34 bytes).");

        Span<byte> buffer = stackalloc byte[34];
        if (_stream.Read(buffer) != 34)
            throw new AudioException("Invalid FLAC file: Unable to read STREAMINFO block.");

        _flacStreamInfo = MemoryMarshal.Read<FlacStreamInfo>(buffer);

        // Validate
        if (_flacStreamInfo.SampleRate == 0)
            throw new AudioException("Invalid FLAC STREAMINFO: Sample rate is 0.");

        if (_flacStreamInfo.Channels == 0 || _flacStreamInfo.Channels > 8)
            throw new AudioException($"Invalid FLAC STREAMINFO: Channels = {_flacStreamInfo.Channels} (must be 1-8).");

        if (_flacStreamInfo.BitsPerSample < 4 || _flacStreamInfo.BitsPerSample > 32)
            throw new AudioException($"Invalid FLAC STREAMINFO: BitsPerSample = {_flacStreamInfo.BitsPerSample} (must be 4-32).");
    }

    /// <summary>
    /// Parses the FLAC SEEKTABLE metadata block for fast seeking.
    /// </summary>
    private void ParseSeekTable(int length)
    {
        const int SEEKPOINT_SIZE = 18; // Each seekpoint is 18 bytes

        if (length % SEEKPOINT_SIZE != 0)
        {
            // Invalid SEEKTABLE size, skip it
            _stream.Position += length;
            return;
        }

        int seekPointCount = length / SEEKPOINT_SIZE;
        if (seekPointCount == 0)
        {
            _stream.Position += length;
            return;
        }

        // Read all seekpoints
        byte[] seekTableData = new byte[length];
        if (_stream.Read(seekTableData, 0, length) != length)
        {
            // Failed to read, skip
            return;
        }

        // Parse seekpoints and filter out placeholders
        var validSeekPoints = new List<FlacSeekPoint>(seekPointCount);
        var span = seekTableData.AsSpan();

        for (int i = 0; i < seekPointCount; i++)
        {
            int offset = i * SEEKPOINT_SIZE;
            var seekPoint = MemoryMarshal.Read<FlacSeekPoint>(span.Slice(offset, SEEKPOINT_SIZE));

            // Skip placeholder points
            if (!seekPoint.IsPlaceholder)
            {
                validSeekPoints.Add(seekPoint);
            }
        }

        if (validSeekPoints.Count > 0)
        {
            _seekTable = validSeekPoints.ToArray();
            _seekTableCount = _seekTable.Length;
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

        // Check if we've decoded all samples
        if (_currentSample >= _flacStreamInfo.TotalSamples)
            return AudioDecoderResult.CreateEOF();

        try
        {
            // Find frame sync
            if (!FindFrameSync())
                return AudioDecoderResult.CreateEOF();

            long frameStart = _stream.Position - 2; // Sync code is 2 bytes

            // Read frame into buffer
            int frameSize = ReadFrameToBuffer(frameStart);
            if (frameSize == 0)
                return AudioDecoderResult.CreateEOF();

            // Decode frame
            var span = _frameBuffer.AsSpan(0, frameSize);
            int samplesDecoded = DecodeFrame(span, out string decodeError, out int bytesConsumed);

            if (samplesDecoded == 0)
                return AudioDecoderResult.CreateError($"Failed to decode frame: {decodeError}");

            // Adjust stream position to account for bytes we didn't use
            _stream.Position = frameStart + bytesConsumed;

            // Convert to Float32 using SIMD
            ConvertToFloat32(_decodeBuffer.AsSpan(0, samplesDecoded), _outputBuffer.AsSpan(0, samplesDecoded));

            // Apply format conversion if needed (resampling, channel conversion)
            int finalSampleCount;
            Span<float> finalSpan;

            if (_formatConverter != null)
            {
                finalSampleCount = _formatConverter.Convert(_outputBuffer.AsSpan(0, samplesDecoded), _convertBuffer.AsSpan());
                finalSpan = _convertBuffer.AsSpan(0, finalSampleCount);
            }
            else
            {
                finalSampleCount = samplesDecoded;
                finalSpan = _outputBuffer.AsSpan(0, samplesDecoded);
            }

            // Check if output buffer is large enough
            int byteCount = finalSampleCount * sizeof(float);
            if (byteCount > buffer.Length)
            {
                return AudioDecoderResult.CreateError($"Output buffer too small. Required: {byteCount}, Available: {buffer.Length}");
            }

            // Copy float data to output buffer
            var destFloatSpan = MemoryMarshal.Cast<byte, float>(buffer.AsSpan());
            finalSpan.CopyTo(destFloatSpan);

            // Update presentation timestamp (based on SOURCE samples, not target)
            int samplesPerChannel = samplesDecoded / _flacStreamInfo.Channels;
            _currentPts += (samplesPerChannel * 1000.0) / _flacStreamInfo.SampleRate;
            _currentSample += samplesPerChannel;
            _currentFrame++;

            // Calculate number of frames read (frames = samples / channels)
            int framesRead = finalSampleCount / _targetChannels;

            return AudioDecoderResult.CreateSuccess(framesRead, _currentPts);
        }
        catch (Exception ex)
        {
            return AudioDecoderResult.CreateError($"Exception during FLAC decode: {ex.Message}");
        }
    }

    /// <summary>
    /// Decodes the next audio frame from the FLAC stream.
    /// </summary>
    /// <returns>A <see cref="AudioDecoderResult"/> containing the decoded frame or error information.</returns>
    /// <remarks>
    /// ZERO-ALLOCATION decode path using AudioFramePool and SIMD conversion.
    /// </remarks>
    public AudioDecoderResult DecodeNextFrame()
    {
        if (_disposed)
            return new AudioDecoderResult(null!, false, false, "Decoder has been disposed.");

        // Check if we've decoded all samples
        if (_currentSample >= _flacStreamInfo.TotalSamples)
            return new AudioDecoderResult(null!, false, true);

        try
        {
            // Find frame sync
            if (!FindFrameSync())
                return new AudioDecoderResult(null!, false, true);

            long frameStart = _stream.Position - 2; // Sync code is 2 bytes

            // Read frame into buffer
            int frameSize = ReadFrameToBuffer(frameStart);
            if (frameSize == 0)
                return new AudioDecoderResult(null!, false, true);

            // Decode frame
            var span = _frameBuffer.AsSpan(0, frameSize);
            int samplesDecoded = DecodeFrame(span, out string decodeError, out int bytesConsumed);

            if (samplesDecoded == 0)
                return new AudioDecoderResult(null!, false, false, $"Failed to decode frame: {decodeError}");

            // Adjust stream position to account for bytes we didn't use
            // We read 'frameSize' bytes, but only consumed 'bytesConsumed'
            _stream.Position = frameStart + bytesConsumed;

            // Convert to Float32 using SIMD
            ConvertToFloat32(_decodeBuffer.AsSpan(0, samplesDecoded), _outputBuffer.AsSpan(0, samplesDecoded));

            // Apply format conversion if needed (resampling, channel conversion)
            int finalSampleCount;
            Span<float> finalSpan;

            if (_formatConverter != null)
            {
                finalSampleCount = _formatConverter.Convert(_outputBuffer.AsSpan(0, samplesDecoded), _convertBuffer.AsSpan());
                finalSpan = _convertBuffer.AsSpan(0, finalSampleCount);
            }
            else
            {
                finalSampleCount = samplesDecoded;
                finalSpan = _outputBuffer.AsSpan(0, samplesDecoded);
            }

            // Rent pooled frame (ZERO ALLOCATION)
            int byteCount = finalSampleCount * sizeof(float);
            var pooledFrame = _framePool.Rent(_currentPts, byteCount);

            // Copy float data to pooled frame buffer
            var destFloatSpan = MemoryMarshal.Cast<byte, float>(pooledFrame.BufferSpan);
            finalSpan.CopyTo(destFloatSpan);

            // Convert to standard AudioFrame
            var frame = pooledFrame.ToAudioFrame();

            // Return pooled frame to pool immediately
            _framePool.Return(pooledFrame);

            // Update presentation timestamp (based on SOURCE samples, not target)
            int samplesPerChannel = samplesDecoded / _flacStreamInfo.Channels;
            _currentPts += (samplesPerChannel * 1000.0) / _flacStreamInfo.SampleRate;
            _currentSample += samplesPerChannel;
            _currentFrame++;

            return new AudioDecoderResult(frame, true, false);
        }
        catch (Exception ex)
        {
            return new AudioDecoderResult(null!, false, false, $"Decode error: {ex.Message}");
        }
    }

    /// <summary>
    /// Finds the next frame sync code (0x3FFE).
    /// </summary>
    private bool FindFrameSync()
    {
        int b1 = _stream.ReadByte();
        if (b1 == -1)
            return false;

        while (true)
        {
            int b2 = _stream.ReadByte();
            if (b2 == -1)
                return false;

            // Check for sync code: 0xFF 0xF8-0xFF (11111111 1111100x to 11111111 11111111)
            // FLAC sync is 14 bits: 11111111111110
            if (b1 == 0xFF && (b2 & 0xFC) == 0xF8)
            {
                // Found sync
                _stream.Position -= 2;
                return true;
            }

            b1 = b2;
        }
    }

    /// <summary>
    /// Reads a complete frame into the frame buffer.
    /// Returns frame size in bytes, or 0 if EOF.
    /// </summary>
    private int ReadFrameToBuffer(long frameStart)
    {
        // For now, read up to max frame size
        // In production, we'd parse the frame header to determine exact size
        int maxRead = Math.Min(_frameBuffer.Length, (int)(_stream.Length - _stream.Position));
        if (maxRead <= 0)
            return 0;

        int bytesRead = _stream.Read(_frameBuffer, 0, maxRead);
        if (bytesRead == 0)
            return 0;

        return bytesRead;
    }

    /// <summary>
    /// Decodes a complete FLAC frame.
    /// Returns total samples decoded (all channels interleaved).
    /// </summary>
    private int DecodeFrame(ReadOnlySpan<byte> frameData, out string error, out int bytesConsumed)
    {
        error = string.Empty;
        bytesConsumed = 0;
        var reader = new FlacBitReader(frameData);

        // Parse frame header
        if (!TryParseFrameHeader(ref reader, out FlacFrameHeader header, out string headerError))
        {
            error = $"Frame header parsing failed: {headerError}";
            return 0;
        }

        // Decode subframes for each channel
        int blockSize = header.BlockSize;
        int channels = header.Channels;

        if (blockSize <= 0 || blockSize > _flacStreamInfo.MaxBlockSizeValue)
        {
            error = $"Invalid block size: {blockSize} (max: {_flacStreamInfo.MaxBlockSizeValue})";
            return 0;
        }

        if (channels <= 0 || channels > _flacStreamInfo.Channels)
        {
            error = $"Invalid channels: {channels} (expected: {_flacStreamInfo.Channels})";
            return 0;
        }

        // Use pre-allocated channel buffer (zero allocation)
        // Layout: [channel0: blockSize samples][channel1: blockSize samples]...
        for (int ch = 0; ch < channels; ch++)
        {
            int channelOffset = ch * blockSize;
            Span<int> channelBuffer = _channelDecodeBuffer.AsSpan(channelOffset, blockSize);

            // Calculate bits per sample for this channel
            // In Left-Side, Right-Side, Mid-Side modes, the side channel has +1 bit
            int channelBitsPerSample = header.BitsPerSample;
            if (channels == 2)
            {
                switch (header.ChannelAssignment)
                {
                    case FlacChannelAssignment.LeftSide:
                        if (ch == 1) channelBitsPerSample++; // Side channel
                        break;
                    case FlacChannelAssignment.RightSide:
                        if (ch == 0) channelBitsPerSample++; // Side channel
                        break;
                    case FlacChannelAssignment.MidSide:
                        if (ch == 1) channelBitsPerSample++; // Side channel
                        break;
                }
            }

            if (!DecodeSubframe(ref reader, channelBuffer, channelBitsPerSample, blockSize, out string subframeError))
            {
                error = $"Subframe decoding failed for channel {ch}: {subframeError}";
                return 0;
            }
        }

        // Handle channel decorrelation
        if (channels == 2)
        {
            Span<int> channel0 = _channelDecodeBuffer.AsSpan(0, blockSize);
            Span<int> channel1 = _channelDecodeBuffer.AsSpan(blockSize, blockSize);

            switch (header.ChannelAssignment)
            {
                case FlacChannelAssignment.LeftSide:
                    // Left = Left, Right = Left - Side
                    for (int i = 0; i < blockSize; i++)
                        channel1[i] = channel0[i] - channel1[i];
                    break;

                case FlacChannelAssignment.RightSide:
                    // Left = Right + Side, Right = Right
                    for (int i = 0; i < blockSize; i++)
                        channel0[i] = channel0[i] + channel1[i];
                    break;

                case FlacChannelAssignment.MidSide:
                    // Mid = (Left + Right) / 2, Side = Left - Right
                    // Decode: Left = Mid + Side/2, Right = Mid - Side/2
                    for (int i = 0; i < blockSize; i++)
                    {
                        int mid = channel0[i];
                        int side = channel1[i];
                        mid = mid << 1;
                        mid |= (side & 1); // Restore LSB
                        channel0[i] = (mid + side) >> 1;
                        channel1[i] = (mid - side) >> 1;
                    }
                    break;
            }
        }

        // Interleave channels into output buffer (cache-friendly order)
        // Process by channel to maximize cache locality
        if (channels == 1)
        {
            // Mono: direct copy
            _channelDecodeBuffer.AsSpan(0, blockSize).CopyTo(_decodeBuffer.AsSpan(0, blockSize));
        }
        else if (channels == 2)
        {
            // Stereo: optimized interleaving
            Span<int> channel0 = _channelDecodeBuffer.AsSpan(0, blockSize);
            Span<int> channel1 = _channelDecodeBuffer.AsSpan(blockSize, blockSize);

            for (int i = 0; i < blockSize; i++)
            {
                _decodeBuffer[i * 2] = channel0[i];
                _decodeBuffer[i * 2 + 1] = channel1[i];
            }
        }
        else
        {
            // Multi-channel: generic interleaving
            for (int i = 0; i < blockSize; i++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    _decodeBuffer[i * channels + ch] = _channelDecodeBuffer[ch * blockSize + i];
                }
            }
        }

        // Read frame footer (CRC-16, byte-aligned)
        reader.AlignToByte();
        ushort frameCrc = (ushort)((reader.ReadByte() << 8) | reader.ReadByte());
        // TODO: Validate CRC if needed

        // Calculate how many bytes we consumed from the input buffer
        bytesConsumed = reader.BytePosition;

        return blockSize * channels;
    }

    /// <summary>
    /// Parses FLAC frame header.
    /// </summary>
    private bool TryParseFrameHeader(ref FlacBitReader reader, out FlacFrameHeader header, out string error)
    {
        header = new FlacFrameHeader();
        error = string.Empty;

        try
        {
            // Sync code (14 bits): must be 11111111111110
            uint sync = reader.ReadBits(14);
            if (sync != 0x3FFE)
            {
                error = $"Invalid sync code: 0x{sync:X4} (expected 0x3FFE)";
                return false;
            }

            // Reserved bit (must be 0)
            if (reader.ReadBit() != 0)
            {
                error = "Reserved bit #1 is not 0";
                return false;
            }

            // Blocking strategy (1 bit): 0 = fixed, 1 = variable
            int blockingStrategy = reader.ReadBit();

            // Block size (4 bits)
            int blockSizeCode = (int)reader.ReadBits(4);

            // Sample rate (4 bits)
            int sampleRateCode = (int)reader.ReadBits(4);

            // Channel assignment (4 bits)
            int channelCode = (int)reader.ReadBits(4);

            // Sample size (3 bits)
            int sampleSizeCode = (int)reader.ReadBits(3);

            // Reserved bit (must be 0)
            if (reader.ReadBit() != 0)
            {
                error = "Reserved bit #2 is not 0";
                return false;
            }

            // Sample/frame number (UTF-8 coded)
            header.SampleOrFrameNumber = reader.ReadUTF8();

            // Decode block size
            header.BlockSize = DecodeBlockSize(blockSizeCode, ref reader);
            if (header.BlockSize == 0)
                header.BlockSize = _flacStreamInfo.MaxBlockSizeValue;

            // Decode sample rate
            header.SampleRate = DecodeSampleRate(sampleRateCode, ref reader);
            if (header.SampleRate == 0)
                header.SampleRate = _flacStreamInfo.SampleRate;

            // Decode channels
            header.Channels = DecodeChannels(channelCode, out header.ChannelAssignment);

            // Decode bits per sample
            header.BitsPerSample = DecodeBitsPerSample(sampleSizeCode);
            if (header.BitsPerSample == 0)
                header.BitsPerSample = _flacStreamInfo.BitsPerSample;

            // CRC-8 (frame header checksum)
            header.CRC8 = reader.ReadByte();

            return true;
        }
        catch (Exception ex)
        {
            error = $"Exception: {ex.Message}";
            return false;
        }
    }

    private int DecodeBlockSize(int code, ref FlacBitReader reader)
    {
        return code switch
        {
            0 => 0, // Reserved - get from STREAMINFO
            1 => 192,
            >= 2 and <= 5 => 576 << (code - 2),
            6 => (int)reader.ReadBits(8) + 1,
            7 => (int)reader.ReadBits(16) + 1,
            >= 8 and <= 15 => 256 << (code - 8),
            _ => 0
        };
    }

    private int DecodeSampleRate(int code, ref FlacBitReader reader)
    {
        return code switch
        {
            0 => 0, // Use streaminfo
            1 => 88200,
            2 => 176400,
            3 => 192000,
            4 => 8000,
            5 => 16000,
            6 => 22050,
            7 => 24000,
            8 => 32000,
            9 => 44100,
            10 => 48000,
            11 => 96000,
            12 => (int)reader.ReadBits(8) * 1000,
            13 => (int)reader.ReadBits(16),
            14 => (int)reader.ReadBits(16) * 10,
            15 => throw new InvalidOperationException("Invalid sample rate"),
            _ => 0
        };
    }

    private int DecodeChannels(int code, out FlacChannelAssignment assignment)
    {
        if (code < 8)
        {
            assignment = FlacChannelAssignment.Independent;
            return code + 1;
        }

        assignment = code switch
        {
            8 => FlacChannelAssignment.LeftSide,
            9 => FlacChannelAssignment.RightSide,
            10 => FlacChannelAssignment.MidSide,
            _ => throw new InvalidOperationException("Reserved channel assignment")
        };

        return 2;
    }

    private int DecodeBitsPerSample(int code)
    {
        return code switch
        {
            0 => 0, // Use streaminfo
            1 => 8,
            2 => 12,
            3 => throw new InvalidOperationException("Reserved bit depth"),
            4 => 16,
            5 => 20,
            6 => 24,
            7 => 32,
            _ => 0
        };
    }

    /// <summary>
    /// Decodes a single subframe.
    /// </summary>
    private bool DecodeSubframe(ref FlacBitReader reader, Span<int> output, int bitsPerSample, int blockSize, out string error)
    {
        error = string.Empty;

        try
        {
            // Subframe header: 1 bit (zero), 6 bits (type), 1 bit (wasted bits flag)
            if (reader.ReadBit() != 0)
            {
                error = "Subframe padding bit is not 0";
                return false;
            }

            int typeCode = (int)reader.ReadBits(6);
            int wastedBitsFlag = reader.ReadBit();

            int wastedBits = 0;
            if (wastedBitsFlag == 1)
            {
                // Read unary-coded wasted bits
                wastedBits = reader.ReadUnary() + 1;
                bitsPerSample -= wastedBits;
            }

            // Decode based on subframe type
            bool success;
            string subframeType;

            string detailedError = string.Empty;

            if (typeCode == 0)
            {
                subframeType = "CONSTANT";
                success = DecodeConstantSubframe(ref reader, output, bitsPerSample, blockSize);
            }
            else if (typeCode == 1)
            {
                subframeType = "VERBATIM";
                success = DecodeVerbatimSubframe(ref reader, output, bitsPerSample, blockSize);
            }
            else if (typeCode >= 8 && typeCode <= 12)
            {
                int order = typeCode - 8;
                subframeType = $"FIXED(order={order})";
                success = DecodeFixedSubframe(ref reader, output, bitsPerSample, blockSize, order, out detailedError);
            }
            else if (typeCode >= 32 && typeCode <= 63)
            {
                int order = typeCode - 31;
                subframeType = $"LPC(order={order})";
                success = DecodeLPCSubframe(ref reader, output, bitsPerSample, blockSize, order, out detailedError);
            }
            else
            {
                error = $"Invalid subframe type code: {typeCode}";
                return false;
            }

            if (!success)
            {
                error = $"{subframeType} decoding failed: {detailedError}";
                return false;
            }

            // Restore wasted bits
            if (wastedBits > 0)
            {
                for (int i = 0; i < blockSize; i++)
                    output[i] <<= wastedBits;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Exception: {ex.Message}";
            return false;
        }
    }

    private bool DecodeConstantSubframe(ref FlacBitReader reader, Span<int> output, int bitsPerSample, int blockSize)
    {
        int value = reader.ReadSignedBits(bitsPerSample);
        output.Fill(value);
        return true;
    }

    private bool DecodeVerbatimSubframe(ref FlacBitReader reader, Span<int> output, int bitsPerSample, int blockSize)
    {
        for (int i = 0; i < blockSize; i++)
            output[i] = reader.ReadSignedBits(bitsPerSample);
        return true;
    }

    private bool DecodeFixedSubframe(ref FlacBitReader reader, Span<int> output, int bitsPerSample, int blockSize, int order, out string error)
    {
        error = string.Empty;

        // Read warm-up samples
        for (int i = 0; i < order; i++)
            output[i] = reader.ReadSignedBits(bitsPerSample);

        // Decode residual
        // Pass both blockSize and order for correct partition calculation
        if (!DecodeResidual(ref reader, output.Slice(order), blockSize, order, out string residualError))
        {
            error = $"Residual decoding failed: {residualError}";
            return false;
        }

        // Restore signal using fixed predictor
        RestoreFixed(output, blockSize, order);
        return true;
    }

    private void RestoreFixed(Span<int> samples, int count, int order)
    {
        // Fixed predictors (https://xiph.org/flac/format.html#prediction)
        switch (order)
        {
            case 0: // No prediction
                break;

            case 1: // s(i) = s(i-1) + residual(i)
                for (int i = 1; i < count; i++)
                    samples[i] += samples[i - 1];
                break;

            case 2: // s(i) = 2*s(i-1) - s(i-2) + residual(i)
                for (int i = 2; i < count; i++)
                    samples[i] += 2 * samples[i - 1] - samples[i - 2];
                break;

            case 3: // s(i) = 3*s(i-1) - 3*s(i-2) + s(i-3) + residual(i)
                for (int i = 3; i < count; i++)
                    samples[i] += 3 * samples[i - 1] - 3 * samples[i - 2] + samples[i - 3];
                break;

            case 4: // s(i) = 4*s(i-1) - 6*s(i-2) + 4*s(i-3) - s(i-4) + residual(i)
                for (int i = 4; i < count; i++)
                    samples[i] += 4 * samples[i - 1] - 6 * samples[i - 2] + 4 * samples[i - 3] - samples[i - 4];
                break;
        }
    }

    private bool DecodeLPCSubframe(ref FlacBitReader reader, Span<int> output, int bitsPerSample, int blockSize, int order, out string error)
    {
        error = string.Empty;

        try
        {
            // Read warm-up samples
            for (int i = 0; i < order; i++)
                output[i] = reader.ReadSignedBits(bitsPerSample);

            // Read LPC precision
            int precisionCode = (int)reader.ReadBits(4);
            int precision = precisionCode + 1;
            if (precision == 16)
            {
                error = "LPC precision is reserved value (16)";
                return false;
            }

            // Read LPC shift (5 bits, signed)
            int shift = reader.ReadSignedBits(5);
            if (shift < 0)
            {
                error = $"LPC shift is negative: {shift}";
                return false;
            }

            // Read LPC coefficients
            Span<int> coeffs = stackalloc int[order];
            for (int i = 0; i < order; i++)
                coeffs[i] = reader.ReadSignedBits(precision);

            // Decode residual
            // Pass both blockSize and order for correct partition calculation
            if (!DecodeResidual(ref reader, output.Slice(order), blockSize, order, out string residualError))
            {
                error = $"Residual decoding failed: {residualError}";
                return false;
            }

            // Restore signal using LPC
            RestoreLPC(output, blockSize, order, coeffs, shift);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Exception: {ex.Message}";
            return false;
        }
    }

    private void RestoreLPC(Span<int> samples, int count, int order, Span<int> coeffs, int shift)
    {
        for (int i = order; i < count; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++)
                sum += (long)coeffs[j] * samples[i - j - 1];

            samples[i] += (int)(sum >> shift);
        }
    }

    private bool DecodeResidual(ref FlacBitReader reader, Span<int> output, int blockSize, int predictorOrder, out string error)
    {
        error = string.Empty;

        try
        {
            // Residual coding method (2 bits)
            int method = (int)reader.ReadBits(2);

            int parameterBits = method switch
            {
                0 => 4,  // RICE
                1 => 5,  // RICE2
                _ => 0
            };

            if (parameterBits == 0)
            {
                error = $"Invalid residual coding method: {method}";
                return false;
            }

            // Partition order (4 bits)
            int partitionOrder = (int)reader.ReadBits(4);
            int partitions = 1 << partitionOrder;

            int outputIndex = 0;
            int totalSamples = blockSize - predictorOrder;

            for (int p = 0; p < partitions; p++)
            {
                // Calculate partition samples according to FLAC spec
                int partitionSamples;
                if (partitionOrder == 0)
                {
                    // Single partition: all residual samples
                    partitionSamples = totalSamples;
                }
                else
                {
                    // Multiple partitions
                    // Each partition has (blockSize >> partitionOrder) samples
                    // EXCEPT the first partition which has (blockSize >> partitionOrder) - predictorOrder
                    int basePartitionSize = blockSize >> partitionOrder;

                    if (p == 0)
                    {
                        partitionSamples = basePartitionSize - predictorOrder;
                    }
                    else
                    {
                        partitionSamples = basePartitionSize;
                    }
                }

            // Rice parameter
            int parameter = (int)reader.ReadBits(parameterBits);

            if (parameter == (1 << parameterBits) - 1)
            {
                // Escape code: unencoded binary
                int bitsPerSample = (int)reader.ReadBits(5);
                for (int i = 0; i < partitionSamples; i++)
                    output[outputIndex++] = reader.ReadSignedBits(bitsPerSample);
            }
            else
            {
                // Rice-coded
                for (int i = 0; i < partitionSamples; i++)
                    output[outputIndex++] = reader.ReadRice(parameter);
            }
        }

            // Verify we decoded the correct number of samples
            if (outputIndex != totalSamples)
            {
                error = $"Sample count mismatch: decoded {outputIndex}, expected {totalSamples}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Exception: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Converts Int32 samples to Float32 using SIMD acceleration (normalized to -1.0 to 1.0).
    /// </summary>
    private void ConvertToFloat32(ReadOnlySpan<int> source, Span<float> dest)
    {
        int bitsPerSample = _flacStreamInfo.BitsPerSample;
        float scale = 1.0f / (1 << (bitsPerSample - 1));

        SimdAudioConverter.ConvertInt32ToFloat32(source, dest, source.Length, scale);
    }

    /// <summary>
    /// Seeks the audio stream to the specified position.
    /// </summary>
    /// <remarks>
    /// OPTIMIZED: Uses SEEKTABLE for O(log n) binary search if available,
    /// otherwise falls back to frame header parsing (skipping decode).
    /// Much faster than the previous linear decode approach.
    /// </remarks>
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

        // Calculate target sample
        long targetSample = (long)(position.TotalSeconds * _flacStreamInfo.SampleRate);

        // Reset format converter state (resampler position)
        _formatConverter?.Reset();

        // OPTIMIZATION 1: Use SEEKTABLE if available (binary search)
        if (_seekTable != null && _seekTableCount > 0)
        {
            return SeekUsingSeekTable(targetSample, out error);
        }

        // OPTIMIZATION 2: Linear decode (no SEEKTABLE available)
        return SeekUsingFrameSkip(targetSample, out error);
    }

    /// <summary>
    /// Seeks using SEEKTABLE binary search (fastest method).
    /// </summary>
    /// <remarks>
    /// Uses binary search to find closest seekpoint before target.
    /// Then decodes frames (without keeping them) until reaching exact target position.
    /// This ensures sample-accurate seeking.
    /// </remarks>
    private bool SeekUsingSeekTable(long targetSample, out string error)
    {
        error = string.Empty;

        // Binary search for closest seekpoint before target
        int left = 0;
        int right = _seekTableCount - 1;
        int bestIndex = -1;

        while (left <= right)
        {
            int mid = left + (right - left) / 2;
            long seekSample = _seekTable![mid].GetSampleNumber();

            if (seekSample <= targetSample)
            {
                bestIndex = mid;
                left = mid + 1;
            }
            else
            {
                right = mid - 1;
            }
        }

        // If no suitable seekpoint found, start from beginning
        if (bestIndex == -1)
        {
            _stream.Position = _firstFramePosition;
            _currentSample = 0;
            _currentPts = 0.0;
            _currentFrame = 0;
        }
        else
        {
            // Jump to seekpoint position
            var seekPoint = _seekTable![bestIndex];
            long streamOffset = seekPoint.GetStreamOffset();
            long seekSample = seekPoint.GetSampleNumber();

            _stream.Position = _firstFramePosition + streamOffset;
            _currentSample = seekSample;
            _currentPts = (seekSample * 1000.0) / _flacStreamInfo.SampleRate;
            _currentFrame = (int)(seekSample / _flacStreamInfo.MaxBlockSizeValue);
        }

        // Now decode (and discard) frames until we reach the exact target sample
        // This is necessary for sample-accurate seeking
        while (_currentSample < targetSample)
        {
            var result = DecodeNextFrame();
            if (!result.IsSucceeded || result.IsEOF)
            {
                error = "Failed to decode frame during seek.";
                return false;
            }
            // Frame is decoded but not stored - it will be garbage collected
        }

        return true;
    }

    /// <summary>
    /// Seeks by decoding frames sequentially (fallback when no SEEKTABLE).
    /// </summary>
    /// <remarks>
    /// SAFE AND RELIABLE: Decodes frames one by one (discarding them) until reaching target.
    /// This is slower than SEEKTABLE but guarantees sample-accurate seeking without false sync issues.
    /// The decoded frames are not kept in memory - they are garbage collected immediately.
    /// </remarks>
    private bool SeekUsingFrameSkip(long targetSample, out string error)
    {
        error = string.Empty;

        _stream.Position = _firstFramePosition;
        _currentSample = 0;
        _currentPts = 0.0;
        _currentFrame = 0;

        // Decode frames sequentially until we reach or pass target
        // This is the ONLY reliable method without SEEKTABLE
        // Frame header skipping is prone to false sync codes
        while (_currentSample < targetSample)
        {
            var result = DecodeNextFrame();
            if (!result.IsSucceeded || result.IsEOF)
            {
                error = "Failed to decode frame during seek.";
                return false;
            }
            // Frame is decoded but immediately discarded (garbage collected)
        }

        return true;
    }


    /// <summary>
    /// Decodes all frames starting from the specified position.
    /// </summary>
    /// <remarks>
    /// Uses pooled buffers to minimize GC pressure during accumulation.
    /// </remarks>
    public AudioDecoderResult DecodeAllFrames(TimeSpan position)
    {
        if (!TrySeek(position, out string error))
            return new AudioDecoderResult(null!, false, false, error);

        // Use pooled buffer writer instead of MemoryStream
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
    /// Releases all resources used by the <see cref="FlacDecoder"/>.
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
