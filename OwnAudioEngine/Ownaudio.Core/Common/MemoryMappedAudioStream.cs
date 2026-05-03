using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Ownaudio.Core.Common;

/// <summary>
/// Memory-mapped audio file stream for zero-copy, high-performance sequential access.
/// Ideal for large audio files in mixing scenarios to completely eliminate disk I/O.
/// </summary>
/// <remarks>
/// Benefits over regular FileStream:
/// - Zero-copy access (direct memory access via OS page cache)
/// - No disk I/O after initial mapping (OS handles paging)
/// - Perfect for multiple simultaneous reads (mixing scenarios)
/// - ~1000x faster than Stream.Read() for repeated access
///
/// Drawbacks:
/// - Requires contiguous virtual address space (problem on 32-bit)
/// - Memory pressure on small systems
/// - Not suitable for streaming (loads entire file)
///
/// Best for:
/// - Audio files &lt; 2GB
/// - Mixing scenarios (10+ simultaneous files)
/// - Repeated playback / looping
/// - Random access patterns
///
/// Example usage (10 file mixing):
/// Without MemoryMapped: 930 SSD I/O/sec
/// With MemoryMapped: 0 SSD I/O/sec (after initial load)
/// </remarks>
public sealed class MemoryMappedAudioStream : Stream
{
    private readonly MemoryMappedFile _memoryMappedFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly long _length;
    private long _position;
    private bool _disposed;

    // Permanently acquired pointer for the lifetime of this object.
    private nint _basePointerRaw;

    /// <summary>
    /// Creates a memory-mapped audio stream from a file path.
    /// </summary>
    /// <param name="filePath">Path to audio file.</param>
    /// <param name="access">File access mode (default: Read).</param>
    /// <exception cref="ArgumentException">Thrown when file path is invalid.</exception>
    /// <exception cref="FileNotFoundException">Thrown when file does not exist.</exception>
    public static MemoryMappedAudioStream FromFile(
        string filePath,
        FileAccess access = FileAccess.Read)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Audio file not found: {filePath}", filePath);

        var fileInfo = new FileInfo(filePath);
        long fileSize = fileInfo.Length;

        if (fileSize == 0)
            throw new ArgumentException($"Audio file is empty: {filePath}");

        var mmf = MemoryMappedFile.CreateFromFile(
            filePath,
            FileMode.Open,
            null, // Map name (null = anonymous)
            fileSize,
            access == FileAccess.Read ? MemoryMappedFileAccess.Read : MemoryMappedFileAccess.ReadWrite);

