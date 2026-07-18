using OwnAudio.Midi.File;
using Logger;
using Microsoft.ML.OnnxRuntime;
using System.Numerics.Tensors;

namespace OwnaudioNET.Features.Extensions;

#region Constants

/// <summary>
/// Tuning values the whole BasicPitch pipeline runs on.
/// </summary>
public static class Constants
{
    /// <summary>
    /// FFT hop in samples.
    /// </summary>
    public const int FFT_HOP = 256;

    /// <summary>
    /// How many frames two neighbouring windows share.
    /// </summary>
    public const int N_OVERLAPPING_FRAMES = 30;

    /// <summary>
    /// Window overlap in samples.
    /// </summary>
    public const int OVERLAP_LEN = N_OVERLAPPING_FRAMES * FFT_HOP;

    /// <summary>
    /// Rate the model expects.
    /// </summary>
    public const int AUDIO_SAMPLE_RATE = 22050;

    /// <summary>
    /// Window length, seconds.
    /// </summary>
    public const int AUDIO_WINDOW_LEN = 2;

    /// <summary>
    /// Samples in one window.
    /// </summary>
    public const int AUDIO_N_SAMPLES = AUDIO_SAMPLE_RATE * AUDIO_WINDOW_LEN - FFT_HOP;

    /// <summary>
    /// Distance between two window starts.
    /// </summary>
    public const int HOP_SIZE = AUDIO_N_SAMPLES - OVERLAP_LEN;

    /// <summary>
    /// Annotation frames per second.
    /// </summary>
    public const int ANNOTATIONS_FPS = AUDIO_SAMPLE_RATE / FFT_HOP;

    /// <summary>
    /// Bin 0 sits on MIDI 21 (A0).
    /// </summary>
    public const int MIDI_OFFSET = 21;

    /// <summary>
    /// Top usable freq bin.
    /// </summary>
    public const int MAX_FREQ_IDX = 87;

    /// <summary>
    /// Annotation frames in one window.
    /// </summary>
    public const int ANNOT_N_FRAMES = ANNOTATIONS_FPS * AUDIO_WINDOW_LEN;

    /// <summary>
    /// Contour resolution: bins per semitone.
    /// </summary>
    public const int CONTOURS_BINS_PER_SEMITONE = 3;

    /// <summary>
    /// Semitones covered by the annotation range.
    /// </summary>
    public const int ANNOTATIONS_N_SEMITONES = 88;

    /// <summary>
    /// A0 in Hz.
    /// </summary>
    public const float ANNOTATIONS_BASE_FREQUENCY = 27.5f;

    /// <summary>
    /// All contour bins together.
    /// </summary>
    public const int N_FREQ_BINS_CONTOURS = ANNOTATIONS_N_SEMITONES * CONTOURS_BINS_PER_SEMITONE;

    /// <summary>
    /// Pitch bend range in MIDI ticks.
    /// </summary>
    public const int N_PITCH_BEND_TICKS = 8192;
}

#endregion

#region Math Utilities

/// <summary>
/// Small array/tensor math bits we need in a couple of places.
/// </summary>
public class MathTool
{
    /// <summary>
    /// numpy-ish arange: count values from start, stepping by step.
    /// </summary>
    public static float[] ARange(float start, float step, int count)
    {
        if (count <= 0) return Array.Empty<float>();

        var data = new float[count];
        for (int i = 0; i < data.Length; i++)
            data[i] = start + i * step;

        return data;
    }

    /// <summary>
    /// Mean over a strided slice — skip is the first index, step the stride, length the element count.
    /// </summary>
    public static float Mean(in float[] data, int skip, int step, int length)
    {
        float sum = 0;
        if (length <= 0) return sum;

        for (int i = 0; i < length; ++i)
            sum += data[skip + i * step];

        return sum / length;
    }
}

#endregion

#region Model Processing

/// <summary>
/// The BasicPitch ONNX net, audio in - notes out.
/// </summary>
public class Model : IDisposable
{
    private InferenceSession _session;
    private OutputName _outputName;

    /// <summary>
    /// Loads the embedded nmp.onnx and spins up a session.
    /// </summary>
    public Model()
    {
        _session = new InferenceSession(_loadModelBytes());
        _outputName = new OutputName(_session);
    }

    /// <summary>
    /// Runs the whole buffer through the net window by window.
    /// progressHandler gets 0..1 as we go.
    /// </summary>
    public ModelOutput Predict(WaveBuffer waveBuffer, Action<double>? progressHandler = null)
    {
        var output = new ModelOutputHelper();

        var inputName = _session.InputMetadata.Keys.First();
        var it = new ModelInput(waveBuffer, _session.InputMetadata.First().Value);
        foreach (var (customTensor, progress) in it.Enumerate())
        {
            var ortTensor = new OrtTensor(
                customTensor.Data!,
                Array.ConvertAll(customTensor.Shape!, x => (int)x));

            var outputs = OrtRunner.Run(_session,
                new[] { (inputName, ortTensor) },
                new[] { _outputName.Contour, _outputName.Note, _outputName.Onset });

            output.Contours.Add(_fromOrt(outputs[0]));
            output.Notes.Add(_fromOrt(outputs[1]));
            output.Onsets.Add(_fromOrt(outputs[2]));

            progressHandler?.Invoke(progress);
        }

        return output.Create(waveBuffer.FloatBufferCount);
    }

