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
public partial class Source : ISource
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
    /// Source name, which is used to identify the source.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The source is being searched
    /// </summary>
    public bool IsSeeking { get; set; }

    /// <summary>
    /// Adjusts and stores the volume of the source.
    /// </summary>
    public float Volume { get => VolumeProcessor.Volume; set => VolumeProcessor.Volume = value.VerifyVolume(); }

    /// <summary>
    /// Sets the playback tempo of the sources as a percentage. 
    /// The value can range from -20 to +20. In the case of a larger or smaller value, 
    /// it automatically takes the minimum or maximum value.
    /// <see cref="SoundTouch"/>
    /// </summary>
    public double Tempo
    {
        get => soundTouch.TempoChange;
        set
        {
            lock (lockObject)
            {
                if (DoubleUtil.AreClose(soundTouch.TempoChange, value))
                    return;

                soundTouch.TempoChange = value.VerifyTempo();
            }
        }
    }

    /// <summary>
    /// Adjusts the pitch of the sources in semitone steps. 
    /// From -6 to +6. 
    /// If you set a higher or lower value, 
    /// it automatically takes the minimum or maximum value.
    /// <see cref="SoundTouch"/>
    /// </summary>
    public double Pitch
    {
        get => soundTouch.PitchSemiTones;
        set
        {
            lock (lockObject)
            {
                if (DoubleUtil.AreClose(soundTouch.PitchSemiTones, value))
                    return;

                soundTouch.PitchSemiTones = value.VerifyPitch();
            }
        }
    }

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
    /// Gets or sets current specified audio URL.
    /// </summary>
    public string? CurrentUrl { get; private set; }

    /// <summary>
    /// Gets or sets current <see cref="IAudioDecoder"/> instance.
    /// </summary>
    protected IAudioDecoder? CurrentDecoder { get; set; }

    /// <summary>
    /// Gets or sets current specified audio stream.
    /// </summary>
    protected Stream? CurrentStream { get; set; }

    /// <summary>
    /// Gets <see cref="VolumeProcessor"/> instance.
    /// </summary>
    protected VolumeProcessor VolumeProcessor { get; }

    /// <summary>
    /// Gets queue object that holds queued audio frames.
    /// </summary>
    protected ConcurrentQueue<AudioFrame> Queue { get; }    

    /// <summary>
    /// Gets current audio decoder thread.
    /// </summary>
    protected Thread? DecoderThread { get; private set; }

    /// <summary>
    /// Gets current audio engine thread.
    /// </summary>
    protected Thread? EngineThread { get; private set; }

    /// <summary>
    /// Gets whether or not the decoder thread reach end of file.
    /// </summary>
    protected bool IsEOF { get; private set; }
}
