using System.Runtime.InteropServices;
using System.Text;
using Ownaudio.Core;

namespace OwnaudioNET.Mixing;

/// <summary>
/// Dumps float samples into a WAV file as Float32 PCM (IEEE float). The RIFF and
/// data chunk sizes are patched in on Dispose. Not thread-safe — sync it yourself.
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
    /// Audio config we're writing with.
    /// </summary>
    public AudioConfig Config => _config;

    /// <summary>
    /// Samples written so far.
    /// </summary>
    public long TotalSamplesWritten => _totalSamplesWritten;

    /// <summary>
    /// Frames written so far.
    /// </summary>
    public long TotalFramesWritten => _totalSamplesWritten / _config.Channels;

    /// <summary>
    /// Current length in seconds.
    /// </summary>
    public double Duration => (double)TotalFramesWritten / _config.SampleRate;

    /// <summary>
    /// Creates the file and lays down the header.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="config"></param>
    public WaveFileWriter(string filePath, AudioConfig config)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

        _config = config ?? throw new ArgumentNullException(nameof(config));

        try
        {
            _stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: false);
            _writeWavHeader();
        }
        catch
        {
            _writer?.Dispose();
            _stream?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Appends interleaved Float32 samples.
    /// </summary>
    /// <remarks>
    /// float is already little-endian Float32 in memory on every supported runtime,
    /// so we blit the whole span in one write. The per-sample loop is only there for
    /// a hypothetical big-endian box.
    /// </remarks>
    /// <param name="samples"></param>
    public void WriteSamples(ReadOnlySpan<float> samples)
    {
        _throwIfDisposed();

        if (samples.IsEmpty)
            return;

        try
        {
            if (BitConverter.IsLittleEndian)
            {
                _writer.Flush();
                _stream.Write(MemoryMarshal.AsBytes(samples));
            }
            else
            {
                for (int i = 0; i < samples.Length; i++)
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
    /// Lays down the RIFF/fmt/data header, Float32 PCM, sizes as placeholders.
    /// </summary>
    private void _writeWavHeader()
    {
        // RIFF header
        _writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        _writer.Write(0);
        _writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        _writer.Write(Encoding.ASCII.GetBytes("fmt "));
        _writer.Write(16);
        _writer.Write((ushort)3);
        _writer.Write((ushort)_config.Channels);
        _writer.Write(_config.SampleRate);
        _writer.Write(_config.SampleRate * _config.Channels * 4);
        _writer.Write((ushort)(_config.Channels * 4));
        _writer.Write((ushort)32);

        // data chunk
        _writer.Write(Encoding.ASCII.GetBytes("data"));
        _dataChunkSizePosition = _stream.Position;
        _writer.Write(0);
    }

    /// <summary>
    /// Patches the RIFF and data sizes into the header on close.
    /// </summary>
    private void _updateWavHeader()
    {
        try
        {
            long dataChunkSize = _totalSamplesWritten * 4;
            long fileSize = 36 + dataChunkSize;

            _stream.Seek(4, SeekOrigin.Begin);
            _writer.Write((int)(fileSize - 8));
            _stream.Seek(_dataChunkSizePosition, SeekOrigin.Begin);
            _writer.Write((int)dataChunkSize);
            _writer.Flush();
            _stream.Flush();
        }
        catch { }
    }

    /// <summary>
    /// Barks if we're already disposed.
    /// </summary>
    private void _throwIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WaveFileWriter));
    }

    /// <summary>
    /// Finalizes the header and closes the file.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            _updateWavHeader();
        }
        finally
        {
            _writer?.Dispose();
            _stream?.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// One-line state dump.
    /// </summary>
    public override string ToString()
    {
        return $"WaveFileWriter: {_config.SampleRate}Hz {_config.Channels}ch, Samples: {_totalSamplesWritten}, Duration: {Duration:F2}s";
    }
}
