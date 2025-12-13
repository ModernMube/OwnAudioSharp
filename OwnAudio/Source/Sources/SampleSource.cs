using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Events;

namespace OwnaudioNET.Sources;

/// <summary>
/// Audio source that plays audio samples from memory.
/// Provides fast, low-latency playback of pre-loaded or dynamically generated audio.
/// </summary>
public sealed class SampleSource : BaseAudioSource
{
    private readonly AudioConfig _config;
    private float[] _sampleData;
    private int _readPosition;
    private readonly object _dataLock = new();
    private double _duration;
    private bool _disposed;

    public override AudioConfig Config => _config;

    public override AudioStreamInfo StreamInfo => new AudioStreamInfo(
        channels: _config.Channels,
        sampleRate: _config.SampleRate,
        duration: TimeSpan.FromSeconds(_duration));

    public override double Position
    {
        get
        {
            lock (_dataLock)
            {
                if (_sampleData == null || _sampleData.Length == 0)
                    return 0.0;
                int currentFrame = _readPosition / _config.Channels;
                return (double)currentFrame / _config.SampleRate;
            }
        }
    }

    public override double Duration => _duration;

    public override bool IsEndOfStream
    {
        get
        {
            lock (_dataLock)
            {
                return _readPosition >= (_sampleData?.Length ?? 0) && !Loop;
            }
        }
    }

    public bool AllowDynamicUpdate { get; set; } = false;

    public SampleSource(float[] samples, AudioConfig config)
    {
        if (samples == null || samples.Length == 0)
            throw new ArgumentNullException(nameof(samples));

        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sampleData = samples;
        _readPosition = 0;

        int totalFrames = samples.Length / config.Channels;
        _duration = (double)totalFrames / config.SampleRate;
    }

    public SampleSource(int bufferSizeInFrames, AudioConfig config)
    {
        if (bufferSizeInFrames <= 0)
            throw new ArgumentException("Buffer size must be positive.", nameof(bufferSizeInFrames));

        _config = config ?? throw new ArgumentNullException(nameof(config));
        int bufferSizeInSamples = bufferSizeInFrames * config.Channels;
        _sampleData = new float[bufferSizeInSamples];
        _readPosition = 0;
        _duration = 0.0;
        AllowDynamicUpdate = true;
    }

    public void SubmitSamples(ReadOnlySpan<float> samples)
    {
        ThrowIfDisposed();

        if (!AllowDynamicUpdate)
            throw new InvalidOperationException("Dynamic sample submission is not enabled.");

        lock (_dataLock)
        {
            if (samples.Length > _sampleData.Length)
            {
                Array.Resize(ref _sampleData, samples.Length);
            }

            samples.CopyTo(_sampleData.AsSpan());
            _readPosition = 0;

            int totalFrames = samples.Length / _config.Channels;
            _duration = (double)totalFrames / _config.SampleRate;
        }
    }

    public void Clear()
    {
        ThrowIfDisposed();

        lock (_dataLock)
        {
            if (_sampleData != null)
            {
                Array.Clear(_sampleData, 0, _sampleData.Length);
            }
            _readPosition = 0;
            _duration = 0.0;
        }
    }

    public override int ReadSamples(Span<float> buffer, int frameCount)
    {
        ThrowIfDisposed();

        if (State != AudioState.Playing)
        {
            FillWithSilence(buffer, frameCount * _config.Channels);
            return frameCount;
        }

        lock (_dataLock)
        {
            if (_sampleData == null || _sampleData.Length == 0)
            {
                FillWithSilence(buffer, frameCount * _config.Channels);
                return frameCount;
            }

            int samplesToRead = frameCount * _config.Channels;
            int samplesAvailable = _sampleData.Length - _readPosition;
            int samplesToCopy = Math.Min(samplesToRead, samplesAvailable);
            int framesRead = 0;

            if (samplesToCopy > 0)
            {
                _sampleData.AsSpan(_readPosition, samplesToCopy).CopyTo(buffer);
                _readPosition += samplesToCopy;
                framesRead = samplesToCopy / _config.Channels;
            }

            if (_readPosition >= _sampleData.Length)
            {
                if (Loop)
                {
                    _readPosition = 0;

                    if (framesRead < frameCount)
                    {
                        int remainingFrames = frameCount - framesRead;
                        int remainingSamples = remainingFrames * _config.Channels;
                        int samplesLeftInBuffer = _sampleData.Length - _readPosition;
                        int samplesToFill = Math.Min(remainingSamples, samplesLeftInBuffer);

                        if (samplesToFill > 0)
                        {
                            int offsetInOutput = framesRead * _config.Channels;
                            _sampleData.AsSpan(_readPosition, samplesToFill).CopyTo(buffer.Slice(offsetInOutput));
                            _readPosition += samplesToFill;
                            framesRead += samplesToFill / _config.Channels;
                        }
                    }
                }
                else
                {
                    State = AudioState.EndOfStream;

                    if (framesRead < frameCount)
                    {
                        int remainingSamples = (frameCount - framesRead) * _config.Channels;
                        int offsetInOutput = framesRead * _config.Channels;
                        FillWithSilence(buffer.Slice(offsetInOutput), remainingSamples);
                    }
                }
            }
            else if (framesRead < frameCount)
            {
                int remainingSamples = (frameCount - framesRead) * _config.Channels;
                int offsetInOutput = framesRead * _config.Channels;
                FillWithSilence(buffer.Slice(offsetInOutput), remainingSamples);
            }

            ApplyVolume(buffer, frameCount * _config.Channels);
            return framesRead;
        }
    }

    public override bool Seek(double positionInSeconds)
    {
        ThrowIfDisposed();

        if (positionInSeconds < 0 || positionInSeconds > Duration)
            return false;

        lock (_dataLock)
        {
            try
            {
                long targetFrame = (long)(positionInSeconds * _config.SampleRate);
                int targetSample = (int)(targetFrame * _config.Channels);
                targetSample = Math.Clamp(targetSample, 0, _sampleData.Length);
                _readPosition = targetSample;
                return true;
            }
            catch (Exception ex)
            {
                OnError(new AudioErrorEventArgs($"Seek failed: {ex.Message}", ex));
                return false;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                lock (_dataLock)
                {
                    _sampleData = null!;
                }
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