    private static CustomTensor _fromOrt(OrtTensor ortTensor)
    {
        return new CustomTensor(ortTensor.Data, Array.ConvertAll(ortTensor.Shape, x => (nint)x));
    }

    private static byte[] _loadModelBytes()
    {
        var assembly = typeof(Model).Assembly;
        string _resourceName = "nmp.onnx";

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.EndsWith(_resourceName)) { _resourceName = name; break; }
        }

        using (Stream stream = assembly.GetManifestResourceStream(_resourceName)!)
        using (var memoryStream = new MemoryStream())
        {
            stream.CopyTo(memoryStream);
            return memoryStream.ToArray();
        }
    }

    /// <summary>
    /// Drops the inference session.
    /// </summary>
    public void Dispose()
    {
        _session?.Dispose();
    }
}

/// <summary>
/// The three output tensor names, sorted so we know which is which.
/// </summary>
public class OutputName
{
    /// <summary>
    /// Contour output.
    /// </summary>
    public readonly string Contour;

    /// <summary>
    /// Note output.
    /// </summary>
    public readonly string Note;

    /// <summary>
    /// Onset output.
    /// </summary>
    public readonly string Onset;

    /// <summary>
    /// Pulls the names out of the session metadata.
    /// </summary>
    /// <param name="session"></param>
    public OutputName(InferenceSession session)
    {
        var names = session.OutputMetadata.Keys.ToList();
        names.Sort();

        Contour = names[0];
        Note = names[1];
        Onset = names[2];
    }
}

/// <summary>
/// Collects the per-window tensors until we can stitch them together.
/// </summary>
public class ModelOutputHelper
{
    /// <summary>
    /// Contour tensors, one per window.
    /// </summary>
    public readonly List<CustomTensor> Contours = new List<CustomTensor>();

    /// <summary>
    /// Note tensors, one per window.
    /// </summary>
    public readonly List<CustomTensor> Notes = new List<CustomTensor>();

    /// <summary>
    /// Onset tensors, one per window.
    /// </summary>
    public readonly List<CustomTensor> Onsets = new List<CustomTensor>();

    /// <summary>
    /// Stitches everything into one ModelOutput. totalFrames is the sample count of the source audio.
    /// </summary>
    public ModelOutput Create(int totalFrames)
    {
        return new ModelOutput(_unwrap(Contours, totalFrames), _unwrap(Notes, totalFrames), _unwrap(Onsets, totalFrames));
    }

    private static Tensor _unwrap(IList<CustomTensor> t, int totalFrames)
    {
        if (t.Count == 0) return new Tensor(null, null);

#nullable disable
        var nOlap = Constants.N_OVERLAPPING_FRAMES / 2;
        var nOutputFramesOri = totalFrames * Constants.ANNOTATIONS_FPS / Constants.AUDIO_SAMPLE_RATE;
        var step = (int)t[0].Shape![t[0].Shape.Length - 1];
        int[] oriShape = [t.Count, t[0].Data!.Length / step];
        var shape0 = Math.Min(oriShape[0] * oriShape[1] - nOlap * 2, nOutputFramesOri);
        var rangeStart = nOlap * step;
        var rangeCount = (oriShape[1] - nOlap) * step - rangeStart;
#nullable restore

        var shape = new nint[] { shape0, step };
        var data = new float[shape[0] * shape[1]];

        int size = 0;
        foreach (var tensor in t)
        {
            var tensorData = tensor.Data!;
            var src = tensorData.AsSpan().Slice(rangeStart, Math.Min(rangeCount, tensorData.Length - rangeStart));

            foreach (var v in src)
            {
                if (size == data.Length) break;
                data[size] = v;
                size += 1;
            }
        }
        return new Tensor(data, shape);
    }
}

/// <summary>
/// Slices the audio into overlapping windows the model can eat.
/// </summary>
public class ModelInput
{
    private readonly WaveBuffer _waveBuffer;
    private readonly ShapeHelper _inputInfo;
    private readonly float[] _tensorData;
    private readonly CustomTensor _reusable;

    /// <summary>
    /// Sets up the window buffer from the model's declared input shape.
    /// </summary>
    /// <param name="waveBuffer"></param>
    /// <param name="metadata"></param>
    public ModelInput(WaveBuffer waveBuffer, NodeMetadata metadata)
    {
        _waveBuffer = waveBuffer;
        _inputInfo = new ShapeHelper(metadata);
        _tensorData = new float[_inputInfo.Count];
        _reusable = new CustomTensor(_tensorData, Array.ConvertAll(_inputInfo.Shape, x => (nint)x));
    }

