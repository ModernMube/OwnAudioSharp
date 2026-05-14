using System.Runtime.CompilerServices;
using Microsoft.ML.OnnxRuntime;

namespace OwnaudioNET.Features.Vocalremover;

/// <summary>
/// AOT-compatible float tensor replacing DenseTensor&lt;float&gt; / Tensor&lt;float&gt;
/// from Microsoft.ML.OnnxRuntime.Tensors.
/// Stores data in row-major flat float[] with explicit shape.
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

    // Dimensions property - compatibility with DenseTensor.Dimensions
    internal int[] Dimensions => Shape;

    internal float GetValue(int i) => Data[i];
    internal void SetValue(int i, float v) => Data[i] = v;

    // 3D indexer [d0, d1, d2]
    internal float this[int d0, int d1, int d2]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Data[d0 * Shape[1] * Shape[2] + d1 * Shape[2] + d2];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Data[d0 * Shape[1] * Shape[2] + d1 * Shape[2] + d2] = value;
    }

    // 4D indexer [d0, d1, d2, d3]
    internal float this[int d0, int d1, int d2, int d3]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Data[((d0 * Shape[1] + d1) * Shape[2] + d2) * Shape[3] + d3];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Data[((d0 * Shape[1] + d1) * Shape[2] + d2) * Shape[3] + d3] = value;
    }

    // 5D indexer [d0, d1, d2, d3, d4]
    internal float this[int d0, int d1, int d2, int d3, int d4]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Data[(((d0 * Shape[1] + d1) * Shape[2] + d2) * Shape[3] + d3) * Shape[4] + d4];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Data[(((d0 * Shape[1] + d1) * Shape[2] + d2) * Shape[3] + d3) * Shape[4] + d4] = value;
    }
}

/// <summary>
/// AOT-compatible ONNX inference helpers.
/// Uses OrtValue API (OnnxRuntime 1.16+) instead of NamedOnnxValue / DenseTensor,
/// which rely on reflection and are not compatible with Native AOT compilation.
/// </summary>
internal static class OrtRunner
{
    /// <summary>
    /// Run single-input, single-output model inference.
    /// </summary>
    internal static OrtTensor Run(
        InferenceSession session,
        OrtTensor input,
        string inputName = "input",
        string outputName = "output")
    {
        var longShape = Array.ConvertAll(input.Shape, x => (long)x);
        using var inputValue = OrtValue.CreateTensorValueFromMemory<float>(
            input.Data, longShape);

        using var runOptions = new RunOptions();
        using var results = session.Run(
            runOptions,
            new[] { inputName },
            new[] { inputValue },
            new[] { outputName });

        return ToOrtTensor(results[0]);
    }

    /// <summary>
    /// Run model with arbitrary inputs and outputs.
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
                inputValues[i] = OrtValue.CreateTensorValueFromMemory<float>(
                    inputs[i].tensor.Data, longShape);
            }

            using var runOptions = new RunOptions();
            using var results = session.Run(runOptions, inputNames, inputValues, outputNames);

            var output = new OrtTensor[results.Count];
            for (int i = 0; i < results.Count; i++)
                output[i] = ToOrtTensor(results[i]);
            return output;
        }
        finally
        {
            foreach (var v in inputValues)
                v?.Dispose();
        }
    }

    private static OrtTensor ToOrtTensor(OrtValue value)
    {
        var data = value.GetTensorDataAsSpan<float>().ToArray();
        var typeInfo = value.GetTensorTypeAndShape();
        var shape = Array.ConvertAll(typeInfo.Shape, x => (int)x);
        return new OrtTensor(data, shape);
    }
}
