using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ownaudio.Decoders;
using Ownaudio.Decoders.FFmpeg;
using Ownaudio.Decoders.Miniaudio;
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
    private const int MinQueueSize = 2;
    private const int MaxQueueSize = 12;
    private bool _disposed;
    private int FixedBufferSize;

    private readonly SoundTouchProcessor soundTouch = new SoundTouchProcessor();     
    private readonly int FramesPerBuffer;
    private readonly object lockObject = new object();

    /// <summary>
    /// Initializes <see cref="Source"/> 
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
    /// Changes the status of the given resource
    /// <see cref="SourceState"/>
    /// </summary>
    /// <param name="state"></param>
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
    /// Prepares the source for playback, starts the decoding and playback threads.
    /// <see cref="OwnaudioException"/>
    /// </summary>
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
    /// Pauses playback.
    /// </summary>
    protected void Pause()
    {
        if (State is SourceState.Playing or SourceState.Buffering)
        {
            SetAndRaiseStateChanged(SourceState.Paused);
        }
    }

    /// <summary>
    /// Stops playback and resets background processes
    /// </summary>
    protected void Stop()
    {
        if(State == SourceState.Idle)
            return;

        State = SourceState.Idle;
        EnsureThreadsDone();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Moves to the position specified in the parameter in the source.
    /// </summary>
    /// <param name="position">The value of the position is determined in time</param>
    public void Seek(TimeSpan position)
    {
        if (!IsLoaded || CurrentDecoder == null)
        {
            return;
        }

        while (Queue.TryDequeue(out _)) { }
        while (SourceSampleData.TryDequeue(out _)) { }
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
    /// Creates an <see cref="IAudioDecoder"/> instance.
    /// By default, it will returns a new <see cref="FFmpegDecoder"/> instance.
    /// </summary>
    /// <param name="url">Audio URL or path to be loaded.</param>
    /// <returns>A new <see cref="FFmpegDecoder"/> instance.</returns>
    protected virtual IAudioDecoder CreateDecoder(string url)
    {
        // Egységes formátum beállítások használata
        var decoderOptions = SourceManager.GetUnifiedDecoderOptions();
        if(OwnAudio.IsFFmpegInitialized)
            return new FFmpegDecoder(url, decoderOptions);
        else
            return new MiniDecoder(url, decoderOptions);
    }

    /// <summary>
    /// Creates an <see cref="IAudioDecoder"/> instance.
    /// By default, it will returns a new <see cref="FFmpegDecoder"/> instance.
    /// </summary>
    /// <param name="stream">Audio stream to be loaded.</param>
    /// <returns>A new <see cref="FFmpegDecoder"/> instance.</returns>
    protected virtual IAudioDecoder CreateDecoder(Stream stream)
    {
        // Egységes formátum beállítások használata
        var decoderOptions = SourceManager.GetUnifiedDecoderOptions();
        return new FFmpegDecoder(stream, decoderOptions);
    }

    /// <summary>
    /// Sets <see cref="State"/> value and raise <see cref="StateChanged"/> if value is changed.
    /// </summary>
    /// <param name="state">Playback state.</param>
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
    /// Sets <see cref="Position"/> value and raise <see cref="PositionChanged"/> if value is changed.
    /// </summary>
    /// <param name="position">Playback position.</param>
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
    /// Run <see cref="VolumeProcessor"/> and <see cref="CustomSampleProcessor"/> to the specified samples.
    /// </summary>
    /// <param name="samples">Audio samples to process to.</param>
    protected virtual void ProcessSampleProcessors(Span<float> samples)
    {
        if (CustomSampleProcessor is { IsEnabled: true })
            CustomSampleProcessor.Process(samples);

        if (VolumeProcessor.Volume != 1.0f)
            VolumeProcessor.Process(samples);
    }

    /// <summary>
    /// Loading the selected data into the decoder.
    /// </summary>
    /// <param name="decoderFactory"></param>
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
    /// Terminate and close threads used by the resource.
    /// </summary>
    private void EnsureThreadsDone()
    {
        EngineThread?.EnsureThreadDone();
        DecoderThread?.EnsureThreadDone();

        EngineThread = null;
        DecoderThread = null;
    }

    /// <inheritdoc />
    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        State = SourceState.Idle;
        EnsureThreadsDone();

        CurrentDecoder?.Dispose();
        while (Queue.TryDequeue(out _)) { }
        while (SourceSampleData.TryDequeue(out _)) { }

        GC.SuppressFinalize(this);

        _disposed = true;
    }
}