    /// <summary>
    /// Walks the windows. The tensor handed back is reused between iterations, so consume it before moving on.
    /// </summary>
    /// <returns>Window plus a 0..1 progress value.</returns>
    public IEnumerable<(CustomTensor, Double)> Enumerate()
    {
        int cursor = Constants.OVERLAP_LEN / -2;
        int offset = -cursor;
        int totalFrames = _waveBuffer.FloatBufferCount;

        int n, j;
        _tensorData.AsSpan().Slice(0, offset).Fill(0);

        while (cursor < totalFrames)
        {
            j = Math.Max(0, cursor);
            n = Math.Min(_inputInfo.Count - offset, totalFrames - j);
            _waveBuffer.FloatBuffer.AsSpan().Slice(j, n).CopyTo(_tensorData.AsSpan().Slice(offset, n));
            offset += n;

            cursor += Constants.HOP_SIZE;

            if (offset == _inputInfo.Count)
            {
                yield return (_reusable, Math.Clamp((double)cursor / (double)totalFrames, 0, 1));
            }
            else
            {
                _tensorData.AsSpan().Slice(offset).Fill(0);
                yield return (_reusable, 1.0);
            }
            offset = 0;
        }
    }
}

/// <summary>
/// Flat float buffer plus shape — what we hand to the ORT wrapper.
/// </summary>
public class CustomTensor
{
    /// <summary>
    /// Row-major payload.
    /// </summary>
    public readonly float[]? Data;

    /// <summary>
    /// Dimensions.
    /// </summary>
    public readonly nint[]? Shape;

    /// <summary>
    /// Wraps an existing array, no copy.
    /// </summary>
    /// <param name="data"></param>
    /// <param name="shape"></param>
    public CustomTensor(float[]? data, nint[]? shape)
    {
        Data = data;
        Shape = shape;
    }

    /// <summary>
    /// Hands this over as a DenseTensor for the managed ORT path.
    /// </summary>
    public Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float> ToOnnxTensor()
    {
        if (Data == null || Shape == null)
            throw new InvalidOperationException("Cannot convert null tensor to ONNX tensor");

        var intShape = new int[Shape.Length];
        for (int i = 0; i < Shape.Length; i++)
            intShape[i] = (int)Shape[i];

        return new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(Data, intShape);
    }
}

/// <summary>
/// Reads the input shape off the node metadata, negative (dynamic) dims taken as absolute.
/// </summary>
public class ShapeHelper
{
    /// <summary>
    /// Dimensions.
    /// </summary>
    public readonly int[] Shape;

    /// <summary>
    /// Element count, all dims multiplied.
    /// </summary>
    public readonly int Count = 1;

    /// <summary>
    /// Pulls dims out of the metadata.
    /// </summary>
    /// <param name="metadata"></param>
    public ShapeHelper(NodeMetadata metadata)
    {
        var shape = metadata.Dimensions;

        Shape = new int[shape.Length];
        for (int i = 0; i < shape.Length; i++)
        {
            var n = Math.Abs(shape[i]);
            Shape[i] = n;
            Count *= n;
        }
    }
}

#endregion

#region Note Representation

/// <summary>
/// One detected note: when, which pitch, how loud, plus optional bend curve.
/// </summary>
public sealed class Note : IComparable<Note>
{
    /// <summary>
    /// Start, seconds.
    /// </summary>
    public readonly float StartTime;

    /// <summary>
    /// End, seconds.
    /// </summary>
    public readonly float EndTime;

    /// <summary>
    /// MIDI note number.
    /// </summary>
    public readonly int Pitch;

    /// <summary>
    /// 0..1 loudness, later scaled to velocity.
    /// </summary>
    public readonly float Amplitude;

    /// <summary>
    /// Bend curve, one value per model frame. Null if we didn't ask for bends.
    /// </summary>
    public float[]? PitchBend;

    /// <summary>
    /// Fills everything in one go.
    /// </summary>
    public Note(float startTime, float endTime, int pitch, float amplitude, float[]? pitchBend)
    {
        StartTime = startTime;
        EndTime = endTime;
        Pitch = pitch;
        Amplitude = amplitude;
        PitchBend = pitchBend;
    }

    /// <summary>
    /// Debug dump.
    /// </summary>
    public override string ToString()
    {
        var nbend = PitchBend != null ? PitchBend!.Length : 0;
        return $"start: {StartTime}, end: {EndTime}, pitch: {Pitch}, amplitude: {Amplitude}, bend: ${nbend}[{string.Join(",", PitchBend ?? [])}]";
    }

