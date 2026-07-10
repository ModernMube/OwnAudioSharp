using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Safe;

/// <summary>
/// Managed wrapper over the native BPM (tempo) detector. Estimates the tempo of interleaved audio
/// fed offline on the caller's thread, matching the algorithm and API of the former managed
/// <c>SoundTouch.BpmDetect</c> it replaces.
/// </summary>
public sealed class BpmDetect : IDisposable
{
    /// <summary>The native detector handle.</summary>
    private readonly BpmDetectHandle _handle;

    /// <summary>Interleaved channel count of the fed samples.</summary>
    private readonly int _channels;

    /// <summary>Whether this instance has been disposed.</summary>
    private bool _disposed;

    /// <summary>
    /// Creates a detector for the given channel count and input sample rate.
    /// </summary>
    /// <param name="channels">Interleaved channel count of the samples fed to
    /// <see cref="InputSamples"/> (clamped to at least 1 natively).</param>
    /// <param name="sampleRate">Input sample rate in Hz.</param>
    /// <exception cref="InvalidOperationException">Thrown when the native detector cannot be created.</exception>
    public BpmDetect(int channels, int sampleRate)
    {
        _channels = Math.Max(1, channels);

        int code = OwnAudioNative.ownaudio_v1_bpm_create(
            (uint)_channels,
            (uint)sampleRate,
            out IntPtr rawHandle);

        if (code != 0 || rawHandle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create the native BPM detector (code {code}).");

        _handle = new BpmDetectHandle();
        Marshal.InitHandle(_handle, rawHandle);
    }

    /// <summary>
    /// Feeds <paramref name="frames"/> interleaved frames from <paramref name="samples"/> into the
    /// detector.
    /// </summary>
    /// <param name="samples">Interleaved samples; must hold at least
    /// <paramref name="frames"/> × channel-count elements.</param>
    /// <param name="frames">Number of frames (not samples) to feed.</param>
    public void InputSamples(ReadOnlySpan<float> samples, int frames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (frames <= 0 || samples.IsEmpty)
            return;

        int needed = frames * _channels;
        int count = Math.Min(needed, samples.Length);

        OwnAudioNative.ownaudio_v1_bpm_input_samples(
            _handle.DangerousGetHandle(),
            ref MemoryMarshal.GetReference(samples),
            (nuint)frames,
            (nuint)count);
    }

    /// <summary>
    /// Returns the estimated tempo in BPM, or <c>0</c> when there is not yet enough data for a
    /// reliable estimate.
    /// </summary>
    /// <returns>The estimated tempo in beats per minute.</returns>
    public float GetBpm()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        OwnAudioNative.ownaudio_v1_bpm_get_bpm(_handle.DangerousGetHandle(), out float bpm);
        return bpm;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _handle.Dispose();
    }
}
