using Logger;

namespace OwnaudioNET.Features.Extensions;

// AOT-compatible drop-in for System.Numerics.Tensors.TensorPrimitives (plain loops, no NuGet dependency)
file static class Tp
{
    public static int IndexOfMax(ReadOnlySpan<float> s)
    {
        if (s.IsEmpty) return -1;
        int idx = 0; float max = s[0];
        for (int i = 1; i < s.Length; i++) { if (s[i] > max) { max = s[i]; idx = i; } }
        return idx;
    }
    public static float Max(ReadOnlySpan<float> s)
    {
        float max = float.MinValue;
        foreach (float v in s) if (v > max) max = v;
        return max;
    }
    public static void Subtract(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> d)
    { for (int i = 0; i < d.Length; i++) d[i] = x[i] - y[i]; }
    public static void Min(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> d)
    { for (int i = 0; i < d.Length; i++) d[i] = x[i] < y[i] ? x[i] : y[i]; }
    public static void Max(ReadOnlySpan<float> x, float scalar, Span<float> d)
    { for (int i = 0; i < d.Length; i++) d[i] = x[i] > scalar ? x[i] : scalar; }
    public static void Max(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> d)
    { for (int i = 0; i < d.Length; i++) d[i] = x[i] > y[i] ? x[i] : y[i]; }
    public static void Multiply(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> d)
    { for (int i = 0; i < d.Length; i++) d[i] = x[i] * y[i]; }
    public static void Multiply(ReadOnlySpan<float> x, float scalar, Span<float> d)
    { for (int i = 0; i < d.Length; i++) d[i] = x[i] * scalar; }
    public static void Divide(ReadOnlySpan<float> x, float scalar, Span<float> d)
    { for (int i = 0; i < d.Length; i++) d[i] = x[i] / scalar; }
    public static void Exp(ReadOnlySpan<float> x, Span<float> d)
    { for (int i = 0; i < d.Length; i++) d[i] = MathF.Exp(x[i]); }
    public static void Add(ReadOnlySpan<float> x, float scalar, Span<float> d)
    { for (int i = 0; i < d.Length; i++) d[i] = x[i] + scalar; }
}

#region Constants

public static class Constants
{
    public const int FFT_HOP = 256;
    public const int N_OVERLAPPING_FRAMES = 30;
    public const int OVERLAP_LEN = N_OVERLAPPING_FRAMES * FFT_HOP;
    public const int AUDIO_SAMPLE_RATE = 22050;
    public const int AUDIO_WINDOW_LEN = 2;
    public const int AUDIO_N_SAMPLES = AUDIO_SAMPLE_RATE * AUDIO_WINDOW_LEN - FFT_HOP;
    public const int HOP_SIZE = AUDIO_N_SAMPLES - OVERLAP_LEN;
    public const int ANNOTATIONS_FPS = AUDIO_SAMPLE_RATE / FFT_HOP;
    public const int MIDI_OFFSET = 21;
    public const int MAX_FREQ_IDX = 87;
    public const int ANNOT_N_FRAMES = ANNOTATIONS_FPS * AUDIO_WINDOW_LEN;
    public const int CONTOURS_BINS_PER_SEMITONE = 3;
    public const int ANNOTATIONS_N_SEMITONES = 88;
    public const float ANNOTATIONS_BASE_FREQUENCY = 27.5f;
    public const int N_FREQ_BINS_CONTOURS = ANNOTATIONS_N_SEMITONES * CONTOURS_BINS_PER_SEMITONE;
    public const int N_PITCH_BEND_TICKS = 8192;
}

#endregion

#region Math Utilities

public class MathTool
{
    public static float[] ARange(float start, float step, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return Array.Empty<float>();
        if (count == 1) return new[] { start };
        var data = new float[count];
        for (int i = 0; i < data.Length; i++)
            data[i] = start + i * step;
        return data;
    }

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
/// BasicPitch note-prediction model. Delegates to the ownaudio_ml native library via
/// OwnAudio.ML.NotesPredictor — no ONNX Runtime dependency in this layer.
/// </summary>
public class Model : IDisposable
{
    public Model() { }

