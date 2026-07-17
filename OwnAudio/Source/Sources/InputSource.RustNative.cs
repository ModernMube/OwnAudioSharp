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
    /// Native track rendering us, null before attach.
    /// </summary>
    private AudioTrack? _rustTrack;

    /// <summary>
    /// Was this source built for the rust-native chain?
    /// </summary>
    internal bool IsRustNativeChain => _rustNative;

    /// <inheritdoc/>
    AudioTrack? IRustNativeChainSource.RustTrack => RustTrack;

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

            //Already playing before attach? Start capture and track so it's audible right away.
            if (State == AudioState.Playing)
            {
                _rustInputTrack.Play();
                _rustTrack.Play();
            }
        }
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
        lock (_rustBackendLock) _input = _rustInputTrack;

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
        }
    }
}
