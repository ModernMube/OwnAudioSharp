using Ownaudio.Decoders;
using Ownaudio.Decoders.FFmpeg;
using Ownaudio.Decoders.MiniAudio;
using Ownaudio.Engines;
using Ownaudio.Exceptions;
using Ownaudio.Processors;
using Ownaudio.Sources.Extensions;
using Ownaudio.Utilities;
using Ownaudio.Utilities.Extensions;
using SoundTouch;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ownaudio.Sources;

/// <summary>
/// A class that provides functionalities for loading and controlling Source playback.
/// <para>Implements: <see cref="ISource"/></para>
/// </summary>
public partial class Source : ISource
{
    /// <summary>
    /// Minimum size for the audio queue buffer.
    /// </summary>
    private const int MinQueueSize = 2;
    
    /// <summary>
    /// Maximum size for the audio queue buffer.
    /// </summary>
    private const int MaxQueueSize = 12;
    
    /// <summary>
    /// Indicates whether this instance has been disposed.
    /// </summary>
    private bool _disposed;
    
    /// <summary>
    /// The fixed size of audio buffers used for processing.
    /// </summary>
    private int FixedBufferSize;

    /// <summary>
    /// Sound processing engine for tempo and pitch modifications.
    /// </summary>
    private readonly SoundTouchProcessor soundTouch = new SoundTouchProcessor();
    
    /// <summary>
    /// Number of audio frames per buffer for processing.
    /// </summary>
    private readonly int FramesPerBuffer;
    
    /// <summary>
    /// Lock object for thread synchronization.
    /// </summary>
    private readonly object lockObject = new object();

    /// <summary>
    /// Initializes a new instance of the <see cref="Source"/> class.
    /// Sets up default volume processor, audio queues, and buffer configurations.
    /// </summary>
    public Source()
    {
        VolumeProcessor = new VolumeProcessor { Volume = 1 };
        Queue = new ConcurrentQueue<AudioFrame>();
        SourceSampleData = new ConcurrentQueue<float[]>();

        FixedBufferSize = SourceManager.EngineFramesPerBuffer;
        FramesPerBuffer = SourceManager.EngineFramesPerBuffer;
    }
   
    /// <summary>
    /// Asynchronously loads an audio source from the specified URL.
    /// </summary>
    /// <param name="url">The URL or file path of the audio source to load.</param>
    /// <returns>A task that represents the asynchronous load operation. The task result indicates whether the load was successful.</returns>
    /// <exception cref="ArgumentNullException">Thrown when the provided URL is null.</exception>
    /// <exception cref="OwnaudioException">Thrown when the playback thread is currently running.</exception>
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

    /// <summary>
    /// Asynchronously loads an audio source from the specified stream.
    /// </summary>
    /// <param name="stream">The stream containing the audio data to load.</param>
    /// <returns>A task that represents the asynchronous load operation. The task result indicates whether the load was successful.</returns>
    /// <exception cref="ArgumentNullException">Thrown when the provided stream is null.</exception>
    /// <exception cref="OwnaudioException">Thrown when the playback thread is currently running.</exception>
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
    /// </summary>
    /// <param name="state">The desired source state to change to.</param>
    /// <remarks>
    /// This method maps the provided state to the appropriate control method:
    /// - <see cref="SourceState.Idle"/> calls <see cref="Stop"/>
    /// - <see cref="SourceState.Playing"/> calls <see cref="Play"/>
    /// - <see cref="SourceState.Paused"/> calls <see cref="Pause"/>
    /// - <see cref="SourceState.Buffering"/> performs no action
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
    /// </summary>
    /// <exception cref="OwnaudioException">Thrown when no audio is loaded for playback.</exception>
    /// <remarks>
    /// This method creates and starts two background threads:
    /// - Decoder Thread: Handles audio decoding operations
    /// - Engine Thread: Manages audio playback with above-normal priority
    /// </remarks>
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
    /// Pauses the current playback if it is currently playing or buffering.
    /// </summary>
    /// <remarks>
    /// This method only changes the state to paused; it does not stop the background threads.
    /// The threads continue running but will not process audio data while in the paused state.
    /// </remarks>
    protected void Pause()
    {
        if (State is SourceState.Playing or SourceState.Buffering)
        {
            SetAndRaiseStateChanged(SourceState.Paused);
        }
    }

