using System;
using System.Collections.Generic;
using System.Threading;
using Logger;
using Ownaudio.Core;
using OwnaudioNET.Exceptions;
using RustSafe = Ownaudio.Safe;

namespace OwnaudioNET.Engine;

/// <summary>
/// The audio data path: Send, TrySend and Receives, plus the session output/capture
/// bookkeeping they run on. This is the only part an audio thread touches.
/// </summary>
internal sealed partial class RustAudioEngine : IAudioEngine
{
    #region IAudioEngine — data path

    /// <inheritdoc />
    public void Send(Span<float> samples)
    {
        if (samples.IsEmpty)
            return;

        RustSafe.AudioOutputStream? _stream = _outputStream;
        if (_stream == null)
            return;

        int _offset = 0;
        var _spinner = new SpinWait();

        while (_offset < samples.Length)
        {
            if (_disposed || !_running || !_outputEnabled)
                return;

            _offset += _stream.Write(samples.Slice(_offset));

            // Ring full, the render callback hasn't drained it yet. Back off and retry, this is
            // the blocking behaviour IAudioEngine promises.
            if (_offset < samples.Length) _spinner.SpinOnce();
            else _spinner.Reset();
        }
    }

    /// <inheritdoc />
    public int TrySend(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty || _disposed || !_running || !_outputEnabled)
            return 0;

        RustSafe.AudioOutputStream? _stream = _outputStream;
        return _stream?.Write(samples) ?? 0;
    }

    /// <summary>
    /// Samples sitting in the render ring, i.e. how far ahead of the DAC we've pushed.
    /// </summary>
    internal int OutputQueuedSamples => _outputStream?.QueuedSamples ?? 0;

    /// <summary>
    /// Ring depth the engine actually opened with, in frames. Zero before the device is open,
    /// and zero on a native session output too: that one renders inside the device callback,
    /// so there is no ring between it and the DAC to pay for.
    /// </summary>
    internal int OutputRingFrames
    {
        get
        {
            lock (_stateLock)
            {
                if (_outputStream is { } _own) return _read(() => _own.RingFrames, _openedRingFrames);
                if (_sessionOutputStream is { } _session) return _read(() => _session.RingFrames, 0);

                return _openedRingFrames;
            }
        }
    }

    /// <summary>
    /// Frames the driver actually hands the render callback. Zero until audio runs. Keeps
    /// answering once a native session took playback, since it reads that stream instead.
    /// </summary>
    internal int OutputCallbackFrames
    {
        get
        {
            lock (_stateLock)
            {
                if (_outputStream is { } _own) return _read(() => _own.CallbackFrames, 0);
                if (_sessionOutputStream is { } _session) return _read(() => _session.CallbackFrames, 0);

                return 0;
            }
        }
    }

    /// <summary>
    /// Same on capture. A native session drains the device through its shared capture bridge,
    /// which keeps no such counter, so this reads 0 there — <see cref="ActualInputChannels"/>
    /// is the one that still answers.
    /// </summary>
    internal int InputCallbackFrames
    {
        get
        {
            lock (_stateLock)
            {
                return _inputStream is { } _s ? _read(() => _s.CallbackFrames, 0) : 0;
            }
        }
    }

    /// <summary>
    /// Channels the playback device really opened with. The requested width is only a request —
    /// a device that can't serve it gets adapted to the nearest it supports — so anything drawing
    /// physical output sockets, or deciding how far a per-track route may reach, has to read this
    /// rather than the config.
    /// </summary>
    /// <remarks>
    /// Survives a native session taking the device over: the session's own stream answers while
    /// it holds it, and the last width the hardware opened with covers the gap between the two.
    /// Only before anything was ever opened does this fall back to the requested width.
    /// </remarks>
    public int ActualOutputChannels
    {
        get
        {
            lock (_stateLock)
            {
                if (_outputStream is { } _own) return _read(() => _own.ChannelCount, _openedOutputChannels);
                if (_sessionOutputStream is { } _session)
                    return _read(() => _session.ChannelCount, _openedOutputChannels);

                return _openedOutputChannels > 0 ? _openedOutputChannels : _config?.EffectiveOutputChannels ?? 0;
            }
        }
    }

    /// <summary>
    /// Same on capture, and the range an InputSource.CaptureChannels map may address. Once a
    /// native session opens its shared capture bridge, that bridge's width answers here.
    /// </summary>
    public int ActualInputChannels
    {
        get
        {
            lock (_stateLock)
            {
                if (_inputStream is { } _s) return _read(() => _s.ChannelCount, _openedInputChannels);
                if (_sessionInputChannels > 0) return _sessionInputChannels;

                return _openedInputChannels > 0 ? _openedInputChannels : _config?.EffectiveInputChannels ?? 0;
            }
        }
    }

    /// <summary>
    /// Reads a diagnostic off a native stream without ever letting it throw at the caller. These
    /// are numbers a meter polls on a UI timer, and a stream disposed a moment earlier must not
    /// turn a level display into an exception.
    /// </summary>
    /// <param name="read"></param>
    /// <param name="fallback">what to report when the stream can no longer answer</param>
    private static int _read(Func<int> read, int fallback)
    {
        try { return read(); }
        catch (ObjectDisposedException) { return fallback; }
        catch (AudioEngineException) { return fallback; }
    }

    /// <summary>
    /// Hands us the output stream a native session opened on our device, so the width, buffer
    /// and ring diagnostics keep answering after <see cref="ReleaseOutput"/> closed ours. Not an
    /// ownership transfer: the session opened it and the session disposes it, we only read.
    /// Pass null when it goes away.
    /// </summary>
    /// <param name="stream"></param>
    internal void TrackSessionOutput(RustSafe.AudioOutputStream? stream)
    {
        lock (_stateLock)
        {
            _sessionOutputStream = stream;
            if (stream is null) return;

            int _channels = _read(() => stream.ChannelCount, 0);
            if (_channels > 0) _openedOutputChannels = _channels;
        }
    }

    /// <summary>
    /// Same for capture: the session's shared bridge opened at this width and ours is closed.
    /// Pass 0 when the session hands capture back.
    /// </summary>
    /// <param name="channels"></param>
    internal void TrackSessionCapture(int channels)
    {
        lock (_stateLock)
        {
            _sessionInputChannels = Math.Max(0, channels);
            if (_sessionInputChannels > 0) _openedInputChannels = _sessionInputChannels;
        }
    }

    /// <summary>
    /// Throws away everything queued for playback.
    /// </summary>
    internal void ClearOutput() => _outputStream?.Clear();

    /// <summary>
    /// Frames the render callback had to fill with silence. Cumulative for the life of the stream.
    /// </summary>
    internal long OutputUnderrunFrames => (long)(_outputStream?.UnderrunFrames ?? 0);

    /// <inheritdoc />
    public int Receives(Span<float> destination)
    {
        if (_disposed || !_running)
            return -1;

        RustSafe.AudioInputStream? _stream = _inputStream;
        if (_stream == null || destination.IsEmpty)
            return 0;

        return _stream.Read(destination);
    }

    #endregion
}
