using System;
using System.Collections.Generic;
using System.Threading;

using Ownaudio.Common;
using Ownaudio.Engines;
using Ownaudio.Processors;
using Ownaudio.Utilities.Extensions;

namespace Ownaudio.Sources;

public partial class SourceManager
{
   /// <summary>
    /// Status change event
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// The position change event
    /// </summary>
    public event EventHandler? PositionChanged;

    /// <summary>
    /// Output audio engine parameters
    /// </summary>
    public static AudioEngineOutputOptions OutputEngineOptions {  get; set; } = new AudioEngineOutputOptions();

    /// <summary>
    /// Input audio engine parameters
    /// </summary>
    public static AudioEngineInputOptions InputEngineOptions { get; set; } = new AudioEngineInputOptions();

    /// <summary>
    /// Audio Engine Frames per Buffer
    /// </summary>
    public static int EngineFramesPerBuffer { get; set; } = 512;

    /// <summary>
    /// List of added sources
    /// </summary>
    public List<ISource> Sources { get; protected set; } = new List<ISource>();

    /// <summary>
    /// List of added sources
    /// </summary>
    public List<ISource> SourcesInput { get; protected set; } = new List<ISource>();

    /// <summary>
    /// List of simple effect sources
    /// </summary>
    public List<SourceSpark> SourcesSpark { get; protected set; } = new List<SourceSpark>();

    /// <summary>
    /// The resources have been loaded successfully
    /// </summary>
    public bool IsLoaded { get; protected set; } = false;

    /// <summary>
    /// Is there an input source added?
    /// </summary>
    public bool IsRecorded { get; protected set; } = false;

    /// <summary>
    /// Length of playback time of sources
    /// </summary>
    public TimeSpan Duration { get; protected set; } = TimeSpan.Zero;

    /// <summary>
    /// Current playback position
    /// </summary>
    public TimeSpan Position { get; protected set; } = TimeSpan.Zero;

    /// <summary>
    /// The current status of the mix
    /// </summary>
    public SourceState State { get; protected set; } = SourceState.Idle;

    /// <summary>
    /// This search is in progress in the mix
    /// </summary>
    public bool IsSeeking { get; private set; } = false;

    /// <summary>
    /// The playback volume of the mix
    /// </summary>
    public float Volume { get => VolumeProcessor.Volume; set => VolumeProcessor.Volume = value.VerifyVolume(); }

    /// <summary>
    /// The process that performs the logging
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// A process that modifies data
    /// </summary>
    //public ISampleProcessor? CustomSampleProcessor { get; set; }
    public SampleProcessorBase? CustomSampleProcessor { get; set; } = new DefaultProcessor();

    /// <summary>
    /// Specifies whether to save audio data during playback.
    /// </summary>
    public bool IsWriteData { get; set; } = false;

    /// <summary>
    /// Stereo audio output level. 
    /// In the case of mono signal, only the left channel value changes.
    /// </summary>
    public (float left, float right) OutputLevels { get; set; } = (0f, 0f);

    /// <summary>
    /// Stereo audio input level. 
    /// In the case of mono signal, only the left channel value changes.
    /// </summary>
    public (float left, float right) InputLevels { get; set; } = (0f, 0f);

    /// <summary>
    /// The name and path of the recorded audio file
    /// </summary>
    protected string? SaveWaveFileName {  get; private set; }

    /// <summary>
    /// The bit depth of the recorded audio file
    /// </summary>
    protected int BitPerSamples { get; private set; }  = OutputEngineOptions.SampleRate;

    /// <summary>
    /// Gets <see cref="IAudioEngine"/> instance.
    /// </summary>
    protected IAudioEngine? Engine { get; private set;  }

    /// <summary>
    /// Gets <see cref="VolumeProcessor"/> instance.
    /// </summary>
    protected VolumeProcessor VolumeProcessor { get; }

    /// <summary>
    /// Gets current audio mixing engine thread.
    /// </summary>
    protected Thread? MixEngineThread { get; private set; }

    /// <summary>
    /// List of output source files.
    /// </summary>
    protected List<string> UrlList { get; private set; } = new List<string>();

    /// <summary>
    /// All source volume
    /// </summary>
    /// <param name="index"></param>
    /// <param name="volume"></param>
    public void SetVolume(int index, float volume)
    {
        if (index >= 0 && index < Sources.Count)
        {
            Sources[index].Volume = volume;
        }
    }

    /// <summary>
    /// Adjust the pitch of each source
    /// </summary>
    /// <param name="index"></param>
    /// <param name="pitch"></param>
    public void SetPitch(int index, double pitch)
    {
        if (index >= 0 && index < Sources.Count)
        {
            Sources[index].Pitch = pitch;
        }
    }

    /// <summary>
    /// Adjust the tempo of each source
    /// </summary>
    /// <param name="index"></param>
    /// <param name="tempo"></param>
    public void SetTempo(int index, double tempo)
    {
        if (index >= 0 && index < Sources.Count)
        {
            Sources[index].Tempo = tempo;
        }
    }
}

/// <summary>
/// Default Customprocessor 
/// </summary>
public class DefaultProcessor : SampleProcessorBase
{
    /// <summary>
    /// Sample data process
    /// </summary>
    /// <param name="sample"></param>
    public override void Process(Span<float> sample) { }

    /// <summary>
    /// Processor reset
    /// </summary>
    public override void Reset() { }
}