    /// <summary>
    /// Sorts by start, then end, pitch, amplitude, bend length.
    /// </summary>
    public int CompareTo(Note? other)
    {
        if (other == null) return 1;

        float fcmp = StartTime - other.StartTime;
        if (fcmp != 0f) return Math.Sign(fcmp);

        fcmp = EndTime - other.EndTime;
        if (fcmp != 0f) return Math.Sign(fcmp);

        var icmp = Pitch - other.Pitch;
        if (icmp != 0) return Math.Sign(icmp);

        fcmp = Amplitude - other.Amplitude;
        if (fcmp != 0f) return Math.Sign(fcmp);

        var l = PitchBend == null ? -1 : PitchBend.Length;
        var r = other.PitchBend == null ? -1 : other.PitchBend.Length;

        return Math.Sign(l - r);
    }
}
#endregion

#region MIDI Generation

/// <summary>
/// Writes the detected notes out as a standard MIDI file.
/// </summary>
public static class MidiWriter
{
    /// <summary>
    /// BPM the last export settled on.
    /// </summary>
    public static int DetectedTempo = 120;

    /// <summary>
    /// Note on/off events at 480 ticks per quarter, tempo meta up front, Rhodes patch on ch0.
    /// With enough notes we sniff the tempo instead of trusting bpm.
    /// </summary>
    /// <param name="notes"></param>
    /// <param name="outputPath">Where the .mid lands.</param>
    /// <param name="bpm">Fallback tempo if we can't detect one.</param>
    public static void GenerateMidiFile(List<Note> notes, string outputPath, int bpm = 120)
    {
        if (notes.Count > 10)
        {
            bpm = DetectTempo(notes);
            DetectedTempo = bpm;
        }

        Log.Info($"Generating MIDI file with BPM: {bpm}");
        Log.Info($"Number of notes: {notes.Count}");

        int microsecondsPerQuarterNote = 60_000_000 / bpm;

        var timedRaw = new List<(long absoluteTime, bool isMeta, byte metaType, byte[]? metaData, byte status, byte data1, byte data2)>();

        byte[] tempoData =
        [
            (byte)((microsecondsPerQuarterNote >> 16) & 0xFF),
            (byte)((microsecondsPerQuarterNote >> 8)  & 0xFF),
            (byte)( microsecondsPerQuarterNote        & 0xFF)
        ];
        timedRaw.Add((0, true, 0x51, tempoData, 0, 0, 0));
        timedRaw.Add((0, false, 0, null, 0xC0, 4, 0));

        double quarterNotesPerSecond = bpm / 60.0;
        foreach (var note in notes)
        {
            long startTicks = (long)(note.StartTime * 480 * quarterNotesPerSecond);
            long endTicks   = (long)(note.EndTime   * 480 * quarterNotesPerSecond);
            byte velocity   = (byte)Math.Clamp((int)(note.Amplitude * 100f), 1, 127);

            timedRaw.Add((startTicks, false, 0, null, 0x90, (byte)note.Pitch, velocity));
            timedRaw.Add((endTicks,   false, 0, null, 0x80, (byte)note.Pitch, 0));
        }

        timedRaw.Sort((a, b) => a.absoluteTime.CompareTo(b.absoluteTime));

        var midiEvents = new List<MidiEvent>(timedRaw.Count);
        long previousTime = 0;
        foreach (var (absoluteTime, isMeta, metaType, metaData, status, data1, data2) in timedRaw)
        {
            int deltaTime = (int)(absoluteTime - previousTime);
            midiEvents.Add(isMeta
                ? new MidiEvent(deltaTime, metaType, metaData!)
                : new MidiEvent(deltaTime, status, data1, data2));
            previousTime = absoluteTime;
        }

        var track    = new MidiTrack(midiEvents);
        var midiFile = new MidiFile(0, 480, [track]);

        MidiFileWriter.Write(midiFile, outputPath);

        Log.Info($"MIDI file saved: {outputPath}");
        Log.Info($"Time division: 480 ticks per quarter note");
        Log.Info($"Tempo: {bpm} BPM ({microsecondsPerQuarterNote} μs per quarter note)");
    }

    /// <summary>
    /// Guesses the tempo from onset gaps, then snaps to a round BPM if we're close.
    /// </summary>
    /// <param name="notes"></param>
    /// <returns></returns>
    public static int DetectTempo(List<Note> notes)
    {
        if (notes.Count < 2) return 120;

        var onsetTimes = notes.Select(n => n.StartTime).OrderBy(t => t).ToList();

        var intervals = new List<float>();
        for (int i = 1; i < onsetTimes.Count; i++)
        {
            float interval = onsetTimes[i] - onsetTimes[i - 1];
            if (interval > 0.05f && interval < 2.0f)
                intervals.Add(interval);
        }

        if (intervals.Count == 0) return 120;

        var beatCandidates = new Dictionary<int, int>();

        foreach (var interval in intervals)
        {
            for (int division = 1; division <= 4; division *= 2)
            {
                int bpm = (int)Math.Round(60.0f / (interval * division));
                if (bpm < 40 || bpm > 200) continue;

                for (int offset = -2; offset <= 2; offset++)
                {
                    int _candidate = bpm + offset;
                    if (_candidate < 40 || _candidate > 200) continue;

                    beatCandidates.TryGetValue(_candidate, out int hits);
                    beatCandidates[_candidate] = hits + 1;
                }
            }
        }

        if (beatCandidates.Count == 0) return 120;

        var detectedBpm = beatCandidates.OrderByDescending(kvp => kvp.Value).First().Key;

        int[] commonTempos = { 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160 };
        foreach (var commonTempo in commonTempos)
        {
            if (Math.Abs(detectedBpm - commonTempo) <= 3) return commonTempo;
        }

        return detectedBpm;
    }
}
#endregion

