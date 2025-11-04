using Ownaudio.Common;
using Ownaudio.Decoders;
using Ownaudio.Processors;
using Ownaudio.Sources.Extensions;
using Ownaudio.Utilities;
using SoundTouch;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ownaudio.Sources;

/// <summary>
/// High-performance audio source implementation that loads complete audio files into memory for fast playback.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="SourceSpark"/> class is a specialized audio source implementation that loads the entire
/// audio content into memory for the fastest possible access. This is particularly useful for short audio files,
/// sound effects, or frequently played audio content.
/// </para>
/// <para>
/// The class provides the following main features:
/// <list type="bullet">
/// <item><description>Complete audio file loading into memory</description></item>
/// <item><description>Real-time audio processing with SoundTouch processor</description></item>
/// <item><description>Multi-threaded playback management</description></item>
/// <item><description>Volume control</description></item>
/// <item><description>Position tracking and seeking</description></item>
/// <item><description>Looping playback support</description></item>
/// </list>
/// </para>
/// <para>
/// The class supports both FFmpeg and MiniAudio decoders, automatically selecting
/// the appropriate decoder based on availability.
/// </para>
/// </remarks>
/// <seealso cref="ISource"/>
/// <seealso cref="SourceManager"/>
/// <seealso cref="VolumeProcessor"/>
[Obsolete("This is legacy code, available only for compatibility!")]
public partial class SourceSpark : ISource
{
    #region Private Fields
    private bool _disposed;
    private bool _isLooping;
    private bool _isPlaying;
    private readonly object lockObject = new object();
    private readonly SoundTouchProcessor soundTouch = new SoundTouchProcessor();
    private int FixedBufferSize;
    private readonly int FramesPerBuffer;
    private Thread? EngineThread;
    private float[]? _processedSamples;
    private int _lastProcessedSize = 0;
    private CancellationTokenSource? _cancellationTokenSource;
    private const int MaxQueueSize = 20;
    #endregion