    public ModelOutput Predict(WaveBuffer waveBuffer, Action<double>? progressHandler = null)
    {
        if (waveBuffer.FloatBuffer == null || waveBuffer.FloatBufferCount == 0)
            return new ModelOutput(new Tensor(null, null), new Tensor(null, null), new Tensor(null, null));

        progressHandler?.Invoke(0.1);

        OwnAudio.ML.NotesPredictionResult result;
        try
        {
            result = OwnAudio.ML.NotesPredictor.Predict(waveBuffer.FloatBuffer, Constants.AUDIO_SAMPLE_RATE);
        }
        catch (Exception ex)
        {
            Log.Warning($"BasicPitch prediction failed: {ex.Message}");
            return new ModelOutput(new Tensor(null, null), new Tensor(null, null), new Tensor(null, null));
        }

        progressHandler?.Invoke(0.9);

        int fc = result.FrameCount;
        int fb = result.FreqBins;

        var contoursTensor = new Tensor(result.Contours, [(nint)fc, (nint)fb]);
        var notesTensor    = new Tensor(result.Notes,    [(nint)fc, 88]);
        var onsetsTensor   = new Tensor(result.Onsets,   [(nint)fc, 88]);

        progressHandler?.Invoke(1.0);

        return new ModelOutput(contoursTensor, notesTensor, onsetsTensor);
    }

    public void Dispose() { }
}

#endregion

#region Note class

public sealed class Note : IComparable<Note>
{
    public readonly float StartTime;
    public readonly float EndTime;
    public readonly int Pitch;
    public readonly float Amplitude;
    public float[]? PitchBend;

    public Note(float startTime, float endTime, int pitch, float amplitude, float[]? pitchBend)
    {
        StartTime = startTime;
        EndTime   = endTime;
        Pitch     = pitch;
        Amplitude = amplitude;
        PitchBend = pitchBend;
    }

    public override string ToString()
    {
        var nbend = PitchBend != null ? PitchBend.Length : 0;
        return $"start: {StartTime}, end: {EndTime}, pitch: {Pitch}, amplitude: {Amplitude}, bend: ${nbend}[{string.Join(",", PitchBend ?? [])}]";
    }

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

public static class MidiWriter
{
    public static int DetectedTempo = 120;

    public static void GenerateMidiFile(List<Note> notes, string outputPath, int bpm = 120)
    {
        if (notes.Count > 10)
        {
            bpm = DetectTempo(notes);
            DetectedTempo = bpm;
        }

        Log.Info($"Generating MIDI file with BPM: {bpm}, Notes: {notes.Count}");

        const int ticksPerBeat = 480;
        int uspb = 60_000_000 / bpm;
        byte[] tempoBytes = [(byte)(uspb >> 16), (byte)(uspb >> 8), (byte)(uspb & 0xFF)];

        double qnps = bpm / 60.0;

        var timedEvents = new List<(long tick, bool isMeta, byte metaType, byte[]? metaData, byte status, byte d1, byte d2)>();

        timedEvents.Add((0, true,  0x51, tempoBytes,      0,    0, 0));
        timedEvents.Add((0, false, 0,    null,             0xC0, 4, 0));

        foreach (var note in notes)
        {
            long startTick = (long)(note.StartTime * ticksPerBeat * qnps);
            long endTick   = (long)(note.EndTime   * ticksPerBeat * qnps);
            byte vel   = (byte)Math.Clamp((int)(note.Amplitude * 100f), 1, 127);
            byte pitch = (byte)Math.Clamp(note.Pitch, 0, 127);
            timedEvents.Add((startTick, false, 0, null, 0x90, pitch, vel));
            timedEvents.Add((endTick,   false, 0, null, 0x80, pitch, 0));
        }

        timedEvents.Sort((a, b) => a.tick.CompareTo(b.tick));

        long lastTick = timedEvents.Count > 0 ? timedEvents[^1].tick : 0;
        timedEvents.Add((lastTick, true, 0x2F, Array.Empty<byte>(), 0, 0, 0));

        var midiEvents = new List<OwnAudio.Midi.File.MidiEvent>();
        long prev = 0;
        foreach (var (tick, isMeta, metaType, metaData, status, d1, d2) in timedEvents)
        {
            int delta = (int)(tick - prev);
            if (isMeta)
                midiEvents.Add(new OwnAudio.Midi.File.MidiEvent(delta, metaType, metaData ?? Array.Empty<byte>()));
            else
                midiEvents.Add(new OwnAudio.Midi.File.MidiEvent(delta, status, d1, d2));
            prev = tick;
        }

        var track    = new OwnAudio.Midi.File.MidiTrack(midiEvents);
        var midiFile = new OwnAudio.Midi.File.MidiFile(0, ticksPerBeat, [track]);
        OwnAudio.Midi.File.MidiFileWriter.Write(midiFile, outputPath);

        Log.Info($"MIDI file saved: {outputPath}");
    }

