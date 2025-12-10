using System.Text;
using Ownaudio.Core;

namespace OwnaudioNET.Mixing;

/// <summary>
/// Writes audio samples to a WAV file.
/// Supports Float32 PCM format for high-quality recording.
///
/// WAV File Format:
/// - RIFF header (12 bytes): "RIFF" + file_size + "WAVE"
/// - fmt chunk (24 bytes): Format info (Float32 PCM)
/// - data chunk (8 bytes + samples): "data" + data_size + samples
///
/// Thread Safety: NOT thread-safe - caller must synchronize access.
/// </summary>
public sealed class WaveFileWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private readonly AudioConfig _config;
    private long _dataChunkSizePosition;
    private long _totalSamplesWritten;
    private bool _disposed;

    /// <summary>
    /// Gets the audio configuration being used.
    /// </summary>
    public AudioConfig Config => _config;

    /// <summary>
    /// Gets the total number of samples written.
    /// </summary>
    public long TotalSamplesWritten => _totalSamplesWritten;

    /// <summary>
    /// Gets the total number of frames written.
    /// </summary>
    public long TotalFramesWritten => _totalSamplesWritten / _config.Channels;

    /// <summary>
    /// Gets the current duration in seconds.
    /// </summary>
    public double Duration => (double)TotalFramesWritten / _config.SampleRate;

    /// <summary>
    /// Initializes a new instance of the WaveFileWriter class.
    /// </summary>
    /// <param name="filePath">Path to the output WAV file.</param>
    /// <param name="config">Audio configuration specification.</param>
    /// <exception cref="ArgumentException">Thrown when filePath is invalid.</exception>
    /// <exception cref="IOException">Thrown when file cannot be created.</exception>
    public WaveFileWriter(string filePath, AudioConfig config)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

        _config = config ?? throw new ArgumentNullException(nameof(config));
        _totalSamplesWritten = 0;

        try
        {
            // Create file stream
            _stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: false);

            // Write WAV header
            WriteWavHeader();
        }
        catch
        {
            _writer?.Dispose();
            _stream?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes audio samples to the WAV file.
    /// </summary>
    /// <param name="samples">Audio samples in Float32 format, interleaved.</param>
    /// <exception cref="ObjectDisposedException">Thrown if writer is disposed.</exception>
    /// <exception cref="IOException">Thrown if write operation fails.</exception>
    public void WriteSamples(ReadOnlySpan<float> samples)
    {
        ThrowIfDisposed();

        if (samples.IsEmpty)
            return;

        try
        {
            // Write samples as Float32 (little-endian)
            for (int i = 0; i < samples.Length; i++)
            {
                _writer.Write(samples[i]);
            }

            _totalSamplesWritten += samples.Length;
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to write samples to WAV file: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Writes the WAV file header.
    /// Format: Float32 PCM (IEEE Float)
    /// </summary>
    private void WriteWavHeader()
    {
        // RIFF header
        _writer.Write(Encoding.ASCII.GetBytes("RIFF")); // ChunkID
        _writer.Write(0); // ChunkSize (placeholder, will be updated in Dispose)
        _writer.Write(Encoding.ASCII.GetBytes("WAVE")); // Format

        // fmt chunk
        _writer.Write(Encoding.ASCII.GetBytes("fmt ")); // Subchunk1ID
        _writer.Write(16); // Subchunk1Size (16 for PCM)
        _writer.Write((ushort)3); // AudioFormat (3 = IEEE Float)
        _writer.Write((ushort)_config.Channels); // NumChannels
        _writer.Write(_config.SampleRate); // SampleRate
        _writer.Write(_config.SampleRate * _config.Channels * 4); // ByteRate (SampleRate * Channels * BytesPerSample)
        _writer.Write((ushort)(_config.Channels * 4)); // BlockAlign (Channels * BytesPerSample)
        _writer.Write((ushort)32); // BitsPerSample (32 for Float32)

        // data chunk
        _writer.Write(Encoding.ASCII.GetBytes("data")); // Subchunk2ID
        _dataChunkSizePosition = _stream.Position;
        _writer.Write(0); // Subchunk2Size (placeholder, will be updated in Dispose)
    }

    /// <summary>
    /// Updates the WAV header with final sizes.
    /// Called during disposal to finalize the file.
    /// </summary>
    private void UpdateWavHeader()
    {
        try
        {
            // Calculate sizes
            long dataChunkSize = _totalSamplesWritten * 4; // 4 bytes per Float32 sample
            long fileSize = 36 + dataChunkSize; // 36 = header size without RIFF header (8 bytes)

            // Update ChunkSize (file size - 8)
            _stream.Seek(4, SeekOrigin.Begin);
            _writer.Write((int)(fileSize - 8));

            // Update Subchunk2Size (data size)
            _stream.Seek(_dataChunkSizePosition, SeekOrigin.Begin);
            _writer.Write((int)dataChunkSize);

            // Flush to ensure all data is written
            _writer.Flush();
            _stream.Flush();
        }
        catch
        {
            // Ignore errors during header update
            // File may be incomplete but partially usable
        }
    }

    /// <summary>
    /// Throws ObjectDisposedException if disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WaveFileWriter));
    }

    /// <summary>
    /// Disposes the writer and finalizes the WAV file.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            // Update header with final sizes
            UpdateWavHeader();
        }
        finally
        {
            _writer?.Dispose();
            _stream?.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// Returns a string representation of the writer's state.
    /// </summary>
    public override string ToString()
    {
        return $"WaveFileWriter: {_config.SampleRate}Hz {_config.Channels}ch, Samples: {_totalSamplesWritten}, Duration: {Duration:F2}s";
    }
}