#region Note Conversion

/// <summary>
/// Knobs for turning raw model output into notes.
/// </summary>
public record struct NotesConvertOptions
{
    /// <summary>
    /// How eager we are to start a new note. 0.05-0.95.
    /// </summary>
    public float OnsetThreshold = 0.5f;

    /// <summary>
    /// Confidence a frame needs to count as sounding. 0.05-0.95.
    /// </summary>
    public float FrameThreshold = 0.3f;

    /// <summary>
    /// Shorter notes than this get thrown away, in frames.
    /// </summary>
    public int MinNoteLength = 11;

    /// <summary>
    /// Quiet frames tolerated before we call the note over.
    /// </summary>
    public int EnergyThreshold = 11;

    /// <summary>
    /// Low cut in Hz, null = off.
    /// </summary>
    public float? MinFreq = null;

    /// <summary>
    /// High cut in Hz, null = off.
    /// </summary>
    public float? MaxFreq = null;

    /// <summary>
    /// Pull extra onsets out of the frame deltas.
    /// </summary>
    public bool InferOnsets = true;

    /// <summary>
    /// Track pitch bends.
    /// </summary>
    public bool IncludePitchBends = true;

    /// <summary>
    /// Second pass that picks up leftover energy the onsets missed.
    /// </summary>
    public bool MelodiaTrick = true;

    /// <summary>
    /// Defaults.
    /// </summary>
    public NotesConvertOptions() { }
}

/// <summary>
/// Turns contour/note/onset tensors into a playable note list.
/// </summary>
public class NotesConverter
{
    private ModelOutput _input;

    /// <summary>
    /// Takes the raw model output to work on.
    /// </summary>
    /// <param name="input"></param>
    public NotesConverter(ModelOutput input)
    {
        _input = input;
    }

    /// <summary>
    /// Full run: pick notes, optionally bends, then convert frames to seconds.
    /// </summary>
    public List<Note> Convert(NotesConvertOptions opt)
    {
        var notes = _toNotesPolyphonic(opt);
        if (opt.IncludePitchBends) { _getPitchBend(ref notes); }
        return _toNoteList(notes);
    }

    private List<InterNote> _toNotesPolyphonic(NotesConvertOptions opt)
    {
        var (onsets, frames) = NotesHelper.ConstrainFrequency(_input.Onsets, _input.Notes, opt.MaxFreq, opt.MinFreq);
        if (opt.InferOnsets)
            onsets = NotesHelper.GetInferedOnsets(onsets, frames);

        var notes = new List<InterNote>();
        if (frames.Data == null) return notes;

        var frameData = frames.Data!;
        var remainingEnergy = new float[frameData.Length];
        frameData.CopyTo(remainingEnergy, 0);
        var onsetIdxs = NotesHelper.FindValidOnsetIndexs(onsets, opt.OnsetThreshold);

        var frameStep = (int)frames.Shape![frames.Shape.Length - 1];
        var nFrames = frames.Shape![0];
        var nFramesMinus1 = nFrames - 1;

        for (int o = onsetIdxs.Count - 1; o >= 0; o--)
        {
            var idx = onsetIdxs[o];
            var noteStartIdx = idx / frameStep;
            var freqIdx = idx % frameStep;

            if (noteStartIdx >= nFramesMinus1) continue;

            var i = noteStartIdx + 1;
            var k = 0;
            while ((i < nFrames - 1) && (k < opt.EnergyThreshold))
            {
                if (remainingEnergy[i * frameStep + freqIdx] < opt.FrameThreshold)
                    k += 1;
                else
                    k = 0;
                i += 1;
            }

            i -= k;

            if (i - noteStartIdx <= opt.MinNoteLength) continue;

            float amplitude = 0;
            for (var j = 0; j < (i - noteStartIdx); ++j)
            {
                var offset = idx + j * frameStep;
                amplitude += frameData[offset];
                remainingEnergy[offset] = 0;
                if (freqIdx < Constants.MAX_FREQ_IDX) remainingEnergy[offset + 1] = 0;
                if (freqIdx > 0) remainingEnergy[offset - 1] = 0;
            }
            amplitude /= (i - noteStartIdx);
            notes.Add(new InterNote(noteStartIdx, i, freqIdx + Constants.MIDI_OFFSET, amplitude));
        }

        if (opt.MelodiaTrick)
        {
            int i = 0;
            int k = 0;
            int startPos = 0;

            while (true)
            {
                var maxIdx = TensorPrimitives.IndexOfMax(remainingEnergy);
                if (remainingEnergy[maxIdx] <= opt.FrameThreshold) break;

                var iMid = maxIdx / frameStep;
                var freqIdx = maxIdx % frameStep;
                remainingEnergy[iMid * frameStep + freqIdx] = 0;

                i = iMid + 1;
                k = 0;
                while ((i < nFrames - 1) && (k < opt.EnergyThreshold))
                {
                    startPos = i * frameStep + freqIdx;
                    if (remainingEnergy[startPos] < opt.FrameThreshold)
                        k += 1;
                    else
                        k = 0;
                    remainingEnergy[startPos] = 0;
                    if (freqIdx < Constants.MAX_FREQ_IDX) remainingEnergy[startPos + 1] = 0;
                    if (freqIdx > 0) remainingEnergy[startPos - 1] = 0;
                    i += 1;
                }
                var iEnd = i - 1 - k;

                i = iMid - 1;
                k = 0;
                while (i > 0 && k < opt.EnergyThreshold)
                {
                    startPos = i * frameStep + freqIdx;
                    if (remainingEnergy[startPos] < opt.FrameThreshold)
                        k += 1;
                    else
                        k = 0;
                    remainingEnergy[startPos] = 0;
                    if (freqIdx < Constants.MAX_FREQ_IDX) remainingEnergy[startPos + 1] = 0;
                    if (freqIdx > 0) remainingEnergy[startPos - 1] = 0;
                    i -= 1;
                }
                var iStart = i + 1 + k;

                var iLen = iEnd - iStart;
                if (iLen <= opt.MinNoteLength) continue;

                var amplitude = MathTool.Mean(frameData, iStart * frameStep + freqIdx, frameStep, iLen);
                notes.Add(new InterNote(iStart, iEnd, freqIdx + Constants.MIDI_OFFSET, amplitude));
            }
        }
        return notes;
    }

