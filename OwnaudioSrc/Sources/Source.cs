using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ownaudio.Decoders;
using Ownaudio.Decoders.FFmpeg;
using Ownaudio.Decoders.MiniAudio;
using Ownaudio.Exceptions;
using Ownaudio.Processors;
using Ownaudio.Utilities;
using Ownaudio.Utilities.Extensions;
using SoundTouch;

namespace Ownaudio.Sources;

/// <summary>
/// A class that provides functionalities for loading and controlling Source playback.
/// <para>Implements: <see cref="ISource"/></para>
/// </summary>
public partial class Source : ISource
{
    /// <summary>
    /// Minimum number of audio frames that should be maintained in the queue for smooth playback.
    /// </summary>
    private const int MinQueueSize = 2;

    /// <summary>
    /// Maximum number of audio frames that can be stored in the queue to prevent excessive memory usage.
    /// </summary>
    private const int MaxQueueSize = 12;

    /// <summary>
    /// Indicates whether this instance has been disposed.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// The fixed size of audio buffers used for processing, set from the engine configuration.
    /// </summary>
    private int FixedBufferSize;

    /// <summary>
    /// SoundTouch processor instance for audio pitch shifting and tempo changes.
    /// </summary>
    private readonly SoundTouchProcessor soundTouch = new SoundTouchProcessor();

    /// <summary>
    /// Number of audio frames per buffer, determined by the engine configuration.
    /// </summary>
    private readonly int FramesPerBuffer;

    /// <summary>
    /// Synchronization object used to ensure thread-safe operations on shared resources.
    /// </summary>
    private readonly object lockObject = new object();

