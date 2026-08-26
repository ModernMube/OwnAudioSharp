using Logger;
using Ownaudio.Decoders;
using OwnaudioNET.Core;
using OwnaudioNET.Exceptions;
using System.Buffers;
using System.Runtime.InteropServices;

namespace OwnaudioNET.Sources;

/// <summary>
/// Raw audio data extraction, uses a temp decoder so playback is untouched.
/// </summary>
public partial class FileSource
{
    #region Fields

    /// <summary>
    /// Bucket width the peak scan starts on when the length is unknown, halved from there.
    /// </summary>
    private const int SeedPeakStep = 1024;

    /// <summary>
    /// Source file path, "stream_source" for stream based sources.
    /// </summary>
    private string? _filePath;

    #endregion

    #region Data Extraction Methods

    /// <summary>
    /// Gets one block of interleaved Float32 out of the walk. Return false to stop it.
    /// </summary>
    private delegate bool BlockHandler(ReadOnlySpan<float> block);

    /// <summary>
    /// Reads raw Float32 interleaved bytes from position, the whole rest of the file
    /// when duration is null.
    /// </summary>
    /// <param name="position"></param>
    /// <param name="duration"></param>
    /// <returns></returns>
    public byte[] GetByteAudioData(TimeSpan position, TimeSpan? duration = null)
    {
        float[] _samples = GetFloatAudioData(position, duration);
        if (_samples.Length == 0) return Array.Empty<byte>();

        long _size = (long)_samples.Length * sizeof(float);
        if (_size > Array.MaxLength)
            throw new AudioException($"{_samples.Length} samples do not fit a byte[], ask for a shorter duration");

        byte[] _bytes = new byte[_size];
        Buffer.BlockCopy(_samples, 0, _bytes, 0, _bytes.Length);
        return _bytes;
    }

    /// <summary>
    /// Float32 samples from position, to the end of the file when duration is null. Sized up
    /// front off the stream info so nothing holds the file twice over, and grown instead when
    /// the container will not say how long it is or its header lied.
    /// </summary>
    /// <param name="position"></param>
    /// <param name="duration"></param>
    /// <returns></returns>
    public float[] GetFloatAudioData(TimeSpan position, TimeSpan? duration = null)
    {
        ThrowIfDisposed();

        long _wanted = _samplesFor(position, duration);
        if (duration.HasValue && _wanted <= 0) return Array.Empty<float>();

        float[] _data = new float[_wanted > 0 ? _wanted : _seedSamples()];
        bool _capped = duration.HasValue;
        int _written = 0;

        _walk(position, _block =>
        {
            if (_capped && _written + _block.Length > _data.Length)
            {
                _block.Slice(0, _data.Length - _written).CopyTo(_data.AsSpan(_written));
                _written = _data.Length;
                return false;
            }

            //An unknown or lying duration must not cost us the tail
            if (_written + _block.Length > _data.Length)
                Array.Resize(ref _data, _written + _block.Length * 9);

            _block.CopyTo(_data.AsSpan(_written));
            _written += _block.Length;
            return true;
        });

        return _written == _data.Length ? _data : _data[.._written];
    }

    /// <summary>
    /// Signed peaks for a waveform display: one decoder pass, one array of exactly points
    /// floats, nothing file-sized in between. Each bucket keeps the loudest sample that fell
    /// in it, sign and all. A stream of unknown length halves its own resolution as it goes,
    /// so the scan never needs a length up front.
    /// </summary>
    /// <param name="points"></param>
    /// <returns></returns>
    public float[] GetPeaks(int points)
    {
        ThrowIfDisposed();
        if (points <= 0) return Array.Empty<float>();

        long _total = _samplesFor(TimeSpan.Zero, null);
        int _step = _total > 0 ? (int)Math.Max(1, (_total + points - 1) / points) : SeedPeakStep;
        float[] _peaks = new float[points];

        int _at = 0, _inBucket = 0;
        float _peak = 0f;

        _walk(TimeSpan.Zero, _block =>
        {
            foreach (float sample in _block)
            {
                if (Math.Abs(sample) > Math.Abs(_peak)) _peak = sample;
                if (++_inBucket < _step) continue;

                if (_at == _peaks.Length && _peaks.Length > 1)
                {
                    _at = _fold(_peaks);
                    _step *= 2;
                    continue;
                }

                if (_at < _peaks.Length) _peaks[_at++] = _peak;
                else if (Math.Abs(_peak) > Math.Abs(_peaks[_at - 1])) _peaks[_at - 1] = _peak;

                _peak = 0f;
                _inBucket = 0;
            }
            return true;
        });

        if (_inBucket > 0 && _at < _peaks.Length) _peaks[_at++] = _peak;

        return _at == _peaks.Length ? _peaks : _peaks[.._at];
    }

    /// <summary>
    /// Halves the resolution in place, each pair collapsing to its loudest, and reports the
    /// new fill. Lets an unknown length keep being scanned without a bigger array.
    /// </summary>
    private static int _fold(float[] peaks)
    {
        int _half = peaks.Length / 2;

        for (int i = 0; i < _half; i++)
            peaks[i] = Math.Abs(peaks[i * 2]) >= Math.Abs(peaks[i * 2 + 1]) ? peaks[i * 2] : peaks[i * 2 + 1];

        if ((peaks.Length & 1) == 1 && Math.Abs(peaks[^1]) > Math.Abs(peaks[_half - 1]))
            peaks[_half - 1] = peaks[^1];

        return _half;
    }

    /// <summary>
    /// One temp decoder from position, blocks handed to onBlock until it says stop or the
    /// file runs out. Playback is untouched - this is a decoder of its own.
    /// </summary>
    private void _walk(TimeSpan position, BlockHandler onBlock)
    {
        if (string.IsNullOrEmpty(_filePath)) return;

        try
        {
            using var _tempDecoder = AudioDecoderFactory.Create(_filePath, _streamInfo.SampleRate, _streamInfo.Channels);

            if (!_tempDecoder.TrySeek(position, out string _seekError))
                throw new AudioException($"Failed to seek to position {position}: {_seekError}");

            var _byteBuffer = ArrayPool<byte>.Shared.Rent(4096 * _streamInfo.Channels * sizeof(float));

            try
            {
                while (true)
                {
                    var _result = _tempDecoder.ReadFrames(_byteBuffer);
                    if (_result.IsEOF || !_result.IsSucceeded || _result.FramesRead == 0) break;

                    int _bytesRead = _result.FramesRead * _streamInfo.Channels * sizeof(float);
                    if (!onBlock(MemoryMarshal.Cast<byte, float>(_byteBuffer.AsSpan(0, _bytesRead)))) break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(_byteBuffer);
            }
        }
        catch (Exception ex) when (ex is not AudioException)
        {
            Log.Error($"[FileSource] Raw data extraction from '{_filePath}' failed", ex);
            throw new AudioException($"Failed to extract audio data: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// How many interleaved samples that slice of the file is worth, 0 when the container
    /// gave no duration to measure it against.
    /// </summary>
    private long _samplesFor(TimeSpan position, TimeSpan? duration)
    {
        TimeSpan _span = duration ?? _streamInfo.Duration - position;
        if (_span <= TimeSpan.Zero) return 0;

        return (long)(_span.TotalSeconds * _streamInfo.SampleRate) * _streamInfo.Channels;
    }

    /// <summary>
    /// One second's worth, what a read starts on when it cannot size itself up front.
    /// </summary>
    private long _seedSamples() => Math.Max(4096, (long)_streamInfo.SampleRate * _streamInfo.Channels);

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
