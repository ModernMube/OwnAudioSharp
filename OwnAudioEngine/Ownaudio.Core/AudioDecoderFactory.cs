using System;
using Logger;
using System.IO;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using Ownaudio.Decoders.FFmpeg;
using Ownaudio.Decoders.Wav;
using Ownaudio.Decoders.Flac;
using Ownaudio.Decoders.Mp3;

namespace Ownaudio.Decoders;

/// <summary>
/// Factory for creating audio decoders based on file format and platform.
/// Automatically detects format from file extension or header magic bytes.
/// </summary>
/// <remarks>
/// Supported formats:
/// - WAV (PCM, IEEE Float, ADPCM) - Platform-independent, pure C#
/// - MP3 (MPEG-1/2 Layer III) - Platform-specific or managed fallback
/// - FLAC (Free Lossless Audio Codec) - Pure C# managed implementation
/// </remarks>
public static class AudioDecoderFactory
{
    /// <summary>
    /// AOT-safe factory delegate registered by Ownaudio.Native via [ModuleInitializer].
    /// Null when the native assembly is not loaded (falls back to managed decoders).
    /// </summary>
    private static Func<string, int, int, IAudioDecoder>? _nativeDecoderFactory;

    /// <summary>
    /// Registers a native decoder factory. Called from Ownaudio.Native at module init time.
    /// </summary>
    public static void RegisterNativeDecoder(Func<string, int, int, IAudioDecoder> factory)
        => _nativeDecoderFactory = factory;

    /// <summary>
    /// True when a native decoder factory has been registered.
    /// </summary>
    private static bool NativeAvailable => _nativeDecoderFactory != null;
    
    /// <summary>
    /// Creates an audio decoder for the specified file.
    /// Automatically detects format from file extension or header.
    /// </summary>
    /// <param name="filePath">Path to audio file.</param>
    /// <param name="targetSampleRate">Target sample rate in Hz (0 = use source rate, no resampling).</param>
    /// <param name="targetChannels">Target channel count (0 = use source channels, no conversion).</param>
    /// <returns>Platform-appropriate decoder instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when filePath is null or empty.</exception>
    /// <exception cref="FileNotFoundException">Thrown when file does not exist.</exception>
    /// <exception cref="AudioException">Thrown when format is unsupported or file cannot be opened.</exception>
    public static IAudioDecoder Create(string filePath, int targetSampleRate = 0, int targetChannels = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new AudioException("AudioDecoderFactory ERROR: ", new ArgumentNullException(nameof(filePath)));

        if (!File.Exists(filePath))
            throw new AudioException("AudioDecoderFactory ERROR: ", new FileNotFoundException($"Audio file not found: {filePath}", filePath));

        FFmpegLoader.Initialize();

        var format = DetectFormatFromExtension(filePath);
        if (format == AudioFormat.Unknown)
        {
            using var stream = File.OpenRead(filePath);
            format = DetectFormat(stream);
        }

        if (format == AudioFormat.Unknown)
            throw new AudioException("AudioDecoderFactory ERROR: ", new AudioException($"Unable to detect audio format for file: {filePath}"));

        if (format == AudioFormat.FFmpeg)
        {
            if (!FFmpegConfig.IsAvailable)
                throw new AudioException("AudioDecoderFactory ERROR: ",
                    new AudioException($"Format '{Path.GetExtension(filePath)}' requires FFmpeg, but FFmpeg libraries were not found."));

            return new FFmpegDecoder(filePath, targetSampleRate, targetChannels);
        }

        if (FFmpegConfig.IsAvailable)
        {
            try
            {
                var decoder = new FFmpegDecoder(filePath, targetSampleRate, targetChannels);
                Log.Info($"Using FFmpeg decoder for {format} format");
                return decoder;
            }
            catch (Exception ex)
            {
                Log.Error($"FFmpeg decoder failed for {filePath}: {ex.Message}");
                Log.Info("Falling back to MiniAudio or managed decoder...");
            }
        }

        if (NativeAvailable)
        {
            try
            {
                var decoder = _nativeDecoderFactory!(filePath, targetSampleRate, targetChannels);
                Log.Info($"Using MiniAudio decoder for {format} format (native)");
                return decoder;
            }
            catch (Exception ex)
            {
                Log.Error($"MiniAudio decoder instantiation failed: {ex.Message}");
                Log.Info("Falling back to managed decoder...");
            }
        }

        if (format == AudioFormat.Mp3)
            return CreateMp3DecoderFromFile(filePath, targetSampleRate, targetChannels);

        var fileStream = File.OpenRead(filePath);
        return CreateDecoderInternal(fileStream, format, true, targetSampleRate, targetChannels);
    }

