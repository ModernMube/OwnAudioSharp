using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Safe;

/// <summary>
/// Tempo estimator over the native detector. Offline, runs on the caller thread, same
/// algorithm and api as the managed SoundTouch.BpmDetect it replaced.
/// </summary>
public sealed class BpmDetect : IDisposable
{
    private readonly BpmDetectHandle _handle;
    private readonly int _channels;
    private bool _disposed;

    /// <summary>
    /// channels = interleaved channel count of what you feed to InputSamples, clamped to 1 minimum.
    /// sampleRate is the input rate in Hz.
    /// </summary>
    public BpmDetect(int channels, int sampleRate)
    {
        _channels = Math.Max(1, channels);

        int code = OwnAudioNative.ownaudio_v1_bpm_create((uint)_channels, (uint)sampleRate, out IntPtr rawHandle);
        if (code != 0 || rawHandle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create the native BPM detector (code {code}).");

        _handle = new BpmDetectHandle();
        Marshal.InitHandle(_handle, rawHandle);
    }

    /// <summary>
    /// Feeds frames worth of interleaved samples, so the span needs frames * channels elements.
    /// </summary>
    public void InputSamples(ReadOnlySpan<float> samples, int frames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frames <= 0 || samples.IsEmpty) return;

        int count = Math.Min(frames * _channels, samples.Length);

        OwnAudioNative.ownaudio_v1_bpm_input_samples(
            _handle.DangerousGetHandle(),
            ref MemoryMarshal.GetReference(samples),
            (nuint)frames,
            (nuint)count);
    }

    /// <summary>
    /// Estimated tempo, 0 while there is not enough data for anything reliable.
    /// </summary>
    public float GetBpm()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        OwnAudioNative.ownaudio_v1_bpm_get_bpm(_handle.DangerousGetHandle(), out float bpm);
        return bpm;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _handle.Dispose();
    }
}
