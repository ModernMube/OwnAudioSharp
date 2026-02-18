using MathNet.Numerics.IntegralTransforms;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Logger;
using System.Numerics;
using System.Reflection;

namespace OwnaudioNET.Features.Vocalremover
{
    /// <summary>
    /// Main audio separation service with parallel processing support (legacy)
    /// </summary>
    public class AudioSeparationService : IDisposable
    {
        #region Events

        /// <summary>Progress update event</summary>
        public event EventHandler<SimpleSeparationProgress>? ProgressChanged;

        /// <summary>Processing started event</summary>
#pragma warning disable CS0067 // Event is never used
        public event EventHandler<string>? ProcessingStarted;

        /// <summary>Processing completed event</summary>
        public event EventHandler<SimpleSeparationResult>? ProcessingCompleted;

        /// <summary>Error occurred event</summary>
        public event EventHandler<Exception>? ErrorOccurred;
#pragma warning restore CS0067

        #endregion

        #region Private Fields

        private readonly SimpleSeparationOptions _options;
        private ModelParameters _modelParams;

#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value
        private InferenceSession? _onnxSession;
        private SemaphoreSlim? _sessionSemaphore;
        private Timer? _memoryMonitorTimer;
#pragma warning restore CS0649

#pragma warning disable CS0414 // Field is assigned but its value is never used
        private volatile bool _isMemoryPressureHigh = false;
#pragma warning restore CS0414

        private bool _disposed = false;
        private const int TargetSampleRate = 44100;

        #endregion

        #region Constructor

        /// <summary>
        /// Initialize audio separation service
        /// </summary>
        /// <param name="options">Separation configuration options</param>
        public AudioSeparationService(SimpleSeparationOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _modelParams = new ModelParameters(
                dimF: options.DimF,
                dimT: options.DimT,
                nFft: options.NFft
            );
        }

        #endregion

        #region Public Methods

        /// <summary>Dispose resources</summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion

        #region Private Methods - Initialization

