using OwnAudio.Midi.File;
using Logger;
using Microsoft.ML.OnnxRuntime;
using System.Numerics.Tensors;

namespace OwnaudioNET.Features.Extensions;

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