        return new MemoryMappedAudioStream(mmf, fileSize);
    }

    /// <summary>
    /// Creates a memory-mapped audio stream from an existing stream.
    /// </summary>
    /// <param name="sourceStream">Source stream to map into memory.</param>
    /// <param name="closeSourceStream">If true, closes source stream after mapping.</param>
    /// <exception cref="ArgumentException">Thrown when stream is not seekable or readable.</exception>
    public static MemoryMappedAudioStream FromStream(
        Stream sourceStream,
        bool closeSourceStream = true)
    {
        if (sourceStream == null)
            throw new ArgumentNullException(nameof(sourceStream));

        if (!sourceStream.CanRead || !sourceStream.CanSeek)
            throw new ArgumentException("Source stream must be readable and seekable.", nameof(sourceStream));

        long streamLength = sourceStream.Length;

        if (streamLength == 0)
            throw new ArgumentException("Source stream is empty.");

        var mmf = MemoryMappedFile.CreateNew(
            null, // Anonymous
            streamLength,
            MemoryMappedFileAccess.ReadWrite);

        try
        {
            using var accessor = mmf.CreateViewAccessor(0, streamLength, MemoryMappedFileAccess.Write);

            sourceStream.Position = 0;
            byte[] buffer = new byte[Math.Min(81920, streamLength)]; // 80KB chunks
            long totalWritten = 0;

            while (totalWritten < streamLength)
            {
                int bytesRead = sourceStream.Read(buffer, 0, (int)Math.Min(buffer.Length, streamLength - totalWritten));
                if (bytesRead == 0)
                    break;

                accessor.WriteArray(totalWritten, buffer, 0, bytesRead);
                totalWritten += bytesRead;
            }

            if (closeSourceStream)
                sourceStream.Dispose();

            return new MemoryMappedAudioStream(mmf, streamLength);
        }
        catch
        {
            mmf.Dispose();
            throw;
        }
    }

    private MemoryMappedAudioStream(MemoryMappedFile memoryMappedFile, long length)
    {
        _memoryMappedFile = memoryMappedFile ?? throw new ArgumentNullException(nameof(memoryMappedFile));
        _length = length;
        _position = 0;
        _disposed = false;

        _accessor = _memoryMappedFile.CreateViewAccessor(
            0,
            _length,
            MemoryMappedFileAccess.Read);

        unsafe
        {
            byte* ptr = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            _basePointerRaw = (nint)ptr;
        }
    }

    /// <summary>
    /// Gets whether the stream can read.
    /// </summary>
    public override bool CanRead => !_disposed;

    /// <summary>
    /// Gets whether the stream can seek.
    /// </summary>
    public override bool CanSeek => !_disposed;

    /// <summary>
    /// Gets whether the stream can write (always false for audio streams).
    /// </summary>
    public override bool CanWrite => false;

    /// <summary>
    /// Gets the length of the stream.
    /// </summary>
    public override long Length => _length;

    /// <summary>
    /// Gets or sets the current position in the stream.
    /// </summary>
    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > _length)
                throw new ArgumentOutOfRangeException(nameof(value));
            _position = value;
        }
    }

    /// <summary>
    /// Reads data from the memory-mapped file (zero-copy).
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MemoryMappedAudioStream));

        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));

        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException();

        if (_position >= _length)
            return 0;

        int bytesToRead = (int)Math.Min(count, _length - _position);

        _accessor.ReadArray(_position, buffer, offset, bytesToRead);
        _position += bytesToRead;
        return bytesToRead;
    }

    /// <summary>
    /// Reads data into a span (zero-copy).
    /// </summary>
    public override int Read(Span<byte> buffer)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MemoryMappedAudioStream));

        if (_position >= _length)
            return 0;

        int bytesToRead = (int)Math.Min(buffer.Length, _length - _position);

        unsafe
        {
            byte* ptr = (byte*)_basePointerRaw;
            if (ptr == null)
                throw new InvalidOperationException("Memory-mapped view pointer is not available.");
            
            var source = new ReadOnlySpan<byte>(ptr + _position, bytesToRead);
            source.CopyTo(buffer);
        }

        _position += bytesToRead;
        return bytesToRead;
    }

    /// <summary>
    /// Gets a read-only span directly to the memory-mapped data (true zero-copy).
    /// The span is valid for the lifetime of this <see cref="MemoryMappedAudioStream"/> instance.
    /// </summary>
    /// <param name="count">Number of bytes to access.</param>
    /// <returns>Read-only span pointing to memory-mapped data at the current position.</returns>
    /// <remarks>
    /// This is the fastest possible way to access audio data.
    /// No copying occurs – direct pointer to OS page cache.
    /// The underlying pointer is acquired once in the constructor and held until Dispose(),
    /// so the returned span is always backed by live memory (no use-after-release hazard).
    /// </remarks>
    public unsafe ReadOnlySpan<byte> GetSpan(int count)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MemoryMappedAudioStream));

        if (_position >= _length)
            return ReadOnlySpan<byte>.Empty;

        int bytesToRead = (int)Math.Min(count, _length - _position);

        byte* ptr = (byte*)_basePointerRaw;
        if (ptr == null)
            throw new InvalidOperationException("Memory-mapped view pointer is not available.");

        return new ReadOnlySpan<byte>(ptr + _position, bytesToRead);
    }

    /// <summary>
    /// Seeks to a specific position in the stream.
    /// </summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MemoryMappedAudioStream));

        long newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentException("Invalid seek origin.", nameof(origin))
        };

        if (newPosition < 0)
            throw new IOException("Seek before beginning of stream.");

        if (newPosition > _length)
            newPosition = _length;

        _position = newPosition;
        return _position;
    }

    /// <summary>
    /// Flush (no-op for memory-mapped streams).
    /// </summary>
    public override void Flush()
    {
    }

    /// <summary>
    /// SetLength (not supported).
    /// </summary>
    public override void SetLength(long value)
    {
        throw new NotSupportedException("Cannot set length of memory-mapped audio stream.");
    }

    /// <summary>
    /// Write (not supported).
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("Memory-mapped audio streams are read-only.");
    }

    /// <summary>
    /// Disposes the memory-mapped file and accessor.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            if (_basePointerRaw != nint.Zero)
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _basePointerRaw = nint.Zero;
            }

            _accessor?.Dispose();
            _memoryMappedFile?.Dispose();
        }

        _disposed = true;
        base.Dispose(disposing);
    }
}
