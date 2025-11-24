using System;
using System.IO;
using System.Runtime.InteropServices;
using Ownaudio.Core.Common;

namespace Ownaudio.Decoders.Mp3;

/// <summary>
/// Platform-independent MP3 decoder wrapper.
/// Automatically selects the best platform-specific implementation.
/// </summary>
/// <remarks>
/// <para><b>Thread Safety:</b> NOT thread-safe. Single thread use only.</para>
/// <para><b>GC Behavior:</b> Zero-allocation during decode loop after initialization.</para>
/// <para><b>Platform Support:</b></para>
/// <list type="bullet">
/// <item>Windows: Media Foundation (hardware-accelerated)</item>
/// <item>macOS: Core Audio (hardware-accelerated)</item>
/// <item>Linux: GStreamer (hardware-accelerated)</item>
/// <item>Android: MediaCodec (hardware-accelerated)</item>
/// </list>
/// </remarks>
public sealed class Mp3Decoder : BaseStreamDecoder
{
    private readonly IPlatformMp3Decoder _platformDecoder;
    private readonly byte[] _decodeBuffer;
    private readonly MutableAudioFrame _mutableFrame;
    private readonly AudioFramePool _framePool;

    private const int DefaultSamplesPerFrame = 4096;

    /// <summary>
    /// Creates a new MP3 decoder from a file path.
    /// </summary>
    /// <param name="filePath">Path to MP3 file.</param>
    /// <param name="targetSampleRate">Target sample rate (0 = use source rate).</param>
    /// <param name="targetChannels">Target channel count (0 = use source channels).</param>
    /// <exception cref="AudioException">Thrown when file cannot be opened or platform decoder unavailable.</exception>
    public Mp3Decoder(string filePath, int targetSampleRate = 0, int targetChannels = 0)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"MP3 file not found: {filePath}", filePath);

        // Open file stream for BaseStreamDecoder compatibility (seek validation)
        // Platform decoder uses file path directly, but we need stream for TrySeek() validation
        _stream = File.OpenRead(filePath);
        _ownsStream = true; // We own this stream, dispose it when decoder is disposed

        // Create platform-specific decoder
        _platformDecoder = CreatePlatformDecoder();
        _platformDecoder.InitializeFromFile(filePath, targetSampleRate, targetChannels);

        // Get stream info from platform decoder
        _streamInfo = _platformDecoder.GetStreamInfo();
        _currentPts = 0.0;

        // Pre-allocate buffers (stereo max)
        int bufferSize = DefaultSamplesPerFrame * 2 * sizeof(float);
        _decodeBuffer = new byte[bufferSize];
        _mutableFrame = new MutableAudioFrame(DefaultSamplesPerFrame * 2);

        // Initialize frame pool
        _framePool = new AudioFramePool(bufferSize: bufferSize * 2, initialPoolSize: 2, maxPoolSize: 8);
    }

    /// <summary>
    /// Creates a new MP3 decoder from a stream.
    /// </summary>
    /// <param name="stream">Stream containing MP3 data. Must support seeking and reading.</param>
    /// <param name="ownsStream">If true, disposes the stream when decoder is disposed.</param>
    /// <param name="targetSampleRate">Target sample rate (0 = use source rate).</param>
    /// <param name="targetChannels">Target channel count (0 = use source channels).</param>
    /// <exception cref="AudioException">Thrown when stream is invalid or platform decoder unavailable.</exception>
    public Mp3Decoder(Stream stream, bool ownsStream = false, int targetSampleRate = 0, int targetChannels = 0)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _ownsStream = ownsStream;

        if (!stream.CanRead)
            throw new ArgumentException("Stream must support reading.", nameof(stream));

        if (!stream.CanSeek)
            throw new ArgumentException("Stream must support seeking.", nameof(stream));

        // Create platform-specific decoder
        _platformDecoder = CreatePlatformDecoder();
        _platformDecoder.InitializeFromStream(stream, targetSampleRate, targetChannels);

        // Get stream info from platform decoder
        _streamInfo = _platformDecoder.GetStreamInfo();
        _currentPts = 0.0;

        // Pre-allocate buffers (stereo max)
        int bufferSize = DefaultSamplesPerFrame * 2 * sizeof(float);
        _decodeBuffer = new byte[bufferSize];
        _mutableFrame = new MutableAudioFrame(DefaultSamplesPerFrame * 2);

        // Initialize frame pool
        _framePool = new AudioFramePool(bufferSize: bufferSize * 2, initialPoolSize: 2, maxPoolSize: 8);
    }

    /// <summary>
    /// Creates the appropriate platform-specific MP3 decoder.
    /// </summary>
    /// <returns>Platform-specific decoder instance.</returns>
    /// <exception cref="AudioException">Thrown when no suitable decoder is available.</exception>
    private static IPlatformMp3Decoder CreatePlatformDecoder()
    {
#if ANDROID
        // Try to load Android MediaCodec decoder
        try
        {
            var androidDecoderType = Type.GetType(
                "Ownaudio.Android.Decoders.MediaCodecMp3Decoder, Ownaudio.Android");

            if (androidDecoderType != null)
            {
                var instance = Activator.CreateInstance(androidDecoderType);

                if (instance is IPlatformMp3Decoder decoder)
                {
                    return decoder;
                }
                else
                {
                    LogError("Mp3Decoder", $"Instance is not IPlatformMp3Decoder! Actual type: {instance?.GetType().FullName}");
                }
            }
            else
            {
                LogError("Mp3Decoder", "MediaCodecMp3Decoder type not found!");
            }
        }
        catch (Exception ex)
        {
            throw new AudioException(
                AudioErrorCategory.PlatformAPI,
                $"Android MediaCodec MP3 decoder not available. Ensure Ownaudio.Android package is referenced. Error: {ex.Message}", ex);
        }

        throw new AudioException(
            AudioErrorCategory.PlatformAPI,
            "Android MediaCodec MP3 decoder not available. Ensure Ownaudio.Android package is referenced.");
#elif WINDOWS
        LogDebug("Mp3Decoder", "Detected Windows platform (compile-time)");
        // Try to load Windows Media Foundation decoder
        try
        {
            var windowsDecoderType = Type.GetType(
                "Ownaudio.Windows.Decoders.WindowsMFMp3Decoder, Ownaudio.Windows");

            if (windowsDecoderType != null)
            {
                var instance = Activator.CreateInstance(windowsDecoderType);
                if (instance is IPlatformMp3Decoder decoder)
                    return decoder;
            }
        }
        catch
        {
            // Fall through to error
        }

        throw new AudioException(
            AudioErrorCategory.PlatformAPI,
            "Windows Media Foundation MP3 decoder not available. Ensure Ownaudio.Windows package is referenced.");
#elif MACOS
        LogDebug("Mp3Decoder", "Detected macOS platform (compile-time)");
        // Try to load macOS Core Audio decoder
        try
        {
            var macosDecoderType = Type.GetType(
                "Ownaudio.macOS.Decoders.CoreAudioMp3Decoder, Ownaudio.macOS");

            if (macosDecoderType != null)
            {
                var instance = Activator.CreateInstance(macosDecoderType);
                if (instance is IPlatformMp3Decoder decoder)
                    return decoder;
            }
        }
        catch
        {
            // Fall through to error
        }

        throw new AudioException(
            AudioErrorCategory.PlatformAPI,
            "macOS Core Audio MP3 decoder not available. Ensure Ownaudio.macOS package is referenced.");
#elif LINUX
        LogDebug("Mp3Decoder", "Detected Linux platform (compile-time)");
        // Try to load Linux GStreamer decoder
        try
        {
            var linuxDecoderType = Type.GetType(
                "Ownaudio.Linux.Decoders.GStreamerMp3Decoder, Ownaudio.Linux");

            if (linuxDecoderType != null)
            {
                var instance = Activator.CreateInstance(linuxDecoderType);
                if (instance is IPlatformMp3Decoder decoder)
                    return decoder;
            }
        }
        catch
        {
            // Fall through to error
        }

        throw new AudioException(
            AudioErrorCategory.PlatformAPI,
            "Linux GStreamer MP3 decoder not available. Ensure Ownaudio.Linux package is referenced and GStreamer 1.0 is installed.");
#else
        LogError("Mp3Decoder", $"Unknown platform! OS={RuntimeInformation.OSDescription}");

        throw new AudioException(
            AudioErrorCategory.PlatformAPI,
            $"No MP3 decoder available for current platform. OS={RuntimeInformation.OSDescription}");
#endif
    }

    /// <summary>
    /// Platform-agnostic debug logging helper.
    /// Uses FileLogger on Android, System.Diagnostics.Debug elsewhere.
    /// </summary>
    private static void LogDebug(string tag, string message)
    {
#if ANDROID
        try
        {
            // Use reflection to call FileLogger.Debug
            var fileLoggerType = Type.GetType("Ownaudio.Android.Common.FileLogger, Ownaudio.Android");
            var debugMethod = fileLoggerType?.GetMethod("Debug", new[] { typeof(string), typeof(string) });
            debugMethod?.Invoke(null, new object[] { tag, message });
        }
        catch
        {
            // Fallback to Debug.WriteLine
            System.Diagnostics.Debug.WriteLine($"[{tag}] {message}");
        }
#else
        System.Diagnostics.Debug.WriteLine($"[{tag}] {message}");
#endif
    }

    /// <summary>
    /// Platform-agnostic error logging helper.
    /// Uses FileLogger on Android, System.Diagnostics.Debug elsewhere.
    /// </summary>
    private static void LogError(string tag, string message)
    {
#if ANDROID
        try
        {
            // Use reflection to call FileLogger.Error
            var fileLoggerType = Type.GetType("Ownaudio.Android.Common.FileLogger, Ownaudio.Android");
            var errorMethod = fileLoggerType?.GetMethod("Error", new[] { typeof(string), typeof(string) });
            errorMethod?.Invoke(null, new object[] { tag, message });
        }
        catch
        {
            // Fallback to Debug.WriteLine
            System.Diagnostics.Debug.WriteLine($"[ERROR][{tag}] {message}");
        }
#else
        System.Diagnostics.Debug.WriteLine($"[ERROR][{tag}] {message}");
#endif
    }

    /// <summary>
    /// Parses stream information (already done by platform decoder).
    /// </summary>
    protected override AudioStreamInfo ParseStreamInfo()
    {
        // Stream info already populated in constructor
        return _streamInfo;
    }

    /// <summary>
    /// Decodes the next audio frame.
    /// </summary>
    /// <returns>Decoded audio frame or EOF/error result.</returns>
    protected override AudioDecoderResult DecodeNextFrameCore()
    {
        if (_platformDecoder.IsEOF)
            return new AudioDecoderResult(null!, false, true);

        // Decode frame using platform decoder
        int bytesDecoded = _platformDecoder.DecodeFrame(_decodeBuffer.AsSpan(), out double pts);

        if (bytesDecoded == 0)
        {
            // EOF
            return new AudioDecoderResult(null!, false, true);
        }

        if (bytesDecoded < 0)
        {
            // Error
            return new AudioDecoderResult(null!, false, false, "Platform decoder error");
        }

        // Update PTS
        _currentPts = pts;

        // Rent pooled frame
        var pooledFrame = _framePool.Rent(_currentPts, bytesDecoded);

        // Copy decoded data to pooled frame
        _decodeBuffer.AsSpan(0, bytesDecoded).CopyTo(pooledFrame.BufferSpan);

        // Convert to AudioFrame
        var frame = pooledFrame.ToAudioFrame();

        // Return pooled frame
        _framePool.Return(pooledFrame);

        return new AudioDecoderResult(frame, true, false);
    }

    /// <summary>
    /// Seeks to the specified sample position.
    /// </summary>
    /// <param name="samplePosition">Target sample position (per channel).</param>
    /// <returns>True if seek succeeded.</returns>
    protected override bool SeekCore(long samplePosition)
    {
        return _platformDecoder.Seek(samplePosition);
    }

    /// <summary>
    /// Releases all resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (_isDisposed)
            return;

        if (disposing)
        {
            _platformDecoder?.Dispose();
            _framePool?.Clear();
        }

        base.Dispose(disposing);
    }
}
