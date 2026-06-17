using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Ownaudio.Audio.Diagnostics;
using Ownaudio.Audio.Streams;
using Ownaudio.Safe;
using Ownaudio.Safe.Callbacks;
using Ownaudio.Safe.Exceptions;
using AudioStreamException = Ownaudio.Audio.Diagnostics.AudioStreamException;
using SampleFormat = Ownaudio.Audio.Streams.SampleFormat;

namespace Ownaudio.Audio.Playback;

/// <summary>
/// Plays back pre-loaded audio through an output device.
/// </summary>
/// <remarks>
/// <para>
/// Obtain instances through <see cref="AudioEngine.CreatePlayer"/>.
/// Load audio with <see cref="Load(Stream, AudioFormat)"/> before calling <see cref="Play"/>.
/// </para>
/// <para>
/// <b>Thread safety:</b> All public methods are safe to call from any thread, but must
/// not be called concurrently on the same instance.
/// </para>
/// <para>
/// <b>Events:</b> <see cref="StateChanged"/> and <see cref="PlaybackEnded"/> may fire on
/// a <see cref="System.Threading.ThreadPool"/> thread when triggered by internal playback
/// completion.  Use appropriate dispatcher marshaling when updating UI from these handlers.
/// </para>
/// </remarks>
public sealed class AudioPlayer : IDisposable
{
    #region Fields

    private readonly Safe.AudioEngine _safeEngine;
    private readonly PlaybackOptions _options;

    private float[]? _buffer;
    private int _positionSamples;
    private bool _reachedEnd;

    private AudioOutputStream? _stream;
    private PlaybackState _state;
    private float _volume;
    private bool _isLooping;
    private bool _disposed;

    private AudioFormat _format;

    #endregion

    #region Events

    /// <summary>
    /// Raised whenever <see cref="State"/> changes.
    /// </summary>
    /// <remarks>
    /// May fire on a <see cref="System.Threading.ThreadPool"/> thread when playback ends
    /// naturally.  The calling thread is not guaranteed to be the UI thread.
    /// </remarks>
    public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised when playback finishes, stops, or fails.
    /// </summary>
    /// <remarks>
    /// May fire on a <see cref="System.Threading.ThreadPool"/> thread when playback ends
    /// naturally.  The calling thread is not guaranteed to be the UI thread.
    /// </remarks>
    public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;

    #endregion

    #region Construction

    internal AudioPlayer(Safe.AudioEngine safeEngine, PlaybackOptions? options)
    {
        _safeEngine = safeEngine;
        _options    = options ?? new PlaybackOptions();
        _volume     = _options.Volume;
        _isLooping  = _options.IsLooping;
        _state      = PlaybackState.Stopped;
    }

    #endregion

    #region Properties

    /// <summary>Current playback state.</summary>
    public PlaybackState State => _state;

    /// <summary>
    /// The format of the currently loaded audio.
    /// Default (zeroed) value when no audio is loaded.
    /// </summary>
    public AudioFormat Format => _format;

    /// <summary>
    /// Total duration of the loaded audio, or <see langword="null"/> when no audio is loaded.
    /// </summary>
    public TimeSpan? Duration
    {
        get
        {
            float[]? buf = _buffer;
            if (buf == null || _format.SampleRate == 0 || _format.Channels == 0)
            {
                return null;
            }

            return _format.DurationForSamples(buf.Length);
        }
    }

    /// <summary>
    /// Current playback position.
    /// </summary>
    /// <remarks>
    /// Read lock-free from any thread.  The value may lag by one audio buffer cycle
    /// on the reading thread.
    /// </remarks>
    public TimeSpan Position
    {
        get
        {
            if (_format.SampleRate == 0 || _format.Channels == 0)
            {
                return TimeSpan.Zero;
            }

            return _format.DurationForSamples(Interlocked.CompareExchange(ref _positionSamples, 0, 0));
        }
    }

