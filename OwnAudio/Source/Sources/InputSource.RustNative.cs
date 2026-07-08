using System;
using Ownaudio.Audio.Tracks;
using OwnaudioNET.Core;

namespace OwnaudioNET.Sources;

/// <summary>
/// Rust-native chain backend for <see cref="InputSource"/>: device capture runs entirely on the
/// native side (a native input stream writes straight into the track's ring buffer), so no audio
/// data flows through managed code and the GC never touches the capture or render path. The managed
/// side is only a controller (start/stop capture, metering).
/// </summary>
/// <remarks>
/// Unlike <see cref="FileSource"/>/<see cref="SampleSource"/>, an input source is only meaningful
/// while attached to a mixer (which supplies the native engine that owns the capture device), so
/// there is no standalone backend: the managed capture thread is simply not started in Rust-native
/// mode, and the native <see cref="InputTrack"/> is created by the mixer on attach.
/// </remarks>
public sealed partial class InputSource : IRustNativeChainSource
{
    /// <summary>
    /// Whether this source runs on the Rust-native chain. Assigned once in the constructor from
    /// <see cref="Engine.RustNativeChain.Enabled"/> so the mode is stable for the source's lifetime.
    /// </summary>
    private readonly bool _rustNative;

    /// <summary>Serializes attachment and teardown of the Rust-native backend.</summary>
    private readonly object _rustBackendLock = new();

    /// <summary>The native input capture feeding the track, when attached to a mixer session.</summary>
    private InputTrack? _rustInputTrack;

    /// <summary>The native track rendering this source, or null before attachment.</summary>
    private AudioTrack? _rustTrack;

    /// <summary>Gets whether this source was constructed to use the Rust-native chain.</summary>
    internal bool IsRustNativeChain => _rustNative;

    /// <inheritdoc/>
    AudioTrack? IRustNativeChainSource.RustTrack => RustTrack;

    /// <summary>
    /// Gets the native track backing this source in the Rust-native chain, or
    /// <see langword="null"/> when running legacy or before it is attached to a mixer.
    /// </summary>
    internal AudioTrack? RustTrack
    {
        get
        {
            lock (_rustBackendLock)
            {
                return _rustTrack;
            }
        }
    }

    /// <summary>
    /// Gets the native input capture driving this source's track, or <see langword="null"/> when
    /// running legacy or before attachment.
    /// </summary>
    internal InputTrack? RustInputTrack
    {
        get
        {
            lock (_rustBackendLock)
            {
                return _rustInputTrack;
            }
        }
    }

    /// <summary>
    /// Attaches this source to a track that lives in a mixer's shared session, fed by a native input
    /// capture. The source references but does not own the track or the capture, so it will not
    /// dispose them.
    /// </summary>
    /// <param name="track">The mixer-session track that renders this source.</param>
    /// <param name="inputTrack">The native input capture feeding <paramref name="track"/>.</param>
    internal void AttachRustTrack(AudioTrack track, InputTrack inputTrack)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(inputTrack);

        if (!_rustNative)
        {
            throw new InvalidOperationException(
                "AttachRustTrack requires the Rust-native chain to be enabled for this source.");
        }

        lock (_rustBackendLock)
        {
            _rustTrack = track;
            _rustInputTrack = inputTrack;
            _rustTrack.Gain = Volume;

            // Preserve the transport state captured before attachment: if the source was already
            // playing, start capture and the track so it is audible through the mixer.
            if (State == AudioState.Playing)
            {
                _rustInputTrack.Play();
                _rustTrack.Play();
            }
        }
    }

    /// <summary>
    /// Detaches this source from a mixer-owned track. The track and capture are owned by the mixer's
    /// session, so they are only unreferenced here, not disposed.
    /// </summary>
    internal void DetachRustTrack()
    {
        lock (_rustBackendLock)
        {
            _rustTrack = null;
            _rustInputTrack = null;
        }
    }

    /// <summary>Rust-native implementation of <see cref="Play"/>: starts native capture and the track.</summary>
    private void RustNativePlay()
    {
        base.Play();

        lock (_rustBackendLock)
        {
            _rustInputTrack?.Play();
            _rustTrack?.Play();
        }
    }

    /// <summary>Rust-native implementation of <see cref="Pause"/>: pauses native capture and the track.</summary>
    private void RustNativePause()
    {
        lock (_rustBackendLock)
        {
            _rustInputTrack?.Pause();
            _rustTrack?.Pause();
        }

        base.Pause();
    }

    /// <summary>Rust-native implementation of <see cref="Stop"/>: stops native capture and the track.</summary>
    private void RustNativeStop()
    {
        lock (_rustBackendLock)
        {
            _rustInputTrack?.Pause();
            _rustTrack?.Stop();
        }

        base.Stop();
    }

    /// <summary>
    /// Rust-native input levels: the native capture's own metering peaks, scaled by volume to match
    /// the legacy behavior. Returns silence before attachment.
    /// </summary>
    private (float left, float right) RustNativeInputLevels()
    {
        InputTrack? inputTrack;
        lock (_rustBackendLock)
        {
            inputTrack = _rustInputTrack;
        }

        if (inputTrack is null)
        {
            return (0f, 0f);
        }

        (float left, float right) = inputTrack.GetInputPeaks();
        return (left * Volume, right * Volume);
    }

    /// <summary>Tears down the Rust-native backend reference (the track is owned by the mixer).</summary>
    private void DisposeRustBackend()
    {
        lock (_rustBackendLock)
        {
            _rustTrack = null;
            _rustInputTrack = null;
        }
    }
}
