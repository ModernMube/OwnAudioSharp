using System;
using System.Runtime.InteropServices;
using Ownaudio.Audio.Effects;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;
using Ownaudio.Safe.Validation;

namespace Ownaudio.Safe.Effects;

/// <summary>
/// One native effect that lives on the caller thread. Same DSP as a mixer twin,
/// different handle — Process() / Matchering must not share the audio-thread instance.
/// </summary>
public sealed class StandaloneEffect : IDisposable
{
    private readonly StandaloneEffectHandle _handle;
    private readonly int _channels;

    /// <summary>
    /// The rust side takes a &amp;mut to the effect, so two calls at once are a data race and
    /// a Dispose racing a Process is a use-after-free. Every entry point goes through here.
    /// </summary>
    private readonly object _gate = new object();

    private bool _disposed;

    /// <summary>
    /// effectType is the ABI id, sampleRate sizes the delay lines, channels is the
    /// interleaved layout process() will see.
    /// </summary>
    public StandaloneEffect(EffectType effectType, float sampleRate, int channels)
    {
        if (!float.IsFinite(sampleRate) || sampleRate <= 0f)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate has to be a positive finite Hz value.");

        _channels = Math.Max(1, channels);

        int code = OwnAudioNative.ownaudio_v1_standalone_effect_create(
            (uint)effectType,
            sampleRate,
            (ushort)_channels,
            out IntPtr raw);
        ErrorCodeMapper.ThrowIfError(code, nameof(StandaloneEffect));
        if (raw == IntPtr.Zero)
            throw new OwnAudioException(Ownaudio.Safe.AudioEngineErrorCode.InternalError, "Standalone effect create returned a null handle.");

        _handle = new StandaloneEffectHandle();
        Marshal.InitHandle(_handle, raw);
    }

    private StandaloneEffect(IntPtr raw, int channels)
    {
        _channels = channels;
        _handle = new StandaloneEffectHandle();
        Marshal.InitHandle(_handle, raw);
    }

    /// <summary>
    /// Bridges an already-loaded vst3 plugin. Same engine-side bridge the mixer chain uses,
    /// so bypass and dry/wet behave the same either way, both delayed by latencySamples so
    /// the dry sits level with the plugin's own output. A block wider than maxBlockFrames is
    /// passed through untouched rather than reallocated, so size it for the biggest you feed.
    /// </summary>
    public static StandaloneEffect ForVst(IntPtr pluginHandle, IntPtr processFn,
        int channels, int maxBlockFrames, int latencySamples)
    {
        if (processFn == IntPtr.Zero)
            throw new ArgumentException("A vst bridge needs the plugin's process entry point.", nameof(processFn));
        if (maxBlockFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBlockFrames), maxBlockFrames, "Block size has to be positive.");

        int _ch = Math.Max(1, channels);

        int code = OwnAudioNative.ownaudio_v1_standalone_effect_create_vst(
            pluginHandle,
            processFn,
            (ushort)_ch,
            (uint)maxBlockFrames,
            (uint)Math.Max(0, latencySamples),
            out IntPtr raw);
        ErrorCodeMapper.ThrowIfError(code, nameof(ForVst));
        if (raw == IntPtr.Zero)
            throw new OwnAudioException(Ownaudio.Safe.AudioEngineErrorCode.InternalError, "Standalone vst effect create returned a null handle.");

        return new StandaloneEffect(raw, _ch);
    }

    /// <summary>
    /// Channel count this instance was built for.
    /// </summary>
    public int Channels => _channels;

    /// <summary>
    /// Look-ahead in frames. Zero-latency effects stay at 0.
    /// </summary>
    public int LatencySamples
    {
        get
        {
            lock (_gate)
            {
                Guard.NotDisposed(_disposed, nameof(StandaloneEffect));
                int code = OwnAudioNative.ownaudio_v1_standalone_effect_latency(
                    _handle.DangerousGetHandle(), out uint latency);
                ErrorCodeMapper.ThrowIfError(code, nameof(LatencySamples));
                return (int)latency;
            }
        }
    }

    /// <summary>
    /// Pushes one param. Unknown ids return false instead of throwing — the
    /// managed mirror has a few no-op fields the native side never grew.
    /// </summary>
    public bool SetParam(uint paramId, float value)
    {
        lock (_gate)
        {
            Guard.NotDisposed(_disposed, nameof(StandaloneEffect));
            int code = OwnAudioNative.ownaudio_v1_standalone_effect_set_param(
                _handle.DangerousGetHandle(), paramId, value);

            if (code == (int)NativeErrorCode.InvalidHandle)
                return false;

            ErrorCodeMapper.ThrowIfError(code, nameof(SetParam));
            return true;
        }
    }

    /// <summary>
    /// Reads a param back, or null when the id is not on this effect.
    /// </summary>
    public float? GetParam(uint paramId)
    {
        lock (_gate)
        {
            Guard.NotDisposed(_disposed, nameof(StandaloneEffect));
            int code = OwnAudioNative.ownaudio_v1_standalone_effect_get_param(
                _handle.DangerousGetHandle(), paramId, out float value);

            if (code == (int)NativeErrorCode.InvalidHandle)
                return null;

            ErrorCodeMapper.ThrowIfError(code, nameof(GetParam));
            return value;
        }
    }

    /// <summary>
    /// In-place process. frameCount is frames, not samples; the span has to cover
    /// frameCount * channels floats.
    /// </summary>
    public void Process(Span<float> buffer, int frameCount)
    {
        if (frameCount <= 0 || buffer.IsEmpty) return;

        if ((long)frameCount * _channels > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(frameCount), frameCount,
                $"The buffer holds {buffer.Length} floats, {frameCount} frames of {_channels} channels need "
                + $"{(long)frameCount * _channels}.");

        lock (_gate)
        {
            Guard.NotDisposed(_disposed, nameof(StandaloneEffect));
            int code = OwnAudioNative.ownaudio_v1_standalone_effect_process(
                _handle.DangerousGetHandle(),
                ref MemoryMarshal.GetReference(buffer),
                (uint)frameCount,
                (ushort)_channels);
            ErrorCodeMapper.ThrowIfError(code, nameof(Process));
        }
    }

    /// <summary>
    /// Drops the tail, params stay.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            Guard.NotDisposed(_disposed, nameof(StandaloneEffect));
            int code = OwnAudioNative.ownaudio_v1_standalone_effect_reset(_handle.DangerousGetHandle());
            ErrorCodeMapper.ThrowIfError(code, nameof(Reset));
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _handle.Dispose();
        }
    }
}