    /// <summary>
    /// Playback volume in the range [0, 1].  Changes take effect on the next audio buffer cycle.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is outside [0, 1].
    /// </exception>
    public float Volume
    {
        get => _volume;
        set
        {
            if (value < 0f || value > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "Volume must be in the range [0, 1].");
            }

            _volume = value;
        }
    }

    /// <summary>
    /// When <see langword="true"/>, playback restarts from the beginning after reaching the end.
    /// </summary>
    public bool IsLooping
    {
        get => _isLooping;
        set => _isLooping = value;
    }

    #endregion

    #region Load

    /// <summary>
    /// Loads raw PCM audio from a <see cref="Stream"/> using the specified format.
    /// </summary>
    /// <param name="audioStream">
    /// Stream containing interleaved IEEE 754 float32 PCM samples (little-endian).
    /// The entire stream is read into memory.
    /// </param>
    /// <param name="format">
    /// The sample rate, channel count, and sample format of the data in <paramref name="audioStream"/>.
    /// </param>
    /// <remarks>
    /// Any active playback is stopped before the new buffer is loaded.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Thrown when this player has been disposed.</exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="audioStream"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the stream cannot be read to completion.
    /// </exception>
    public void Load(Stream audioStream, AudioFormat format)
    {
        ThrowIfDisposed();

        if (audioStream is null)
        {
            throw new ArgumentNullException(nameof(audioStream));
        }

        StopInternal(PlaybackEndReason.Stopped, fireEvents: false);

        using var ms = new MemoryStream();
        audioStream.CopyTo(ms);
        byte[] bytes = ms.ToArray();

        float[] floats;

        if (format.SampleType == SampleFormat.Float32)
        {
            floats = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length - (bytes.Length % 4));
        }
        else
        {
            floats = ConvertPcmToFloat(bytes, format.SampleType);
        }

        _buffer          = floats;
        _format          = format;
        _positionSamples = 0;
        _reachedEnd      = false;
    }

    /// <summary>
    /// File-based loading is not yet implemented.
    /// </summary>
    /// <remarks>
    /// File format decoding (MP3/WAV/FLAC) will be added in a future release.
    /// Use <see cref="Load(Stream, AudioFormat)"/> with a pre-decoded PCM stream.
    /// </remarks>
    /// <exception cref="NotImplementedException">Always thrown.</exception>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public void Load(string filePath)
    {
        throw new NotImplementedException(
            "File decoding is not yet implemented. " +
            "Use Load(Stream, AudioFormat) with pre-decoded PCM data.");
    }

    #endregion

    #region Playback control

    /// <summary>
    /// Starts or resumes audio output.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when this player has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no audio has been loaded.</exception>
    /// <exception cref="AudioStreamException">Thrown when the native stream cannot be opened.</exception>
    public void Play()
    {
        ThrowIfDisposed();

        if (_buffer == null)
        {
            throw new InvalidOperationException(
                "No audio loaded. Call Load() before Play().");
        }

        if (_state == PlaybackState.Playing)
        {
            return;
        }

        if (_reachedEnd && !_isLooping)
        {
            _positionSamples = 0;
            _reachedEnd      = false;
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

        SetState(PlaybackState.Playing);
    }

    /// <summary>
    /// Pauses playback, preserving the current position.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when this player has been disposed.</exception>
    /// <exception cref="AudioStreamException">Thrown when the native pause call fails.</exception>
    public void Pause()
    {
        ThrowIfDisposed();

        if (_state != PlaybackState.Playing)
        {
            return;
        }

        try
        {
            _stream?.Pause();
        }
        catch (StreamException ex)
        {
            throw new AudioStreamException((int)ex.ErrorCode, ex.Message, ex);
        }

        SetState(PlaybackState.Paused);
    }

    /// <summary>
    /// Stops playback and resets the position to the beginning.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when this player has been disposed.</exception>
    public void Stop()
    {
        ThrowIfDisposed();
        StopInternal(PlaybackEndReason.Stopped, fireEvents: true);
    }

    /// <summary>
    /// Seeks to the specified position in the loaded audio.
    /// </summary>
    /// <param name="position">Target position.  Clamped to [0, Duration].</param>
    /// <exception cref="ObjectDisposedException">Thrown when this player has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no audio has been loaded.</exception>
    public void Seek(TimeSpan position)
    {
        ThrowIfDisposed();

        if (_buffer == null)
        {
            throw new InvalidOperationException("No audio loaded.");
        }

        bool wasPlaying = _state == PlaybackState.Playing;

        if (wasPlaying)
        {
            _stream?.Pause();
        }

        long sampleIndex = _format.SamplesForDuration(position);
        sampleIndex = System.Math.Max(0, System.Math.Min(sampleIndex, _buffer.Length));

        Interlocked.Exchange(ref _positionSamples, (int)sampleIndex);
        _reachedEnd = false;

        if (wasPlaying)
        {
            _stream?.Play();
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Stops playback and releases the native stream.
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

    private AudioOutputStream OpenStream()
    {
        Safe.AudioDevice? device = ResolveOutputDevice();

        var safeFormat = (Ownaudio.Safe.SampleFormat)(int)_format.SampleType;
        var config = new AudioStreamConfig(
            _format.SampleRate,
            _format.Channels,
            safeFormat,
            _options.BufferSizeFrames);

        try
        {
            return _safeEngine.OpenOutputStream(device, config, OnAudioCallback);
        }
        catch (StreamException ex)
        {
            throw new AudioStreamException((int)ex.ErrorCode, ex.Message, ex);
        }
    }

    private Safe.AudioDevice? ResolveOutputDevice()
    {
        if (_options.DeviceName == null)
        {
            return null;
        }

        IReadOnlyList<Safe.AudioDevice> devices = _safeEngine.EnumerateOutputDevices();

        foreach (Safe.AudioDevice d in devices)
        {
            if (d.Name == _options.DeviceName)
            {
                return d;
            }
        }

        return null;
    }

    private void StopInternal(PlaybackEndReason reason, bool fireEvents)
    {
        if (_state == PlaybackState.Stopped)
        {
            return;
        }

        try
        {
            _stream?.Pause();
        }
        catch
        {
            // Best-effort pause during cleanup; ignore native errors.
        }

        _stream?.Dispose();
        _stream = null;

        Interlocked.Exchange(ref _positionSamples, 0);
        _reachedEnd = false;

        SetState(PlaybackState.Stopped);

        if (fireEvents)
        {
            PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs(reason));
        }
    }

    #endregion

    #region Private — audio callback

    private void OnAudioCallback(in AudioOutputCallbackArgs args)
    {
        float[]? buf = _buffer;

        if (buf == null || _reachedEnd)
        {
            args.Buffer.Clear();
            return;
        }

        int position  = _positionSamples;
        int available = buf.Length - position;
        int needed    = args.Buffer.Length;
        int toCopy    = System.Math.Min(available, needed);

        if (toCopy > 0)
        {
            buf.AsSpan(position, toCopy).CopyTo(args.Buffer);

            float vol = _volume;
            if (vol != 1.0f)
            {
                Span<float> filled = args.Buffer.Slice(0, toCopy);
                for (int i = 0; i < filled.Length; i++)
                {
                    filled[i] *= vol;
                }
            }

            Interlocked.Exchange(ref _positionSamples, position + toCopy);
        }

        if (toCopy < needed)
        {
            args.Buffer.Slice(toCopy).Clear();
        }

        if (position + toCopy >= buf.Length)
        {
            if (_isLooping)
            {
                Interlocked.Exchange(ref _positionSamples, 0);
            }
            else
            {
                _reachedEnd = true;
                ThreadPool.QueueUserWorkItem(_ => OnNaturalEnd());
            }
        }
    }

    private void OnNaturalEnd()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _stream?.Pause();
        }
        catch
        {
            // Ignore; stream may already be closing.
        }

        SetState(PlaybackState.Stopped);
        PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs(PlaybackEndReason.Finished));
    }

    #endregion

    #region Private — state and format helpers

    private void SetState(PlaybackState newState)
    {
        PlaybackState old = _state;

        if (old == newState)
        {
            return;
        }

        _state = newState;
        StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(old, newState));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioPlayer));
        }
    }

    private static float[] ConvertPcmToFloat(byte[] source, SampleFormat format)
    {
        if (format == SampleFormat.Int16)
        {
            float[] result = new float[source.Length / 2];
            ReadOnlySpan<short> shorts = MemoryMarshal.Cast<byte, short>(source);

            for (int i = 0; i < result.Length; i++)
            {
                result[i] = shorts[i] / 32768f;
            }

            return result;
        }

        if (format == SampleFormat.UInt16)
        {
            float[] result = new float[source.Length / 2];
            ReadOnlySpan<ushort> ushorts = MemoryMarshal.Cast<byte, ushort>(source);

            for (int i = 0; i < result.Length; i++)
            {
                result[i] = (ushorts[i] / 32768f) - 1.0f;
            }

            return result;
        }

        return Array.Empty<float>();
    }

    #endregion
}
