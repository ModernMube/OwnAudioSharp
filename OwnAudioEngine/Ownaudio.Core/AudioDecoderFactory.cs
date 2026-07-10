using System;
using System.IO;
using Logger;
using Ownaudio.Core;
using Ownaudio.Core.Common;

namespace Ownaudio.Decoders;

/// <summary>
/// Factory for creating audio decoders.
/// </summary>
/// <remarks>
/// As of 4.0 all decoding is handled by the native Rust (Symphonia) decoder, registered by the
/// OwnaudioNET engine layer at module-init time. The built-in managed WAV/MP3/FLAC decoders and the
/// optional FFmpeg fallback were removed; the native decoder reads MP3, FLAC, WAV (PCM/ADPCM), AAC,
/// MP4/M4A, OGG/Vorbis and AIFF out of the box.
/// </remarks>
public static class AudioDecoderFactory
{
    /// <summary>
    /// The native decoder factory registered by the OwnaudioNET engine layer, or
    /// <see langword="null"/> when that assembly has not been loaded.
    /// </summary>
    private static Func<string, int, int, IAudioDecoder>? _nativeDecoderFactory;

    /// <summary>
    /// Registers the native decoder factory. Called from the OwnaudioNET engine layer at module-init
    /// time so callers of this factory receive the native decoder without any setup code.
    /// </summary>
    /// <param name="factory">A delegate that opens a decoder for a file path, target sample rate and
    /// target channel count.</param>
    public static void RegisterNativeDecoder(Func<string, int, int, IAudioDecoder> factory)
        => _nativeDecoderFactory = factory;

    /// <summary>
    /// Creates a native audio decoder for the specified file. The format is auto-detected from the
    /// file content by the native decoder.
    /// </summary>
    /// <param name="filePath">Path to the audio file.</param>
    /// <param name="targetSampleRate">Target sample rate in Hz (0 = use source rate, no resampling).</param>
    /// <param name="targetChannels">Target channel count (0 = use source channels, no conversion).</param>
    /// <returns>A native decoder instance for the file.</returns>
    /// <exception cref="AudioException">
    /// Thrown when the path is null/empty, the file does not exist, the native decoder is not
    /// available, or the file cannot be decoded.
    /// </exception>
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
    /// Creates a native audio decoder for the specified stream.
    /// </summary>
    /// <remarks>
    /// The native decoder is file-based, so the stream is buffered to a temporary file that is
    /// deleted when the returned decoder is disposed.
    /// </remarks>
    /// <param name="stream">Stream containing audio data. Must support reading.</param>
    /// <param name="format">Audio format of the stream, used only as the temporary file's extension
    /// hint; the native decoder auto-detects the actual format from the content.</param>
    /// <param name="targetSampleRate">Target sample rate in Hz (0 = use source rate, no resampling).</param>
    /// <param name="targetChannels">Target channel count (0 = use source channels, no conversion).</param>
    /// <returns>A native decoder instance backed by a temporary file.</returns>
    /// <exception cref="AudioException">
    /// Thrown when the stream is null or not readable, or when the buffered content cannot be decoded.
    /// </exception>
    public static IAudioDecoder Create(Stream stream, AudioFormat format, int targetSampleRate = 0, int targetChannels = 0)
    {
        if (stream == null)
            throw new AudioException("AudioDecoderFactory ERROR: ", new ArgumentNullException(nameof(stream)));

        if (!stream.CanRead)
            throw new AudioException("AudioDecoderFactory ERROR: ", new ArgumentException("Stream must support reading.", nameof(stream)));

        string tempPath = Path.Combine(Path.GetTempPath(), $"ownaudio_stream_{Guid.NewGuid():N}{ExtensionFor(format)}");

        try
        {
            using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                if (stream.CanSeek)
                    stream.Position = 0;
                stream.CopyTo(file);
            }

            var inner = Create(tempPath, targetSampleRate, targetChannels);
            return new TempFileDecoder(inner, tempPath);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Detects the audio format from a stream's header (magic bytes), restoring the stream position.
    /// </summary>
    /// <param name="stream">Stream to read from. The position is restored after detection.</param>
    /// <returns>The detected format, or <see cref="AudioFormat.Unknown"/>.</returns>
    /// <exception cref="AudioException">Thrown when the stream is null.</exception>
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
    /// Maps an <see cref="AudioFormat"/> to a temporary-file extension hint for stream decoding.
    /// </summary>
    /// <param name="format">The format to map.</param>
    /// <returns>A file extension including the leading dot.</returns>
    private static string ExtensionFor(AudioFormat format) => format switch
    {
        AudioFormat.Wav => ".wav",
        AudioFormat.Mp3 => ".mp3",
        AudioFormat.Flac => ".flac",
        _ => ".bin"
    };

    /// <summary>
    /// Wraps a file-based native decoder created from a buffered stream, deleting the temporary file
    /// when disposed.
    /// </summary>
    private sealed class TempFileDecoder : IAudioDecoder
    {
        /// <summary>The underlying native decoder reading the temporary file.</summary>
        private readonly IAudioDecoder _inner;

        /// <summary>The temporary file to delete on dispose.</summary>
        private readonly string _tempPath;

        /// <summary>Whether this wrapper has been disposed.</summary>
        private bool _disposed;

        /// <summary>
        /// Initializes a new wrapper over <paramref name="inner"/>, taking ownership of the temporary
        /// file at <paramref name="tempPath"/>.
        /// </summary>
        /// <param name="inner">The native decoder to wrap.</param>
        /// <param name="tempPath">The temporary file to delete on dispose.</param>
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
            if (_disposed)
                return;

            _disposed = true;
            _inner.Dispose();

            try { File.Delete(_tempPath); } catch { /* best effort */ }
        }
    }
}
