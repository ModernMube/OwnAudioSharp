using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using Ownaudio.Core.Common;
using Ownaudio.Decoders;
using Ownaudio.Decoders.Mp3;
using Android.Media;
using AndroidLog = global::Android.Util.Log;
using Ownaudio.Android.Common;

namespace Ownaudio.Android.Decoders;

/// <summary>
/// Android MediaCodec-based MP3 decoder implementation.
/// Uses Android's native MediaCodec API for hardware-accelerated MP3 decoding with zero external dependencies.
/// </summary>
/// <remarks>
/// This decoder:
/// - Uses MediaCodec and MediaExtractor APIs available on Android 4.1+ (API level 16)
/// - Outputs Float32 PCM samples, interleaved
/// - Pre-allocates all buffers for zero-allocation decode path
/// - Thread-safe for construction, but not for concurrent decode calls
/// - Supports seeking by sample position
/// - Automatically handles format conversion (MP3 → Float32 PCM)
/// - BUFFERING: Accumulates multiple small MediaCodec frames into larger frames (matches WAV/FLAC behavior)
///
/// PTS (Presentation Timestamp) Handling:
/// - Uses sample-accurate PTS calculation based on DECODED DATA SIZE
/// - Frame duration = (samplesPerChannel * 1000.0) / sampleRate
/// - PTS incremented by frame duration (_currentPts += duration)
/// - Consistent with other platform decoders for multi-file sync
/// - Seek sets PTS to seek position for correct multi-file playback
///
/// GC Optimization:
/// - Pre-allocated decode buffers (4096 samples default)
/// - Pinned memory for buffer operations (GCHandle)
/// - Span&lt;T&gt; usage for zero-copy operations
/// - Immediate release of MediaCodec buffers
///
/// Required Android APIs:
/// - android.media.MediaCodec (API 16+)
/// - android.media.MediaExtractor (API 16+)
/// - android.media.MediaFormat (API 16+)
/// </remarks>
public sealed class MediaCodecMp3Decoder : IPlatformMp3Decoder
{
    private const int DefaultSamplesPerFrame = 4096;
    private const long TimeoutUs = 0; // Non-blocking dequeue - burst mode for parallel decoders
    private const int CodecPrefillFrames = 10; // Pre-fill codec with this many input frames

    // Ring Buffer for smoothing MediaCodec's burst-mode output
    // INCREASED for 4-5 parallel decoders: 131072 = ~3 seconds stereo 44.1kHz
    private const int InternalBufferCapacity = 131072; // Power of 2 for ring buffer optimization

    // ArrayPool for zero-allocation float buffer management
    private static readonly ArrayPool<float> FloatBufferPool = ArrayPool<float>.Shared;

    // MediaCodec components
    private MediaCodec? _codec;
    private MediaExtractor? _extractor;
    private MediaFormat? _format;

    // Internal ring buffer for burst-mode smoothing (Producer: DrainCodec, Consumer: DecodeFrame)
    private LockFreeRingBuffer<float>? _internalBuffer;
    private double _lastDrainedPts; // PTS of last drained frame (for calculating output PTS)

    // Stream state
    private AudioStreamInfo _streamInfo;
    private double _currentPts; // in milliseconds

    // Source format (original MP3 format)
    private int _sourceChannels;
    private int _sourceSampleRate;

    // Client format (output format - Float32 PCM)
    private int _clientChannels;
    private int _clientSampleRate;

    // Pre-allocated buffers for zero-allocation decode
    private readonly byte[] _decodeBuffer;
    private readonly GCHandle _bufferHandle;
    private readonly IntPtr _bufferPtr;

    // Resampling support (for sample rate conversion)
    private AudioResampler? _resampler;
    private float[]? _resampleInputBuffer;  // PCM Float32 at source rate (pooled)
    private float[]? _resampleOutputBuffer; // PCM Float32 at target rate (pooled)
    private int _resampleInputBufferSize;   // Actual size rented from pool
    private int _resampleOutputBufferSize;  // Actual size rented from pool

    private bool _disposed;
    private bool _isEOF;
    private bool _inputEOS;
    private long _durationUs; // Duration in microseconds

