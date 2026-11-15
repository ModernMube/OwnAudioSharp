using System;
using System.Threading;
using System.Threading.Tasks;
using Ownaudio.Audio.Tracks;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Mixing;

/// <summary>
/// Fades the engine runs itself, on the master and on one source: frame by frame on the audio
/// thread, straight in dB, on top of the volumes the user set. Rust-native only — the managed
/// mix has no stage for them, and a fade that quietly does nothing is worse than an error.
/// </summary>
public sealed partial class AudioMixer
{
    /// <summary>
    /// The last master fade asked for, so one asked before the session exists (a fade in armed
    /// before the first source) lands on it the moment it is built.
    /// </summary>
    private float _masterFadeTarget = 1f;

    /// <summary>
    /// Ramps the mix inside the master scope to target (0 silence, 1 unity) over duration,
    /// before the master gain — MasterVolume does not move. A new call takes over from wherever
    /// the last one got to; a zero duration jumps.
    /// </summary>
    /// <param name="target"></param>
    /// <param name="duration"></param>
    public void FadeMaster(float target, TimeSpan duration)
    {
        _throwIfDisposed();
        _throwUnlessRustNative(nameof(FadeMaster));

        lock (_rustSessionLock)
        {
            _masterFadeTarget = target;
            _rustSession?.FadeMaster(target, duration);
        }
    }

    /// <summary>
    /// Fades the master and returns once the audio thread has rendered the end of it — the
    /// moment a stop after a fade out no longer cuts anything. Returns straight away while no
    /// output runs, since nothing would move the fade.
    /// </summary>
    public async Task FadeMasterAsync(float target, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        FadeMaster(target, duration);

        int _blockMs = Math.Max(1, _config.BufferSize * 1000 / Math.Max(1, _config.SampleRate));

        while (_rustOutputStream is not null && !IsMasterFadeDone)
            await Task.Delay(_blockMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True once the last master fade has been rendered to its end.
    /// </summary>
    public bool IsMasterFadeDone
    {
        get
        {
            lock (_rustSessionLock) return _rustSession?.MasterFadeState.Done ?? true;
        }
    }

    /// <summary>
    /// The master fade's level after the last rendered block, 1 when none ever ran.
    /// </summary>
    public float MasterFadeLevel
    {
        get
        {
            lock (_rustSessionLock) return _rustSession?.MasterFadeState.Level ?? _masterFadeTarget;
        }
    }

    /// <summary>
    /// The same ramp on one source's native track, after its effects — the reverb tail fades
    /// with it. False when the source has no native track (not on the mixer). The fade lives on
    /// that track: taken off and put back, the source starts at unity again.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="target"></param>
    /// <param name="duration"></param>
    public bool FadeSource(IAudioSource source, float target, TimeSpan duration)
    {
        _throwIfDisposed();
        _throwUnlessRustNative(nameof(FadeSource));
        ArgumentNullException.ThrowIfNull(source);

        lock (_rustSessionLock)
        {
            AudioTrack? _track = _resolveRustBacked(source).Track;
            if (_track is null) return false;

            _track.FadeTo(target, duration);
            return true;
        }
    }

    /// <summary>
    /// True once the source's last fade was rendered to its end, or it has no native track.
    /// </summary>
    /// <param name="source"></param>
    public bool IsSourceFadeDone(IAudioSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_rustSessionLock) return _resolveRustBacked(source).Track?.FadeState.Done ?? true;
    }

    private void _throwUnlessRustNative(string what)
    {
        if (!_rustNative)
            throw new NotSupportedException($"{what} runs on the rust-native mixer only.");
    }
}
