using System;
using System.IO;
using System.Runtime.InteropServices;
using Ownaudio.Core.Common;
using Ownaudio.Decoders;
using Ownaudio.Decoders.Mp3;
using Ownaudio.Linux.Interop;

namespace Ownaudio.Linux.Decoders;

/// <summary>
/// Linux GStreamer-based MP3 decoder implementation.
/// Uses native GStreamer framework for hardware-accelerated MP3 decoding with zero external dependencies.
/// </summary>
/// <remarks>
/// This decoder:
/// - Uses GStreamer 1.0 (available on all modern Linux distributions)
/// - Outputs Float32 PCM samples, interleaved
/// - Pre-allocates all buffers for zero-allocation decode path
/// - Thread-safe for construction, but not for concurrent decode calls
/// - Supports seeking by time position
/// - Automatically handles format conversion (MP3 → Float32 PCM)
///
/// PTS (Presentation Timestamp) Handling - OPTIMIZED:
/// - Uses sample-accurate PTS calculation based on DECODED DATA SIZE
/// - Frame duration = (samplesPerChannel * 1000.0) / sampleRate
/// - PTS incremented by frame duration (_currentPts += duration)
/// - Consistent with WAV/FLAC/Windows/macOS decoders for multi-file sync
/// - Seek sets PTS to seek position (not 0) for correct multi-file playback
///
/// GC Optimization:
/// - Pre-allocated decode buffers (4096 samples default)
/// - Pinned memory for P/Invoke calls (GCHandle)
/// - Span&lt;T&gt; usage for zero-copy operations
/// - Immediate release of GStreamer objects (gst_sample_unref)
///
/// Required packages:
/// - libgstreamer1.0-0
/// - gstreamer1.0-plugins-base
/// - gstreamer1.0-plugins-good
/// - gstreamer1.0-plugins-ugly (for MP3 support)
/// </remarks>
public sealed class GStreamerMp3Decoder : IPlatformMp3Decoder
{
    private const int DefaultSamplesPerFrame = 4096;
    private const ulong PullTimeoutNs = 1_000_000_000; // 1 second in nanoseconds

    // Static reference counter for GStreamer initialization
    private static int _gstReferenceCount = 0;
    private static readonly object _gstLock = new object();

    // GStreamer pipeline elements
    private IntPtr _pipeline;
    private IntPtr _appsink;
    private IntPtr _bus;
    private bool _gstInitialized;

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

    private bool _disposed;
    private bool _isEOF;
    private long _durationNs; // Duration in nanoseconds

