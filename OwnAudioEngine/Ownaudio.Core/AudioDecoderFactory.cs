using System;
using System.IO;
using Logger;
using Ownaudio.Core;
using Ownaudio.Core.Common;

namespace Ownaudio.Decoders;

/// <summary>
/// Hands out decoders. Since 4.0 there is exactly one: the native Rust/Symphonia one,
/// registered by the OwnaudioNET layer at module init. It eats mp3, flac, wav, aac,
/// m4a, ogg and aiff on its own — the old managed decoders and the FFmpeg fallback are gone.
/// </summary>
public static class AudioDecoderFactory
{
    /// <summary>
    /// The native factory, or null while the engine assembly isn't loaded yet.
    /// </summary>
    private static Func<string, int, int, IAudioDecoder>? _nativeDecoderFactory;

    /// <summary>
    /// Called from the engine layer's module initializer, so nobody has to wire this up by hand.
    /// The delegate takes file path, target sample rate and target channel count.
    /// </summary>
    public static void RegisterNativeDecoder(Func<string, int, int, IAudioDecoder> factory)
        => _nativeDecoderFactory = factory;

    /// <summary>
    /// Opens a decoder for a file — format is sniffed natively from the content.
    /// targetSampleRate/targetChannels of 0 mean "leave it as the source has it".
    /// </summary>
    /// <exception cref="AudioException">Bad path, missing file, no native decoder, or it choked.</exception>
    public static IAudioDecoder Create(string filePath, int targetSampleRate = 0, int targetChannels = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new AudioException("AudioDecoderFactory ERROR: ", new ArgumentNullException(nameof(filePath)));

        if (!File.Exists(filePath))
            throw new AudioException("AudioDecoderFactory ERROR: ", new FileNotFoundException($"Audio file not found: {filePath}", filePath));

        var factory = _nativeDecoderFactory
            ?? throw new AudioException("AudioDecoderFactory ERROR: ",
                new AudioException("The native audio decoder is not available. Ensure the OwnaudioNET engine assembly is loaded."));

        try
        {
            var decoder = factory(filePath, targetSampleRate, targetChannels);
            Log.Info($"Using native decoder for '{Path.GetExtension(filePath)}' file");
            return decoder;
        }
        catch (Exception ex) when (ex is not AudioException)
        {
            throw new AudioException("AudioDecoderFactory ERROR: ",
                new AudioException($"Native decoder failed for {filePath}: {ex.Message}", ex));
        }
    }

    /// <summary>
    /// Stream flavour. The native side is file-based, so we spill the stream into a temp
    /// file and nuke it when the decoder gets disposed. format is only an extension hint.
    /// </summary>
    /// <exception cref="AudioException">Null/unreadable stream, or the content won't decode.</exception>
    public static IAudioDecoder Create(Stream stream, AudioFormat format, int targetSampleRate = 0, int targetChannels = 0)
    {
        if (stream == null)
            throw new AudioException("AudioDecoderFactory ERROR: ", new ArgumentNullException(nameof(stream)));

        if (!stream.CanRead)
            throw new AudioException("AudioDecoderFactory ERROR: ", new ArgumentException("Stream must support reading.", nameof(stream)));

        string tempPath = Path.Combine(Path.GetTempPath(), $"ownaudio_stream_{Guid.NewGuid():N}{_extensionFor(format)}");

        try
        {
            using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                if (stream.CanSeek) stream.Position = 0;
                stream.CopyTo(file);
            }

            return new TempFileDecoder(Create(tempPath, targetSampleRate, targetChannels), tempPath);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Magic-byte sniffing. Stream position is put back where we found it.
    /// </summary>
    /// <returns>The detected format, or <see cref="AudioFormat.Unknown"/>.</returns>
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
            if (stream.Read(header) < 12)
                return AudioFormat.Unknown;

            if (header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' &&
                header[8] == 'W' && header[9] == 'A' && header[10] == 'V' && header[11] == 'E')
                return AudioFormat.Wav;

            if ((header[0] == 0xFF && (header[1] & 0xE0) == 0xE0) ||
                (header[0] == 'I' && header[1] == 'D' && header[2] == '3'))
                return AudioFormat.Mp3;

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
    /// Extension hint for the temp file, dot included.
    /// </summary>
    private static string _extensionFor(AudioFormat format) => format switch
    {
        AudioFormat.Wav => ".wav",
        AudioFormat.Mp3 => ".mp3",
        AudioFormat.Flac => ".flac",
        _ => ".bin"
    };

    /// <summary>
    /// Passthrough decoder that owns the temp file behind it.
    /// </summary>
    private sealed class TempFileDecoder : IAudioDecoder
    {
        private readonly IAudioDecoder _inner;
        private readonly string _tempPath;
        private bool _disposed;

        /// <summary>
        /// Takes over the temp file at tempPath, deletes it on dispose.
        /// </summary>
        public TempFileDecoder(IAudioDecoder inner, string tempPath)
        {
            _inner = inner;
            _tempPath = tempPath;
        }

        /// <inheritdoc/>
        public AudioStreamInfo StreamInfo => _inner.StreamInfo;

        /// <inheritdoc/>
        public AudioDecoderResult ReadFrames(byte[] buffer) => _inner.ReadFrames(buffer);

        /// <inheritdoc/>
        public bool TrySeek(TimeSpan position, out string error) => _inner.TrySeek(position, out error);

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _inner.Dispose();

            try { File.Delete(_tempPath); } catch { }
        }
    }
}