    /// <summary>
    /// Creates an audio decoder for the specified stream and format.
    /// </summary>
    /// <param name="stream">Stream containing audio data. Must support seeking.</param>
    /// <param name="format">Audio format of the stream.</param>
    /// <param name="targetSampleRate">Target sample rate in Hz (0 = use source rate, no resampling).</param>
    /// <param name="targetChannels">Target channel count (0 = use source channels, no conversion).</param>
    /// <returns>Platform-appropriate decoder instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    /// <exception cref="ArgumentException">Thrown when stream does not support seeking or reading.</exception>
    /// <exception cref="AudioException">Thrown when format is unsupported or unknown.</exception>
    /// <example>
    /// <code>
    /// using var stream = new MemoryStream(audioData);
    /// using var decoder = AudioDecoderFactory.Create(stream, AudioFormat.Wav, targetSampleRate: 48000, targetChannels: 2);
    /// byte[] buffer = new byte[8192];
    /// var result = decoder.ReadFrames(buffer);
    /// </code>
    /// </example>
    public static IAudioDecoder Create(Stream stream, AudioFormat format, int targetSampleRate = 0, int targetChannels = 0)
    {
        if (stream == null)
            throw new AudioException("AudioDecoderFactory ERROR: ", new ArgumentNullException(nameof(stream)));

        if (!stream.CanRead)
            throw new AudioException("AudioDecoderFactory ERROR: ", new ArgumentException("Stream must support reading.", nameof(stream)));

        if (!stream.CanSeek)
            throw new AudioException("AudioDecoderFactory ERROR: ", new ArgumentException("Stream must support seeking.", nameof(stream)));

        if (format == AudioFormat.Unknown)
            throw new AudioException("AudioDecoderFactory ERROR: ", new AudioException("Audio format must be specified and cannot be Unknown."));

        return CreateDecoderInternal(stream, format, false, targetSampleRate, targetChannels);
    }

    /// <summary>
    /// Detects audio format from file header (magic bytes).
    /// </summary>
    /// <param name="stream">Stream to read from. Position will be reset after detection.</param>
    /// <returns>Detected audio format or <see cref="AudioFormat.Unknown"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    /// <remarks>
    /// This method reads the first 12 bytes of the stream to detect the format.
    /// Stream position is restored to original position after detection.
    /// </remarks>
    public static AudioFormat DetectFormat(Stream stream)
    {
        if (stream == null)
            throw new AudioException("AudioDecoderFactory ERROR: ", new ArgumentNullException(nameof(stream)));

        if (!stream.CanRead || !stream.CanSeek)
            return AudioFormat.Unknown;

        long originalPos = stream.Position;

        try
        {
            Span<byte> header = stackalloc byte[12];
            int bytesRead = stream.Read(header);

            if (bytesRead < 12)
                return AudioFormat.Unknown;

            // WAV: "RIFF....WAVE"
            if (header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' &&
                header[8] == 'W' && header[9] == 'A' && header[10] == 'V' && header[11] == 'E')
                return AudioFormat.Wav;

            // MP3: 0xFF 0xFB or 0xFF 0xFA (sync word) OR ID3 tag
            if ((header[0] == 0xFF && (header[1] & 0xE0) == 0xE0) ||
                (header[0] == 'I' && header[1] == 'D' && header[2] == '3'))
                return AudioFormat.Mp3;

            // FLAC: "fLaC"
            if (header[0] == 'f' && header[1] == 'L' && header[2] == 'a' && header[3] == 'C')
                return AudioFormat.Flac;

            return AudioFormat.Unknown;
        }
        catch
        {
            return AudioFormat.Unknown;
        }
        finally
        {
            stream.Position = originalPos;
        }
    }

