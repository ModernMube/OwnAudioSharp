using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ownaudio.Audio.Capture;
using Ownaudio.Audio.Devices;
using Ownaudio.Audio.Diagnostics;
using Ownaudio.Audio.Playback;
using Ownaudio.Safe.Exceptions;

namespace Ownaudio.Audio;

/// <summary>
/// Main entry point for the high-level OwnAudio API.
/// Manages the native engine lifecycle and creates <see cref="AudioPlayer"/> and
/// <see cref="AudioRecorder"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// One instance per process is recommended.  Multiple instances are supported but each
/// owns an independent native engine context.
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="Create"/> is safe to call from any thread.
/// All other methods must not be called concurrently on the same instance.
/// </para>
/// <para>
/// <b>Disposal:</b> Call <see cref="DisposeAsync"/> (or synchronous <see cref="Dispose"/>)
/// when the engine is no longer needed.  Dispose all child players and recorders before
/// disposing the engine, or they will be disposed automatically.
/// </para>
/// </remarks>
public sealed class AudioEngine : IAsyncDisposable, IDisposable
{
    #region Fields

    private readonly Safe.AudioEngine _safeEngine;
    private readonly AudioEngineOptions _options;
    private readonly List<AudioPlayer> _players   = new();
    private readonly List<AudioRecorder> _recorders = new();
    private readonly object _childLock = new();

    private AudioEngineState _state;
    private bool _disposed;

    #endregion

    #region Events

    /// <summary>
    /// Raised when the native engine encounters an asynchronous, unrecoverable error.
    /// After this event, <see cref="State"/> transitions to <see cref="AudioEngineState.Faulted"/>
    /// and all child players and recorders become inoperable.
    /// </summary>
    /// <remarks>
    /// The event fires on a <see cref="System.Threading.ThreadPool"/> thread.
    /// Dispose this engine and create a new instance to recover.
    /// </remarks>
    public event EventHandler<AudioEngineFaultedEventArgs>? Faulted;

    #endregion

    #region Construction

    private AudioEngine(Safe.AudioEngine safeEngine, AudioEngineOptions options)
    {
        _safeEngine = safeEngine;
        _options    = options;
        _state      = AudioEngineState.Running;
        Devices     = new AudioDeviceManager(safeEngine);
    }

    /// <summary>
    /// Creates and initializes a new <see cref="AudioEngine"/> instance.
    /// </summary>
    /// <param name="options">
    /// Initialization options, or <see langword="null"/> for defaults (44 100 Hz, stereo, float32).
    /// </param>
    /// <returns>A new, ready-to-use <see cref="AudioEngine"/>.</returns>
    /// <exception cref="AudioEngineException">
    /// Thrown when the native engine fails to initialize.
    /// </exception>
    public static AudioEngine Create(AudioEngineOptions? options = null)
    {
        var effectiveOptions = options ?? new AudioEngineOptions();

        Safe.AudioEngine safeEngine;

        try
        {
            safeEngine = Safe.AudioEngine.Create();
        }
        catch (OwnAudioException ex)
        {
            throw new AudioEngineException((int)ex.ErrorCode, ex.Message, ex);
        }

        return new AudioEngine(safeEngine, effectiveOptions);
    }

    #endregion

    #region Properties

    /// <summary>Current lifecycle state of this engine.</summary>
    public AudioEngineState State => _state;

    /// <summary>
    /// Provides access to the list of available audio input and output devices.
    /// </summary>
    public AudioDeviceManager Devices { get; }

    #endregion

    #region Factory methods

    /// <summary>
    /// Creates a new <see cref="AudioPlayer"/> backed by this engine.
    /// </summary>
    /// <param name="options">
    /// Playback options, or <see langword="null"/> for defaults derived from
    /// <see cref="AudioEngineOptions"/>.
    /// </param>
    /// <returns>A new, ready-to-use <see cref="AudioPlayer"/>.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when this engine has been disposed.</exception>
    public AudioPlayer CreatePlayer(PlaybackOptions? options = null)
    {
        ThrowIfDisposed();

        var player = new AudioPlayer(_safeEngine, MergePlaybackDefaults(options));

        lock (_childLock)
        {
            _players.Add(player);
        }

        return player;
    }

    /// <summary>
    /// Creates a new <see cref="AudioRecorder"/> backed by this engine.
    /// </summary>
    /// <param name="options">
    /// Recorder options, or <see langword="null"/> for defaults derived from
    /// <see cref="AudioEngineOptions"/>.
    /// </param>
    /// <returns>A new, ready-to-use <see cref="AudioRecorder"/>.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when this engine has been disposed.</exception>
    public AudioRecorder CreateRecorder(RecorderOptions? options = null)
    {
        ThrowIfDisposed();

        var recorder = new AudioRecorder(_safeEngine, MergeRecorderDefaults(options));

        lock (_childLock)
        {
            _recorders.Add(recorder);
        }

        return recorder;
    }

    #endregion

    #region IAsyncDisposable / IDisposable

    /// <summary>
    /// Asynchronously disposes all child players and recorders and then destroys the
    /// native engine context.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> that completes when disposal is finished.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _state    = AudioEngineState.Stopped;

        List<AudioPlayer> players;
        List<AudioRecorder> recorders;

        lock (_childLock)
        {
            players   = new List<AudioPlayer>(_players);
            recorders = new List<AudioRecorder>(_recorders);
            _players.Clear();
            _recorders.Clear();
        }

        await Task.Run(() =>
        {
            foreach (AudioPlayer p in players)
            {
                p.Dispose();
            }

            foreach (AudioRecorder r in recorders)
            {
                r.Dispose();
            }

            _safeEngine.Dispose();
        });
    }

    /// <summary>
    /// Synchronously disposes all child players, recorders, and the native engine context.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="DisposeAsync"/> when calling from an async context to avoid
    /// blocking the current thread during native stream cleanup.
    /// </remarks>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    #endregion

    #region Private helpers

    private PlaybackOptions MergePlaybackDefaults(PlaybackOptions? provided)
    {
        if (provided != null)
        {
            return provided;
        }

        return new PlaybackOptions
        {
            BufferSizeFrames = _options.DefaultBufferSizeFrames,
        };
    }

    private RecorderOptions MergeRecorderDefaults(RecorderOptions? provided)
    {
        if (provided != null)
        {
            return provided;
        }

        return new RecorderOptions
        {
            SampleRate       = _options.DefaultSampleRate,
            Channels         = _options.DefaultChannels,
            SampleType       = (Streams.SampleFormat)(int)_options.DefaultSampleFormat,
            BufferSizeFrames = _options.DefaultBufferSizeFrames,
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioEngine));
        }
    }

    private void OnEngineFault(Exception ex)
    {
        _state = AudioEngineState.Faulted;
        ThreadPool.QueueUserWorkItem(
            _ => Faulted?.Invoke(this, new AudioEngineFaultedEventArgs(ex)));
    }

    #endregion
}
