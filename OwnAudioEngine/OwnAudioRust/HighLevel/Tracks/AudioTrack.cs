using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// One track inside a <see cref="MultiTrackSession"/>. Feed it with Write, everything
/// else (gain/pan/tempo/pitch) goes straight down to the Rust side.
/// </summary>
public sealed class AudioTrack : IDisposable
{
    #region Fields

    /// <summary>
    /// Ring capacity in seconds of audio. Enough look-ahead that a producer can run
    /// ahead of the audio thread without overflowing.
    /// </summary>
    public const float SourceBufferSeconds = 2.0f;

    private readonly TrackHandle _handle;
    private readonly ushort _channels;
    private TrackSourceHandle _sourceHandle;
    private readonly IntPtr _mixerHandle;
    private readonly float _sampleRate;
    private bool _disposed;

    private float _gain            = 1.0f;
    private float _pan             = 0.0f;
    private float _tempo           = 1.0f;
    private float _pitchSemitones  = 0.0f;
    private bool  _muted           = false;

    #endregion

    #region Construction

    internal AudioTrack(TrackHandle handle, IntPtr mixerHandle, float sampleRate, ushort channels)
    {
        _handle      = handle;
        _mixerHandle = mixerHandle;
        _sampleRate  = sampleRate;
        _channels    = Math.Max((ushort)1, channels);
        Effects      = new TrackEffectChain(mixerHandle, handle.DangerousGetHandle());

        _sourceHandle = _installRingSource();
    }

    /// <summary>
    /// Hangs a fresh lock-free ring on the track and hands back its write handle.
    /// </summary>
    private TrackSourceHandle _installRingSource()
    {
        nuint capacity = (nuint)Math.Max(1, (long)(_sampleRate * _channels * SourceBufferSeconds));
        int code = OwnAudioNative.ownaudio_v1_track_set_ring_source(
            _mixerHandle,
            _handle.DangerousGetHandle(),
            capacity,
            out IntPtr rawSource);
        ErrorCodeMapper.ThrowIfError(code, nameof(_installRingSource));

        var handle = new TrackSourceHandle();
        Marshal.InitHandle(handle, rawSource);
        return handle;
    }

    #endregion

    #region Propertyes

    /// <summary>
    /// The track's effect chain.
    /// </summary>
    public TrackEffectChain Effects { get; }

    /// <summary>
    /// Linear gain, 1.0 = unity. The sync tick re-assigns this every tick, so we skip
    /// the P/Invoke when nothing changed — the mirror starts at the native default.
    /// </summary>
    public float Gain
    {
        get => _gain;
        set
        {
            float clamped = MathF.Max(0f, value);
            if (clamped == _gain) { return; }

            _gain = clamped;
            if (!_disposed)
                OwnAudioNative.ownaudio_v1_track_set_gain(_handle.DangerousGetHandle(), _gain);
        }
    }

    /// <summary>
    /// Stereo pan, -1..+1, equal-power law normalized at center so a centered track
    /// passes through untouched. Same unchanged-value skip as Gain.
    /// </summary>
    public float Pan
    {
        get => _pan;
        set
        {
            float clamped = Math.Clamp(value, -1.0f, 1.0f);
            if (clamped == _pan) { return; }

            _pan = clamped;
            if (!_disposed)
                OwnAudioNative.ownaudio_v1_track_set_pan(_handle.DangerousGetHandle(), _pan);
        }
    }

    /// <summary>
    /// Tempo ratio, 1.0 = normal speed. Clamped to 0.25..4.
    /// </summary>
    public float Tempo
    {
        get => _tempo;
        set
        {
            float clamped = Math.Clamp(value, 0.25f, 4.0f);
            if (clamped == _tempo) { return; }

            _tempo = clamped;
            if (!_disposed)
                OwnAudioNative.ownaudio_v1_track_set_tempo(_handle.DangerousGetHandle(), _tempo);
        }
    }

    /// <summary>
    /// Pitch shift in semitones, -24..+24.
    /// </summary>
    public float PitchSemitones
    {
        get => _pitchSemitones;
        set
        {
            float clamped = Math.Clamp(value, -24f, 24f);
            if (clamped == _pitchSemitones) { return; }

            _pitchSemitones = clamped;
            if (!_disposed)
                OwnAudioNative.ownaudio_v1_track_set_pitch(_handle.DangerousGetHandle(), _pitchSemitones);
        }
    }

