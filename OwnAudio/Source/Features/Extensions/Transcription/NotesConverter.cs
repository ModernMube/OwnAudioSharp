using OwnAudio.Midi.File;
using Logger;
using Microsoft.ML.OnnxRuntime;
using System.Numerics.Tensors;

namespace OwnaudioNET.Features.Extensions;

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
