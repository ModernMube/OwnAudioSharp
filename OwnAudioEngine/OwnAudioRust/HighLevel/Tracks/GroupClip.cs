using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// An audio file loaded for group tracks, independent of any session. Short files are decoded
/// into memory once, longer ones are only probed and stream from disk wherever they are placed.
/// Either way placing it on a <see cref="GroupTrack"/> is cheap, as often as you like.
/// </summary>
public sealed class GroupClip : IDisposable
{
    private readonly GroupClipHandle _handle;

    private GroupClip(GroupClipHandle handle, string filePath, ulong lengthFrames, bool inMemory,
        int sampleRate, int channels)
    {
        _handle = handle;
        FilePath = filePath;
        LengthFrames = lengthFrames;
        InMemory = inMemory;
        SampleRate = sampleRate;
        Channels = channels;
    }

    /// <summary>
    /// Loads a file decoded to sampleRate and channels — both have to match the session the groups
    /// run in. Files up to memoryMaxSeconds decode into memory right here, so keep this off the
    /// UI thread.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="sampleRate"></param>
    /// <param name="channels"></param>
    /// <param name="memoryMaxSeconds"></param>
    public static GroupClip Open(string filePath, int sampleRate, int channels, double memoryMaxSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        ulong memoryMaxFrames = (ulong)Math.Round(Math.Max(0.0, memoryMaxSeconds) * sampleRate);

        int code = OwnAudioNative.ownaudio_v1_group_clip_open(
            filePath,
            (uint)sampleRate,
            (uint)channels,
            memoryMaxFrames,
            out IntPtr rawClip,
            out ulong lengthFrames,
            out byte inMemory);
        ErrorCodeMapper.ThrowIfError(code, nameof(Open));

        var handle = new GroupClipHandle();
        Marshal.InitHandle(handle, rawClip);

        return new GroupClip(handle, filePath, lengthFrames, inMemory != 0, sampleRate, channels);
    }

    /// <summary>
    /// Where it was loaded from.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Length at <see cref="SampleRate"/>.
    /// </summary>
    public ulong LengthFrames { get; }

    /// <summary>
    /// Decoded up front, otherwise streamed from disk.
    /// </summary>
    public bool InMemory { get; }

    /// <summary>
    /// Rate it decodes to.
    /// </summary>
    public int SampleRate { get; }

    /// <summary>
    /// Width it decodes to.
    /// </summary>
    public int Channels { get; }

    /// <summary>
    /// Length as time.
    /// </summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(LengthFrames / (double)SampleRate);

    internal IntPtr DangerousGetHandle()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        return _handle.DangerousGetHandle();
    }

    /// <summary>
    /// Drops our hold on it. Groups it was placed on keep playing it.
    /// </summary>
    public void Dispose() => _handle.Dispose();
}