    /// <summary>
    /// Pins the SoundTouch stretch stage on for the track's whole life. A tempo/pitch
    /// capable source calls this once at bind time, so the first tempo change lands on
    /// an already-warm FIFO instead of clicking its way in from bypass.
    /// </summary>
    /// <param name="enabled">true = keep the stretch stage always in the path.</param>
    public void SetStretchAlwaysOn(bool enabled)
    {
        if (_disposed) { return; }

        int code = OwnAudioNative.ownaudio_v1_track_set_stretch_always_on(
            _handle.DangerousGetHandle(),
            enabled ? 1 : 0);
        ErrorCodeMapper.ThrowIfError(code, nameof(SetStretchAlwaysOn));
    }

    /// <summary>
    /// Silences the track output.
    /// </summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            if (value == _muted) { return; }

            _muted = value;
            if (!_disposed)
                OwnAudioNative.ownaudio_v1_track_set_mute(_handle.DangerousGetHandle(), value ? 1f : 0f);
        }
    }

    /// <summary>
    /// Output frames actually rendered into the mix since the last reset. This is the
    /// rendered position, it lags the fed one by the ring depth — that's why Position
    /// rides on this and not on what Write accepted.
    /// </summary>
    public ulong RenderedFrames
    {
        get
        {
            if (_disposed) { return 0; }

            int code = OwnAudioNative.ownaudio_v1_track_get_rendered_frames(
                _handle.DangerousGetHandle(),
                out ulong frames);
            ErrorCodeMapper.ThrowIfError(code, nameof(RenderedFrames));
            return frames;
        }
    }

    /// <summary>
    /// Wall-clock playback position: advances at the real rate no matter the tempo.
    /// This is the one to drive a shared master clock with.
    /// </summary>
    public TimeSpan Position
    {
        get
        {
            if (_sampleRate <= 0f) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(RenderedFrames / _sampleRate);
        }
    }

    /// <summary>
    /// Content-timeline frames advanced through since the last reset — the per-block
    /// tempo integrated (Σ frames × tempo), so it follows a live time-stretch.
    /// </summary>
    public double RenderedContentFrames
    {
        get
        {
            if (_disposed) { return 0.0; }

            int code = OwnAudioNative.ownaudio_v1_track_get_rendered_content_frames(
                _handle.DangerousGetHandle(),
                out double frames);
            ErrorCodeMapper.ThrowIfError(code, nameof(RenderedContentFrames));
            return frames;
        }
    }

    /// <summary>
    /// Tempo-aware position: where in the source the audio you hear actually is.
    /// A file source reports this one, matching the old managed chain.
    /// </summary>
    public TimeSpan ContentPosition
    {
        get
        {
            if (_sampleRate <= 0f) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(RenderedContentFrames / _sampleRate);
        }
    }

    /// <summary>
    /// Last measured output peaks of this track's own contribution, post effects and
    /// gain. Only updated while rendering, so a stopped track keeps its last value.
    /// </summary>
    public (float Left, float Right) Peaks
    {
        get
        {
            if (_disposed) { return (0f, 0f); }

            int code = OwnAudioNative.ownaudio_v1_track_get_peaks(
                _handle.DangerousGetHandle(),
                out float left,
                out float right);
            ErrorCodeMapper.ThrowIfError(code, nameof(Peaks));
            return (left, right);
        }
    }

    /// <summary>
    /// Start-offset silence: the track emits this many frames of nothing before it
    /// starts contributing. Sample-accurate late entry without moving the source. 0 clears it.
    /// </summary>
    /// <param name="frames">Silence length in output frames.</param>
    public void SetStartDelayFrames(long frames)
    {
        if (_disposed) { return; }

        ulong value = frames <= 0 ? 0UL : (ulong)frames;
        int code = OwnAudioNative.ownaudio_v1_track_set_start_delay_frames(
            _handle.DangerousGetHandle(),
            value);
        ErrorCodeMapper.ThrowIfError(code, nameof(SetStartDelayFrames));
    }

    /// <summary>
    /// Per-track output routing: source channel i lands on output mapping[i], every
    /// other output gets nothing from us. Empty span clears the routing.
    /// </summary>
    /// <param name="mapping">Zero-based output channel index per source channel.</param>
    public void SetOutputChannelMap(ReadOnlySpan<int> mapping)
    {
        if (_disposed) { return; }

        if (mapping.IsEmpty)
        {
            ClearOutputChannelMap();
            return;
        }

        Span<uint> map = stackalloc uint[mapping.Length];
        for (int i = 0; i < mapping.Length; i++)
        {
            if(mapping[i] < 0)
                throw new ArgumentException($"Output channel index at position {i} is negative ({mapping[i]}).", nameof(mapping));

            map[i] = (uint)mapping[i];
        }

        int code = OwnAudioNative.ownaudio_v1_track_set_output_channel_map(
            _handle.DangerousGetHandle(),
            in map[0],
            (nuint)map.Length);
        ErrorCodeMapper.ThrowIfError(code, nameof(SetOutputChannelMap));
    }

    /// <summary>
    /// Back to the straight identity mix, channel i → output i.
    /// </summary>
    public void ClearOutputChannelMap()
    {
        if (_disposed) { return; }

        int code = OwnAudioNative.ownaudio_v1_track_clear_output_channel_map(
            _handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(ClearOutputChannelMap));
    }

    #endregion

    #region Transport

    /// <summary>
    /// Starts or resumes this track.
    /// </summary>
    public void Play()
    {
        _throwIfDisposed();
        int code = OwnAudioNative.ownaudio_v1_track_play(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Play));
    }

    /// <summary>
    /// Pauses, position kept.
    /// </summary>
    public void Pause()
    {
        _throwIfDisposed();
        int code = OwnAudioNative.ownaudio_v1_track_pause(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Pause));
    }

    /// <summary>
    /// Stops and rewinds to zero.
    /// </summary>
    public void Stop()
    {
        _throwIfDisposed();
        int code = OwnAudioNative.ownaudio_v1_track_stop(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Stop));
    }

    /// <summary>
    /// Seeks the track and resets the rendered counter, so Position restarts from the
    /// target. The decoder seek itself happens on the feeder side.
    /// </summary>
    /// <param name="position"></param>
    public void Seek(TimeSpan position)
    {
        _throwIfDisposed();
        ulong sample = (ulong)(position.TotalSeconds * _sampleRate);
        int code = OwnAudioNative.ownaudio_v1_track_seek(_handle.DangerousGetHandle(), sample);
        ErrorCodeMapper.ThrowIfError(code, nameof(Seek));

        int resetCode = OwnAudioNative.ownaudio_v1_track_reset_position(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(resetCode, nameof(Seek));
    }

    #endregion

    #region Source feed

    /// <summary>
    /// Throws away whatever is sitting in the source ring. The feeder uses this on a
    /// seek so the stale pre-seek look-ahead doesn't play out first.
    /// </summary>
    public void ClearSource()
    {
        _throwIfDisposed();
        int code = OwnAudioNative.ownaudio_v1_track_clear_source(_mixerHandle, _handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(ClearSource));
    }

    /// <summary>
    /// Drops the queued feed and re-arms with a fresh empty ring, so the next Write is
    /// the next thing heard. ClearSource alone detaches the ring for good, which is no
    /// good for a producer that keeps feeding. Call it from the feeding thread.
    /// </summary>
    public void ResetFeed()
    {
        _throwIfDisposed();

        TrackSourceHandle stale = _sourceHandle;
        _sourceHandle = _installRingSource();
        stale.Dispose();
    }

    /// <summary>
    /// Pushes interleaved f32 into the lock-free feed. Non-blocking: when the ring is
    /// full fewer (or zero) samples land and the caller retries the rest later.
    /// </summary>
    /// <returns>How many samples actually landed there.</returns>
    public int Write(ReadOnlySpan<float> samples)
    {
        _throwIfDisposed();
        if (samples.IsEmpty) { return 0; }

        int code = OwnAudioNative.ownaudio_v1_track_source_write(
            _sourceHandle.DangerousGetHandle(),
            in MemoryMarshal.GetReference(samples),
            (nuint)samples.Length,
            out nuint written);
        ErrorCodeMapper.ThrowIfError(code, nameof(Write));
        return (int)written;
    }

    /// <summary>
    /// How many samples fit right now without overflowing.
    /// </summary>
    public int FreeSampleCount
    {
        get
        {
            _throwIfDisposed();
            int code = OwnAudioNative.ownaudio_v1_track_source_free_samples(
                _sourceHandle.DangerousGetHandle(),
                out nuint free);
            ErrorCodeMapper.ThrowIfError(code, nameof(FreeSampleCount));
            return (int)free;
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Drops the source ring and the track handle.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sourceHandle.Dispose();
        _handle.Dispose();
    }

    #endregion

    #region Internal helpers

    /// <summary>
    /// Raw native handle for the session.
    /// </summary>
    internal IntPtr GetNativeHandle() => _handle.DangerousGetHandle();

    private void _throwIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AudioTrack));
    }

    #endregion
}