    private void _getPitchBend(ref List<InterNote> notes, int nBinsTolerance = 25)
    {
        if (_input.Contours.Data == null || notes.Count == 0) return;
        var contourSpan = _input.Contours.Data!.AsSpan();
        var contourStep = (int)_input.Contours.Shape![_input.Contours.Shape.Length - 1];

        var windowLen = nBinsTolerance * 2 + 1;
        var freqGaussianSpan = NotesHelper.MakeGaussianWindow(windowLen, 5).AsSpan();
        int freqIdx;
        int freqStartIdx;
        int freqEndIdx;
        int gaussianIdxStart;
        int gaussianIdxEnd;
        int cols;
        int rows;
        float pbShift;
        int mulLength;

        var pitchBendSubMatrix = new float[Constants.N_FREQ_BINS_CONTOURS];
        var bends = new List<float>();
        foreach (InterNote note in notes)
        {
            freqIdx = (int)Math.Round(NotesHelper.MidiPitchToContourBin(note.Pitch));
            freqStartIdx = Math.Max(freqIdx - nBinsTolerance, 0);
            freqEndIdx = Math.Min(Constants.N_FREQ_BINS_CONTOURS, freqIdx + nBinsTolerance + 1);

            rows = note.IEndTime - note.IStartTime;
            cols = freqEndIdx - freqStartIdx;
            if (pitchBendSubMatrix.Length < cols)
                pitchBendSubMatrix = new float[cols];
            pitchBendSubMatrix.AsSpan().Fill(float.MinValue);

            gaussianIdxStart = Math.Max(nBinsTolerance - freqIdx, 0);
            gaussianIdxEnd = windowLen - Math.Max(freqIdx - (Constants.N_FREQ_BINS_CONTOURS - nBinsTolerance - 1), 0);
            if (gaussianIdxStart >= freqGaussianSpan.Length || gaussianIdxEnd > freqGaussianSpan.Length)
                throw new Exception($"GetPitchBend failed, gaussian idx error: [{gaussianIdxStart},{gaussianIdxEnd}] {freqGaussianSpan.Length}");

            bends.Clear();
            pbShift = -(float)(nBinsTolerance - Math.Max(0, nBinsTolerance - freqIdx));
            for (int i = 0; i < rows; ++i)
            {
                var start = (note.IStartTime + i) * contourStep + freqStartIdx;
                mulLength = Math.Min(cols, gaussianIdxEnd - gaussianIdxStart);
                var pstart = contourSpan.Slice(start, mulLength);
                var gaussianStart = freqGaussianSpan.Slice(gaussianIdxStart, mulLength);
                TensorPrimitives.Multiply(pstart, gaussianStart, pitchBendSubMatrix);

                bends.Add((float)TensorPrimitives.IndexOfMax(pitchBendSubMatrix.AsSpan().Slice(0, mulLength)));
            }
            if (bends.Count > 0)
            {
                note.PitchBend = bends.ToArray();
                TensorPrimitives.Add(note.PitchBend!, pbShift, note.PitchBend!);
            }
        }
    }

