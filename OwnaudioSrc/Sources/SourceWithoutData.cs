using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Ownaudio.Exceptions;
using Ownaudio.Processors;
using Ownaudio.Utilities.Extensions;

namespace Ownaudio.Sources;

/// <summary>
/// A placeholder source that provides audio processing infrastructure without actual audio content.
/// This class is useful for scenarios where audio processing is needed but no specific audio file is loaded,
/// such as mixing operations with only input sources or real-time generated audio.
/// <para>Implements: <see cref="ISource"/></para>
/// </summary>
public partial class SourceWithoutData : ISource
{
    /// <summary>
    /// Maximum number of audio sample buffers that can be queued for processing.
    /// </summary>
    private const int MaxQueueSize = 12;
    
    /// <summary>
    /// Indicates whether this instance has been disposed.
    /// </summary>
    private bool _disposed;
    
    /// <summary>
    /// Number of audio frames per buffer, synchronized with the engine configuration.
    /// </summary>
    private readonly int FramesPerBuffer;
    
    /// <summary>
    /// Lock object for thread synchronization operations.
    /// </summary>
    private readonly object lockObject = new object();

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceWithoutData"/> class.
    /// Sets up the basic audio processing infrastructure with default configurations.
    /// </summary>
    /// <remarks>
    /// This constructor initializes:
    /// - Volume processor at 100% volume (1.0f)
    /// - Thread-safe queue for sample data
    /// - Frame buffer size from the SourceManager configuration
    /// - Default duration of 1 minute for placeholder timing
    /// 
    /// The 1-minute duration serves as a reasonable default for mixing operations
    /// where specific timing constraints are not critical.
    /// </remarks>
    public SourceWithoutData()
    {
        VolumeProcessor = new VolumeProcessor {  Volume = 1.0f  };
        SourceSampleData = new ConcurrentQueue<float[]>();

        FramesPerBuffer = SourceManager.EngineFramesPerBuffer;

        Duration = new TimeSpan(0, 1, 0);
    }

