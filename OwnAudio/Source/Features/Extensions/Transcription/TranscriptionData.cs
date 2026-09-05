using OwnAudio.Midi.File;
using Logger;
using Microsoft.ML.OnnxRuntime;
using System.Numerics.Tensors;

namespace OwnaudioNET.Features.Extensions;

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