    public static int DetectTempo(List<Note> notes)
    {
        if (notes.Count < 2) return 120;

        var onsetTimes = notes.Select(n => n.StartTime).OrderBy(t => t).ToList();
        var intervals  = new List<float>();
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
                float beatInterval = interval * division;
                int   bpm          = (int)Math.Round(60.0f / beatInterval);
                if (bpm >= 40 && bpm <= 200)
                {
                    for (int offset = -2; offset <= 2; offset++)
                    {
                        int cb = bpm + offset;
                        if (cb >= 40 && cb <= 200)
                            beatCandidates[cb] = beatCandidates.GetValueOrDefault(cb) + 1;
                    }
                }
            }
        }

        if (beatCandidates.Count == 0) return 120;

        int detected = beatCandidates.OrderByDescending(kvp => kvp.Value).First().Key;
        int[] common = { 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160 };
        foreach (int ct in common)
        {
            if (Math.Abs(detected - ct) <= 3)
                return ct;
        }
        return detected;
    }
}

#endregion

#region Note Conversion

public record struct NotesConvertOptions
{
    public float OnsetThreshold  = 0.5f;
    public float FrameThreshold  = 0.3f;
    public int   MinNoteLength   = 11;
    public int   EnergyThreshold = 11;
    public float? MinFreq        = null;
    public float? MaxFreq        = null;
    public bool  InferOnsets     = true;
    public bool  IncludePitchBends = true;
    public bool  MelodiaTrick    = true;
    public NotesConvertOptions() { }
}

public class NotesConverter
{
    private ModelOutput input;

    public NotesConverter(ModelOutput input) { this.input = input; }

    public List<Note> Convert(NotesConvertOptions opt)
    {
        var notes = ToNotesPolyphonic(opt);
        if (opt.IncludePitchBends)
            GetPitchBend(ref notes);
        return ToNoteList(notes);
    }

