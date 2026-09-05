using OwnAudio.Midi.File;
using Logger;
using Microsoft.ML.OnnxRuntime;
using System.Numerics.Tensors;

namespace OwnaudioNET.Features.Extensions;

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