        private void AutoDetectModelDimensions()
        {
            if (_onnxSession == null) return;

            try
            {
                var inputMetadata = _onnxSession.InputMetadata;
                if (inputMetadata.ContainsKey("input"))
                {
                    var inputShape = inputMetadata["input"].Dimensions;

                    if (inputShape.Length >= 4)
                    {
                        int expectedFreq = (int)inputShape[2];
                        int expectedTime = (int)inputShape[3];

                        Log.Info($"Model expects: Frequency={expectedFreq}, Time={expectedTime}");
                        Log.Info($"Current config: Frequency={_modelParams.DimF}, Time={_modelParams.DimT}");

                        if (expectedFreq != _modelParams.DimF || expectedTime != _modelParams.DimT)
                        {
                            Log.Info("Auto-adjusting model parameters to match ONNX model...");
                            int newDimT = (int)Math.Log2(expectedTime);

                            _modelParams = new ModelParameters(
                                dimF: expectedFreq,
                                dimT: newDimT,
                                nFft: _options.NFft
                            );

                            Log.Info($"Updated to: DimF={_modelParams.DimF}, DimT={_modelParams.DimT}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not auto-detect model dimensions: {ex.Message}");
                Log.Info("Using provided configuration parameters...");
            }
        }

        #endregion

        #region Private Methods - Audio Processing

        private void ReportProgress(SimpleSeparationProgress progress)
        {
            ProgressChanged?.Invoke(this, progress);
        }

        #endregion

        #region Private Methods - Single Chunk Processing

        private float[,] ProcessSingleChunk(float[,] mixChunk, long chunkKey, List<long> allKeys, int margin)
        {
            int nSample = mixChunk.GetLength(1);
            int trim = _modelParams.NFft / 2;
            int genSize = _modelParams.ChunkSize - 2 * trim;

            if (genSize <= 0)
                throw new ArgumentException($"Invalid genSize: {genSize}. Check FFT parameters.");

            int pad = genSize - (nSample % genSize);
            if (nSample % genSize == 0) pad = 0;

            var mixPadded = new float[2, trim + nSample + pad + trim];
            for (int ch = 0; ch < 2; ch++)
            {
                for (int i = 0; i < nSample; i++)
                {
                    mixPadded[ch, trim + i] = mixChunk[ch, i];
                }
            }

            int frameCount = (nSample + pad) / genSize;
            var mixWaves = new float[frameCount, 2, _modelParams.ChunkSize];

            for (int i = 0; i < frameCount; i++)
            {
                int offset = i * genSize;
                for (int ch = 0; ch < 2; ch++)
                {
                    for (int j = 0; j < _modelParams.ChunkSize; j++)
                    {
                        mixWaves[i, ch, j] = mixPadded[ch, offset + j];
                    }
                }
            }

            var stftTensor = ComputeStft(mixWaves);
            var outputTensor = RunModelInference(stftTensor);
            var resultWaves = ComputeIstft(outputTensor);

            var result = ExtractSignal(resultWaves, nSample, trim, genSize);
            return ApplyMargin(result, chunkKey, allKeys, margin);
        }

        #endregion

        #region Private Methods - STFT/ISTFT Processing

        private DenseTensor<float> ComputeStft(float[,,] mixWaves)
        {
            int batchSize = mixWaves.GetLength(0);
            var tensor = new DenseTensor<float>(new[] { batchSize, 4, _modelParams.DimF, _modelParams.DimT });

            for (int b = 0; b < batchSize; b++)
            {
                for (int ch = 0; ch < 2; ch++)
                {
                    int padSize = _modelParams.NFft / 2;
                    var paddedSignal = new float[_modelParams.ChunkSize + 2 * padSize];

                    for (int i = 0; i < padSize; i++)
                    {
                        int srcIdx = Math.Min(padSize - 1 - i, _modelParams.ChunkSize - 1);
                        paddedSignal[i] = mixWaves[b, ch, srcIdx];
                    }

                    for (int i = 0; i < _modelParams.ChunkSize; i++)
                    {
                        paddedSignal[padSize + i] = mixWaves[b, ch, i];
                    }

                    for (int i = 0; i < padSize; i++)
                    {
                        int srcIdx = Math.Max(0, _modelParams.ChunkSize - 1 - i);
                        paddedSignal[padSize + _modelParams.ChunkSize + i] = mixWaves[b, ch, srcIdx];
                    }

                    for (int t = 0; t < _modelParams.DimT; t++)
                    {
                        int frameStart = t * _modelParams.Hop;
                        var frame = new Complex[_modelParams.NFft];

                        for (int i = 0; i < _modelParams.NFft; i++)
                        {
                            if (frameStart + i < paddedSignal.Length)
                            {
                                double windowValue = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / _modelParams.NFft));
                                frame[i] = new Complex(paddedSignal[frameStart + i] * windowValue, 0);
                            }
                        }

                        Fourier.Forward(frame, FourierOptions.NoScaling);

                        for (int f = 0; f < Math.Min(_modelParams.DimF, _modelParams.NBins); f++)
                        {
                            tensor[b, ch * 2, f, t] = (float)frame[f].Real;
                            tensor[b, ch * 2 + 1, f, t] = (float)frame[f].Imaginary;
                        }
                    }
                }
            }
            return tensor;
        }

        private float[,,] ComputeIstft(Tensor<float> spectrum)
        {
            int batchSize = spectrum.Dimensions[0];
            var result = new float[batchSize, 2, _modelParams.ChunkSize];

            for (int b = 0; b < batchSize; b++)
            {
                for (int ch = 0; ch < 2; ch++)
                {
                    int padSize = _modelParams.NFft / 2;
                    var reconstructed = new double[_modelParams.ChunkSize + 2 * padSize];
                    var windowSum = new double[_modelParams.ChunkSize + 2 * padSize];

                    int realIdx = ch * 2;
                    int imagIdx = ch * 2 + 1;

                    for (int t = 0; t < _modelParams.DimT; t++)
                    {
                        var frame = new Complex[_modelParams.NFft];

                        for (int f = 0; f < _modelParams.NBins && f < _modelParams.NFft; f++)
                        {
                            if (f < _modelParams.DimF && f < spectrum.Dimensions[2])
                            {
                                frame[f] = new Complex(spectrum[b, realIdx, f, t], spectrum[b, imagIdx, f, t]);
                            }
                            else
                            {
                                frame[f] = Complex.Zero;
                            }
                        }

                        for (int f = 1; f < _modelParams.NFft / 2; f++)
                        {
                            if (_modelParams.NFft - f < frame.Length)
                            {
                                frame[_modelParams.NFft - f] = Complex.Conjugate(frame[f]);
                            }
                        }

                        Fourier.Inverse(frame, FourierOptions.NoScaling);

                        for (int i = 0; i < _modelParams.NFft; i++)
                        {
                            frame[i] /= _modelParams.NFft;
                        }

                        int frameStart = t * _modelParams.Hop;
                        for (int i = 0; i < _modelParams.NFft; i++)
                        {
                            int targetIdx = frameStart + i;
                            if (targetIdx >= 0 && targetIdx < reconstructed.Length)
                            {
                                double windowValue = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / _modelParams.NFft));
                                reconstructed[targetIdx] += frame[i].Real * windowValue;
                                windowSum[targetIdx] += windowValue * windowValue;
                            }
                        }
                    }

                    for (int i = 0; i < _modelParams.ChunkSize; i++)
                    {
                        int srcIdx = i + padSize;
                        if (srcIdx >= 0 && srcIdx < reconstructed.Length)
                        {
                            if (windowSum[srcIdx] > 1e-10)
                            {
                                result[b, ch, i] = (float)(reconstructed[srcIdx] / windowSum[srcIdx]);
                            }
                            else
                            {
                                result[b, ch, i] = (float)reconstructed[srcIdx];
                            }
                        }
                    }
                }
            }
            return result;
        }