    private List<InterNote> ToNotesPolyphonic(NotesConvertOptions opt)
    {
        var (onsets, frames) = NotesHelper.ConstrainFrequency(input.Onsets, input.Notes, opt.MaxFreq, opt.MinFreq);
        if (opt.InferOnsets)
            onsets = NotesHelper.GetInferedOnsets(onsets, frames);

        var notes = new List<InterNote>();
        if (frames.Data == null) return notes;

        var remainingEnergy = new float[frames.Data.Length];
        var frameData = frames.Data!;
        frameData.CopyTo(remainingEnergy, 0);
        var onsetIdxs = NotesHelper.FindValidOnsetIndexs(onsets, opt.OnsetThreshold).Reverse();

        var frameStep = (int)frames.Shape![frames.Shape.Length - 1];
        var nFrames = frames.Shape![0];
        var nFramesMinus1 = nFrames - 1;

        foreach (var idx in onsetIdxs)
        {
            var noteStartIdx = idx / frameStep;
            var freqIdx      = idx % frameStep;

            if (noteStartIdx >= nFramesMinus1) continue;

            var i = noteStartIdx + 1;
            var k = 0;
            while ((i < nFrames - 1) && (k < opt.EnergyThreshold))
            {
                if (remainingEnergy[i * frameStep + freqIdx] < opt.FrameThreshold) k++;
                else k = 0;
                i++;
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
                if (freqIdx > 0)                      remainingEnergy[offset - 1] = 0;
            }
            amplitude /= (i - noteStartIdx);
            notes.Add(new InterNote(noteStartIdx, i, freqIdx + Constants.MIDI_OFFSET, amplitude));
        }

        if (opt.MelodiaTrick)
        {
            float maxValue;
            int maxIdx, i, k, startPos;

            while (true)
            {
                maxIdx   = Tp.IndexOfMax(remainingEnergy);
                maxValue = remainingEnergy[maxIdx];
                if (maxValue <= opt.FrameThreshold) break;

                var iMid   = maxIdx / frameStep;
                var freqIdx = maxIdx % frameStep;
                remainingEnergy[iMid * frameStep + freqIdx] = 0;

                i = iMid + 1; k = 0;
                while ((i < nFrames - 1) && (k < opt.EnergyThreshold))
                {
                    startPos = i * frameStep + freqIdx;
                    if (remainingEnergy[startPos] < opt.FrameThreshold) k++;
                    else k = 0;
                    remainingEnergy[startPos] = 0;
                    if (freqIdx < Constants.MAX_FREQ_IDX) remainingEnergy[startPos + 1] = 0;
                    if (freqIdx > 0)                      remainingEnergy[startPos - 1] = 0;
                    i++;
                }
                var iEnd = i - 1 - k;

                i = iMid - 1; k = 0;
                while (i > 0 && k < opt.EnergyThreshold)
                {
                    startPos = i * frameStep + freqIdx;
                    if (remainingEnergy[startPos] < opt.FrameThreshold) k++;
                    else k = 0;
                    remainingEnergy[startPos] = 0;
                    if (freqIdx < Constants.MAX_FREQ_IDX) remainingEnergy[startPos + 1] = 0;
                    if (freqIdx > 0)                      remainingEnergy[startPos - 1] = 0;
                    i--;
                }
                var iStart = i + 1 + k;

                if (iStart < 0)  throw new Exception($"iStart is: {iStart}");
                if (iEnd >= nFrames) throw new Exception($"iEnd is: {iEnd}, nFrames is: {nFrames}");

                var iLen = iEnd - iStart;
                if (iLen <= opt.MinNoteLength) continue;

                float amplitude = MathTool.Mean(frameData, iStart * frameStep + freqIdx, frameStep, iLen);
                notes.Add(new InterNote(iStart, iEnd, freqIdx + Constants.MIDI_OFFSET, amplitude));
            }
        }
        return notes;
    }

    private void GetPitchBend(ref List<InterNote> notes, int nBinsTolerance = 25)
    {
        if (input.Contours.Data == null || notes.Count == 0) return;
        var contourSpan  = input.Contours.Data!.AsSpan();
        var contourStep  = (int)input.Contours.Shape![input.Contours.Shape.Length - 1];
        var windowLen    = nBinsTolerance * 2 + 1;
        var freqGaussian = NotesHelper.MakeGaussianWindow(windowLen, 5).AsSpan();

        var pbSubMatrix = new float[Constants.N_FREQ_BINS_CONTOURS];
        var bends       = new List<float>();

        foreach (InterNote note in notes)
        {
            int freqIdx      = (int)Math.Round(NotesHelper.MidiPitchToContourBin(note.Pitch));
            int freqStartIdx = Math.Max(freqIdx - nBinsTolerance, 0);
            int freqEndIdx   = Math.Min(Constants.N_FREQ_BINS_CONTOURS, freqIdx + nBinsTolerance + 1);
            int rows         = note.IEndTime - note.IStartTime;
            int cols         = freqEndIdx - freqStartIdx;

            if (pbSubMatrix.Length < cols) pbSubMatrix = new float[cols];
            pbSubMatrix.AsSpan().Fill(float.MinValue);

            int gaussStart = Math.Max(nBinsTolerance - freqIdx, 0);
            int gaussEnd   = windowLen - Math.Max(freqIdx - (Constants.N_FREQ_BINS_CONTOURS - nBinsTolerance - 1), 0);

            bends.Clear();
            float pbShift = -(float)(nBinsTolerance - Math.Max(0, nBinsTolerance - freqIdx));

            for (int i = 0; i < rows; ++i)
            {
                int    start    = (note.IStartTime + i) * contourStep + freqStartIdx;
                int    mulLen   = Math.Min(cols, gaussEnd - gaussStart);
                var    pslice   = contourSpan.Slice(start, mulLen);
                var    gslice   = freqGaussian.Slice(gaussStart, mulLen);
                Tp.Multiply(pslice, gslice, pbSubMatrix);

                int maxI = Tp.IndexOfMax(pbSubMatrix.AsSpan(0, mulLen));
                bends.Add(maxI);
            }

            if (bends.Count > 0)
            {
                note.PitchBend = bends.ToArray();
                Tp.Add(note.PitchBend!, pbShift, note.PitchBend!);
            }
        }
    }

