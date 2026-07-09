using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// Represents a single audio track within a <see cref="MultiTrackSession"/>.
/// </summary>
/// <remarks>
/// <para>
/// A track holds a lock-free ring buffer on the native side; fill it with decoded
/// audio samples by calling <c>Write</c> from any thread.  Playback transport,
/// tempo, and pitch are controlled via the properties below, all of which forward
/// immediately to the native Rust layer.
/// </para>
/// <para>
/// Effects are managed through the <see cref="Effects"/> chain property.
/// </para>
/// </remarks>
public sealed class AudioTrack : IDisposable
{
    #region Fields

    /// <summary>
    /// Ring-buffer feed capacity expressed in seconds of audio; the native source
    /// is sized to <c>sampleRate × channels × this</c> so producers can run ahead
    /// of the audio thread without overflowing under normal scheduling.
    /// </summary>
    private const float SourceBufferSeconds = 2.0f;

    private readonly TrackHandle _handle;
    private readonly TrackSourceHandle _sourceHandle;
    private readonly IntPtr _mixerHandle;
    private readonly float _sampleRate;
    private bool _disposed;

    private float _gain            = 1.0f;
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
        Effects      = new TrackEffectChain(mixerHandle, handle.DangerousGetHandle());

        // Install a lock-free ring buffer as the track's audio source so decoded
        // samples can be pushed via Write from any thread; the audio thread owns
        // the read side. Size it for a couple of seconds of look-ahead.
        nuint capacity = (nuint)Math.Max(1, (long)(sampleRate * Math.Max((ushort)1, channels) * SourceBufferSeconds));
        int code = OwnAudioNative.ownaudio_v1_track_set_ring_source(
            mixerHandle,
            handle.DangerousGetHandle(),
            capacity,
            out IntPtr rawSource);
        ErrorCodeMapper.ThrowIfError(code, nameof(AudioTrack));

