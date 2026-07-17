using Ownaudio.Decoders;
using OwnaudioNET.Core;
using OwnaudioNET.Exceptions;
using System.Buffers;

namespace OwnaudioNET.Sources;

/// <summary>
/// Raw audio data extraction, uses a temp decoder so playback is untouched.
/// </summary>
public partial class FileSource
{
    #region Fields

    /// <summary>
    /// Source file path, "stream_source" for stream based sources.
    /// </summary>
    private string? _filePath;

    #endregion

    #region Data Extraction Methods

    /// <summary>
    /// Reads raw Float32 interleaved bytes from position, the whole rest of the file
    /// when duration is null.
    /// </summary>
    /// <param name="position"></param>
    /// <param name="duration"></param>
    /// <returns></returns>
    public byte[] GetByteAudioData(TimeSpan position, TimeSpan? duration = null)
    {
        ThrowIfDisposed();

        try
        {
            if (string.IsNullOrEmpty(_filePath)) return Array.Empty<byte>();

            using var _tempDecoder = AudioDecoderFactory.Create(_filePath, _streamInfo.SampleRate, _streamInfo.Channels);

            if (!_tempDecoder.TrySeek(position, out string _seekError))
                throw new AudioException($"Failed to seek to position {position}: {_seekError}");

            bool _readUntilEOF = duration == null;

            int _targetBytes = 0;
            if (!_readUntilEOF)
            {
                int _targetFrames = (int)(duration!.Value.TotalSeconds * _streamInfo.SampleRate);
                _targetBytes = _targetFrames * _streamInfo.Channels * sizeof(float);
                if (_targetBytes <= 0) return Array.Empty<byte>();
            }

            using var _memoryStream = new MemoryStream(_targetBytes > 0 ? _targetBytes : 65536);
            var _byteBuffer = ArrayPool<byte>.Shared.Rent(4096 * _streamInfo.Channels * sizeof(float));

            try
            {
                int _bytesWritten = 0;

                while (true)
                {
                    var _result = _tempDecoder.ReadFrames(_byteBuffer);
                    if (_result.IsEOF || !_result.IsSucceeded || _result.FramesRead == 0) break;

                    int _bytesRead = _result.FramesRead * _streamInfo.Channels * sizeof(float);

                    if (_readUntilEOF)
                    {
                        _memoryStream.Write(_byteBuffer, 0, _bytesRead);
                        _bytesWritten += _bytesRead;
                    }
                    else
                    {
                        int _toCopy = Math.Min(_bytesRead, _targetBytes - _bytesWritten);
                        _memoryStream.Write(_byteBuffer, 0, _toCopy);
                        _bytesWritten += _toCopy;
                        if (_bytesWritten >= _targetBytes) break;
                    }
                }

                return _memoryStream.ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(_byteBuffer);
            }
        }
        catch (Exception ex) when (ex is not AudioException)
        {
            throw new AudioException($"Failed to extract audio data: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Same as GetByteAudioData but returns Float32 samples.
    /// </summary>
    /// <param name="position"></param>
    /// <param name="duration"></param>
    /// <returns></returns>
    public float[] GetFloatAudioData(TimeSpan position, TimeSpan? duration = null)
    {
        byte[] _byteData = GetByteAudioData(position, duration);
        if (_byteData.Length == 0) return Array.Empty<float>();

        float[] _floatData = new float[_byteData.Length / sizeof(float)];
        Buffer.BlockCopy(_byteData, 0, _floatData, 0, _byteData.Length);
        return _floatData;
    }

    #endregion

    #region Output Level Monitoring

    /// <summary>
    /// Peak levels for the left and right channel while playing, 0.0 to 1.0.
    /// </summary>
    /// <returns></returns>
    public (float left, float right)? GetOutputLevels()
    {
        ThrowIfDisposed();

        return State == AudioState.Playing && RustTrack is not null ? OutputLevels : (0f, 0f);
    }

    #endregion
}