    private List<Note> ToNoteList(in List<InterNote> notes)
    {
        if (notes.Count == 0 || input.Contours.Shape == null)
            return new List<Note>();

        return notes.Select(i => new Note(
            NotesHelper.ModelFrameToTime(i.IStartTime),
            NotesHelper.ModelFrameToTime(i.IEndTime),
            i.Pitch,
            i.Amplitude,
            i.PitchBend
        )).ToList();
    }
}

#endregion

#region Helper Classes

public class NotesHelper
{
    public static int HzToMidi(float freq)
        => (int)Math.Round(12 * (Math.Log2(freq) - Math.Log2(440.0)) + 69);

    public static float MidiToHz(int pitch)
        => (float)(Math.Pow(2, (pitch - 69) / 12f) * 440);

    public static float ModelFrameToTime(int n)
    {
        if (n < 1) return 0f;
        return (n * Constants.FFT_HOP) / (float)Constants.AUDIO_SAMPLE_RATE;
    }

    public static (Tensor, Tensor) ConstrainFrequency(in Tensor onsets, in Tensor frames, float? maxFreq, float? minFreq)
    {
        if (maxFreq == null && minFreq == null) return (onsets, frames);

        var newOnsets = onsets.DeepClone();
        var newFrames = frames.DeepClone();

        if (maxFreq != null)
        {
            var pitch = HzToMidi(maxFreq.Value) - Constants.MIDI_OFFSET;
            ZeroPitch(ref newOnsets, Range.StartAt(pitch));
            ZeroPitch(ref newFrames, Range.StartAt(pitch));
        }
        if (minFreq != null)
        {
            var pitch = HzToMidi(minFreq.Value) - Constants.MIDI_OFFSET;
            ZeroPitch(ref newOnsets, Range.EndAt(pitch));
            ZeroPitch(ref newFrames, Range.EndAt(pitch));
        }
        return (newOnsets, newFrames);
    }

    public static Tensor GetInferedOnsets(in Tensor onsets, in Tensor frames, int nDiff = 2)
    {
        if (frames.Data == null) return new Tensor(null, null);

        var frameData      = frames.Data!;
        int frameSize      = (int)frames.Shape![frames.Shape.Length - 1];
        int totalFrameSize = frameData.Length;
        float[] diffs      = new float[nDiff * totalFrameSize];
        var diffsSpan      = diffs.AsSpan();

        for (int i = 0; i < nDiff; i++)
        {
            int start  = i * totalFrameSize;
            int offset = frameSize * (i + 1);
            int length = Math.Max(totalFrameSize - offset, 0);
            if (length > 0) Array.Copy(frameData, 0, diffs, start + offset, length);
            var dest = diffsSpan.Slice(start, totalFrameSize);
            Tp.Subtract(frameData, dest, dest);
        }

        var frameDiff = diffsSpan.Slice(0, totalFrameSize);
        for (int i = 1; i < nDiff; i++)
            Tp.Min(diffsSpan.Slice(i * totalFrameSize, totalFrameSize), frameDiff, frameDiff);

        Tp.Max(frameDiff, 0f, frameDiff);
        diffsSpan.Slice(0, nDiff * frameSize).Clear();

        var onsetData = onsets.Data!;
        float maxDiff = Tp.Max(frameDiff);
        float scale   = Tp.Max(onsetData);
        if (maxDiff != 0f) scale /= maxDiff;
        for (int j = 0; j < frameDiff.Length; j++) frameDiff[j] *= scale;

        float[] ret   = new float[onsetData.Length];
        Tp.Max(frameDiff, onsetData, ret);
        nint[] shape  = new nint[onsets.Shape!.Length];
        onsets.Shape!.CopyTo(shape, 0);
        return new Tensor(ret, shape);
    }