    /// <summary>
    /// Default constructor (required for reflection-based creation).
    /// </summary>
    public GStreamerMp3Decoder()
    {
        _pipeline = IntPtr.Zero;
        _appsink = IntPtr.Zero;
        _bus = IntPtr.Zero;

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
            // 1. Initialize GStreamer (with reference counting)
            lock (_gstLock)
            {
                if (_gstReferenceCount == 0)
                {
                    if (!GStreamerInterop.gst_is_initialized())
                    {
                        GStreamerInterop.gst_init(IntPtr.Zero, IntPtr.Zero);
                    }
                }

                _gstReferenceCount++;
                _gstInitialized = true;
            }

            // 2. Build GStreamer pipeline
            // filesrc → decodebin → audioconvert → audioresample → capsfilter → appsink
            // This pipeline automatically detects MP3 format and decodes it
            //
            // IMPORTANT: We build the caps string dynamically to support resampling and channel mixing
            // If targetSampleRate or targetChannels are specified, they will be enforced by the capsfilter
            string capsString = "audio/x-raw,format=F32LE,layout=interleaved";

            // Add sample rate to caps if specified (this forces resampling via audioresample element)
            if (targetSampleRate > 0)
            {
                capsString += $",rate={targetSampleRate}";
            }

            // Add channels to caps if specified (this forces channel mixing via audioconvert element)
            if (targetChannels > 0)
            {
                capsString += $",channels={targetChannels}";
            }

            string pipelineDesc = $"filesrc location=\"{filePath}\" ! " +
                                  "decodebin ! " +
                                  "audioconvert ! " +
                                  "audioresample ! " +
                                  $"{capsString} ! " +
                                  "appsink name=sink sync=false";

            _pipeline = GStreamerInterop.gst_parse_launch(pipelineDesc, out IntPtr error);

            if (_pipeline == IntPtr.Zero)
            {
                string errorMsg = "Unknown error";
                if (error != IntPtr.Zero)
                {
                    var gError = Marshal.PtrToStructure<GStreamerInterop.GError>(error);
                    errorMsg = Marshal.PtrToStringAnsi(gError.message) ?? "Unknown error";
                    GStreamerInterop.g_error_free(error);
                }
                throw new AudioException(
                    AudioErrorCategory.PlatformAPI,
                    $"Failed to create GStreamer pipeline: {errorMsg}");
            }

            // 3. Get appsink element
            _appsink = GStreamerInterop.gst_bin_get_by_name(_pipeline, "sink");
            if (_appsink == IntPtr.Zero)
                throw new AudioException(
                    AudioErrorCategory.PlatformAPI,
                    "Failed to get appsink element from pipeline");

            // 4. Get bus for message handling
            _bus = GStreamerInterop.gst_element_get_bus(_pipeline);

            // 5. Set pipeline to PAUSED to preroll and get stream info
            var stateRet = GStreamerInterop.gst_element_set_state(
                _pipeline,
                GStreamerInterop.GstState.Paused);

            if (stateRet == GStreamerInterop.GstStateChangeReturn.Failure)
                throw new AudioException(
                    AudioErrorCategory.PlatformAPI,
                    "Failed to set pipeline to PAUSED state");

            // Wait for PAUSED state (with timeout)
            stateRet = GStreamerInterop.gst_element_get_state(
                _pipeline,
                out var state,
                out var pending,
                5_000_000_000); // 5 seconds timeout in nanoseconds

            if (stateRet == GStreamerInterop.GstStateChangeReturn.Failure)
            {
                // Check for error messages on bus
                string errorMsg = CheckBusForErrors();
                throw new AudioException(
                    AudioErrorCategory.PlatformAPI,
                    $"Pipeline failed to reach PAUSED state. {errorMsg}");
            }

            // 6. Query duration
            bool hasDuration = GStreamerInterop.gst_element_query_duration(
                _pipeline,
                GStreamerInterop.GstFormat.Time,
                out _durationNs);

            TimeSpan duration = hasDuration
                ? TimeSpan.FromMilliseconds(GStreamerInterop.GstTimeToMs(_durationNs))
                : TimeSpan.Zero;

            // 7. Set pipeline to PLAYING to start data flow
            stateRet = GStreamerInterop.gst_element_set_state(
                _pipeline,
                GStreamerInterop.GstState.Playing);

            if (stateRet == GStreamerInterop.GstStateChangeReturn.Failure)
            {
                string errorMsg = CheckBusForErrors();
                throw new AudioException(
                    AudioErrorCategory.PlatformAPI,
                    $"Failed to set pipeline to PLAYING state. {errorMsg}");
            }

            // Wait a bit for data to flow
            System.Threading.Thread.Sleep(100);

            // 8. Pull first sample to get format information
            _appsink = GStreamerInterop.gst_bin_get_by_name(_pipeline, "sink");
            IntPtr sample = GStreamerInterop.gst_app_sink_try_pull_sample(_appsink, PullTimeoutNs);

            if (sample == IntPtr.Zero)
            {
                string errorMsg = CheckBusForErrors();
                throw new AudioException(
                    AudioErrorCategory.PlatformAPI,
                    $"Failed to pull initial sample for format detection. {errorMsg}");
            }

            try
            {
                // Get caps (format) from sample
                IntPtr caps = GStreamerInterop.gst_sample_get_caps(sample);
                if (caps == IntPtr.Zero)
                    throw new AudioException(
                        AudioErrorCategory.PlatformAPI,
                        "Failed to get caps from sample");

                // Parse caps to get sample rate and channels
                IntPtr structure = GStreamerInterop.gst_caps_get_structure(caps, 0);
                if (structure == IntPtr.Zero)
                    throw new AudioException(
                        AudioErrorCategory.PlatformAPI,
                        "Failed to get structure from caps");

                if (!GStreamerInterop.gst_structure_get_int(structure, "rate", out int sampleRate))
                    throw new AudioException(
                        AudioErrorCategory.PlatformAPI,
                        "Failed to get sample rate from caps");

                if (!GStreamerInterop.gst_structure_get_int(structure, "channels", out int channels))
                    throw new AudioException(
                        AudioErrorCategory.PlatformAPI,
                        "Failed to get channels from caps");

                // Store format information
                // Note: The sample rate and channels we get from caps are AFTER conversion
                // (i.e., the output format), not the source MP3 format
                _sourceChannels = channels;
                _sourceSampleRate = sampleRate;
                _clientChannels = channels;      // Already converted by pipeline
                _clientSampleRate = sampleRate;  // Already converted by pipeline

                // Log if resampling/mixing occurred
                // We can't easily detect the original MP3 format before conversion,
                // but if targetSampleRate/targetChannels were specified, we know conversion happened
                if (targetSampleRate > 0 && targetSampleRate != sampleRate)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[GStreamer MP3] ERROR: Expected {targetSampleRate}Hz but got {sampleRate}Hz from pipeline. " +
                        "Caps filter may have failed.");
                }

