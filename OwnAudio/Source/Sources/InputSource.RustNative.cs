using System;
using Ownaudio.Audio.Tracks;
using OwnaudioNET.Core;

namespace OwnaudioNET.Sources;

/// <summary>
/// Rust-native backend for InputSource: capture runs fully native, managed side is just a controller.
/// Unlike File/SampleSource there's no standalone backend - an input source only makes sense on a
/// mixer (that owns the capture device), the native InputTrack is created by the mixer on attach.
/// </summary>
public sealed partial class InputSource : IRustNativeChainSource
{
    /// <summary>
    /// Runs on the rust-native chain? Latched once in the ctor, stable for life.
    /// </summary>
    private readonly bool _rustNative;

    /// <summary>
    /// Guards attach/teardown of the native backend.
    /// </summary>
    private readonly object _rustBackendLock = new();

    /// <summary>
    /// Native input capture feeding the track, when attached to a mixer session.
    /// </summary>
    private InputTrack? _rustInputTrack;

    /// <summary>
    /// The shared capture bridge we tap, when the mixer went that way instead of opening a
    /// stream per track. Exactly one of this and _rustInputTrack is ever set.
    /// </summary>
    private CaptureBridge? _rustCapture;

    private int[]? _captureChannels;

    /// <summary>
    /// Native track rendering us, null before attach.
    /// </summary>
    private AudioTrack? _rustTrack;

    /// <summary>
    /// Was this source built for the rust-native chain?
    /// </summary>
    internal bool IsRustNativeChain => _rustNative;

    /// <inheritdoc/>
    AudioTrack? IRustNativeChainSource.RustTrack => RustTrack;

    /// <inheritdoc/>
    void IRustNativeChainSource.DetachRustTrack() => DetachRustTrack();

    /// <summary>
    /// The native track backing us, null on legacy or before attach.
    /// </summary>
    internal AudioTrack? RustTrack
    {
        get { lock (_rustBackendLock) return _rustTrack; }
    }

    /// <summary>
    /// The native input capture driving our track, null on legacy or before attach.
    /// </summary>
    internal InputTrack? RustInputTrack
    {
        get { lock (_rustBackendLock) return _rustInputTrack; }
    }

    /// <summary>
    /// Attaches us to a mixer-session track fed by a native capture. We reference but don't own
    /// them, so we won't dispose them.
    /// </summary>
    /// <param name="track"></param>
    /// <param name="inputTrack"></param>
    internal void AttachRustTrack(AudioTrack track, InputTrack inputTrack)
    {
        lock (_rustBackendLock)
        {
            _rustTrack = track;
            _rustInputTrack = inputTrack;
            _rustTrack.Gain = Volume;
            _rustTrack.Pan = Pan;

            //Already playing before attach? Start capture and track so it's audible right away.
            if (State == AudioState.Playing)
            {
                _rustInputTrack.Play();
                _rustTrack.Play();
            }
        }
    }

    /// <summary>
    /// Attaches us to a mixer-session track fed by the shared capture bridge. Same deal as
    /// AttachRustTrack, except the device stream belongs to every input track at once, so we
    /// never pause it - only our own track.
    /// </summary>
    /// <param name="track"></param>
    /// <param name="bridge"></param>
    internal void AttachRustCapture(AudioTrack track, CaptureBridge bridge)
    {
        lock (_rustBackendLock)
        {
            _rustTrack = track;
            _rustCapture = bridge;
            _rustTrack.Gain = Volume;
            _rustTrack.Pan = Pan;

            if (State == AudioState.Playing)
            {
                bridge.Play();
                _rustTrack.Play();
            }
        }
    }

    /// <summary>
    /// Which physical capture channels feed us: CaptureChannels[i] becomes our channel i, and
    /// the length is our own width. null means the mixer's default (the first N of the device,
    /// duplicating a mono input the way it always did). Only means anything on the shared
    /// bridge; the mixer picks a change up on its next control tick.
    /// </summary>
    public int[]? CaptureChannels
    {
        get { lock (_rustBackendLock) return _captureChannels; }
        set
        {
            if (value is { Length: 0 })
                throw new ArgumentException("A capture tap needs at least one channel.", nameof(value));

            lock (_rustBackendLock) _captureChannels = value is null ? null : (int[])value.Clone();
        }
    }

    /// <summary>
    /// The bridge we tap, null when we're on a per-track capture or not attached yet.
    /// </summary>
    internal CaptureBridge? RustCapture
    {
        get { lock (_rustBackendLock) return _rustCapture; }
    }

    /// <summary>
    /// Detaches from a mixer-owned track. The mixer owns it, we just drop the refs.
    /// </summary>
    internal void DetachRustTrack()
    {
        lock (_rustBackendLock)
        {
            _rustTrack = null;
            _rustInputTrack = null;
            _rustCapture = null;
        }
    }

    /// <summary>
    /// Rust-native Play: starts capture and the track.
    /// </summary>
    private void _rustPlay()
    {
        base.Play();

        lock (_rustBackendLock)
        {
            _rustInputTrack?.Play();
            _rustCapture?.Play();
            _rustTrack?.Play();
        }
    }

    /// <summary>
    /// Rust-native Pause: pauses capture and the track.
    /// </summary>
    private void _rustPause()
    {
        lock (_rustBackendLock)
        {
            _rustInputTrack?.Pause();
            _rustTrack?.Pause();
        }

        base.Pause();
    }

    /// <summary>
    /// Rust-native Stop: stops capture and the track.
    /// </summary>
    private void _rustStop()
    {
        lock (_rustBackendLock)
        {
            _rustInputTrack?.Pause();
            _rustTrack?.Stop();
        }

        base.Stop();
    }

    /// <summary>
    /// Native capture's metering peaks scaled by volume, matches legacy behavior. Silence before attach.
    /// </summary>
    /// <returns></returns>
    private (float left, float right) _rustInputLevels()
    {
        InputTrack? _input;
        AudioTrack? _track;
        lock (_rustBackendLock)
        {
            _input = _rustInputTrack;
            _track = _rustCapture is null ? null : _rustTrack;
        }

        //On the shared bridge the device meter is everyone's, so our own track's peaks are
        //the only per-source level - and they're already post gain, hence no extra scaling.
        if (_track is not null) return _track.Peaks;
        if (_input == null) return (0f, 0f);

        (float _left, float _right) = _input.GetInputPeaks();
        return (_left * Volume, _right * Volume);
    }

    /// <summary>
    /// Drops the backend refs (the track is owned by the mixer).
    /// </summary>
    private void _disposeRustBackend()
    {
        lock (_rustBackendLock)
        {
            _rustTrack = null;
            _rustInputTrack = null;
            _rustCapture = null;
        }
    }
}