    /// <summary>
    /// Initializes a new instance of the <see cref="Source"/> class.
    /// Sets up internal processors, queues, and buffer configurations.
    /// </summary>
    public Source()
    {
        VolumeProcessor = new VolumeProcessor { Volume = 1 };
        Queue = new ConcurrentQueue<AudioFrame>();
        SourceSampleData = new ConcurrentQueue<float[]>();

        FixedBufferSize = SourceManager.EngineFramesPerBuffer;
        FramesPerBuffer = SourceManager.EngineFramesPerBuffer;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when given url is null.</exception>
    public Task<bool> LoadAsync(string url)
    {
        Ensure.NotNull(url, nameof(url));
        Ensure.That<OwnaudioException>(State == SourceState.Idle, "Playback thread is currently running.");

        LoadInternal(() => CreateDecoder(url));

        if (IsLoaded)
        {
            CurrentUrl = url;
            CurrentStream = null;
        }

        return Task.FromResult(IsLoaded);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when given stream is null.</exception>
    public Task<bool> LoadAsync(Stream stream)
    {
        Ensure.NotNull(stream, nameof(stream));
        Ensure.That<OwnaudioException>(State == SourceState.Idle, "Playback thread is currently running.");

        LoadInternal(() => CreateDecoder(stream));

        if (IsLoaded)
        {
            CurrentUrl = null;
            CurrentStream = stream;
        }

        return Task.FromResult(IsLoaded);
    }

    /// <summary>
    /// Changes the playback state of the audio source to the specified state.
    /// This method acts as a state machine controller that delegates to appropriate methods.
    /// </summary>
    /// <param name="state">The desired playback state to transition to.</param>
    /// <remarks>
    /// Supported state transitions:
    /// - <see cref="SourceState.Idle"/>: Stops playback
    /// - <see cref="SourceState.Playing"/>: Starts or resumes playback
    /// - <see cref="SourceState.Paused"/>: Pauses current playback
    /// - <see cref="SourceState.Buffering"/>: No action taken (handled internally)
    /// </remarks>
    public void ChangeState(SourceState state)
    {
        switch (state)
        {
            case SourceState.Idle:
                Stop();
                break;
            case SourceState.Playing:
                Play();
                break;
            case SourceState.Paused:
                Pause();
                break;
            case SourceState.Buffering:
                break;
        }
    }

    /// <summary>
    /// Prepares the source for playback and starts the decoding and playback threads.
    /// If already playing or buffering, this method returns without action.
    /// If paused, resumes playback without reinitializing threads.
    /// </summary>
    /// <exception cref="OwnaudioException">Thrown when no audio is loaded for playback.</exception>
    protected void Play()
    {
        Ensure.That<OwnaudioException>(IsLoaded, "No loaded audio for playback.");

        if (State is SourceState.Playing or SourceState.Buffering)
        {
            return;
        }

        if (State == SourceState.Paused)
        {
            SetAndRaiseStateChanged(SourceState.Playing);
            return;
        }

        EnsureThreadsDone();

        Seek(Position);
        IsEOF = false;

        DecoderThread = new Thread(RunDecoder) { Name = "Decoder Thread", IsBackground = true };
        EngineThread = new Thread(RunEngine) { Name = "Engine Thread", IsBackground = true, Priority = ThreadPriority.AboveNormal };

        SetAndRaiseStateChanged(SourceState.Playing);

        DecoderThread.Start();
        EngineThread.Start();
    }

    /// <summary>
    /// Pauses the current playback if the source is currently playing or buffering.
    /// The playback can be resumed later by calling <see cref="Play"/>.
    /// </summary>
    protected void Pause()
    {
        if (State is SourceState.Playing or SourceState.Buffering)
        {
            SetAndRaiseStateChanged(SourceState.Paused);
        }
    }

    /// <summary>
    /// Stops playback completely and resets all background processes.
    /// This method terminates decoder and engine threads and resets the state to idle.
    /// </summary>
    protected void Stop()
    {
        if (State == SourceState.Idle)
            return;

        State = SourceState.Idle;
        EnsureThreadsDone();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Seeks to the specified position in the audio stream with efficient buffer management.
    /// Clears all internal buffers and queues before seeking to ensure clean playback from the new position.
    /// </summary>
    /// <param name="position">The target position to seek to in the audio stream.</param>
    /// <remarks>
    /// This method performs the following operations:
    /// - Clears audio queues and returns buffers to the pool
    /// - Clears SoundTouch processor buffers
    /// - Attempts to seek in the decoder
    /// - Updates the current position and raises position changed event
    /// </remarks>
    public void Seek(TimeSpan position)
    {
        if (!IsLoaded || CurrentDecoder == null)
        {
            return;
        }

        // Efficient buffer clearing
        ClearQueuesWithPoolReturn();

        lock (lockObject)
        {
            soundTouch.Clear();
            _soundTouchCircularBuffer?.Clear();
        }

        Logger?.LogInfo($"Seeking to: {position}.");

        if (!CurrentDecoder.TrySeek(position, out var error))
        {
            Logger?.LogError($"Unable to seek audio stream: {error}");
            IsSeeking = false;
            return;
        }

        SetAndRaisePositionChanged(position);
        Logger?.LogInfo($"Successfully seeks to {position}.");
    }

    /// <summary>
    /// Creates an audio decoder instance for the specified URL.
    /// The decoder type is determined by FFmpeg initialization status.
    /// </summary>
    /// <param name="url">Audio URL or file path to be loaded.</param>
    /// <returns>
    /// A new <see cref="FFmpegDecoder"/> instance if FFmpeg is initialized,
    /// otherwise a new <see cref="MiniDecoder"/> instance.
    /// </returns>
    /// <remarks>
    /// Uses unified decoder options from <see cref="SourceManager.GetUnifiedDecoderOptions"/>
    /// to ensure consistent audio format handling across different decoder types.
    /// </remarks>
    protected virtual IAudioDecoder CreateDecoder(string url)
    {
        // Egységes formátum beállítások használata
        var decoderOptions = SourceManager.GetUnifiedDecoderOptions();
        if (OwnAudio.IsFFmpegInitialized)
            return new FFmpegDecoder(url, decoderOptions);
        else
            return new MiniDecoder(url, decoderOptions);
    }

    /// <summary>
    /// Creates an audio decoder instance for the specified stream.
    /// Currently only supports FFmpeg decoder for stream-based input.
    /// </summary>
    /// <param name="stream">Audio stream to be loaded.</param>
    /// <returns>A new <see cref="FFmpegDecoder"/> instance configured for stream input.</returns>
    /// <remarks>
    /// Uses unified decoder options from <see cref="SourceManager.GetUnifiedDecoderOptions"/>
    /// to ensure consistent audio format handling.
    /// </remarks>
    protected virtual IAudioDecoder CreateDecoder(Stream stream)
    {
        // Egységes formátum beállítások használata
        var decoderOptions = SourceManager.GetUnifiedDecoderOptions();
        return new FFmpegDecoder(stream, decoderOptions);
    }

    /// <summary>
    /// Sets the playback state and raises the <see cref="StateChanged"/> event if the state has changed.
    /// This method ensures that state change events are only fired when the state actually changes.
    /// </summary>
    /// <param name="state">The new playback state to set.</param>
    protected virtual void SetAndRaiseStateChanged(SourceState state)
    {
        var raise = State != state;
        State = state;

        if (raise && StateChanged != null)
        {
            StateChanged.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Sets the playback position and raises the <see cref="PositionChanged"/> event if the position has changed.
    /// This method ensures that position change events are only fired when the position actually changes.
    /// </summary>
    /// <param name="position">The new playback position to set.</param>
    protected virtual void SetAndRaisePositionChanged(TimeSpan position)
    {
        var raise = position != Position;
        Position = position;

        if (raise && PositionChanged != null)
        {
            PositionChanged.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Applies audio processing to the specified audio samples using configured processors.
    /// Processes samples through custom sample processor first, then volume processor if needed.
    /// </summary>
    /// <param name="samples">Audio samples to process. The span is modified in-place.</param>
    /// <remarks>
    /// Processing order:
    /// 1. Custom sample processor (if enabled)
    /// 2. Volume processor (if volume is not 1.0f)
    /// This order ensures that volume changes are applied after any custom effects.
    /// </remarks>
    protected virtual void ProcessSampleProcessors(Span<float> samples)
    {
        if (CustomSampleProcessor is { IsEnabled: true })
            CustomSampleProcessor.Process(samples);

        if (VolumeProcessor.Volume != 1.0f)
            VolumeProcessor.Process(samples);
    }

    /// <summary>
    /// Internal method for loading audio data using the specified decoder factory.
    /// Handles decoder creation, error handling, and property initialization.
    /// </summary>
    /// <param name="decoderFactory">Factory function that creates the appropriate decoder instance.</param>
    /// <remarks>
    /// This method:
    /// - Disposes any existing decoder
    /// - Creates a new decoder using the factory
    /// - Sets up duration and loading status
    /// - Handles exceptions and logs appropriate messages
    /// - Resets position to zero
    /// </remarks>
    private void LoadInternal(Func<IAudioDecoder> decoderFactory)
    {
        Logger?.LogInfo("Loading audio to the player.");

        CurrentDecoder?.Dispose();
        CurrentDecoder = null;
        IsLoaded = false;

        try
        {
            CurrentDecoder = decoderFactory();
            Duration = CurrentDecoder.StreamInfo.Duration;

            Logger?.LogInfo("Audio successfully loaded.");
            IsLoaded = true;
        }
        catch (Exception ex)
        {
            CurrentDecoder = null;
            Logger?.LogError($"Failed to load audio: {ex.Message}");
            IsLoaded = false;
        }

        SetAndRaisePositionChanged(TimeSpan.Zero);
    }

    /// <summary>
    /// Ensures that all background threads (decoder and engine) are properly terminated and cleaned up.
    /// This method blocks until both threads have completed execution.
    /// </summary>
    /// <remarks>
    /// This method is called during state transitions and disposal to ensure clean shutdown.
    /// Thread references are set to null after termination to prevent accidental reuse.
    /// </remarks>
    private void EnsureThreadsDone()
    {
        EngineThread?.EnsureThreadDone();
        DecoderThread?.EnsureThreadDone();

        EngineThread = null;
        DecoderThread = null;
    }

    /// <summary>
    /// Performs enhanced disposal with proper buffer cleanup and resource management.
    /// This method ensures all resources are properly released and prevents multiple disposal calls.
    /// </summary>
    /// <remarks>
    /// Disposal process:
    /// - Sets state to idle and terminates threads
    /// - Disposes current decoder
    /// - Clears queues and returns buffers to pool
    /// - Disposes SoundTouch buffers
    /// - Logs buffer pool statistics in debug builds
    /// - Suppresses finalization to improve GC performance
    /// </remarks>
    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        State = SourceState.Idle;
        EnsureThreadsDone();

        CurrentDecoder?.Dispose();

        // Efficient queue clearing with pool return
        ClearQueuesWithPoolReturn();

        // Dispose SoundTouch buffers
        DisposeSoundTouchBuffers();

#if DEBUG
        Logger?.LogInfo($"Buffer Pool Stats - Hits: {_bufferPoolHits}, Misses: {_bufferPoolMisses}");
#endif

        GC.SuppressFinalize(this);
        _disposed = true;
    }
}