    /// <summary>
    /// Changes the operational state of the source to the specified state.
    /// </summary>
    /// <param name="state">The desired source state to transition to.</param>
    /// <remarks>
    /// This method maps the provided state to the appropriate control method:
    /// - <see cref="SourceState.Idle"/> calls <see cref="Stop"/>
    /// - <see cref="SourceState.Playing"/> calls <see cref="Play"/>
    /// - <see cref="SourceState.Paused"/> calls <see cref="Pause"/>
    /// - <see cref="SourceState.Buffering"/> performs no action (handled internally)
    /// 
    /// This provides a unified interface for state management that can be called
    /// from external controllers or the SourceManager.
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
    /// Starts the audio processing engine and begins generating silent audio data.
    /// </summary>
    /// <remarks>
    /// This method handles playback initialization:
    /// - Returns early if already playing or buffering
    /// - Resumes from paused state without creating new threads
    /// - Ensures any existing threads are properly terminated
    /// - Creates and starts a new engine thread with above-normal priority
    /// - Sets the state to Playing and raises appropriate events
    /// 
    /// The engine thread generates silent audio frames that can be processed
    /// by volume and custom sample processors, maintaining consistency with
    /// other audio sources in the mixing pipeline.
    /// </remarks>
    protected void Play()
    {
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

        EngineThread = new Thread(RunEngine) { Name = "Engine Thread", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        SetAndRaiseStateChanged(SourceState.Playing);

        EngineThread.Start();
    }

    /// <summary>
    /// Pauses the audio processing if currently playing or buffering.
    /// </summary>
    /// <remarks>
    /// This method pauses the source by setting the state to Paused.
    /// The engine thread continues running but will not generate new audio data
    /// while in the paused state. This allows for quick resumption without
    /// the overhead of stopping and restarting threads.
    /// </remarks>
    protected void Pause()
    {
        if (State is SourceState.Playing or SourceState.Buffering)
        {
            SetAndRaiseStateChanged(SourceState.Paused);
        }
    }

    /// <summary>
    /// Stops audio processing completely and terminates all background threads.
    /// </summary>
    /// <remarks>
    /// This method performs a complete shutdown:
    /// - Returns early if already in idle state
    /// - Sets the state to Idle immediately
    /// - Ensures all background threads are properly terminated
    /// - Raises the StateChanged event to notify listeners
    /// 
    /// After calling this method, the source returns to its initial state
    /// and can be started again with Play().
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
    /// Seeks to the specified position in the audio timeline.
    /// </summary>
    /// <param name="position">The target position to seek to (ignored for sources without data).</param>
    /// <remarks>
    /// This method is provided for interface compliance but performs no operation
    /// since sources without data have no meaningful position concept. The placeholder
    /// duration and continuous generation of silent audio make seeking unnecessary.
    /// </remarks>
    public void Seek(TimeSpan position) { }

    /// <summary>
    /// Returns audio content as a byte array.
    /// </summary>
    /// <param name="position">The position parameter (ignored for sources without data).</param>
    /// <returns>An empty byte array since this source contains no actual audio data.</returns>
    /// <remarks>
    /// This method is provided for interface compliance but always returns an empty array
    /// because sources without data do not contain any actual audio content to retrieve.
    /// This maintains consistency with the ISource interface while accurately representing
    /// the absence of stored audio data.
    /// </remarks>
    public byte[] GetByteAudioData(TimeSpan position)
    {
      byte[] _byte = new byte[] {};
      return _byte;
    }

    /// <summary>
    /// Returns audio content as a float array.
    /// </summary>
    /// <param name="position">The position parameter (ignored for sources without data).</param>
    /// <returns>An empty float array since this source contains no actual audio data.</returns>
    /// <remarks>
    /// This method is provided for interface compliance but always returns an empty array
    /// because sources without data do not contain any actual audio content to retrieve.
    /// This maintains consistency with the ISource interface while accurately representing
    /// the absence of stored audio data.
    /// </remarks>
    public float[] GetFloatAudioData(TimeSpan position)
    {
      float[] _float = new float[] {};
      return _float;
    }
    
    /// <summary>
    /// Sets the <see cref="State"/> value and raises the <see cref="StateChanged"/> event if the value has changed.
    /// </summary>
    /// <param name="state">The new source state to set.</param>
    /// <remarks>
    /// This method provides thread-safe state management by only raising the event when the state actually changes.
    /// The StateChanged event is invoked synchronously on the calling thread, allowing listeners to respond
    /// immediately to state transitions.
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
    /// <param name="position">The new position to set.</param>
    /// <remarks>
    /// This method provides thread-safe position management by only raising the event when the position actually changes.
    /// For sources without data, position updates are primarily used for synchronization with other sources
    /// in mixing scenarios and for maintaining consistent timing behavior.
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
    /// Applies audio processing to the specified samples using volume and custom sample processors.
    /// </summary>
    /// <param name="samples">The audio samples to process (typically silent data for sources without content).</param>
    /// <remarks>
    /// This method applies processing in the following order:
    /// 1. Custom sample processor (if enabled) - allows for audio generation or effects
    /// 2. Volume processor (if volume is not at 100%) - applies volume scaling
    /// 
    /// Even though this source generates silent audio, the processors can transform
    /// this silence into actual audio content (e.g., synthesized sounds, effects)
    /// or modify the volume characteristics for mixing purposes.
    /// </remarks>
    protected virtual void ProcessSampleProcessors(Span<float> samples)
    {
        if (CustomSampleProcessor is { IsEnabled: true })
            CustomSampleProcessor.Process(samples);

        if (VolumeProcessor.Volume != 1.0f)
            VolumeProcessor.Process(samples);
    }

    /// <summary>
    /// Main engine loop that continuously generates and processes silent audio data.
    /// Runs in a background thread until the source is stopped.
    /// </summary>
    /// <remarks>
    /// This method performs the following operations in a continuous loop:
    /// - Handles paused and seeking states by sleeping and continuing
    /// - Controls queue size to prevent excessive memory usage
    /// - Generates silent audio frames when in playing state
    /// - Applies sample processors to the generated audio
    /// - Enqueues processed audio for the mixing engine
    /// - Updates position with a fixed 1-second increment for timing
    /// - Uses appropriate sleep intervals to balance responsiveness and CPU usage
    /// 
    /// The engine generates frames sized according to the engine configuration
    /// and channel count, ensuring compatibility with the mixing pipeline.
    /// When the loop exits, it resets position and state appropriately.
    /// </remarks>
    private void RunEngine()
    {
        Logger?.LogInfo("Engine thread is started.");

        while (State != SourceState.Idle)
        {
            if (State == SourceState.Paused || IsSeeking)
            {
                Thread.Sleep(20);
                continue;
            }

            if (SourceSampleData.Count >= MaxQueueSize)
            {
                Thread.Sleep(20);
                continue;
            }

            if(State == SourceState.Playing)
            {
               int samplesSize = FramesPerBuffer * (int)SourceManager.OutputEngineOptions.Channels;
               float[] samples = new float[samplesSize];

               ProcessSampleProcessors(samples);
               
               SourceSampleData.Enqueue(samples);

               SetAndRaisePositionChanged(new TimeSpan(0,0,1));
            }

            Thread.Sleep(5);
        }
        
        SetAndRaisePositionChanged(TimeSpan.Zero);

        Task.Run(() => SetAndRaiseStateChanged(SourceState.Idle)); 

        Logger?.LogInfo("Engine thread is completed.");
    }

    /// <summary>
    /// Ensures that the engine thread is properly terminated and cleaned up.
    /// </summary>
    /// <remarks>
    /// This method safely terminates the background engine thread:
    /// - Calls EnsureThreadDone() to wait for proper thread termination
    /// - Sets the thread reference to null to enable garbage collection
    /// 
    /// This method should be called before starting new threads or during disposal
    /// to prevent thread leaks and ensure clean shutdown.
    /// </remarks>
    private void EnsureThreadsDone()
    {
        EngineThread?.EnsureThreadDone();

        EngineThread = null;
    }

    /// <summary>
    /// Releases all resources used by the <see cref="SourceWithoutData"/> instance.
    /// </summary>
    /// <remarks>
    /// This method performs complete cleanup:
    /// - Sets the state to Idle to stop processing
    /// - Terminates all background threads
    /// - Clears all queued sample data
    /// - Suppresses finalizer execution for better performance
    /// - Sets the disposed flag to prevent multiple disposal
    /// 
    /// This method is safe to call multiple times and follows the standard dispose pattern.
    /// After disposal, the source instance should not be used for any operations.
    /// </remarks>
    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        State = SourceState.Idle;
        EnsureThreadsDone();
        while (SourceSampleData.TryDequeue(out _)) { }

        GC.SuppressFinalize(this);

        _disposed = true;
    }
}