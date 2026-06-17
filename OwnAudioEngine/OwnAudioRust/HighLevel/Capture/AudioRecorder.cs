using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Ownaudio.Audio.Diagnostics;
using Ownaudio.Audio.Streams;
using Ownaudio.Safe;
using Ownaudio.Safe.Callbacks;
using Ownaudio.Safe.Exceptions;
using AudioStreamException = Ownaudio.Audio.Diagnostics.AudioStreamException;

namespace Ownaudio.Audio.Capture;

/// <summary>
/// Captures audio from an input device and exposes it through events.
/// </summary>
/// <remarks>
/// <para>
/// Obtain instances through <see cref="AudioEngine.CreateRecorder"/>.
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="Start"/>, <see cref="Stop"/>, and <see cref="Dispose"/>
/// must not be called concurrently on the same instance.
/// </para>
/// <para>
/// <b>DataAvailable:</b> fires synchronously on the real-time audio thread.
/// Do not allocate, lock, or perform blocking I/O inside its handler.
/// Use a lock-free queue or <c>System.Threading.Channels.Channel&lt;T&gt;</c> to
/// hand data to a background thread.
/// </para>
/// <para>
/// <b>LevelChanged:</b> fires on a <see cref="ThreadPool"/> thread at approximately 30 Hz.
/// Safe to use directly for UI level meters.
/// </para>
/// </remarks>
public sealed class AudioRecorder : IDisposable
{
    #region Fields

    private readonly Safe.AudioEngine _safeEngine;
    private readonly RecorderOptions _options;

    private AudioInputStream? _stream;
    private RecorderState _state;
    private bool _disposed;

    private readonly Stopwatch _levelTimer = Stopwatch.StartNew();
    private const long LevelThrottleMs = 33;

    #endregion

    #region Events

    /// <summary>
    /// Raised for each captured audio buffer on the real-time audio thread.
    /// </summary>
    /// <remarks>
    /// <b>Do not</b> allocate objects, acquire locks, or perform I/O inside this handler.
    /// The handler executes on the native audio thread.
    /// </remarks>
    public event EventHandler<AudioDataAvailableEventArgs>? DataAvailable;

    /// <summary>
    /// Raised approximately 30 times per second on a <see cref="ThreadPool"/> thread
    /// with RMS and peak level information.
    /// </summary>
    public event EventHandler<AudioLevelEventArgs>? LevelChanged;

    #endregion

    #region Construction

    internal AudioRecorder(Safe.AudioEngine safeEngine, RecorderOptions? options)
    {
        _safeEngine = safeEngine;
        _options    = options ?? new RecorderOptions();
        _state      = RecorderState.Stopped;
    }

    #endregion

    #region Properties

    /// <summary>Current capture state.</summary>
    public RecorderState State => _state;

    /// <summary>
    /// The audio format used for capture.
    /// Derived from <see cref="RecorderOptions"/> supplied at creation time.
    /// </summary>
    public AudioFormat Format => new AudioFormat(
        _options.SampleRate,
        _options.Channels,
        _options.SampleType);

    #endregion

    #region Capture control

    /// <summary>
    /// Opens a capture stream and starts receiving audio data.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when this recorder has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when capture is already active.</exception>
    /// <exception cref="AudioStreamException">Thrown when the native stream cannot be opened.</exception>
    public void Start()
    {
        ThrowIfDisposed();

        if (_state == RecorderState.Recording)
        {
            throw new InvalidOperationException("Capture is already active.");
        }

        if (_stream == null)
        {
            _stream = OpenStream();
        }

        try
        {
            _stream.Play();
        }
        catch (StreamException ex)
        {
            throw new AudioStreamException((int)ex.ErrorCode, ex.Message, ex);
        }

        _levelTimer.Restart();
        SetState(RecorderState.Recording);
    }

    /// <summary>
    /// Stops audio capture and releases the native stream.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when this recorder has been disposed.</exception>
    public void Stop()
    {
        ThrowIfDisposed();

        if (_state == RecorderState.Stopped)
        {
            return;
        }

        try
        {
            _stream?.Pause();
        }
        catch
        {
            // Best-effort; ignore errors during stop.
        }

        _stream?.Dispose();
        _stream = null;

        SetState(RecorderState.Stopped);
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Stops capture and releases the native stream.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _stream?.Dispose();
        _stream = null;
    }

    #endregion

    #region Private — stream management

    private AudioInputStream OpenStream()
    {
        Safe.AudioDevice? device = ResolveInputDevice();

        var safeFormat = (Ownaudio.Safe.SampleFormat)(int)_options.SampleType;
        var config = new AudioStreamConfig(
            _options.SampleRate,
            _options.Channels,
            safeFormat,
            _options.BufferSizeFrames);

        try
        {
            AudioInputStream stream = _safeEngine.OpenInputStream(device, config, OnAudioCallback);
            stream.CallbackError += OnCallbackError;
            return stream;
        }
        catch (StreamException ex)
        {
            throw new AudioStreamException((int)ex.ErrorCode, ex.Message, ex);
        }
    }

    private Safe.AudioDevice? ResolveInputDevice()
    {
        if (_options.DeviceName == null)
        {
            return null;
        }

        IReadOnlyList<Safe.AudioDevice> devices = _safeEngine.EnumerateInputDevices();

        foreach (Safe.AudioDevice d in devices)
        {
            if (d.Name == _options.DeviceName)
            {
                return d;
            }
        }

        return null;
    }

    #endregion

    #region Private — audio callback

    private void OnAudioCallback(in AudioInputCallbackArgs args)
    {
        float[] copy = new float[args.Buffer.Length];
        args.Buffer.CopyTo(copy);

        var mem = new ReadOnlyMemory<float>(copy);
        DataAvailable?.Invoke(this,
            new AudioDataAvailableEventArgs(mem, args.FrameCount, args.Channels));

        if (_levelTimer.ElapsedMilliseconds >= LevelThrottleMs)
        {
            _levelTimer.Restart();

            float rms  = ComputeRms(args.Buffer);
            float peak = ComputePeak(args.Buffer);

            EventHandler<AudioLevelEventArgs>? handler = LevelChanged;

            if (handler is not null)
            {
                var levelArgs = new AudioLevelEventArgs(rms, peak);
                ThreadPool.QueueUserWorkItem(
                    _ => handler.Invoke(this, levelArgs));
            }
        }
    }

    private void OnCallbackError(object? sender, Exception ex)
    {
        // Propagate callback errors to the engine's fault channel if wired up.
        // For now, swallow silently — the stream remains functional.
    }

    private static float ComputeRms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0f;
        }

        double sumSq = 0.0;

        for (int i = 0; i < samples.Length; i++)
        {
            double s = samples[i];
            sumSq += s * s;
        }

        return (float)Math.Sqrt(sumSq / samples.Length);
    }

    private static float ComputePeak(ReadOnlySpan<float> samples)
    {
        float peak = 0f;

        for (int i = 0; i < samples.Length; i++)
        {
            float abs = Math.Abs(samples[i]);
            if (abs > peak)
            {
                peak = abs;
            }
        }

        return peak;
    }

    #endregion

    #region Private — state helper

    private void SetState(RecorderState newState)
    {
        _state = newState;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioRecorder));
        }
    }

    #endregion
}
