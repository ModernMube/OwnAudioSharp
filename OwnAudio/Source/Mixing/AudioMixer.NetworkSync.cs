using System.Threading;
using OwnaudioNET.Core;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Sources;

namespace OwnaudioNET.Mixing;

/// <summary>
/// What the network sync reads off the mixer and does to it. The song position comes from the
/// tracks' content, not the clock — the clock counts output frames, so a tempo trim would
/// never show on it.
/// </summary>
public sealed partial class AudioMixer
{
    /// <summary>
    /// Render-to-speaker delay: the device buffer plus what the driver reports.
    /// </summary>
    internal double OutputLatencySeconds
        => (_config.BufferSize + _engine.OutputLatencyFrames) / (double)_config.SampleRate;

    /// <summary>
    /// Song position in project seconds, offset + content / tempo of the furthest playing
    /// timeline track. Returns whether any timeline track plays; rendering stays false while
    /// every one of them still sits in its start-offset silence, the clock stands in then.
    /// </summary>
    internal bool ReadSongPosition(out double position, out bool rendering)
    {
        bool _playing = false;
        position = -1.0;

        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        foreach (IAudioSource source in _sources)
        {
            IRustClockedSource? _fs = _resolve<IRustClockedSource>(source);
            if (_fs is null || _fs.State != AudioState.Playing) continue;

            _playing = true;
            if (_fs.StartOffset > 0.0 && (_fs.RustTrack?.RenderedFrames ?? 0UL) == 0UL) continue;

            float _tempo = _fs.Tempo <= 0f ? 1f : _fs.Tempo;
            double _p = _fs.StartOffset + _fs.Position / _tempo;
            if (_p > position) position = _p;
        }

        rendering = position >= 0.0;
        if (!rendering) position = _masterClock.CurrentTimestamp;
        return _playing;
    }

    /// <summary>
    /// Puts the timeline tracks where the network says: the whole mixer seeks there, then the
    /// tracks start or pause to match.
    /// </summary>
    internal void FollowTransport(double projectSeconds, bool play)
    {
        if (!play) _setTimelinePlaying(false);
        Seek(projectSeconds);
        if (play) _setTimelinePlaying(true);
    }

    private void _setTimelinePlaying(bool play)
    {
        foreach (IAudioSource source in Volatile.Read(ref _rustSourceSnapshot))
        {
            IRustClockedSource? _fs = _resolve<IRustClockedSource>(source);
            if (_fs is null || (_fs.State == AudioState.Playing) == play) continue;

            if (play) source.Play();
            else source.Pause();
        }
    }
}
