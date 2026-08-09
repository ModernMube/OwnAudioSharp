using System;
using Ownaudio.Audio.Tracks;

namespace OwnaudioNET.Monitoring;

/// <summary>
/// A live window onto an effect chain: hands back the same block of audio twice, as the chain
/// got it and as the chain left it. Built by AudioMixer.CreateEffectTap / CreateMasterEffectTap,
/// dispose it to stop the native side mirroring.
/// </summary>
public sealed class EffectTap : IDisposable
{
    private readonly AudioTrack? _track;
    private readonly MultiTrackSession? _session;
    private readonly Func<int> _chainLatency;

    private float[] _delay = Array.Empty<float>();
    private int _delayPos;
    private int _appliedLatency = -1;
    private bool _disposed;

    /// <summary>
    /// Track-level tap.
    /// </summary>
    internal EffectTap(AudioTrack track, Func<int> chainLatency, int channels, int sampleRate)
        : this(chainLatency, channels, sampleRate)
    {
        _track = track;
    }

    /// <summary>
    /// Master-bus tap.
    /// </summary>
    internal EffectTap(MultiTrackSession session, Func<int> chainLatency, int channels, int sampleRate)
        : this(chainLatency, channels, sampleRate)
    {
        _session = session;
    }

    private EffectTap(Func<int> chainLatency, int channels, int sampleRate)
    {
        _chainLatency = chainLatency;
        Channels = Math.Max(1, channels);
        SampleRate = sampleRate;
    }

    /// <summary>
    /// Interleaved channel count of the tapped audio.
    /// </summary>
    public int Channels { get; }

    /// <summary>
    /// Sample rate of the tapped audio.
    /// </summary>
    public int SampleRate { get; }

    /// <summary>
    /// Latency of the running effects, in frames. Read compensates for it, this is just here so a
    /// caller can show it or decide the chain is too laggy to compare meaningfully.
    /// </summary>
    public int LatencySamples => Math.Max(0, _chainLatency());

    /// <summary>
    /// True until Dispose.
    /// </summary>
    public bool IsActive => !_disposed;

    /// <summary>
    /// Pulls whatever the render thread has queued into the two spans. Both come back filled to
    /// the same length and lined up in time, so pre[i] and post[i] are the same instant. 0 means
    /// nothing new yet - poll it from a UI timer, don't spin on it.
    /// </summary>
    /// <returns>How many samples landed in each span.</returns>
    public int Read(Span<float> pre, Span<float> post)
    {
        if (_disposed) { return 0; }

        int read = _track is not null
            ? _track.ReadFxTap(pre, post)
            : _session!.ReadMasterFxTap(pre, post);

        if (read > 0) _holdBackDry(pre.Slice(0, read));
        return read;
    }

    /// <summary>
    /// Slides the dry signal by the chain's own latency. A lookahead limiter or a hosted plugin
    /// answers late, and comparing spectra across that offset smears the picture - phase goes
    /// first, then anything transient.
    /// </summary>
    private void _holdBackDry(Span<float> pre)
    {
        int latency = LatencySamples;
        if (latency != _appliedLatency) { _resizeDelay(latency); }
        if (_delay.Length == 0) { return; }

        for (int i = 0; i < pre.Length; i++)
        {
            float held = _delay[_delayPos];
            _delay[_delayPos] = pre[i];
            pre[i] = held;
            _delayPos = (_delayPos + 1) % _delay.Length;
        }
    }

    /// <summary>
    /// Re-arms the hold-back line after a chain edit. The samples already in flight are lost, so
    /// the next window or two reads short - beats keeping a stale alignment.
    /// </summary>
    private void _resizeDelay(int latencyFrames)
    {
        _appliedLatency = latencyFrames;
        int size = latencyFrames * Channels;
        _delay = size > 0 ? new float[size] : Array.Empty<float>();
        _delayPos = 0;
    }

    /// <summary>
    /// Stops the mirroring on the native side.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_track is not null)
            _track.StopFxTap();
        else
            _session!.StopMasterFxTap();
    }
}
