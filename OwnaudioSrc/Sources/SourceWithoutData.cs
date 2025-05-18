using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Ownaudio.Exceptions;
using Ownaudio.Processors;
using Ownaudio.Utilities.Extensions;

namespace Ownaudio.Sources;

/// <summary>
/// A class that provides functionalities for loading and controlling Source playback.
/// <para>Implements: <see cref="ISource"/></para>
/// </summary>
public partial class SourceWithoutData : ISource
{
    private const int MaxQueueSize = 12;
    private bool _disposed; 
    private readonly int FramesPerBuffer;
    private readonly object lockObject = new object();

    /// <summary>
    /// Initializes <see cref="Source"/> 
    /// </summary>
    public SourceWithoutData()
    {
        VolumeProcessor = new VolumeProcessor {  Volume = 1.0f  };
        SourceSampleData = new ConcurrentQueue<float[]>();

        FramesPerBuffer = SourceManager.EngineFramesPerBuffer;

        Duration = new TimeSpan(0, 1, 0);
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
    public void Seek(TimeSpan position) { }

    /// <summary>
    /// Returns the contents of the audio file loaded into the source in a byte array.
    /// </summary>
    /// <param name="position">
    /// Jumps to the position specified in the parameter after decoding all the data. 
    /// The most typical is zero (the beginning of the file).
    /// </param>
    /// <returns>The array containing the data.</returns>
    public byte[] GetByteAudioData(TimeSpan position)
    {
      byte[] _byte = new byte[] {};
      return _byte;
    }

    /// <summary>
    /// Returns the contents of the audio file loaded into the source in a float array.
    /// </summary>
    /// <param name="position">
    /// Jumps to the position specified in the parameter after decoding all the data. 
    /// The most typical is zero (the beginning of the file).
    /// </param>
    /// <returns>The array containing the data.</returns>
    public float[] GetFloatAudioData(TimeSpan position)
    {
      float[] _float = new float[] {};
      return _float;
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
    /// Continuous processing and preparation of data for the output audio engine
    /// </summary>
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
    /// Terminate and close threads used by the resource.
    /// </summary>
    private void EnsureThreadsDone()
    {
        EngineThread?.EnsureThreadDone();

        EngineThread = null;
    }

    /// <summary>
    /// Dispose
    /// </summary>
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