        _sourceHandle = new TrackSourceHandle();
        Marshal.InitHandle(_sourceHandle, rawSource);
    }

    #endregion

    #region Properties

    /// <summary>
    /// Gets the effect chain for this track.
    /// </summary>
    public TrackEffectChain Effects { get; }

    /// <summary>
    /// Gets or sets the track gain (linear amplitude; 1.0 = unity, 0.0 = silence).
    /// </summary>
    public float Gain
    {
        get => _gain;
        set
        {
            float clamped = MathF.Max(0f, value);
            // Skip the native call when the value is unchanged. The mirror starts at the native
            // default (1.0 = unity), so the two never drift. The control-rate sync tick re-assigns
            // this every tick from the source volume; without this guard that is an unconditional
            // P/Invoke per track per tick even when nothing changed.
            if (clamped == _gain)
            {
                return;
            }
            _gain = clamped;
            if (!_disposed)
            {
                OwnAudioNative.ownaudio_v1_track_set_gain(_handle.DangerousGetHandle(), _gain);
            }
        }
    }

    /// <summary>
    /// Gets or sets the tempo ratio (1.0 = normal speed, 2.0 = double speed).
    /// </summary>
    public float Tempo
    {
        get => _tempo;
        set
        {
            float clamped = Math.Clamp(value, 0.25f, 4.0f);
            if (clamped == _tempo)
            {
                return;
            }
            _tempo = clamped;
            if (!_disposed)
            {
                OwnAudioNative.ownaudio_v1_track_set_tempo(_handle.DangerousGetHandle(), _tempo);
            }
        }
    }

    /// <summary>
    /// Gets or sets the pitch shift in semitones (−24 to +24).
    /// </summary>
    public float PitchSemitones
    {
        get => _pitchSemitones;
        set
        {
            float clamped = Math.Clamp(value, -24f, 24f);
            if (clamped == _pitchSemitones)
            {
                return;
            }
            _pitchSemitones = clamped;
            if (!_disposed)
            {
                OwnAudioNative.ownaudio_v1_track_set_pitch(_handle.DangerousGetHandle(), _pitchSemitones);
            }
        }
    }

    /// <summary>
    /// Pins the track's SoundTouch time-stretch stage on for its whole lifetime.
    /// </summary>
    /// <remarks>
    /// A pinned track routes through the stretch stage from its first rendered block — even at
    /// unity tempo/pitch — and is never released back to the zero-latency bypass path. Call this
    /// once from a tempo/pitch-capable source (a file source) when it binds the track, so the very
    /// first tempo or pitch change lands on an already-warm FIFO with a constant, plugin-delay-
    /// compensated latency. Without it the first change switches the stage in from bypass, which
    /// clicks, comb-filters against the bypass tail, and desyncs the track from the others. A
    /// bypass-only source (e.g. a metronome whose tempo is baked into its audio) leaves it off.
    /// No-op when the track is disposed.
    /// </remarks>
    /// <param name="enabled"><see langword="true"/> to pin the stretch stage on.</param>
    public void SetStretchAlwaysOn(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        int code = OwnAudioNative.ownaudio_v1_track_set_stretch_always_on(
            _handle.DangerousGetHandle(),
            enabled ? 1 : 0);
        ErrorCodeMapper.ThrowIfError(code, nameof(SetStretchAlwaysOn));
    }

    /// <summary>
    /// Gets or sets a value indicating whether the track output is silenced.
    /// </summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            if (value == _muted)
            {
                return;
            }
            _muted = value;
            if (!_disposed)
            {
                OwnAudioNative.ownaudio_v1_track_set_mute(_handle.DangerousGetHandle(), value ? 1f : 0f);
            }
        }
    }

    /// <summary>
    /// Gets the number of output frames this track has actually rendered into the
    /// mix since the last position reset (a seek or source swap).
    /// </summary>
    /// <remarks>
    /// This is the <em>rendered</em> position, advanced on the audio thread as each
    /// block is mixed. It lags the <em>fed</em> position by the native ring-buffer
    /// depth, which is why it — not the amount written via <see cref="Write"/> — is
    /// the authoritative source for <see cref="Position"/>. Returns zero when the
    /// track is disposed.
    /// </remarks>
    public ulong RenderedFrames
    {
        get
        {
            if (_disposed)
            {
                return 0;
            }

            int code = OwnAudioNative.ownaudio_v1_track_get_rendered_frames(
                _handle.DangerousGetHandle(),
                out ulong frames);
            ErrorCodeMapper.ThrowIfError(code, nameof(RenderedFrames));
            return frames;
        }
    }

    /// <summary>
    /// Gets the track's current playback position, derived from <see cref="RenderedFrames"/>
    /// and the session sample rate.
    /// </summary>
    /// <remarks>
    /// This is the <em>output</em> (wall-clock) position: it advances at the real playback
    /// rate regardless of tempo, and is the right quantity to drive a shared multi-track
    /// master clock. For the tempo-aware content-time position (where in the source the
    /// audio actually is), use <see cref="ContentPosition"/>.
    /// </remarks>
    public TimeSpan Position
    {
        get
        {
            if (_sampleRate <= 0f)
            {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds(RenderedFrames / _sampleRate);
        }
    }

    /// <summary>
    /// Gets the number of content (source-timeline) frames this track has advanced through
    /// since the last position reset (a seek or source swap).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="RenderedFrames"/> (output frames, wall-clock time), this integrates
    /// the per-block tempo (<c>Σ output_frames × tempo</c>), so it tracks the source content
    /// actually rendered through a live time-stretch. Returns zero when the track is disposed.
    /// </remarks>
    public double RenderedContentFrames
    {
        get
        {
            if (_disposed)
            {
                return 0.0;
            }

            int code = OwnAudioNative.ownaudio_v1_track_get_rendered_content_frames(
                _handle.DangerousGetHandle(),
                out double frames);
            ErrorCodeMapper.ThrowIfError(code, nameof(RenderedContentFrames));
            return frames;
        }
    }

    /// <summary>
    /// Gets the track's current content-time playback position (where in the source the
    /// rendered audio is), derived from <see cref="RenderedContentFrames"/> and the session
    /// sample rate.
    /// </summary>
    /// <remarks>
    /// This is the tempo-aware position: at non-unity tempo it advances faster or slower than
    /// <see cref="Position"/>, tracking the audio actually heard. It is the quantity a file
    /// source reports as its content-time position, matching the legacy managed chain.
    /// </remarks>
    public TimeSpan ContentPosition
    {
        get
        {
            if (_sampleRate <= 0f)
            {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds(RenderedContentFrames / _sampleRate);
        }
    }

    /// <summary>
    /// Gets the track's most recently measured output peak levels — the absolute
    /// peak of the track's own contribution to the mix, after its effect chain and
    /// gain, for the last rendered block.
    /// </summary>
    /// <remarks>
    /// Values range from <c>0.0</c> (silence) upward, reaching <c>1.0</c> at full
    /// scale. A mono track reports the same value on both channels. The peak is
    /// updated on the audio thread only while the track is actually rendering, so a
    /// stopped or paused track keeps its last value; gate the read on the transport
    /// state if a decaying meter is desired. Returns <c>(0, 0)</c> when disposed.
    /// </remarks>
    public (float Left, float Right) Peaks
    {
        get
        {
            if (_disposed)
            {
                return (0f, 0f);
            }

            int code = OwnAudioNative.ownaudio_v1_track_get_peaks(
                _handle.DangerousGetHandle(),
                out float left,
                out float right);
            ErrorCodeMapper.ThrowIfError(code, nameof(Peaks));
            return (left, right);
        }
    }

    /// <summary>
    /// Sets the track's start-offset silence: the number of output frames the track
    /// emits as silence (without reading its source) before it begins contributing.
    /// </summary>
    /// <remarks>
    /// Delays the track's entry against the mixer's shared clock sample-accurately,
    /// realising a positive per-track start offset without moving the source position.
    /// Pass <c>0</c> to clear any pending delay (the track enters immediately). No-op
    /// when the track is disposed.
    /// </remarks>
    /// <param name="frames">Start-offset silence length in output frames (≥ 0).</param>
    public void SetStartDelayFrames(long frames)
    {
        if (_disposed)
        {
            return;
        }

        ulong value = frames <= 0 ? 0UL : (ulong)frames;
        int code = OwnAudioNative.ownaudio_v1_track_set_start_delay_frames(
            _handle.DangerousGetHandle(),
            value);
        ErrorCodeMapper.ThrowIfError(code, nameof(SetStartDelayFrames));
    }

    /// <summary>
    /// Installs a per-track output-channel routing map: source channel <c>i</c> is
    /// summed into physical output channel <c>mapping[i]</c>, and every output channel
    /// not named by the map receives no contribution from this track (silence). This
    /// places the track onto a chosen subset of a multi-channel output bus.
    /// </summary>
    /// <remarks>
    /// Passing an empty span clears any routing (equivalent to
    /// <see cref="ClearOutputChannelMap"/>). Negative indices are invalid; out-of-range
    /// destinations are ignored at render time. No-op when the track is disposed.
    /// </remarks>
    /// <param name="mapping">Zero-based output-channel index per source channel.</param>
    /// <exception cref="ArgumentException">Thrown when any index is negative.</exception>
    public void SetOutputChannelMap(ReadOnlySpan<int> mapping)
    {
        if (_disposed)
        {
            return;
        }

        if (mapping.IsEmpty)
        {
            ClearOutputChannelMap();
            return;
        }

        Span<uint> map = stackalloc uint[mapping.Length];
        for (int i = 0; i < mapping.Length; i++)
        {
            if (mapping[i] < 0)
            {
                throw new ArgumentException(
                    $"Output channel index at position {i} is negative ({mapping[i]}).",
                    nameof(mapping));
            }
            map[i] = (uint)mapping[i];
        }

        int code = OwnAudioNative.ownaudio_v1_track_set_output_channel_map(
            _handle.DangerousGetHandle(),
            in map[0],
            (nuint)map.Length);
        ErrorCodeMapper.ThrowIfError(code, nameof(SetOutputChannelMap));
    }

    /// <summary>
    /// Clears any per-track output-channel routing, returning the track to the
    /// straight identity mix (source channel <c>i</c> → output channel <c>i</c>).
    /// No-op when the track is disposed.
    /// </summary>
    public void ClearOutputChannelMap()
    {
        if (_disposed)
        {
            return;
        }

        int code = OwnAudioNative.ownaudio_v1_track_clear_output_channel_map(
            _handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(ClearOutputChannelMap));
    }

    #endregion

    #region Transport

    /// <summary>
    /// Starts or resumes playback of this track.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the track is disposed.</exception>
    public void Play()
    {
        ThrowIfDisposed();
        int code = OwnAudioNative.ownaudio_v1_track_play(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Play));
    }

    /// <summary>
    /// Pauses playback without resetting the position.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the track is disposed.</exception>
    public void Pause()
    {
        ThrowIfDisposed();
        int code = OwnAudioNative.ownaudio_v1_track_pause(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Pause));
    }

    /// <summary>
    /// Stops playback and resets the position to zero.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the track is disposed.</exception>
    public void Stop()
    {
        ThrowIfDisposed();
        int code = OwnAudioNative.ownaudio_v1_track_stop(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Stop));
    }

    /// <summary>
    /// Seeks the track to the specified time position.
    /// </summary>
    /// <remarks>
    /// Also resets the rendered-frame counter so <see cref="Position"/> restarts from
    /// the seek target. In the intermediate phase the decoder seek itself happens on
    /// the managed side (via the track feeder); this call keeps the native rendered
    /// position consistent with that reset.
    /// </remarks>
    /// <param name="position">Target playback position.</param>
    /// <exception cref="ObjectDisposedException">Thrown when the track is disposed.</exception>
    public void Seek(TimeSpan position)
    {
        ThrowIfDisposed();
        ulong sample = (ulong)(position.TotalSeconds * _sampleRate);
        int code = OwnAudioNative.ownaudio_v1_track_seek(_handle.DangerousGetHandle(), sample);
        ErrorCodeMapper.ThrowIfError(code, nameof(Seek));

        int resetCode = OwnAudioNative.ownaudio_v1_track_reset_position(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(resetCode, nameof(Seek));
    }

    #endregion

    #region Source feed

    /// <summary>
    /// Drops any samples currently buffered in the track's source ring, discarding
    /// audio that was queued before a seek so the next reads start from fresh data.
    /// </summary>
    /// <remarks>
    /// Used by the track feeder when seeking: after the decoder jumps to a new
    /// position the stale look-ahead already pushed via <see cref="Write"/> must be
    /// cleared, otherwise the pre-seek audio would still play out first.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Thrown when the track is disposed.</exception>
    internal void ClearSource()
    {
        ThrowIfDisposed();
        int code = OwnAudioNative.ownaudio_v1_track_clear_source(_mixerHandle, _handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(ClearSource));
    }

    /// <summary>
    /// Pushes decoded interleaved <c>f32</c> samples into the track's lock-free
    /// audio feed and returns the number of samples actually accepted.
    /// </summary>
    /// <remarks>
    /// Real-time safe and non-blocking: when the native ring buffer is full, fewer
    /// samples (or zero) are accepted and the caller should retry the remainder
    /// later. Samples are interleaved per the session's channel count. Use
    /// <see cref="FreeSampleCount"/> to size writes against the available space.
    /// </remarks>
    /// <param name="samples">Interleaved samples to push.</param>
    /// <returns>The number of samples accepted into the buffer.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the track is disposed.</exception>
    public int Write(ReadOnlySpan<float> samples)
    {
        ThrowIfDisposed();
        if (samples.IsEmpty)
        {
            return 0;
        }

        int code = OwnAudioNative.ownaudio_v1_track_source_write(
            _sourceHandle.DangerousGetHandle(),
            in MemoryMarshal.GetReference(samples),
            (nuint)samples.Length,
            out nuint written);
        ErrorCodeMapper.ThrowIfError(code, nameof(Write));
        return (int)written;
    }

    /// <summary>
    /// Gets the number of samples that can currently be written without overflow.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the track is disposed.</exception>
    public int FreeSampleCount
    {
        get
        {
            ThrowIfDisposed();
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
    /// Disposes the track handle and releases native resources.
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

    /// <summary>Returns the raw native handle value for use by <see cref="MultiTrackSession"/>.</summary>
    internal IntPtr GetNativeHandle() => _handle.DangerousGetHandle();

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioTrack));
        }
    }

    #endregion
}
