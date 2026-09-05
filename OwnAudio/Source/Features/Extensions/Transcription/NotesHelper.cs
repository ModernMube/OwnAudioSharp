using OwnAudio.Midi.File;
using Logger;
using Microsoft.ML.OnnxRuntime;
using System.Numerics.Tensors;

namespace OwnaudioNET.Features.Extensions;

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
