using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using Ownaudio.Common;
using Ownaudio.Decoders;
using Ownaudio.Processors;
using Ownaudio.Utilities;
using Ownaudio.Utilities.Extensions;

namespace Ownaudio.Sources;

/// <summary>
/// A class that provides functionalities for loading and controlling Source playback.
/// <para>Implements: <see cref="ISource"/></para>
/// </summary>
public partial class SourceWithoutData : ISource
{
   /// <summary>
    /// Event that is raised when source state has been changed.
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Event that is raised when source position has been changed.
    /// </summary>
    public event EventHandler? PositionChanged;

    /// <summary>
    /// Gets whether or not an audio source is loaded and ready for playback.
    /// </summary>
    public bool IsLoaded { get; protected set; }

    /// <summary>
    /// Time of source length
    /// </summary>
    public TimeSpan Duration { get; protected set; }

    /// <summary>
    /// The current playback position of the source.
    /// </summary>
    public TimeSpan Position { get; protected set; }

    /// <summary>
    /// It stores the current activity and status of the source <see cref="SourceState"/>
    /// </summary>
    public SourceState State { get; protected set; }

    /// <summary>
    /// The source is being searched
    /// </summary>
    public bool IsSeeking { get; set; }

    /// <summary>
    /// Source name, which is used to identify the source.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Adjusts and stores the volume of the source.
    /// </summary>
    public float Volume { get => VolumeProcessor.Volume; set => VolumeProcessor.Volume = value.VerifyVolume(); }

    /// <summary>
    /// Sets the playback tempo of the sources as a percentage.
    /// </summary>
    public double Tempo{ get; set; }

    /// <summary>
    /// Adjusts the pitch of the sources in semitone steps. 
    /// </summary>
    public double Pitch{ get; set; }

    /// <summary>
    /// Processing unit connected to the sourceinput <see cref="ISampleProcessor"/>
    /// </summary>
    [NotNull]
    public SampleProcessorBase? CustomSampleProcessor { get; set; }

    /// <summary>
    /// Gets or sets logger instance.
    /// </summary>
    [NotNull]
    public ILogger? Logger { get; set; }  

    /// <summary>
    /// Gets queue object that holds queued audio frames.
    /// </summary>
    public ConcurrentQueue<float[]> SourceSampleData { get; }
    
    /// <summary>
    /// Gets <see cref="VolumeProcessor"/> instance.
    /// </summary>
    protected VolumeProcessor VolumeProcessor { get; }

    /// <summary>
    /// Gets current audio engine thread.
    /// </summary>
    protected Thread? EngineThread { get; private set; }
}