                if (targetChannels > 0 && targetChannels != channels)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[GStreamer MP3] ERROR: Expected {targetChannels}ch but got {channels}ch from pipeline. " +
                        "Caps filter may have failed.");
                }

                // 9. Create AudioStreamInfo
                // Use CLIENT format (output format after conversion)
                _streamInfo = new AudioStreamInfo(
                    channels: _clientChannels,
                    sampleRate: _clientSampleRate,
                    duration: duration);

                _currentPts = 0.0;
                _isEOF = false;

                System.Diagnostics.Debug.WriteLine(
                    $"[GStreamer MP3] Opened: {filePath}, " +
                    $"Format: {_clientChannels}ch/{_clientSampleRate}Hz, " +
                    $"Duration: {duration.TotalSeconds:F2}s");

                // Process the first sample we pulled
                ProcessSample(sample);
            }
            finally
            {
                GStreamerInterop.gst_sample_unref(sample);
            }
        }
        catch (DllNotFoundException dllEx)
        {
            Dispose();
            throw new AudioException(
                AudioErrorCategory.PlatformAPI,
                $"GStreamer libraries not found. Please install GStreamer 1.0 and required plugins. " +
                $"On Ubuntu/Debian: sudo apt-get install libgstreamer1.0-0 gstreamer1.0-plugins-base " +
                $"gstreamer1.0-plugins-good gstreamer1.0-plugins-ugly. Error: {dllEx.Message}",
                dllEx);
        }
        catch (Exception ex) when (!(ex is AudioException))
        {
            // Cleanup on failure
            Dispose();
            throw new AudioException(
                AudioErrorCategory.PlatformAPI,
                $"Failed to initialize MP3 decoder: {ex.Message}",
                ex);
        }
    }

    /// <inheritdoc/>
    public void InitializeFromStream(Stream stream, int targetSampleRate, int targetChannels)
    {
        throw new NotImplementedException(
            "Stream-based MP3 decoding not yet implemented for GStreamer. " +
            "GStreamer requires file path or custom source element. " +
            "Consider writing stream to temporary file first.");
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
            throw new ObjectDisposedException(nameof(GStreamerMp3Decoder));

        if (_isEOF)
        {
            pts = _currentPts;
            return 0; // EOF
        }

        if (_appsink == IntPtr.Zero)
        {
            pts = 0.0;
            return -1; // Error
        }

        try
        {
            // Check for EOS first
            if (GStreamerInterop.gst_app_sink_is_eos(_appsink))
            {
                System.Diagnostics.Debug.WriteLine("[GStreamer MP3] End of stream");
                _isEOF = true;
                pts = _currentPts;
                return 0; // EOF
            }

            // Pull sample from appsink
            IntPtr sample = GStreamerInterop.gst_app_sink_try_pull_sample(_appsink, PullTimeoutNs);

            if (sample == IntPtr.Zero)
            {
                // Check if we've reached EOS
                if (GStreamerInterop.gst_app_sink_is_eos(_appsink))
                {
                    System.Diagnostics.Debug.WriteLine("[GStreamer MP3] End of stream (after pull)");
                    _isEOF = true;
                    pts = _currentPts;
                    return 0; // EOF
                }

                // Check for error messages on bus
                if (_bus != IntPtr.Zero)
                {
                    IntPtr msg = GStreamerInterop.gst_bus_pop_filtered(
                        _bus,
                        GStreamerInterop.GstMessageType.Error);

                    if (msg != IntPtr.Zero)
                    {
                        System.Diagnostics.Debug.WriteLine("[GStreamer MP3] Error message on bus");
                        GStreamerInterop.gst_message_unref(msg);
                        pts = 0.0;
                        return -1; // Error
                    }
                }

                // Timeout without sample
                System.Diagnostics.Debug.WriteLine("[GStreamer MP3] Pull timeout without sample");
                pts = 0.0;
                return -1; // Error
            }

            try
            {
                // Process sample
                int bytesDecoded = ProcessSample(sample, outputBuffer, out pts);
                return bytesDecoded;
            }
            finally
            {
                GStreamerInterop.gst_sample_unref(sample);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GStreamer MP3] Decode exception: {ex.Message}");
            pts = 0.0;
            return -1; // Error
        }
    }

    /// <summary>
    /// Process a GStreamer sample and extract audio data.
    /// </summary>
    private int ProcessSample(IntPtr sample)
    {
        return ProcessSample(sample, _decodeBuffer.AsSpan(), out _);
    }

    /// <summary>
    /// Process a GStreamer sample and extract audio data.
    /// </summary>
    private int ProcessSample(IntPtr sample, Span<byte> outputBuffer, out double pts)
    {
        // Get buffer from sample
        IntPtr buffer = GStreamerInterop.gst_sample_get_buffer(sample);
        if (buffer == IntPtr.Zero)
        {
            pts = 0.0;
            return -1; // Error
        }

        // Get buffer size
        nuint bufferSize = GStreamerInterop.gst_buffer_get_size(buffer);
        if (bufferSize == 0)
        {
            System.Diagnostics.Debug.WriteLine("[GStreamer MP3] WARNING: Empty buffer");
            pts = 0.0;
            return -1; // Error
        }

        int bytesDecoded = (int)bufferSize;

        // Check buffer overflow
        if (bytesDecoded > outputBuffer.Length)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[GStreamer MP3] WARNING: Buffer overflow prevented. " +
                $"Buffer size {bytesDecoded} bytes, output size {outputBuffer.Length} bytes");
            bytesDecoded = outputBuffer.Length;
        }

        // Extract data from buffer
        nuint bytesExtracted = GStreamerInterop.gst_buffer_extract(
            buffer,
            0, // offset
            _bufferPtr,
            (nuint)bytesDecoded);

        if (bytesExtracted == 0)
        {
            System.Diagnostics.Debug.WriteLine("[GStreamer MP3] Failed to extract data from buffer");
            pts = 0.0;
            return -1; // Error
        }

        bytesDecoded = (int)bytesExtracted;

        // Calculate frame duration using CLIENT sample rate
        int totalFloatSamples = bytesDecoded / sizeof(float);
        int samplesPerChannel = totalFloatSamples / _clientChannels;
        double frameDurationMs = (samplesPerChannel * 1000.0) / _clientSampleRate;

        // SIMPLE PTS CALCULATION (same as Windows/macOS decoders):
        // Current frame PTS, then increment for next frame
        double framePts = _currentPts;
        _currentPts += frameDurationMs;

        // Copy to output buffer
        _decodeBuffer.AsSpan(0, bytesDecoded).CopyTo(outputBuffer);

        pts = framePts;

        //System.Diagnostics.Debug.WriteLine(
        //    $"[GStreamer MP3] Decoded {samplesPerChannel} frames, {bytesDecoded} bytes, " +
        //    $"PTS: {framePts:F2}ms, duration: {frameDurationMs:F2}ms");

        return bytesDecoded;
    }

    /// <inheritdoc/>
    public bool Seek(long samplePosition)
    {
        if (_disposed)
            return false;

        if (_pipeline == IntPtr.Zero)
            return false;

        try
        {
            // Convert sample position to time (nanoseconds)
            double timeMs = (samplePosition * 1000.0) / _clientSampleRate;
            long timeNs = GStreamerInterop.MsToGstTime(timeMs);

            // Perform seek
            bool success = GStreamerInterop.gst_element_seek_simple(
                _pipeline,
                GStreamerInterop.GstFormat.Time,
                GStreamerInterop.GstSeekFlags.Flush | GStreamerInterop.GstSeekFlags.Accurate,
                timeNs);

            if (!success)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GStreamer MP3] Seek failed to position {samplePosition} samples ({timeMs:F2}ms)");
                return false;
            }

            // Update state
            _currentPts = timeMs;
            _isEOF = false;

            System.Diagnostics.Debug.WriteLine(
                $"[GStreamer MP3] Seeked to sample {samplePosition}, PTS: {_currentPts:F2}ms");

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GStreamer MP3] Seek exception: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc/>
    public double CurrentPts => _currentPts;

    /// <inheritdoc/>
    public bool IsEOF => _isEOF;

    /// <summary>
    /// Checks the bus for error messages and returns a formatted error string.
    /// </summary>
    /// <returns>Error message or empty string if no errors.</returns>
    private string CheckBusForErrors()
    {
        if (_bus == IntPtr.Zero)
            return string.Empty;

        try
        {
            IntPtr msg = GStreamerInterop.gst_bus_pop_filtered(
                _bus,
                GStreamerInterop.GstMessageType.Error | GStreamerInterop.GstMessageType.Warning);

            if (msg != IntPtr.Zero)
            {
                try
                {
                    // Try to extract error message
                    // Note: This is a simplified version, full implementation would parse GstMessage structure
                    return "Pipeline error detected (check GStreamer installation and plugins)";
                }
                finally
                {
                    GStreamerInterop.gst_message_unref(msg);
                }
            }
        }
        catch
        {
            // Ignore errors during error checking
        }

        return string.Empty;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Stop and cleanup pipeline
        if (_pipeline != IntPtr.Zero)
        {
            try
            {
                GStreamerInterop.gst_element_set_state(
                    _pipeline,
                    GStreamerInterop.GstState.Null);

                GStreamerInterop.g_object_unref(_pipeline);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GStreamer MP3] Pipeline cleanup warning: {ex.Message}");
            }

            _pipeline = IntPtr.Zero;
        }

        // Cleanup appsink (already released by pipeline, just null the reference)
        _appsink = IntPtr.Zero;

        // Cleanup bus
        if (_bus != IntPtr.Zero)
        {
            try
            {
                GStreamerInterop.g_object_unref(_bus);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GStreamer MP3] Bus cleanup warning: {ex.Message}");
            }

            _bus = IntPtr.Zero;
        }

        // Deinitialize GStreamer (with reference counting)
        if (_gstInitialized)
        {
            lock (_gstLock)
            {
                _gstReferenceCount--;
                _gstInitialized = false;

                // Note: We don't call gst_deinit() as it's not recommended
                // GStreamer should be deinitialized only when the application exits
            }
        }

        // Free pinned buffer
        if (_bufferHandle.IsAllocated)
        {
            _bufferHandle.Free();
        }

        System.Diagnostics.Debug.WriteLine("[GStreamer MP3] Decoder disposed");
    }
}