    private List<Note> _toNoteList(in List<InterNote> notes)
    {
        var ret = new List<Note>(notes.Count);
        if (_input.Contours.Shape == null) return ret;

        foreach (var i in notes)
        {
            ret.Add(new Note(
                NotesHelper.ModelFrameToTime(i.IStartTime),
                NotesHelper.ModelFrameToTime(i.IEndTime),
                i.Pitch,
                i.Amplitude,
                i.PitchBend));
        }
        return ret;
    }
}

#endregion

#region Helper Classes

/// <summary>
/// Pitch/frame conversions and the tensor massaging the converter needs.
/// </summary>
public class NotesHelper
{
    /// <summary>
    /// Hz to nearest MIDI note.
    /// </summary>
    public static int HzToMidi(float freq)
    {
        return (int)Math.Round(12 * (Math.Log2(freq) - Math.Log2(440.0)) + 69);
    }

    /// <summary>
    /// MIDI note back to Hz.
    /// </summary>
    public static float MidiToHz(int pitch)
    {
        return (float)(Math.Pow(2, (pitch - 69) / 12f) * 440);
    }

    /// <summary>
    /// Model frame index to seconds.
    /// </summary>
    public static float ModelFrameToTime(int n)
    {
        if (n < 1) return 0f;

        return (n * Constants.FFT_HOP) / (float)Constants.AUDIO_SAMPLE_RATE;
    }

    /// <summary>
    /// Zeroes everything outside the min/max Hz band. Null limit means that side stays open,
    /// and with both null the originals come straight back.
    /// </summary>
    public static (Tensor, Tensor) ConstrainFrequency(in Tensor onsets, in Tensor frames, float? maxFreq, float? minFreq)
    {
        if (maxFreq == null && minFreq == null) return (onsets, frames);

        var newOnsets = onsets.DeepClone();
        var newFrames = frames.DeepClone();

        if (maxFreq != null)
        {
            var r = Range.StartAt(HzToMidi(maxFreq.Value) - Constants.MIDI_OFFSET);
            _zeroPitch(ref newOnsets, r);
            _zeroPitch(ref newFrames, r);
        }

        if (minFreq != null)
        {
            var r = Range.EndAt(HzToMidi(minFreq.Value) - Constants.MIDI_OFFSET);
            _zeroPitch(ref newOnsets, r);
            _zeroPitch(ref newFrames, r);
        }

        return (newOnsets, newFrames);
    }

    /// <summary>
    /// Extra onsets from rising frame energy — nDiff is how many frames back we look.
    /// </summary>
    public static Tensor GetInferedOnsets(in Tensor onsets, in Tensor frames, int nDiff = 2)
    {
        if (frames.Data == null) return new Tensor(null, null);

        var frameData = frames.Data!;
        int frameSize = (int)frames.Shape![frames.Shape.Length - 1];
        int totalFrameSize = frameData.Length;
        float[] diffs = new float[nDiff * totalFrameSize];
        var diffsSpan = diffs.AsSpan();
        for (int i = 0; i < nDiff; i++)
        {
            var start = i * totalFrameSize;
            var offset = frameSize * (i + 1);
            var length = Math.Max(totalFrameSize - offset, 0);
            if (length > 0)
                Array.Copy(frameData, 0, diffs, start + offset, length);
            var dest = diffsSpan.Slice(start, totalFrameSize);
            TensorPrimitives.Subtract(frameData, dest, dest);
        }

        var frameDiff = diffsSpan.Slice(0, totalFrameSize);
        for (int i = 1; i < nDiff; i++)
            TensorPrimitives.Min(diffsSpan.Slice(i * totalFrameSize, totalFrameSize), frameDiff, frameDiff);

        TensorPrimitives.Max(frameDiff, 0f, frameDiff);

        diffsSpan.Slice(0, nDiff * frameSize).Clear();

        var onsetData = onsets.Data!;
        var maxDiff = TensorPrimitives.Max(frameDiff);
        float scale = TensorPrimitives.Max(onsetData);
        if (maxDiff != 0f) scale = scale / maxDiff;
        TensorPrimitives.Multiply(frameDiff, scale, frameDiff);

        float[] ret = new float[onsetData.Length];
        TensorPrimitives.Max(frameDiff, onsetData, ret);
        nint[] shape = new nint[onsets.Shape!.Length];
        onsets.Shape!.CopyTo(shape, 0);
        return new Tensor(ret, shape);
    }