    #region Constructor     
    /// <summary>
    /// Initializes a new instance of the <see cref="SourceSpark"/> class.
    /// </summary>
    /// <remarks>This constructor sets up the default state of the <see cref="SourceSpark"/> instance,
    /// including initializing the volume processor, sample data queue, buffer sizes, and the default name of the
    /// source.</remarks>
    public SourceSpark()
    {
        VolumeProcessor = new VolumeProcessor { Volume = 1 };
        SourceSampleData = new ConcurrentQueue<float[]>();
        FixedBufferSize = SourceManager.EngineFramesPerBuffer;
        FramesPerBuffer = SourceManager.EngineFramesPerBuffer;
        Name = "SimpleSource";
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceSpark"/> class and loads audio data from the specified file.
    /// </summary>
    /// <remarks>The constructor synchronously waits for the audio file to be loaded. Ensure the file path
    /// points to a valid audio file.</remarks>
    /// <param name="filePath">The path to the audio file to load. Cannot be null or empty.</param>
    /// <param name="looping">A value indicating whether the audio should loop during playback. Defaults to <see langword="false"/>.</param>
    public SourceSpark(string filePath, bool looping = false) : this()
    {
        IsLooping = looping;
        LoadAsync(filePath).Wait();
    }
    #endregion

    #region Public Methods

    /// <summary>
    /// Asynchronously loads audio data from the specified URL into memory for playback.
    /// </summary>
    /// <param name="url">The URL or file path of the audio to be loaded.</param>
    /// <returns>A task representing the asynchronous operation. The task result is a boolean indicating whether the loading was successful.</returns>
    public Task<bool> LoadAsync(string url)
    {
        Ensure.NotNull(url, nameof(url));

        try
        {
            Logger?.LogInfo($"Loading simple source: {url}");

            var decoderOptions = SourceManager.GetUnifiedDecoderOptions();
            CurrentDecoder = AudioDecoderFactory.Create(url, decoderOptions.samplerate, decoderOptions.channels);

            Duration = CurrentDecoder.StreamInfo.Duration;

            // Load entire audio into memory for fast access
            var result = CurrentDecoder.DecodeAllFrames(TimeSpan.Zero);
            AudioData = MemoryMarshal.Cast<byte, float>(result.Frame.Data).ToArray();

            CurrentUrl = url;
            IsLoaded = true;
            Logger?.LogInfo("Simple source successfully loaded.");
        }
        catch (Exception ex)
        {
            Logger?.LogError($"Failed to load simple source: {ex.Message}");
            IsLoaded = false;
        }

        return Task.FromResult(IsLoaded);
    }

    /// <summary>
    /// Starts playback of the audio from the beginning if the source is loaded and ready.
    /// </summary>
    /// <remarks>
    /// This method ensures that the audio source is loaded before initiating playback. If the source
    /// is already playing, an informational message is logged, and the method returns without performing
    /// any additional actions. During playback, an internal engine thread is started to manage the real-time
    /// playback process. Playback state transitions and threading priorities are configured to optimize
    /// performance during playback.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the source is not loaded.</exception>
    public void Play()
    {
        if (!IsLoaded || AudioData == null)
        {
            Logger?.LogWarning("Cannot play: audio not loaded.");
            return;
        }

        if (_isPlaying)
        {
            Logger?.LogInfo("Simple source already playing.");
            return;
        }

        _isPlaying = true;
        CurrentSampleIndex = 0;

        SetAndRaiseStateChanged(SourceState.Playing);

        _cancellationTokenSource = new CancellationTokenSource();
        EngineThread = new Thread(() => RunEngine(_cancellationTokenSource.Token))
        {
            Name = $"SimpleSource Engine Thread - {Name}",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };

        EngineThread.Start();
        Logger?.LogInfo($"Simple source started: {Name}");
    }

    /// <summary>
    /// Stops the playback of the current <see cref="SourceSpark"/> instance.
    /// </summary>
    /// <remarks>
    /// This method halts any ongoing audio playback, resets the playback state to idle,
    /// clears the current playback position, cancels any ongoing tasks associated with playback,
    /// and ensures that all playback-related threads are completed. It is safe to call this method
    /// even if playback is not currently active.
    /// </remarks>
    public void Stop()
    {
        if (!_isPlaying)
            return;

        _isPlaying = false;
        _cancellationTokenSource?.Cancel();

        SetAndRaiseStateChanged(SourceState.Idle);
        SetAndRaisePositionChanged(TimeSpan.Zero);

        EnsureThreadsDone();
        CurrentSampleIndex = 0;

        Logger?.LogInfo($"Simple source stopped: {Name}");
    }

    /// <summary>
    /// Pauses the playback of the audio source if it is in the playing state.
    /// </summary>
    /// <remarks>
    /// This method transitions the state of the <see cref="SourceSpark"/> instance from
    /// <see cref="SourceState.Playing"/> to <see cref="SourceState.Paused"/>.
    /// It also logs the action using the configured <see cref="ILogger"/> if available.
    /// </remarks>
    public void Pause()
    {
        if (State == SourceState.Playing)
        {
            SetAndRaiseStateChanged(SourceState.Paused);
            Logger?.LogInfo($"Simple source paused: {Name}");
        }
    }

    /// <summary>
    /// Resumes audio playback if the current state is <see cref="SourceState.Paused"/>.
    /// </summary>
    /// <remarks>
    /// This method transitions the audio source from the <see cref="SourceState.Paused"/> state
    /// to the <see cref="SourceState.Playing"/> state and logs the action, if a logger is provided.
    /// </remarks>
    public void Resume()
    {
        if (State == SourceState.Paused)
        {
            SetAndRaiseStateChanged(SourceState.Playing);
            Logger?.LogInfo($"Simple source resumed: {Name}");
        }
    }

    /// <summary>
    /// Seeks the audio playback position to the specified timestamp within the loaded audio data.
    /// </summary>
    /// <param name="position">The target position, represented as a <see cref="TimeSpan"/>, to seek to in the audio stream. This value is clamped between the start and end of the audio data.</param>
    public void Seek(TimeSpan position)
    {
        if (!IsLoaded || AudioData == null)
            return;

        var totalSamples = AudioData.Length;
        var channels = CurrentDecoder?.StreamInfo.Channels ?? 2;
        var sampleRate = CurrentDecoder?.StreamInfo.SampleRate ?? 44100;

        var targetSample = (int)(position.TotalSeconds * sampleRate * channels);
        CurrentSampleIndex = Math.Max(0, Math.Min(targetSample, totalSamples - 1));

        // CRITICAL: Clear SoundTouch buffer to prevent old audio from playing after seek
        soundTouch.Clear();

        // CRITICAL: Clear queued samples to prevent old audio from playing
        while (SourceSampleData.TryDequeue(out var buffer))
        {
            SimpleAudioBufferPool.Return(buffer);
        }

        SetAndRaisePositionChanged(position);
        Logger?.LogInfo($"Simple source seeked to: {position}");
    }

    /// <summary>
    /// Changes the playback state of the audio source to the specified state.
    /// </summary>
    /// <param name="state">The desired state of the audio source.
    /// Possible values are defined in the <see cref="SourceState"/> enumeration.</param>
    public void ChangeState(SourceState state)
    {
        switch (state)
        {
            case SourceState.Idle:
                Stop();
                break;
            case SourceState.Playing:
                if (State == SourceState.Paused)
                    Resume();
                else
                    Play();
                break;
            case SourceState.Paused:
                Pause();
                break;
        }
    }

    /// <summary>
    /// Retrieves raw audio data as a byte array starting from the specified position.
    /// </summary>
    /// <param name="position">The position of the audio material to which the position should be returned after reading the data.</param>
    /// <returns>A byte array containing the audio data starting from the specified position,
    /// or <c>null</c> if the source is not loaded or the decoder is unavailable.</returns>
    public byte[] GetByteAudioData(TimeSpan position)
    {
        #nullable disable
        if (!IsLoaded || CurrentDecoder == null)
            return null;
        #nullable restore

        var result = CurrentDecoder.DecodeAllFrames(position);
        return result.Frame.Data;
    }

    /// <summary>
    /// Retrieves audio data in the form of a float array at the specified position.
    /// </summary>
    /// <param name="position">The position of the audio material to which the position should be returned after reading the data.</param>
    /// <returns>A float array containing the audio data from the specified position, or <c>null</c> if the source is not loaded or audio data is unavailable.</returns>
    public float[] GetFloatAudioData(TimeSpan position)
    {
        #nullable disable
        if (!IsLoaded || AudioData == null)
            return null;
        #nullable restore

        return (float[])AudioData.Clone();
    }
    #endregion

    #region Private Methods
    /// <summary>
    /// Updates the current state of the source and raises the <see cref="StateChanged"/> event if the state has changed.
    /// </summary>
    /// <param name="state">The new state to set for the source. Can be one of the values from the <see cref="SourceState"/> enumeration.</param>
    private void SetAndRaiseStateChanged(SourceState state)
    {
        var raise = State != state;
        State = state;

        if (raise && StateChanged != null)
            StateChanged.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Updates the current playback position and raises the <see cref="PositionChanged"/> event if the position has changed.
    /// </summary>
    /// <param name="position">The new playback position to be set.</param>
    private void SetAndRaisePositionChanged(TimeSpan position)
    {
        var raise = position != Position;
        Position = position;

        if (raise && PositionChanged != null)
            PositionChanged.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Clears the specified buffer of audio samples.
    /// </summary>
    /// <param name="buffer">The buffer to clear.</param>
    /// <param name="length">The number of samples to clear.</param>
    /// <remarks>This method is optimized for performance and does not allocate memory.</remarks>
    private static void FastClear(float[] buffer, int length)
    {
        if (buffer == null) return;

        int clearLength = Math.Min(buffer.Length, length);

        if (clearLength <= 1024)
            buffer.AsSpan(0, clearLength).Clear();
        else
            Array.Clear(buffer, 0, clearLength);
    }
    #endregion
    
    #region Dispose
    /// <summary>
    /// Releases all resources used by the <see cref="SourceSpark"/> instance.
    /// </summary>
    /// <remarks>
    /// This method disposes of all resources associated with the current <see cref="SourceSpark"/> instance,
    /// including stopping audio playback, disposing of the current audio decoder, and releasing audio buffers.
    /// Any remaining resources in the <see cref="SourceSampleData"/> queue are returned to the buffer pool.
    /// This method ensures that the object is properly cleaned up and suppresses finalization to optimize garbage collection.
    /// Safe to call multiple times; subsequent calls will have no effect.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        CurrentDecoder?.Dispose();

        while (SourceSampleData.TryDequeue(out var buffer))
            SimpleAudioBufferPool.Return(buffer);

        GC.SuppressFinalize(this);
        _disposed = true;
    }
    #endregion
}
