using System;
using System.Runtime.InteropServices;
using Ownaudio;
using Ownaudio.Decoders;

namespace Ownaudio.OwnaudioNET.Tests.Characterization;

/// <summary>
/// A fully deterministic, seekable <see cref="IAudioDecoder"/> used as the golden
/// signal source for the WS0 (plan 14 / D.0) characterization tests.
/// </summary>
/// <remarks>
/// The decoder emits a bounded sawtooth whose value is a pure function of the absolute
/// frame index (<see cref="SampleAt"/>), identical on every channel. Because the value
/// uniquely identifies the source frame within a large prime window, tests can assert
/// exactly which frame region the playback pipeline is reading — making seek accuracy,
/// loop wrap-around and (at tempo 1.0, where <c>FileSource</c> bypasses SoundTouch)
/// sample-for-sample identity provable without any external audio file or timing jitter.
/// </remarks>
internal sealed class DeterministicSignalDecoder : IAudioDecoder
{
    /// <summary>
    /// Prime period of the sawtooth signal. A large prime keeps the value distinct across
    /// any realistic seek window while remaining bounded to <c>[0, 1)</c>.
    /// </summary>
    private const int Period = 997;

    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly long _totalFrames;
    private long _positionFrames;

    /// <summary>
    /// Initializes a new deterministic decoder with the given format and length.
    /// </summary>
    /// <param name="channels">Channel count of the emitted stream.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="totalFrames">Total number of frames before end-of-stream.</param>
    public DeterministicSignalDecoder(int channels, int sampleRate, long totalFrames)
    {
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (totalFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalFrames));

        _channels = channels;
        _sampleRate = sampleRate;
        _totalFrames = totalFrames;
        _positionFrames = 0;
    }

    /// <inheritdoc />
    public AudioStreamInfo StreamInfo => new AudioStreamInfo(
        channels: _channels,
        sampleRate: _sampleRate,
        duration: TimeSpan.FromSeconds((double)_totalFrames / _sampleRate),
        bitDepth: 32);

    /// <summary>
    /// The deterministic sample value emitted for a given absolute frame index.
    /// Tests call this to compute the expected signal at any playback position.
    /// </summary>
    /// <param name="frameIndex">Absolute frame index from the start of the stream.</param>
    /// <returns>The bounded sawtooth value in <c>[0, 1)</c>.</returns>
    public static float SampleAt(long frameIndex)
    {
        long phase = frameIndex % Period;
        if (phase < 0)
            phase += Period;
        return (float)phase / Period;
    }

    /// <inheritdoc />
    public AudioDecoderResult ReadFrames(byte[] buffer)
    {
        if (_positionFrames >= _totalFrames)
            return AudioDecoderResult.CreateEOF();

        Span<float> floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan());
        int capacityFrames = floats.Length / _channels;
        if (capacityFrames == 0)
            return AudioDecoderResult.CreateSuccess(0);

        int framesToWrite = (int)Math.Min(capacityFrames, _totalFrames - _positionFrames);
        for (int f = 0; f < framesToWrite; f++)
        {
            float value = SampleAt(_positionFrames + f);
            int baseIndex = f * _channels;
            for (int c = 0; c < _channels; c++)
                floats[baseIndex + c] = value;
        }

        _positionFrames += framesToWrite;
        double pts = (double)_positionFrames / _sampleRate * 1000.0;
        return AudioDecoderResult.CreateSuccess(framesToWrite, pts);
    }

    /// <inheritdoc />
    public bool TrySeek(TimeSpan position, out string error)
    {
        long target = (long)Math.Round(position.TotalSeconds * _sampleRate);
        _positionFrames = Math.Clamp(target, 0, _totalFrames);
        error = string.Empty;
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
