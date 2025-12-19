using System;
using Logger;
using System.IO;
using System.Runtime.InteropServices;
using Ownaudio.Core.Common;
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

        // Try to detect format from extension first
        var format = DetectFormatFromExtension(filePath);

        // If extension detection fails, try magic bytes
        if (format == AudioFormat.Unknown)
        {
            using var stream = File.OpenRead(filePath);
            format = DetectFormat(stream);
        }

        if (format == AudioFormat.Unknown)
            throw new AudioException("AudioDecoderFactory ERROR: ", new AudioException($"Unable to detect audio format for file: {filePath}"));
        
        try
        {
            var assembly = System.Reflection.Assembly.Load("Ownaudio.Native");
            var decoderType = assembly.GetType("Ownaudio.Native.Decoders.MaDecoder");

            if (decoderType != null)
            {
                var decoder = Activator.CreateInstance(decoderType, filePath, targetSampleRate, targetChannels) as IAudioDecoder;
                if (decoder != null)
                {
                    Log.Info($"Using MiniAudio decoder for {format} format (native)");
                    return decoder;
                }
            }
        }
        catch (Exception ex)
        {
            // MiniAudio not available - fallback to managed decoders
            Log.Error($"MiniAudio decoder not available: {ex.Message}");
            Log.Info("Falling back to managed decoder...");
        }

        // For MP3, use platform-specific decoders if available
        if (format == AudioFormat.Mp3)
        {
            return CreateMp3DecoderFromFile(filePath, targetSampleRate, targetChannels);
        }

        // For WAV and FLAC, use managed decoders
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
    /// var result = decoder.DecodeNextFrame();
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
            ".wav" => AudioFormat.Wav,
            ".mp3" => AudioFormat.Mp3,
            ".flac" => AudioFormat.Flac,
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
        // For stream-based decoding, use managed decoders
        // (MiniAudio decoder requires file path for now)
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
        // PRIMARY: Try MiniAudio decoder first (supports MP3, WAV, FLAC, and more)
        try
        {
            var assembly = System.Reflection.Assembly.Load("Ownaudio.Native");
            var decoderType = assembly.GetType("Ownaudio.Native.Decoders.MaDecoder");

            if (decoderType != null)
            {
                var decoder = Activator.CreateInstance(decoderType, filePath, targetSampleRate, targetChannels) as IAudioDecoder;
                if (decoder != null)
                {
                    Log.Info("Using MiniAudio decoder (native)");
                    return decoder;
                }
            }
        }
        catch (Exception ex)
        {
            // MiniAudio not available - fallback to platform-specific or managed
            Log.Error($"MiniAudio decoder not available: {ex.Message}");
            Log.Error("Falling back to platform-specific decoder...");
        }

        // FALLBACK 1: Platform-specific decoders
#if ANDROID || IOS
        // Mobile platforms: Use Mp3Decoder wrapper which uses compile-time platform detection
        try
        {
            return new Mp3Decoder(filePath, targetSampleRate, targetChannels);
        }
        catch (Exception ex) when (!(ex is AudioException))
        {
            throw new AudioException("AudioDecoderFactory ERROR: ", new AudioException($"Failed to create MP3 decoder: {ex.Message}", ex));
        }
#elif WINDOWS
        // Desktop Windows: Use Media Foundation MP3 decoder directly via reflection
        // (Mp3Decoder wrapper doesn't work correctly on desktop Windows - causes fast playback tempo)
        try
        {
            var assembly = System.Reflection.Assembly.Load("Ownaudio.Windows");
            var decoderType = assembly.GetType("Ownaudio.Windows.Decoders.MFMp3Decoder");

            if (decoderType == null)
                throw new AudioException("MFMp3Decoder type not found in Ownaudio.Windows assembly");

            var decoder = Activator.CreateInstance(decoderType, filePath, targetSampleRate, targetChannels) as IAudioDecoder;

            if (decoder == null)
                throw new AudioException("Failed to create MFMp3Decoder instance");

            return decoder;
        }
        catch (Exception ex) when (!(ex is AudioException))
        {
            throw new AudioException("AudioDecoderFactory ERROR: ", new AudioException($"Failed to load Windows MP3 decoder: {ex.Message}", ex));
        }
#else
        // macOS, Linux, other desktop platforms: Use Mp3Decoder wrapper
        try
        {
            return new Mp3Decoder(filePath, targetSampleRate, targetChannels);
        }
        catch (Exception ex) when (!(ex is AudioException))
        {
            throw new AudioException("AudioDecoderFactory ERROR: ", new AudioException($"Failed to create MP3 decoder: {ex.Message}", ex));
        }
#endif
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
            // Mp3Decoder wrapper automatically selects platform-specific implementation
            return new Mp3Decoder(stream, ownsStream, targetSampleRate, targetChannels);
        }
        catch (Exception ex) when (!(ex is AudioException))
        {
            throw new AudioException("AudioDecoderFactory ERROR: ", new AudioException($"Failed to create MP3 decoder from stream: {ex.Message}", ex));
        }
    }
}