    // Debug: Track decode statistics
    private int _totalFramesDecoded;
    private int _totalSamplesDecoded;

    // File path or stream
    private string? _filePath;
    private System.IO.Stream? _stream;
    private bool _ownsStream;

    /// <summary>
    /// Default constructor (required for reflection-based creation).
    /// </summary>
    public MediaCodecMp3Decoder()
    {
        _codec = null;
        _extractor = null;
        _format = null;

        // Pre-allocate decode buffer (Float32 = 4 bytes per sample)
        int bufferSize = DefaultSamplesPerFrame * 2 * sizeof(float); // Stereo max
        _decodeBuffer = new byte[bufferSize];
        _bufferHandle = GCHandle.Alloc(_decodeBuffer, GCHandleType.Pinned);
        _bufferPtr = _bufferHandle.AddrOfPinnedObject();
    }

    /// <inheritdoc/>
    public void InitializeFromFile(string filePath, int targetSampleRate, int targetChannels)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"MP3 file not found: {filePath}", filePath);

        try
        {
            _filePath = filePath;

            // 1. Create MediaExtractor and set data source
            _extractor = new MediaExtractor();
            if (_extractor == null)
                throw new AudioException(
                    AudioErrorCategory.PlatformAPI,
                    "Failed to create MediaExtractor");

            _extractor.SetDataSource(filePath);

            // 2. Find audio track
            int trackIndex = -1;
            int trackCount = _extractor.TrackCount;

            for (int i = 0; i < trackCount; i++)
            {
                MediaFormat? format = _extractor.GetTrackFormat(i);
                if (format != null)
                {
                    string? mime = format.GetString(MediaFormat.KeyMime);

                    if (mime != null && mime.StartsWith("audio/"))
                    {
                        trackIndex = i;
                        _format = format;
                        break;
                    }
                }
            }

            if (trackIndex < 0 || _format == null)
                throw new AudioException(
                    AudioErrorCategory.PlatformAPI,
                    $"No audio track found in MP3 file. Track count: {trackCount}");

            // 3. Select audio track
            _extractor.SelectTrack(trackIndex);

            // 4. Get source format information
            _sourceSampleRate = _format.GetInteger(MediaFormat.KeySampleRate);
            _sourceChannels = _format.GetInteger(MediaFormat.KeyChannelCount);

            // 5. Determine output format
            _clientSampleRate = targetSampleRate > 0 ? targetSampleRate : _sourceSampleRate;
            _clientChannels = targetChannels > 0 ? targetChannels : _sourceChannels;

            // 5a. Initialize resampler if sample rates differ
            if (_sourceSampleRate != _clientSampleRate)
            {
                //FileLogger.Info("MediaCodecMp3",
                //    $"Sample rate mismatch. Creating resampler: {_sourceSampleRate} Hz → {_clientSampleRate} Hz");

                _resampler = new AudioResampler(
                    sourceRate: _sourceSampleRate,
                    targetRate: _clientSampleRate,
                    channels: _sourceChannels,
                    maxFrameSize: DefaultSamplesPerFrame);

                // Allocate resampling buffers from pool
                _resampleInputBufferSize = DefaultSamplesPerFrame * _sourceChannels;
                _resampleInputBuffer = FloatBufferPool.Rent(_resampleInputBufferSize);

                int outputSize = _resampler.CalculateOutputSize(_resampleInputBufferSize);
                _resampleOutputBufferSize = outputSize;
                _resampleOutputBuffer = FloatBufferPool.Rent(_resampleOutputBufferSize);
            }

            // 6. Get duration
            if (_format.ContainsKey(MediaFormat.KeyDuration))
            {
                _durationUs = _format.GetLong(MediaFormat.KeyDuration);
            }
            else
            {
                _durationUs = 0;
            }

            TimeSpan duration = TimeSpan.FromMilliseconds(_durationUs / 1000.0);

            // 7. Create MediaCodec
            string? mimeType = _format.GetString(MediaFormat.KeyMime);

            if (string.IsNullOrEmpty(mimeType))
                throw new AudioException(
                    AudioErrorCategory.PlatformAPI,
                    "Could not determine MIME type from audio track");

            _codec = MediaCodec.CreateDecoderByType(mimeType);
            if (_codec == null)
                throw new AudioException(
                    AudioErrorCategory.PlatformAPI,
                    $"Failed to create MediaCodec for MIME type: {mimeType}");

            // 8. Configure MediaCodec
            _codec.Configure(_format, null, null, MediaCodecConfigFlags.None);
            _codec.Start();

            // 8a. Query actual output format from MediaCodec
            MediaFormat? outputFormat = _codec.OutputFormat;
            if (outputFormat != null)
            {
                int actualOutputSampleRate = outputFormat.ContainsKey(MediaFormat.KeySampleRate)
                    ? outputFormat.GetInteger(MediaFormat.KeySampleRate)
                    : _sourceSampleRate;

                int actualOutputChannels = outputFormat.ContainsKey(MediaFormat.KeyChannelCount)
                    ? outputFormat.GetInteger(MediaFormat.KeyChannelCount)
                    : _sourceChannels;

                //FileLogger.Info("MediaCodecMp3",
                //    $"MediaCodec output format: {actualOutputSampleRate} Hz, {actualOutputChannels} ch");

                // Update source parameters to match actual codec output
                if (actualOutputSampleRate != _sourceSampleRate || actualOutputChannels != _sourceChannels)
                {
                    //FileLogger.Warn("MediaCodecMp3",
                    //    $"MediaCodec output differs from source! " +
                    //    $"Source: {_sourceSampleRate}Hz/{_sourceChannels}ch, " +
                    //    $"Output: {actualOutputSampleRate}Hz/{actualOutputChannels}ch");

                    _sourceSampleRate = actualOutputSampleRate;
                    _sourceChannels = actualOutputChannels;

                    // Recreate resampler if needed
                    if (_sourceSampleRate != _clientSampleRate && _resampler != null)
                    {
                        // Return old buffers to pool
                        if (_resampleInputBuffer != null)
                        {
                            FloatBufferPool.Return(_resampleInputBuffer);
                            _resampleInputBuffer = null;
                        }
                        if (_resampleOutputBuffer != null)
                        {
                            FloatBufferPool.Return(_resampleOutputBuffer);
                            _resampleOutputBuffer = null;
                        }

                        _resampler = new AudioResampler(
                            sourceRate: _sourceSampleRate,
                            targetRate: _clientSampleRate,
                            channels: _sourceChannels,
                            maxFrameSize: DefaultSamplesPerFrame);

                        // Rent new buffers from pool
                        _resampleInputBufferSize = DefaultSamplesPerFrame * _sourceChannels;
                        _resampleInputBuffer = FloatBufferPool.Rent(_resampleInputBufferSize);

                        int outputSize = _resampler.CalculateOutputSize(_resampleInputBufferSize);
                        _resampleOutputBufferSize = outputSize;
                        _resampleOutputBuffer = FloatBufferPool.Rent(_resampleOutputBufferSize);
                    }
                }
            }

            // 9. Create AudioStreamInfo (use CLIENT format - output after conversion)
            _streamInfo = new AudioStreamInfo(
                channels: _clientChannels,
                sampleRate: _clientSampleRate,
                duration: duration);

            _currentPts = 0.0;
            _lastDrainedPts = 0.0;
            _isEOF = false;
            _inputEOS = false;

            // Initialize internal ring buffer for burst-mode smoothing
            _internalBuffer = new LockFreeRingBuffer<float>(InternalBufferCapacity);

            // Pre-fill codec with input frames to ensure smooth playback start
            PrefillCodec();
        }
        catch (AudioException)
        {
            Dispose();
            throw;
        }
        catch (Exception ex)
        {
            //FileLogger.Error("MediaCodecMp3", $"Init error: {ex.GetType().Name}: {ex.Message}");
            Dispose();
            throw new AudioException(
                AudioErrorCategory.PlatformAPI,
                $"Failed to initialize MP3 decoder: {ex.Message}",
                ex);
        }
    }

    /// <inheritdoc/>
    public void InitializeFromStream(System.IO.Stream stream, int targetSampleRate, int targetChannels)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        if (!stream.CanRead)
            throw new ArgumentException("Stream must be readable", nameof(stream));

        try
        {
            _stream = stream;
            _ownsStream = false;

            // Create temporary file (MediaExtractor limitation)
            string tempPath = Path.Combine(Path.GetTempPath(), $"ownaudio_temp_{Guid.NewGuid()}.mp3");

            using (System.IO.FileStream fs = File.Create(tempPath))
            {
                stream.CopyTo(fs);
            }

            // Initialize from temporary file
            InitializeFromFile(tempPath, targetSampleRate, targetChannels);
            _filePath = tempPath;
        }
        catch (Exception ex) when (!(ex is AudioException))
        {
            Dispose();
            throw new AudioException(
                AudioErrorCategory.PlatformAPI,
                $"Failed to initialize MP3 decoder from stream: {ex.Message}",
                ex);
        }
    }

    /// <inheritdoc/>
    public AudioStreamInfo GetStreamInfo()
    {
        return _streamInfo;
    }

    /// <inheritdoc/>
    public int DecodeFrame(Span<byte> outputBuffer, out double pts)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MediaCodecMp3Decoder));

        if (_codec == null || _extractor == null || _internalBuffer == null)
        {
            pts = 0.0;
            return -1;
        }

        try
        {
            // NEW STRATEGY: Two-stage buffering
            // 1. Producer (DrainCodec): Extract ALL available frames from MediaCodec to ring buffer (burst mode)
            // 2. Consumer (this method): Read smoothly from ring buffer

            if (_isEOF && _internalBuffer.Available == 0)
            {
                pts = _currentPts;
                return 0; // EOF - no more data in buffer
            }

            // STEP 1: Demand-driven buffer fill strategy
            // Goal: Keep buffer between min (one frame) and max (50% capacity)
            // This prevents both underruns AND overflows with multiple parallel decoders
            int floatSamplesNeeded = outputBuffer.Length / sizeof(float);
            const int MaxBufferThreshold = InternalBufferCapacity / 2; // Don't overfill (max 50%)

            // Drain if buffer is below max threshold (demand-driven)
            // IMPORTANT: Non-blocking! Just try to extract what MediaCodec has ready
            if (_internalBuffer.Available < MaxBufferThreshold && !_isEOF && !_inputEOS)
            {
                // Feed input to codec (non-blocking)
                FeedCodecInput();

                // Drain from codec to internal buffer (burst mode, non-blocking)
                DrainCodecToInternalBuffer();
            }

            // STEP 2: Read from internal buffer (Consumer)
            pts = _currentPts;

            if (_internalBuffer.Available == 0)
            {
                // No data available
                if (_isEOF)
                {
                    return 0; // EOF
                }
                else
                {
                    // Pipeline starvation - decoder is still processing
                    // Return 0 to signal "no data yet" (caller will try again)
                    return 0;
                }
            }

            // Read whatever we have available (preferably full frame, but partial is OK)
            // The ring buffer smooths out MediaCodec's burst behavior
            int floatSamplesToRead = Math.Min(floatSamplesNeeded, _internalBuffer.Available);

            // Read from ring buffer
            Span<float> floatOutput = MemoryMarshal.Cast<byte, float>(outputBuffer);
            int samplesRead = _internalBuffer.Read(floatOutput.Slice(0, floatSamplesToRead));

            // Update PTS (output PTS, not the drained PTS)
            pts = _currentPts;

            // Calculate frame duration based on samples consumed
            int samplesPerChannel = samplesRead / _clientChannels;
            double frameDurationMs = (samplesPerChannel * 1000.0) / _clientSampleRate;
            _currentPts += frameDurationMs;

            int bytesReturned = samplesRead * sizeof(float);

            return bytesReturned;
        }
        catch (Exception ex)
        {
            //FileLogger.Error("MediaCodecMp3", $"Decode error: {ex.Message}");
            pts = 0.0;
            return -1;
        }
    }

    /// <summary>
    /// Feeds input data to MediaCodec (fills input buffers).
    /// Called by DecodeFrame to keep the codec pipeline full.
    /// </summary>
    private void FeedCodecInput()
    {
        if (_codec == null || _extractor == null || _inputEOS)
            return;

        try
        {
            // Try to feed multiple input frames to keep pipeline saturated
            for (int i = 0; i < 5; i++)
            {
                int inputBufferIndex = _codec.DequeueInputBuffer(0); // Non-blocking
                if (inputBufferIndex < 0)
                    break; // No more input buffers available

                Java.Nio.ByteBuffer? inputBuffer = _codec.GetInputBuffer(inputBufferIndex);
                if (inputBuffer != null)
                {
                    int sampleSize = _extractor.ReadSampleData(inputBuffer, 0);

                    if (sampleSize < 0)
                    {
                        _codec.QueueInputBuffer(inputBufferIndex, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                        _inputEOS = true;
                        break;
                    }
                    else
                    {
                        long presentationTimeUs = _extractor.SampleTime;
                        _codec.QueueInputBuffer(inputBufferIndex, 0, sampleSize, presentationTimeUs, MediaCodecBufferFlags.None);
                        _extractor.Advance();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            //FileLogger.Warn("MediaCodecMp3", $"FeedInput warning: {ex.Message}");
        }
    }

    /// <summary>
    /// Converts PCM16 samples to Float32 samples using SIMD acceleration.
    /// Uses the same conversion method as WAV and FLAC decoders for consistency.
    /// </summary>
    private void ConvertPcm16ToFloat32(ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<float> float32 = MemoryMarshal.Cast<byte, float>(output);
        int sampleCount = Math.Min(input.Length / sizeof(short), float32.Length);

        SimdAudioConverter.ConvertPCM16ToFloat32(input, float32, sampleCount);
    }

    /// <inheritdoc/>
    public bool Seek(long samplePosition)
    {
        if (_disposed)
            return false;

        if (_extractor == null || _codec == null)
            return false;

        try
        {
            // Convert sample position to time (microseconds)
            double timeMs = (samplePosition * 1000.0) / _clientSampleRate;
            long timeUs = (long)(timeMs * 1000.0);

            // Seek extractor
            _extractor.SeekTo(timeUs, MediaExtractorSeekTo.ClosestSync);

            // Flush codec
            _codec.Flush();

            // Reset resampler
            _resampler?.Reset();

            // Clear internal ring buffer
            _internalBuffer?.Clear();

            // Reset state
            _currentPts = timeMs;
            _lastDrainedPts = timeMs;
            _isEOF = false;
            _inputEOS = false;
            _totalFramesDecoded = 0;
            _totalSamplesDecoded = 0;

            // Pre-fill codec after seek
            PrefillCodec();

            //FileLogger.Info("MediaCodecMp3", $"Seek completed to {timeMs:F2}ms");

            return true;
        }
        catch (Exception ex)
        {
            //FileLogger.Error("MediaCodecMp3", $"Seek error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Drains decoded frames from MediaCodec to internal ring buffer (BURST MODE).
    /// This method extracts ALL available decoded frames from MediaCodec in a single call,
    /// smoothing out the burst-mode behavior for continuous playback.
    /// </summary>
    /// <remarks>
    /// MediaCodec decoders (especially hardware) work in bursts: they decode multiple frames
    /// at once, then go idle. This method drains all available output to the internal buffer,
    /// which DecodeFrame() then reads from smoothly.
    ///
    /// Uses ZERO timeout (non-blocking) to prevent stalls when multiple decoders run in parallel.
    /// </remarks>
    private void DrainCodecToInternalBuffer()
    {
        if (_codec == null || _internalBuffer == null)
            return;

        MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();
        int framesExtracted = 0;

        // BURST MODE: Extract ALL available frames from MediaCodec in one go
        // Keep draining until we get TryAgainLater (do NOT check buffer fullness here!)
        // IMPORTANT: We must drain MediaCodec even if our buffer is full to prevent MediaCodec stalls
        int maxDrainIterations = 50; // Safety limit to prevent infinite loops
        int iterations = 0;

        while (iterations < maxDrainIterations)
        {
            // CRITICAL: Timeout = 0 (non-blocking) - don't wait if no data available
            int outputBufferIndex = _codec.DequeueOutputBuffer(bufferInfo, TimeoutUs);
            iterations++;

            if (outputBufferIndex >= 0)
            {
                // Process this output buffer
                Java.Nio.ByteBuffer? outputBufferNative = _codec.GetOutputBuffer(outputBufferIndex);

                if (outputBufferNative != null && bufferInfo.Size > 0)
                {
                    int bytesDecoded = bufferInfo.Size;

                    // Copy from MediaCodec buffer to managed buffer
                    // CRITICAL PERFORMANCE FIX: Use BULK copy instead of byte-by-byte!
                    // The byte-by-byte loop was causing 400ms delays per frame!
                    outputBufferNative.Position(bufferInfo.Offset);
                    outputBufferNative.Get(_decodeBuffer, 0, Math.Min(bytesDecoded, _decodeBuffer.Length));

                    // Convert PCM16 to Float32 and write to ring buffer
                    int sampleCount = bytesDecoded / sizeof(short);

                    // Handle resampling
                    if (_resampler != null && _resampler.IsResamplingNeeded &&
                        _resampleInputBuffer != null && _resampleOutputBuffer != null)
                    {
                        // Convert PCM16 to Float32 at source rate
                        int sourceFloatSamples = Math.Min(sampleCount, _resampleInputBuffer.Length);
                        ConvertPcm16ToFloat32(
                            _decodeBuffer.AsSpan(0, sourceFloatSamples * sizeof(short)),
                            MemoryMarshal.Cast<float, byte>(_resampleInputBuffer.AsSpan(0, sourceFloatSamples)));

                        // Resample
                        int resampledSampleCount = _resampler.Resample(
                            _resampleInputBuffer.AsSpan(0, sourceFloatSamples),
                            _resampleOutputBuffer.AsSpan());

                        // Check if buffer has enough space
                        if (_internalBuffer.WritableCount < resampledSampleCount)
                        {
                            // Buffer full - release this frame and continue draining
                            _codec.ReleaseOutputBuffer(outputBufferIndex, false);
                            //FileLogger.Warn("MediaCodecMp3",
                            //    $"Ring buffer overflow (resampled)! Dropping {resampledSampleCount} samples. Available: {_internalBuffer.WritableCount}");
                            continue;
                        }

                        // Write to ring buffer
                        int samplesWritten = _internalBuffer.Write(_resampleOutputBuffer.AsSpan(0, resampledSampleCount));

                        // Update PTS (based on SOURCE samples, not resampled)
                        int sourceSamplesPerChannel = sourceFloatSamples / _sourceChannels;
                        double frameDurationMs = (sourceSamplesPerChannel * 1000.0) / _sourceSampleRate;
                        _lastDrainedPts += frameDurationMs;

                        framesExtracted++;
                    }
                    else
                    {
                        // No resampling - direct conversion
                        int floatSamples = sampleCount;

                        // Check if buffer has enough space, otherwise skip this frame (buffer overflow protection)
                        if (_internalBuffer.WritableCount < floatSamples)
                        {
                            // Buffer full - release this frame and continue draining
                            _codec.ReleaseOutputBuffer(outputBufferIndex, false);
                            //FileLogger.Warn("MediaCodecMp3",
                            //    $"Ring buffer overflow! Dropping {floatSamples} samples. Available: {_internalBuffer.WritableCount}");

                            // Continue to next frame (don't break - we must keep draining!)
                            continue;
                        }

                        // Convert PCM16 to Float32 using ArrayPool for zero-allocation
                        // Rent buffer from pool (pool may return larger buffer than requested)
                        float[] tempFloatBuffer = FloatBufferPool.Rent(floatSamples);

                        try
                        {
                            ConvertPcm16ToFloat32(
                                _decodeBuffer.AsSpan(0, floatSamples * sizeof(short)),
                                MemoryMarshal.Cast<float, byte>(tempFloatBuffer.AsSpan(0, floatSamples)));

                            // Write to ring buffer (only the used portion)
                            int samplesWritten = _internalBuffer.Write(tempFloatBuffer.AsSpan(0, floatSamples));
                        }
                        finally
                        {
                            // CRITICAL: Always return buffer to pool
                            FloatBufferPool.Return(tempFloatBuffer);
                        }

                        // Update PTS
                        int samplesPerChannel = floatSamples / _clientChannels;
                        double frameDurationMs = (samplesPerChannel * 1000.0) / _clientSampleRate;
                        _lastDrainedPts += frameDurationMs;

                        framesExtracted++;
                    }
                }

                _codec.ReleaseOutputBuffer(outputBufferIndex, false);

                // Check for end of stream
                if ((bufferInfo.Flags & MediaCodecBufferFlags.EndOfStream) != 0)
                {
                    _isEOF = true;
                    break;
                }
            }
            else if (outputBufferIndex == (int)MediaCodecInfoState.TryAgainLater)
            {
                // No more data available right now - exit burst loop
                break;
            }
            else if (outputBufferIndex == (int)MediaCodecInfoState.OutputFormatChanged)
            {
                // Handle format change
                MediaFormat? newFormat = _codec.OutputFormat;
                if (newFormat != null)
                {
                    int newSampleRate = newFormat.ContainsKey(MediaFormat.KeySampleRate)
                        ? newFormat.GetInteger(MediaFormat.KeySampleRate)
                        : _sourceSampleRate;

                    int newChannels = newFormat.ContainsKey(MediaFormat.KeyChannelCount)
                        ? newFormat.GetInteger(MediaFormat.KeyChannelCount)
                        : _sourceChannels;

                    if (newSampleRate != _sourceSampleRate || newChannels != _sourceChannels)
                    {
                        _sourceSampleRate = newSampleRate;
                        _sourceChannels = newChannels;

                        // Recreate resampler
                        if (_sourceSampleRate != _clientSampleRate)
                        {
                            // Return old buffers to pool
                            if (_resampleInputBuffer != null)
                            {
                                FloatBufferPool.Return(_resampleInputBuffer);
                                _resampleInputBuffer = null;
                            }
                            if (_resampleOutputBuffer != null)
                            {
                                FloatBufferPool.Return(_resampleOutputBuffer);
                                _resampleOutputBuffer = null;
                            }

                            _resampler = new AudioResampler(
                                sourceRate: _sourceSampleRate,
                                targetRate: _clientSampleRate,
                                channels: _sourceChannels,
                                maxFrameSize: DefaultSamplesPerFrame);

                            // Rent new buffers from pool
                            _resampleInputBufferSize = DefaultSamplesPerFrame * _sourceChannels;
                            _resampleInputBuffer = FloatBufferPool.Rent(_resampleInputBufferSize);

                            int outputSize = _resampler.CalculateOutputSize(_resampleInputBufferSize);
                            _resampleOutputBufferSize = outputSize;
                            _resampleOutputBuffer = FloatBufferPool.Rent(_resampleOutputBufferSize);
                        }

                        //FileLogger.Info("MediaCodecMp3",
                        //    $"Format changed: {_sourceSampleRate}Hz/{_sourceChannels}ch");
                    }
                }
            }
            else
            {
                // Other status codes - exit loop
                break;
            }
        }

        // Debug: Log burst performance
        //if (framesExtracted > 0)
        //{
        //    FileLogger.Debug("MediaCodecMp3",
        //        $"BURST: Extracted {framesExtracted} frames, Buffer: {_internalBuffer.Available}/{_internalBuffer.Capacity}");
        //}
    }

    /// <summary>
    /// Pre-fills the MediaCodec with input frames to ensure the decode pipeline is ready.
    /// This prevents stuttering at playback start by ensuring decoded frames are available immediately.
    /// </summary>
    private void PrefillCodec()
    {
        if (_codec == null || _extractor == null || _inputEOS || _internalBuffer == null)
            return;

        try
        {
            int framesFilled = 0;

            // STEP 1: Feed multiple input frames to codec
            for (int i = 0; i < CodecPrefillFrames && !_inputEOS; i++)
            {
                int inputBufferIndex = _codec.DequeueInputBuffer(0); // Non-blocking
                if (inputBufferIndex >= 0)
                {
                    Java.Nio.ByteBuffer? inputBuffer = _codec.GetInputBuffer(inputBufferIndex);
                    if (inputBuffer != null)
                    {
                        int sampleSize = _extractor.ReadSampleData(inputBuffer, 0);

                        if (sampleSize < 0)
                        {
                            // End of stream
                            _codec.QueueInputBuffer(inputBufferIndex, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                            _inputEOS = true;
                        }
                        else
                        {
                            // Queue input buffer
                            long presentationTimeUs = _extractor.SampleTime;
                            _codec.QueueInputBuffer(inputBufferIndex, 0, sampleSize, presentationTimeUs, MediaCodecBufferFlags.None);
                            _extractor.Advance();
                            framesFilled++;
                        }
                    }
                }
            }

            // STEP 2: CRITICAL! Drain codec output to ring buffer
            // This fills the ring buffer BEFORE the first DecodeFrame() call
            // Without this, the first frame starts with an empty buffer (causes stuttering!)
            int drainAttempts = 0;
            const int MaxDrainAttempts = 100; // Increased - try many times
            const int TargetBufferFill = InternalBufferCapacity / 4; // Fill to 25%

            int lastAvailable = 0;
            int noProgressCount = 0;

            while (_internalBuffer.Available < TargetBufferFill && drainAttempts < MaxDrainAttempts)
            {
                DrainCodecToInternalBuffer();
                drainAttempts++;

                // Check if we're making progress
                if (_internalBuffer.Available == lastAvailable)
                {
                    noProgressCount++;

                    // If no progress after 3 attempts, give MediaCodec time to decode
                    if (noProgressCount >= 3)
                    {
                        System.Threading.Thread.Sleep(5); // 5ms - allow MediaCodec to decode
                        noProgressCount = 0; // Reset after sleep
                    }
                }
                else
                {
                    noProgressCount = 0; // Reset if we got new data
                    lastAvailable = _internalBuffer.Available;
                }

                // Exit if we've tried many times with no progress
                if (noProgressCount >= 10)
                {
                    break; // MediaCodec not producing more data
                }
            }
        }
        catch (Exception ex)
        {
            // Prefill error - log but don't throw
        }
    }

    /// <inheritdoc/>
    public double CurrentPts => _currentPts;

    /// <inheritdoc/>
    public bool IsEOF => _isEOF;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Return pooled buffers to ArrayPool
        if (_resampleInputBuffer != null)
        {
            FloatBufferPool.Return(_resampleInputBuffer);
            _resampleInputBuffer = null;
        }

        if (_resampleOutputBuffer != null)
        {
            FloatBufferPool.Return(_resampleOutputBuffer);
            _resampleOutputBuffer = null;
        }

        // Stop and release codec
        if (_codec != null)
        {
            try
            {
                _codec.Stop();
                _codec.Release();
                _codec.Dispose();
            }
            catch (Exception ex)
            {
                FileLogger.Warn("MediaCodecMp3", $"Codec cleanup: {ex.Message}");
            }

            _codec = null;
        }

        // Release extractor
        if (_extractor != null)
        {
            try
            {
                _extractor.Release();
                _extractor.Dispose();
            }
            catch (Exception ex)
            {
                FileLogger.Warn("MediaCodecMp3", $"Extractor cleanup: {ex.Message}");
            }

            _extractor = null;
        }

        // Cleanup temporary file
        if (_filePath != null && _stream != null && File.Exists(_filePath))
        {
            try
            {
                File.Delete(_filePath);
            }
            catch (Exception ex)
            {
                FileLogger.Warn("MediaCodecMp3", $"Temp file cleanup: {ex.Message}");
            }
        }

        // Dispose stream
        if (_ownsStream && _stream != null)
        {
            _stream.Dispose();
        }

        // Free pinned buffer
        if (_bufferHandle.IsAllocated)
        {
            _bufferHandle.Free();
        }
    }
}
