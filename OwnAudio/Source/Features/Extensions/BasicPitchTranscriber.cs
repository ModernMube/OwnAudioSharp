namespace OwnaudioNET.Features.Extensions;

/// <summary>
/// The embedded BasicPitch net behind <see cref="INoteTranscriber"/>. This is what ChordDetect
/// used before MT3 showed up, and it stays the default: nmp.onnx is 200 KB and ships in the
/// package, so it works with no setup at all.
/// </summary>
public sealed class BasicPitchTranscriber : INoteTranscriber, IDisposable
{
    private readonly NotesConvertOptions _options;
    private Model? _model;

    /// <summary>
    /// Uses the note conversion settings ChordDetect has always run with.
    /// </summary>
    public BasicPitchTranscriber() : this(_chordDetectDefaults()) { }

    /// <summary>
    /// Same, with your own conversion thresholds.
    /// </summary>
    public BasicPitchTranscriber(NotesConvertOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 22050 Hz — what the model was trained on. Changing it means retuning the frequency limits.
    /// </summary>
    public int PreferredSampleRate => Constants.AUDIO_SAMPLE_RATE;

    /// <summary>
    /// Runs the net over the whole buffer. sampleRate is accepted for interface symmetry but the
    /// model only makes sense at <see cref="PreferredSampleRate"/>.
    /// </summary>
    public IReadOnlyList<Note> Transcribe(float[] samples, int sampleRate, Action<double>? progress = null)
    {
        _model ??= new Model();

        var output = _model.Predict(new WaveBuffer(samples), progress);
        return new NotesConverter(output).Convert(_options);
    }

    /// <summary>
    /// Drops the inference session.
    /// </summary>
    public void Dispose()
    {
        _model?.Dispose();
        _model = null;
    }

    private static NotesConvertOptions _chordDetectDefaults()
    {
        return new NotesConvertOptions
        {
            OnsetThreshold = 0.5f,
            FrameThreshold = 0.2f,
            MinNoteLength = 15,
            MinFreq = 32.7f,
            MaxFreq = 2800f,
            IncludePitchBends = false,
            MelodiaTrick = true
        };
    }
}
