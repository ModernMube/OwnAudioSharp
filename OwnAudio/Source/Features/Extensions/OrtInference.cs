using System.Runtime.CompilerServices;
using Microsoft.ML.OnnxRuntime;

namespace OwnaudioNET.Features.Extensions;

/// <summary>
/// AOT friendly stand-in for DenseTensor&lt;float&gt;: flat row-major array plus an explicit shape.
/// </summary>
internal sealed class OrtTensor
{
    internal readonly float[] Data;
    internal readonly int[] Shape;

    internal OrtTensor(int[] shape)
    {
        Shape = shape;
        int total = 1;
        foreach (int d in shape) total *= d;
        Data = new float[total];
    }

    internal OrtTensor(float[] data, int[] shape)
    {
        Data = data;
        Shape = shape;
    }

    internal int Length => Data.Length;

    internal int[] Dimensions => Shape;

    internal float GetValue(int i) => Data[i];
    internal void SetValue(int i, float v) => Data[i] = v;

    internal float this[int d0, int d1, int d2]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Data[d0 * Shape[1] * Shape[2] + d1 * Shape[2] + d2];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Data[d0 * Shape[1] * Shape[2] + d1 * Shape[2] + d2] = value;
    }

    internal float this[int d0, int d1, int d2, int d3]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Data[((d0 * Shape[1] + d1) * Shape[2] + d2) * Shape[3] + d3];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Data[((d0 * Shape[1] + d1) * Shape[2] + d2) * Shape[3] + d3] = value;
    }

    internal float this[int d0, int d1, int d2, int d3, int d4]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Data[(((d0 * Shape[1] + d1) * Shape[2] + d2) * Shape[3] + d3) * Shape[4] + d4];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Data[(((d0 * Shape[1] + d1) * Shape[2] + d2) * Shape[3] + d3) * Shape[4] + d4] = value;
    }
}

/// <summary>
/// Inference through the OrtValue API (ORT 1.16+). NamedOnnxValue/DenseTensor need reflection,
/// so they're out for Native AOT.
/// </summary>
internal static class OrtRunner
{
    /// <summary>
    /// One tensor in, one out.
    /// </summary>
    internal static OrtTensor Run(
        InferenceSession session,
        OrtTensor input,
        string inputName = "input",
        string outputName = "output")
    {
        var longShape = Array.ConvertAll(input.Shape, x => (long)x);
        using (var inputValue = OrtValue.CreateTensorValueFromMemory<float>(input.Data, longShape))
        using (var runOptions = new RunOptions())
        using (var results = session.Run(runOptions, new[] { inputName }, new[] { inputValue }, new[] { outputName }))
        {
            return _toOrtTensor(results[0]);
        }
    }

    /// <summary>
    /// Same thing with as many named inputs/outputs as the model wants.
    /// </summary>
    internal static OrtTensor[] Run(
        InferenceSession session,
        (string name, OrtTensor tensor)[] inputs,
        string[] outputNames)
    {
        var inputNames  = new string[inputs.Length];
        var inputValues = new OrtValue[inputs.Length];

        try
        {
            for (int i = 0; i < inputs.Length; i++)
            {
                inputNames[i] = inputs[i].name;
                var longShape = Array.ConvertAll(inputs[i].tensor.Shape, x => (long)x);
                inputValues[i] = OrtValue.CreateTensorValueFromMemory<float>(inputs[i].tensor.Data, longShape);
            }

            using (var runOptions = new RunOptions())
            using (var results = session.Run(runOptions, inputNames, inputValues, outputNames))
            {
                var output = new OrtTensor[results.Count];
                for (int i = 0; i < results.Count; i++)
                    output[i] = _toOrtTensor(results[i]);
                return output;
            }
        }
        finally
        {
            foreach (var v in inputValues)
                v?.Dispose();
        }
    }

    private static OrtTensor _toOrtTensor(OrtValue value)
    {
        var data = value.GetTensorDataAsSpan<float>().ToArray();
        var shape = Array.ConvertAll(value.GetTensorTypeAndShape().Shape, x => (int)x);
        return new OrtTensor(data, shape);
    }
}