        #endregion

        #region Private Methods - Model Inference

        private Tensor<float> RunModelInference(DenseTensor<float> stftTensor)
        {
            if (_onnxSession == null)
                throw new InvalidOperationException("ONNX session not initialized");

            return RunModelInferenceWithSession(stftTensor, _onnxSession);
        }

        private Tensor<float> RunModelInferenceWithSession(DenseTensor<float> stftTensor, InferenceSession session)
        {
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input", stftTensor) };

            if (!_options.DisableNoiseReduction)
            {
                var stftTensorNeg = new DenseTensor<float>(stftTensor.Dimensions);
                for (int idx = 0; idx < stftTensor.Length; idx++)
                {
                    stftTensorNeg.SetValue(idx, -stftTensor.GetValue(idx));
                }
                var inputsNeg = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input", stftTensorNeg) };

                using var outputs = session.Run(inputs);
                using var outputsNeg = session.Run(inputsNeg);

                var specPred = outputs.First().AsTensor<float>();
                var specPredNeg = outputsNeg.First().AsTensor<float>();

                var result = new DenseTensor<float>(specPred.Dimensions);

                for (int b = 0; b < specPred.Dimensions[0]; b++)
                    for (int c = 0; c < specPred.Dimensions[1]; c++)
                        for (int f = 0; f < specPred.Dimensions[2]; f++)
                            for (int t = 0; t < specPred.Dimensions[3]; t++)
                            {
                                float val = -specPredNeg[b, c, f, t] * 0.5f + specPred[b, c, f, t] * 0.5f;
                                ((DenseTensor<float>)result)[b, c, f, t] = val;
                            }

                return result;
            }
            else
            {
                using var outputs = session.Run(inputs);
                var result = outputs.First().AsTensor<float>();

                var resultCopy = new DenseTensor<float>(result.Dimensions);
                for (int i = 0; i < result.Length; i++)
                {
                    resultCopy.SetValue(i, result.GetValue(i));
                }
                return resultCopy;
            }
        }

        #endregion

        #region Private Methods - Signal Processing

        private float[,] ExtractSignal(float[,,] waves, int nSample, int trim, int genSize)
        {
            int frameCount = waves.GetLength(0);
            var signal = new float[2, nSample];

            for (int i = 0; i < frameCount; i++)
            {
                int destOffset = i * genSize;
                for (int ch = 0; ch < 2; ch++)
                {
                    for (int j = 0; j < genSize && destOffset + j < nSample; j++)
                    {
                        int sourceIndex = trim + j;
                        if (sourceIndex < _modelParams.ChunkSize - trim)
                        {
                            signal[ch, destOffset + j] = waves[i, ch, sourceIndex];
                        }
                    }
                }
            }

            return signal;
        }

        private float[,] ApplyMargin(float[,] signal, long chunkKey, List<long> allKeys, int margin)
        {
            int nSample = signal.GetLength(1);
            int start = chunkKey == 0 ? 0 : margin;
            int end = chunkKey == allKeys.Last() ? nSample : nSample - margin;
            if (margin == 0) end = nSample;

            var result = new float[2, end - start];
            for (int ch = 0; ch < 2; ch++)
            {
                for (int i = 0; i < end - start; i++)
                {
                    result[ch, i] = signal[ch, start + i];
                }
            }

            return result;
        }

        private float[,] ConcatenateChunks(List<float[,]> chunks)
        {
            int totalLength = chunks.Sum(c => c.GetLength(1));
            var result = new float[2, totalLength];
            int currentPos = 0;

            foreach (var chunk in chunks)
            {
                for (int ch = 0; ch < 2; ch++)
                {
                    for (int i = 0; i < chunk.GetLength(1); i++)
                    {
                        result[ch, currentPos + i] = chunk[ch, i];
                    }
                }
                currentPos += chunk.GetLength(1);
            }

            return result;
        }

        #endregion

        #region Private Methods - File I/O