    public static IList<int> FindValidOnsetIndexs(in Tensor onsets, float threshold)
    {
        if (onsets.Shape![0] < 3) return [];

        var data = onsets.Data!;
        float[] mask = new float[data.Length];
        for (int i = 0; i < data.Length; i++) mask[i] = Math.Min(data[i], threshold);

        var step  = (int)onsets.Shape![onsets.Shape.Length - 1];
        var limit = mask.Length - step;
        var ret   = new List<int>();
        for (int i = step; i < limit; ++i)
        {
            if (mask[i] < threshold) continue;
            float v = data[i];
            if (v > data[i - step] && v > data[i + step])
                ret.Add(i);
        }
        return ret;
    }

    public static float[] MakeGaussianWindow(int count, int std)
    {
        if (count <= 0) return [];
        if (count == 1) return [1.0f];

        var n = MathTool.ARange(-0.5f * (count - 1), 1.0f, count);
        float sig2 = std * std * 2f;
        Tp.Multiply(n, n, n);
        Tp.Divide(n, -sig2, n);
        Tp.Exp(n, n);
        return n;
    }

    public static float MidiPitchToContourBin(int pitch)
    {
        float hz = MidiToHz(pitch);
        return 12f * Constants.CONTOURS_BINS_PER_SEMITONE * (float)Math.Log2(hz / Constants.ANNOTATIONS_BASE_FREQUENCY);
    }

    private static void ZeroPitch(ref Tensor t, Range pitchRange)
    {
        if (t.Data == null) return;
        var limit = t.Shape![1];
        var l = pitchRange.Start.Value;
        if (l < 0 || l > limit) return;
        var r = pitchRange.End.Equals(Index.End) ? limit : pitchRange.End.Value;
        if (r < 0 || r > limit || r < l) return;
        var step = (int)t.Shape![t.Shape.Length - 1];
        for (nint i = 0; i < t.Shape![0]; i++)
            for (int j = l; j < r; j++)
                t.Data![i * step + j] = 0;
    }
}

#endregion

#region Intermediate Note

record InterNote(int IStartTime, int IEndTime, int Pitch, float Amplitude, float[]? PitchBend = null)
{
    public float[]? PitchBend { get; set; } = PitchBend;
}

#endregion

#region Data Structures

public class Tensor
{
    public readonly float[]? Data;
    public readonly nint[]?  Shape;

    public Tensor(float[]? data, nint[]? shape)
    {
        Data  = data;
        Shape = shape;
    }

    public Tensor DeepClone()
    {
        float[]? data  = Data  != null ? (float[])Data.Clone()  : null;
        nint[]?  shape = Shape != null ? (nint[]) Shape.Clone() : null;
        return new Tensor(data, shape);
    }
}

public class ModelOutput
{
    public readonly Tensor Contours;
    public readonly Tensor Notes;
    public readonly Tensor Onsets;

    public ModelOutput(Tensor c, Tensor n, Tensor o)
    {
        Contours = c;
        Notes    = n;
        Onsets   = o;
    }
}

public class WaveBuffer
{
    public int    FloatBufferCount { get; set; }
    public float[]? FloatBuffer   { get; set; }

    public WaveBuffer() { }

    public WaveBuffer(float[] buffer)
    {
        FloatBuffer      = buffer;
        FloatBufferCount = buffer.Length;
    }

    public WaveBuffer(Span<float> buffer)
    {
        FloatBufferCount = buffer.Length;
        FloatBuffer      = buffer.ToArray();
    }
}

#endregion
