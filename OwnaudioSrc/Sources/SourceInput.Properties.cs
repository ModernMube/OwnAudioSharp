using System;
using System.Collections.Concurrent;                                               

using Ownaudio.Common;
using Ownaudio.Engines;
using Ownaudio.Processors;
using Ownaudio.Utilities.Extensions;

namespace Ownaudio.Sources;

public partial class SourceInput : ISource
{
    /// <summary>
    /// Event that is raised when sourceinput state has been changed.
    /// </summary>
    public event EventHandler StateChanged;

    /// <summary>
    /// Event that is raised when sourceInput position has been changed.
    /// </summary>
    public event EventHandler PositionChanged;

    /// <summary>
    /// It stores the current activity and status of the sourceInput <see cref="SourceState"/>
    /// </summary>
    public SourceState State { get; protected set; }

    /// <inheritdoc />
    public TimeSpan Duration { get; protected set; }

    /// <inheritdoc />
    public TimeSpan Position { get; protected set; }

    /// <inheritdoc />
    public bool IsSeeking { get; set; }

        /// <summary>
        /// Adjusts and stores the volume of the sourceInput.
        /// </summary>
        public float Volume  { get => VolumeProcessor.Volume; set => VolumeProcessor.Volume = value.VerifyVolume(); }

    /// <inheritdoc />
    public double Tempo { get; set; }

        /// <inheritdoc />
        public double Pitch {  get; set; }

    /// <summary>
    /// Processing unit connected to the sourceinput <see cref="ISampleProcessor"/>
    /// </summary>
    public SampleProcessorBase? CustomSampleProcessor { get; set; }

    /// <summary>
    /// Gets <see cref="VolumeProcessor"/> instance.
    /// </summary>
    protected VolumeProcessor VolumeProcessor { get; }

    /// <summary>
    /// Gets or sets logger instance.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// Gets or sets the current URL of the source input.
    /// </summary>
    public string? CurrentUrl { get; private set; }

    /// <summary>
    /// Gets queue object that holds queued audio frames.
    /// </summary>
    public ConcurrentQueue<float[]> SourceSampleData { get; }

        /// <summary>
        /// Gets <see cref="IAudioEngine"/> instance.
        /// </summary>
        protected IAudioEngine Engine { get; set; }
}