        private void SaveAudio(string filePath, float[,] audio, int sampleRate)
        {
            int channels = audio.GetLength(0);
            int samples = audio.GetLength(1);

            float maxVal = 0f;
            for (int ch = 0; ch < audio.GetLength(0); ch++)
            {
                for (int i = 0; i < audio.GetLength(1); i++)
                {
                    maxVal = Math.Max(maxVal, Math.Abs(audio[ch, i]));
                }
            }

            float scale = maxVal > 0.95f ? 0.95f / maxVal : 1.0f;

            var interleaved = new float[samples * channels];
            for (int i = 0; i < samples; i++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    interleaved[i * channels + ch] = audio[ch, i] * scale;
                }
            }

            OwnaudioNET.Recording.WaveFile.Create(filePath, interleaved, sampleRate, channels, 16);
        }

        #endregion

        #region Dispose Pattern

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _memoryMonitorTimer?.Dispose();
                _sessionSemaphore?.Dispose();
                _onnxSession?.Dispose();
                _disposed = true;
            }
        }

        #endregion
    }

    /// <summary>
    /// Helper extension methods for easier usage
    /// </summary>
    public static class AudioSeparationExtensions
    {
        /// <summary>
        /// Creates a default instance of the <see cref="AudioSeparationService"/> using the specified model path.
        /// </summary>
        public static AudioSeparationService CreateDefaultService(string modelPath)
        {
            return createDefaultService(modelPath, InternalModel.None);
        }

        /// <summary>
        /// Creates a default instance of the <see cref="AudioSeparationService"/> using the specified model.
        /// </summary>
        public static AudioSeparationService CreateDefaultService(InternalModel _model)
        {
            return createDefaultService("", _model);
        }

        /// <summary>
        /// Creates an instance of the <see cref="AudioSeparationService"/> using the specified model path and output directory.
        /// </summary>
        public static AudioSeparationService CreatetService(string _modelPath, string _output)
        {
            return createService(_modelPath, InternalModel.None, _output);
        }

        /// <summary>
        /// Creates an instance of the <see cref="AudioSeparationService"/> using the specified model and output path.
        /// </summary>
        public static AudioSeparationService CreatetService(InternalModel _model, string _output)
        {
            return createService("", _model, _output);
        }

        private static AudioSeparationService createDefaultService(string? modelPath, InternalModel inmodel)
        {
            var options = new SimpleSeparationOptions
            {
                ModelPath = modelPath,
                Model = inmodel,
            };
            return new AudioSeparationService(options);
        }

        private static AudioSeparationService createService(string modelPath, InternalModel inmodel, string outputDirectory)
        {
            var options = new SimpleSeparationOptions
            {
                ModelPath = modelPath,
                Model = inmodel,
                OutputDirectory = outputDirectory
            };
            return new AudioSeparationService(options);
        }

        /// <summary>Validate audio file format</summary>
        public static bool IsValidAudioFile(string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var supportedFormats = new[] { ".wav", ".mp3", ".flac" };

            return supportedFormats.Contains(extension);
        }

        /// <summary>Get estimated processing time based on file size</summary>
        public static TimeSpan EstimateProcessingTime(string filePath)
        {
            if (!File.Exists(filePath))
                return TimeSpan.Zero;

            var fileInfo = new FileInfo(filePath);
            var fileSizeMB = fileInfo.Length / (1024.0 * 1024.0);

            var estimatedMinutes = fileSizeMB * 1.5;
            return TimeSpan.FromMinutes(Math.Max(0.5, estimatedMinutes));
        }

        /// <summary>
        /// Loads the model data as a byte array from the embedded resource.
        /// </summary>
        public static byte[] LoadModelBytes(InternalModel _model)
        {
            var assembly = Assembly.GetExecutingAssembly();
            string? resourceName = "";

            switch (_model)
            {
                case InternalModel.None:
                    throw new InvalidOperationException("Model is not set. Please initialize the model first.");
                case InternalModel.Default:
                    resourceName = "default.onnx";
                    break;
                case InternalModel.Best:
                    resourceName = "best.onnx";
                    break;
                case InternalModel.Karaoke:
                    resourceName = "karaoke.onnx";
                    break;
                case InternalModel.HTDemucs:
                    resourceName = "htdemucs.onnx";
                    break;
            }

            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith(resourceName))
                {
                    resourceName = name;
                    break;
                }
            }

            using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            return memoryStream.ToArray();
        }
    }

    /// <summary>
    /// Simplified factory with Separator method
    /// </summary>
    public static class SimpleSeparator
    {
        /// <summary>
        /// Creates and initializes a simple audio separator service
        /// </summary>
        public static (SimpleAudioSeparationService? service, string vocalPath, string instrumentPath) Separator(
            InternalModel model, string outputDirectory)
        {
            var options = new SimpleSeparationOptions
            {
                Model = model,
                OutputDirectory = outputDirectory,
                DisableNoiseReduction = false
            };

            var service = new SimpleAudioSeparationService(options);
            service.Initialize();

            return (service, string.Empty, string.Empty);
        }
    }
}