    /// <summary>
    /// Stops playback completely and terminates all background processing threads.
    /// </summary>
    /// <remarks>
    /// This method performs a complete shutdown of playback operations:
    /// - Sets the state to Idle
    /// - Ensures all background threads are terminated
    /// - Raises the StateChanged event
    /// </remarks>
    protected void Stop()
    {
        if(State == SourceState.Idle)
            return;

        State = SourceState.Idle;
        EnsureThreadsDone();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Seeks to the specified position in the audio source.
    /// </summary>
    /// <param name="position">The target position in the audio stream, specified as a TimeSpan.</param>
    /// <remarks>
    /// This method performs the following operations:
    /// - Clears all queued audio data and returns buffers to the pool
    /// - Clears the SoundTouch processor state
    /// - Attempts to seek the decoder to the specified position
    /// - Updates the current position and raises the PositionChanged event
    /// If seeking fails, an error is logged and the operation is aborted.
    /// </remarks>
    public void Seek(TimeSpan position)
    {
        if (!IsLoaded || CurrentDecoder == null)
            return;

        while (Queue.TryDequeue(out _)) { }
        while (SourceSampleData.TryDequeue(out var buffer))
        {
            SimpleAudioBufferPool.Return(buffer);
        }

        soundTouch.Clear();

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
    /// Creates an <see cref="IAudioDecoder"/> instance for the specified URL.
    /// </summary>
    /// <param name="url">The audio URL or file path to be loaded.</param>
    /// <returns>
    /// A new decoder instance. Returns <see cref="FFmpegDecoder"/> if FFmpeg is initialized,
    /// otherwise returns <see cref="MiniDecoder"/>.
    /// </returns>
    /// <remarks>
    /// The decoder selection is based on the availability of FFmpeg initialization.
    /// Both decoders use unified decoder options obtained from the SourceManager.
    /// </remarks>
    protected virtual IAudioDecoder CreateDecoder(string url)
    {
        var decoderOptions = SourceManager.GetUnifiedDecoderOptions();
        if(OwnAudio.IsFFmpegInitialized)
            return new FFmpegDecoder(url, decoderOptions);
        else
            return new MiniDecoder(url, decoderOptions);
    }

    /// <summary>
    /// Creates an <see cref="IAudioDecoder"/> instance for the specified stream.
    /// </summary>
    /// <param name="stream">The audio stream to be loaded.</param>
    /// <returns>A new <see cref="FFmpegDecoder"/> instance configured for stream input.</returns>
    /// <remarks>
    /// Currently only supports FFmpeg decoder for stream input.
    /// Uses unified decoder options obtained from the SourceManager.
    /// </remarks>
    protected virtual IAudioDecoder CreateDecoder(Stream stream)
    {
        var decoderOptions = SourceManager.GetUnifiedDecoderOptions();
        return new FFmpegDecoder(stream, decoderOptions);
    }

    /// <summary>
    /// Sets the <see cref="State"/> value and raises the <see cref="StateChanged"/> event if the value has changed.
    /// </summary>
    /// <param name="state">The new playback state to set.</param>
    /// <remarks>
    /// This method provides thread-safe state management by only raising the event when the state actually changes.
    /// The StateChanged event is invoked synchronously on the calling thread.
    /// </remarks>
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
    /// Sets the <see cref="Position"/> value and raises the <see cref="PositionChanged"/> event if the value has changed.
    /// </summary>
    /// <param name="position">The new playback position to set.</param>
    /// <remarks>
    /// This method provides thread-safe position management by only raising the event when the position actually changes.
    /// The PositionChanged event is invoked synchronously on the calling thread.
    /// </remarks>
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
    /// Applies audio processing to the specified samples using <see cref="VolumeProcessor"/> and <see cref="CustomSampleProcessor"/>.
    /// </summary>
    /// <param name="samples">The audio samples to process.</param>
    /// <remarks>
    /// Processing is applied in the following order:
    /// 1. Custom sample processor (if enabled)
    /// 2. Volume processor (if volume is not 1.0)
    /// This ordering ensures that custom processing occurs before volume adjustment.
    /// </remarks>
    protected virtual void ProcessSampleProcessors(Span<float> samples)
    {
        if (CustomSampleProcessor is { IsEnabled: true })
            CustomSampleProcessor.Process(samples);

        if (VolumeProcessor.Volume != 1.0f)
            VolumeProcessor.Process(samples);

        OutputLevels = SourceManager.OutputEngineOptions.Channels ==  OwnAudioEngine.EngineChannels.Stereo
            ? CalculateLevels.CalculateAverageStereoLevelsSpan(samples)
            : CalculateLevels.CalculateAverageMonoLevelSpan(samples);
    }

    /// <summary>
    /// Internal method for loading audio data using the provided decoder factory.
    /// </summary>
    /// <param name="decoderFactory">A function that creates and returns an IAudioDecoder instance.</param>
    /// <remarks>
    /// This method handles the complete loading process:
    /// - Disposes of any existing decoder
    /// - Creates a new decoder using the provided factory
    /// - Extracts stream information including duration
    /// - Sets the loaded state and logs the operation result
    /// - Resets the position to zero
    /// Any exceptions during loading are caught and logged, with the loaded state set to false.
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
    /// Ensures that all background threads used by the source are properly terminated and cleaned up.
    /// </summary>
    /// <remarks>
    /// This method safely terminates both the Engine and Decoder threads:
    /// - Calls EnsureThreadDone() on each thread to wait for proper termination
    /// - Sets thread references to null to enable garbage collection
    /// This method should be called before starting new threads or during disposal.
    /// </remarks>
    private void EnsureThreadsDone()
    {
        EngineThread?.EnsureThreadDone();
        DecoderThread?.EnsureThreadDone();

        EngineThread = null;
        DecoderThread = null;
    }

    /// <summary>
    /// Releases all resources used by the <see cref="Source"/> instance.
    /// </summary>
    /// <remarks>
    /// This method performs complete cleanup:
    /// - Sets state to Idle and terminates all threads
    /// - Disposes of the current decoder
    /// - Clears all queued data
    /// - Suppresses finalizer to improve performance
    /// - Sets the disposed flag to prevent multiple disposal
    /// This method is safe to call multiple times.
    /// </remarks>
    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        State = SourceState.Idle;
        EnsureThreadsDone();

        //DisposeEngineResources();

        CurrentDecoder?.Dispose();
        while (Queue.TryDequeue(out _)) { }
        while (SourceSampleData.TryDequeue(out _)) { }

        GC.SuppressFinalize(this);

        _disposed = true;
    }
}