    /// <summary>
    /// Detects audio format from file extension.
    /// Returns <see cref="AudioFormat.FFmpeg"/> for formats that require FFmpeg
    /// (OGG, Opus, AAC, M4A, WMA, AIFF, APE, etc.) when FFmpeg is available.
    /// </summary>
    /// <param name="filePath">File path to analyze.</param>
    /// <returns>Detected audio format or <see cref="AudioFormat.Unknown"/>.</returns>
    private static AudioFormat DetectFormatFromExtension(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return AudioFormat.Unknown;

        string extension = Path.GetExtension(filePath).ToLowerInvariant();

        return extension switch
        {
            ".wav"  => AudioFormat.Wav,
            ".mp3"  => AudioFormat.Mp3,
            ".flac" => AudioFormat.Flac,

            ".ogg"  or ".oga"  or
            ".opus" or
            ".aac"  or
            ".m4a"  or ".m4b"  or ".m4r" or
            ".mp4"  or
            ".wma"  or
            ".aiff" or ".aif"  or
            ".ape"  or
            ".wv"   or
            ".mka"  or
            ".ac3"  or
            ".dts"  or
            ".amr"  or
            ".au"   or ".snd"  or
            ".tta"  or
            ".ra"   or ".rm"
                => AudioFormat.FFmpeg,

            _ => AudioFormat.Unknown
        };
    }

    /// <summary>
    /// Internal method to create decoder instance based on format.
    /// Strategy: Try MiniAudio decoder first, fallback to managed decoders.
    /// Note: MiniAudio decoder currently only supports file-based decoding, not streams.
    /// </summary>
    private static IAudioDecoder CreateDecoderInternal(Stream stream, AudioFormat format, bool ownsStream, int targetSampleRate = 0, int targetChannels = 0)
    {
        return format switch
        {
            AudioFormat.Wav => new WavDecoder(stream, ownsStream, targetSampleRate, targetChannels),
            AudioFormat.Mp3 => CreateMp3Decoder(stream, ownsStream, targetSampleRate, targetChannels),
            AudioFormat.Flac => new FlacDecoder(stream, ownsStream, targetSampleRate, targetChannels),
            _ => throw new AudioException($"Unsupported audio format: {format}")
        };
    }

    /// <summary>
    /// Creates platform-specific MP3 decoder from file path.
    /// Strategy:
    /// 1. Try MiniAudio decoder first (preferred cross-platform solution)
    /// 2. Fallback to platform-specific decoders if MiniAudio fails
    /// 3. Fallback to managed Mp3Decoder as last resort
    /// </summary>
    private static IAudioDecoder CreateMp3DecoderFromFile(string filePath, int targetSampleRate, int targetChannels)
    {
        if (NativeAvailable)
        {
            try
            {
                var decoder = _nativeDecoderFactory!(filePath, targetSampleRate, targetChannels);
                Log.Info("Using MiniAudio decoder (native)");
                return decoder;
            }
            catch (Exception ex)
            {
                Log.Error($"MiniAudio MP3 decoder instantiation failed: {ex.Message}");
                Log.Info("Falling back to managed Mp3Decoder...");
            }
        }

        try
        {
            return new Mp3Decoder(filePath, targetSampleRate, targetChannels);
        }
        catch (Exception ex) when (ex is not AudioException)
        {
            throw new AudioException("AudioDecoderFactory ERROR: ", new AudioException($"Failed to create MP3 decoder: {ex.Message}", ex));
        }
    }

    /// <summary>
    /// Creates platform-specific MP3 decoder from stream.
    /// Uses Mp3Decoder wrapper which automatically selects the correct platform implementation:
    /// Windows: Media Foundation (not fully supported for streams)
    /// macOS: Core Audio (not supported for streams - requires file path)
    /// Other: Managed decoder fallback (not yet implemented)
    /// </summary>
    private static IAudioDecoder CreateMp3Decoder(Stream stream, bool ownsStream, int targetSampleRate, int targetChannels)
    {
        try
        {
            return new Mp3Decoder(stream, ownsStream, targetSampleRate, targetChannels);
        }
        catch (Exception ex) when (!(ex is AudioException))
        {
            throw new AudioException("AudioDecoderFactory ERROR: ", new AudioException($"Failed to create MP3 decoder from stream: {ex.Message}", ex));
        }
    }
}