    /// <summary>
    /// Onset peaks above the threshold that also beat their vertical neighbours.
    /// </summary>
    public static IList<int> FindValidOnsetIndexs(in Tensor onsets, float threshold)
    {
        if (onsets.Shape![0] < 3) return [];

        var data = onsets.Data!;
        var step = (int)onsets.Shape![onsets.Shape.Length - 1];
        var limit = data.Length - step;
        float v;
        var ret = new List<int>();
        for (int i = step; i < limit; ++i)
        {
            if (data[i] < threshold) continue;
            v = data[i];
            if ((v > data[i - step]) && (v > data[i + step]))
                ret.Add(i);
        }
        return ret;
    }

    /// <summary>
    /// Gaussian window, count samples wide with the given std.
    /// </summary>
    public static float[] MakeGaussianWindow(int count, int std)
    {
        if (count <= 0) return [];
        if (count == 1) return [1.0f];

        var n = MathTool.ARange(-0.5f * (count - 1), 1.0f, count);
        var sig2 = (float)(std * std * 2);

        TensorPrimitives.Multiply(n, n, n);
        TensorPrimitives.Divide(n, -sig2, n);
        TensorPrimitives.Exp(n, n);

        return n;
    }

    /// <summary>
    /// MIDI pitch to its contour bin.
    /// </summary>
    public static float MidiPitchToContourBin(int pitch)
    {
        var hz = MidiToHz(pitch);
        return 12f * Constants.CONTOURS_BINS_PER_SEMITONE * (float)Math.Log2(hz / Constants.ANNOTATIONS_BASE_FREQUENCY);
    }

    private static void _zeroPitch(ref Tensor t, Range pitchRange)
    {
        if (t.Data == null) return;

        var limit = t.Shape![1];
        var l = pitchRange.Start.Value;
        if (l < 0 || l > limit) return;
        var r = pitchRange.End.Equals(Index.End) ? limit : pitchRange.End.Value;
        if (r < 0 || r > limit || r < l) return;

        var step = (int)t.Shape![t.Shape.Length - 1];
        for (nint i = 0; i < t.Shape![0]; i++)
        {
            for (int j = l; j < r; j++)
                t.Data![i * step + j] = 0;
        }
    }
}

/// <summary>
/// Working copy of a note while we're still counting in model frames.
/// </summary>
record InterNote(int IStartTime, int IEndTime, int Pitch, float Amplitude, float[]? PitchBend = null)
{
    /// <summary>
    /// Bend curve, filled in by the second pass.
    /// </summary>
    public float[]? PitchBend { get; set; } = PitchBend;
}

#endregion

#region Data Structrures

/// <summary>
/// Flat float array plus its dimensions — our poor man's tensor.
/// </summary>
public class Tensor
{
    /// <summary>
    /// Row-major payload.
    /// </summary>
    public readonly float[]? Data;

    /// <summary>
    /// Dimensions.
    /// </summary>
    public readonly nint[]? Shape;

    /// <summary>
    /// Wraps data and shape as-is, both may be null for an empty tensor.
    /// </summary>
    public Tensor(float[]? data, nint[]? shape)
    {
        Data = data;
        Shape = shape;
    }

    /// <summary>
    /// Copies data and shape into a fresh tensor.
    /// </summary>
    public Tensor DeepClone()
    {
        float[]? data = null;
        nint[]? shape = null;

        if (Data != null)
        {
            data = new float[Data.Length];
            Data.CopyTo(data, 0);
        }

        if (Shape != null)
        {
            shape = new nint[Shape.Length];
            Shape.CopyTo(shape, 0);
        }

        return new Tensor(data, shape);
    }
}

/// <summary>
/// What one full model run gives back.
/// </summary>
public class ModelOutput
{
    /// <summary>
    /// Fine grained pitch contours.
    /// </summary>
    public readonly Tensor Contours;

    /// <summary>
    /// Per frame note activations.
    /// </summary>
    public readonly Tensor Notes;

    /// <summary>
    /// Note starts.
    /// </summary>
    public readonly Tensor Onsets;

    /// <summary>
    /// c = contours, n = notes, o = onsets.
    /// </summary>
    public ModelOutput(Tensor c, Tensor n, Tensor o)
    {
        Contours = c;
        Notes = n;
        Onsets = o;
    }
}

/// <summary>
/// Float sample buffer we feed the model with.
/// </summary>
public class WaveBuffer
{
    /// <summary>
    /// How many samples are actually in there.
    /// </summary>
    public int FloatBufferCount { get; set; }

    /// <summary>
    /// The samples.
    /// </summary>
    public float[]? FloatBuffer { get; set; }

    /// <summary>
    /// Empty buffer, fill the properties yourself.
    /// </summary>
    public WaveBuffer() { }

    /// <summary>
    /// Takes the array over, no copy.
    /// </summary>
    public WaveBuffer(float[] _buffer)
    {
        FloatBuffer = _buffer;
        FloatBufferCount = _buffer.Length;
    }

    /// <summary>
    /// Copies the span out, since we need to hold on to it.
    /// </summary>
    public WaveBuffer(Span<float> _buffer)
    {
        FloatBufferCount = _buffer.Length;
        FloatBuffer = _buffer.ToArray();
    }
}
#endregion
