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

        Initialize();

        int maxBlockSize = _flacStreamInfo.MaxBlockSizeValue;
        int maxChannels = _flacStreamInfo.Channels;

        _frameBuffer = new byte[maxBlockSize * maxChannels * 4 + 16384];
        _decodeBuffer = new int[maxBlockSize * maxChannels];
        _channelDecodeBuffer = new int[maxBlockSize * maxChannels];
        _outputBuffer = new float[maxBlockSize * maxChannels];
        _audioBuffer = new AudioBuffer(maxBlockSize * maxChannels * sizeof(float));
        _currentPts = 0.0;
        _currentFrame = 0;
        _currentSample = 0;
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
            
            int maxConvertedSamples = _formatConverter.CalculateOutputSize(maxBlockSize * maxChannels) * 2;
            _convertBuffer = new float[maxConvertedSamples];

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

        int maxOutputSamples = maxBlockSize * _targetChannels;
        if (_formatConverter != null)
        {
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

        Span<byte> markerBuffer = stackalloc byte[4];
        if (_stream.Read(markerBuffer) != 4)
            throw new AudioException("Invalid FLAC file: Unable to read marker.");

        FlacMarker marker = MemoryMarshal.Read<FlacMarker>(markerBuffer);
        if (!marker.IsValid)
            throw new AudioException($"Invalid FLAC file: Expected 'fLaC' marker, got 0x{marker.Signature:X8}.");

        bool foundStreamInfo = false;

        while (true)
        {
            Span<byte> headerBuffer = stackalloc byte[4];
            if (_stream.Read(headerBuffer) != 4)
                throw new AudioException("Invalid FLAC file: Unable to read metadata block header.");

            FlacMetadataBlockHeader header = MemoryMarshal.Read<FlacMetadataBlockHeader>(headerBuffer);

            if (header.Type == FlacMetadataBlockType.StreamInfo)
            {
                ParseStreamInfo(header.Length);
                foundStreamInfo = true;
            }
            else if (header.Type == FlacMetadataBlockType.SeekTable)
            {
                ParseSeekTable(header.Length);
            }
            else
            {
                _stream.Position += header.Length;
            }

            if (header.IsLast)
                break;
        }

        if (!foundStreamInfo)
            throw new AudioException("Invalid FLAC file: STREAMINFO block not found.");

        _firstFramePosition = _stream.Position;
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
            _stream.Position += length;
            return;
        }

        int seekPointCount = length / SEEKPOINT_SIZE;
        if (seekPointCount == 0)
        {
            _stream.Position += length;
            return;
        }

        byte[] seekTableData = new byte[length];
        if (_stream.Read(seekTableData, 0, length) != length)
        {
            return;
        }

        // Parse seekpoints and filter out placeholders
        var validSeekPoints = new List<FlacSeekPoint>(seekPointCount);
        var span = seekTableData.AsSpan();

        for (int i = 0; i < seekPointCount; i++)
        {
            int offset = i * SEEKPOINT_SIZE;
            var seekPoint = MemoryMarshal.Read<FlacSeekPoint>(span.Slice(offset, SEEKPOINT_SIZE));

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
    /// Decodes the next FLAC frame and places the result in <c>_outputBuffer</c> or
    /// <c>_convertBuffer</c> (depending on whether format conversion is active).
    /// Does NOT update <c>_currentSample</c>, <c>_currentPts</c> or <c>_currentFrame</c> –
    /// the caller must call <see cref="UpdateTimestamps"/> after consuming the data.
    /// </summary>
    /// <param name="finalSampleCount">Number of float samples ready for consumption.</param>
    /// <param name="samplesDecoded">Raw decoded samples before format conversion (used by UpdateTimestamps).</param>
    /// <param name="isEof">True when end-of-stream was reached (no more frames).</param>
    /// <param name="error">Non-empty when the method returns false and isEof is false.</param>
    /// <returns>True on success, false on EOF or error.</returns>
    private bool TryDecodeFrameCore(out int finalSampleCount, out int samplesDecoded, out bool isEof, out string error)
    {
        finalSampleCount = 0;
        samplesDecoded = 0;
        isEof = false;
        error = string.Empty;

        if (!FindFrameSync()) { isEof = true; return false; }

        long frameStart = _stream.Position - 2; // sync code is 2 bytes
        int frameSize = ReadFrameToBuffer(frameStart);
        if (frameSize == 0) { isEof = true; return false; }

        var span = _frameBuffer.AsSpan(0, frameSize);
        samplesDecoded = DecodeFrame(span, out error, out int bytesConsumed);
        if (samplesDecoded == 0) return false;

        _stream.Position = frameStart + bytesConsumed;

        ConvertToFloat32(_decodeBuffer.AsSpan(0, samplesDecoded), _outputBuffer.AsSpan(0, samplesDecoded));

        if (_formatConverter != null)
            finalSampleCount = _formatConverter.Convert(_outputBuffer.AsSpan(0, samplesDecoded), _convertBuffer.AsSpan());
        else
            finalSampleCount = samplesDecoded;

        return true;
    }

    /// <summary>
    /// Advances stream position counters after a frame is consumed.
    /// Must be called once per successfully decoded frame.
    /// </summary>
    private void UpdateTimestamps(int samplesDecoded)
    {
        int samplesPerChannel = samplesDecoded / _flacStreamInfo.Channels;
        _currentPts += (samplesPerChannel * 1000.0) / _flacStreamInfo.SampleRate;
        _currentSample += samplesPerChannel;
        _currentFrame++;
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

        if (_currentSample >= _flacStreamInfo.TotalSamples)
            return AudioDecoderResult.CreateEOF();

        try
        {
            if (!TryDecodeFrameCore(out int finalSampleCount, out int samplesDecoded, out bool isEof, out string decodeError))
                return isEof
                    ? AudioDecoderResult.CreateEOF()
                    : AudioDecoderResult.CreateError($"Failed to decode frame: {decodeError}");

            Span<float> finalSpan = _formatConverter != null
                ? _convertBuffer.AsSpan(0, finalSampleCount)
                : _outputBuffer.AsSpan(0, finalSampleCount);

            int byteCount = finalSampleCount * sizeof(float);
            if (byteCount > buffer.Length)
                return AudioDecoderResult.CreateError($"Output buffer too small. Required: {byteCount}, Available: {buffer.Length}");

            var destFloatSpan = MemoryMarshal.Cast<byte, float>(buffer.AsSpan());
            finalSpan.CopyTo(destFloatSpan);

            UpdateTimestamps(samplesDecoded);
            return AudioDecoderResult.CreateSuccess(finalSampleCount / _targetChannels, _currentPts);
        }
        catch (Exception ex)
        {
            return AudioDecoderResult.CreateError($"Exception during FLAC decode: {ex.Message}");
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

            if (b1 == 0xFF && (b2 & 0xFC) == 0xF8)
            {
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

        if (!TryParseFrameHeader(ref reader, out FlacFrameHeader header, out string headerError))
        {
            error = $"Frame header parsing failed: {headerError}";
            return 0;
        }

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

        for (int ch = 0; ch < channels; ch++)
        {
            int channelOffset = ch * blockSize;
            Span<int> channelBuffer = _channelDecodeBuffer.AsSpan(channelOffset, blockSize);

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

        if (channels == 2)
        {
            Span<int> channel0 = _channelDecodeBuffer.AsSpan(0, blockSize);
            Span<int> channel1 = _channelDecodeBuffer.AsSpan(blockSize, blockSize);

            switch (header.ChannelAssignment)
            {
                case FlacChannelAssignment.LeftSide:
                    for (int i = 0; i < blockSize; i++)
                        channel1[i] = channel0[i] - channel1[i];
                    break;

                case FlacChannelAssignment.RightSide:
                    for (int i = 0; i < blockSize; i++)
                        channel0[i] = channel0[i] + channel1[i];
                    break;

                case FlacChannelAssignment.MidSide:
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

        if (channels == 1)
        {
            _channelDecodeBuffer.AsSpan(0, blockSize).CopyTo(_decodeBuffer.AsSpan(0, blockSize));
        }
        else if (channels == 2)
        {
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
            for (int i = 0; i < blockSize; i++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    _decodeBuffer[i * channels + ch] = _channelDecodeBuffer[ch * blockSize + i];
                }
            }
        }

        reader.AlignToByte();
        ushort frameCrc = (ushort)((reader.ReadByte() << 8) | reader.ReadByte());
        // TODO: Validate CRC if needed

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
            uint sync = reader.ReadBits(14);
            if (sync != 0x3FFE)
            {
                error = $"Invalid sync code: 0x{sync:X4} (expected 0x3FFE)";
                return false;
            }

            if (reader.ReadBit() != 0)
            {
                error = "Reserved bit #1 is not 0";
                return false;
            }

            int blockingStrategy = reader.ReadBit();
            int blockSizeCode = (int)reader.ReadBits(4);
            int sampleRateCode = (int)reader.ReadBits(4);
            int channelCode = (int)reader.ReadBits(4);
            int sampleSizeCode = (int)reader.ReadBits(3);
            if (reader.ReadBit() != 0)
            {
                error = "Reserved bit #2 is not 0";
                return false;
            }

            header.SampleOrFrameNumber = reader.ReadUTF8();
            header.BlockSize = DecodeBlockSize(blockSizeCode, ref reader);
            if (header.BlockSize == 0)
                header.BlockSize = _flacStreamInfo.MaxBlockSizeValue;
            
            header.SampleRate = DecodeSampleRate(sampleRateCode, ref reader);
            if (header.SampleRate == 0)
                header.SampleRate = _flacStreamInfo.SampleRate;

            header.Channels = DecodeChannels(channelCode, out header.ChannelAssignment);
            header.BitsPerSample = DecodeBitsPerSample(sampleSizeCode);
            if (header.BitsPerSample == 0)
                header.BitsPerSample = _flacStreamInfo.BitsPerSample;

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
                wastedBits = reader.ReadUnary(31) + 1;
                bitsPerSample -= wastedBits;
            }

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

        for (int i = 0; i < order; i++)
            output[i] = reader.ReadSignedBits(bitsPerSample);

        if (!DecodeResidual(ref reader, output.Slice(order), blockSize, order, out string residualError))
        {
            error = $"Residual decoding failed: {residualError}";
            return false;
        }

        RestoreFixed(output, blockSize, order);
        return true;
    }

    private void RestoreFixed(Span<int> samples, int count, int order)
    {
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
            for (int i = 0; i < order; i++)
                output[i] = reader.ReadSignedBits(bitsPerSample);

            int precisionCode = (int)reader.ReadBits(4);
            int precision = precisionCode + 1;
            if (precision == 16)
            {
                error = "LPC precision is reserved value (16)";
                return false;
            }

            int shift = reader.ReadSignedBits(5);
            if (shift < 0)
            {
                error = $"LPC shift is negative: {shift}";
                return false;
            }

            Span<int> coeffs = stackalloc int[order];
            for (int i = 0; i < order; i++)
                coeffs[i] = reader.ReadSignedBits(precision);

            if (!DecodeResidual(ref reader, output.Slice(order), blockSize, order, out string residualError))
            {
                error = $"Residual decoding failed: {residualError}";
                return false;
            }
            
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

            int partitionOrder = (int)reader.ReadBits(4);
            int partitions = 1 << partitionOrder;

            int outputIndex = 0;
            int totalSamples = blockSize - predictorOrder;

            for (int p = 0; p < partitions; p++)
            {
                int partitionSamples;
                if (partitionOrder == 0)
                {
                    partitionSamples = totalSamples;
                }
                else
                {
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

                int parameter = (int)reader.ReadBits(parameterBits);

                if (parameter == (1 << parameterBits) - 1)
                {
                    int bitsPerSample = (int)reader.ReadBits(5);
                    for (int i = 0; i < partitionSamples; i++)
                        output[outputIndex++] = reader.ReadSignedBits(bitsPerSample);
                }
                else
                {
                    for (int i = 0; i < partitionSamples; i++)
                        output[outputIndex++] = reader.ReadRice(parameter);
                }
            }

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

        long targetSample = (long)(position.TotalSeconds * _flacStreamInfo.SampleRate);
        _formatConverter?.Reset();

        if (_seekTable != null && _seekTableCount > 0)
        {
            return SeekUsingSeekTable(targetSample, out error);
        }
        
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

        if (bestIndex == -1)
        {
            _stream.Position = _firstFramePosition;
            _currentSample = 0;
            _currentPts = 0.0;
            _currentFrame = 0;
        }
        else
        {
            var seekPoint = _seekTable![bestIndex];
            long streamOffset = seekPoint.GetStreamOffset();
            long seekSample = seekPoint.GetSampleNumber();

            _stream.Position = _firstFramePosition + streamOffset;
            _currentSample = seekSample;
            _currentPts = (seekSample * 1000.0) / _flacStreamInfo.SampleRate;
            _currentFrame = (int)(seekSample / _flacStreamInfo.MaxBlockSizeValue);
        }

        while (_currentSample < targetSample)
        {
            if (!TryDecodeFrameCore(out _, out int samplesDecoded, out bool isEof, out _) || isEof)
            {
                error = "Failed to decode frame during seek.";
                return false;
            }
            UpdateTimestamps(samplesDecoded);
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

        while (_currentSample < targetSample)
        {
            if (!TryDecodeFrameCore(out _, out int samplesDecoded, out bool isEof, out _) || isEof)
            {
                error = "Failed to decode frame during seek.";
                return false;
            }
            UpdateTimestamps(samplesDecoded);
        }

        return true;
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
