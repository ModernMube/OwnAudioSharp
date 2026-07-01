using Microsoft.ML.OnnxRuntime;
using OwnaudioNET.RustNext.Dsp;
using Logger;
using System.Numerics;

namespace OwnaudioNET.RustNext.Features.Vocalremover
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
#pragma warning disable CS0067
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

#pragma warning disable CS0649
        private InferenceSession? _onnxSession;
        private SemaphoreSlim? _sessionSemaphore;
        private Timer? _memoryMonitorTimer;
#pragma warning restore CS0649

#pragma warning disable CS0414
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

            var stftTensor    = ComputeStft(mixWaves);
            var outputTensor  = RunModelInference(stftTensor);
            var resultWaves   = ComputeIstft(outputTensor);

            var result = ExtractSignal(resultWaves, nSample, trim, genSize);
            return ApplyMargin(result, chunkKey, allKeys, margin);
        }

        #endregion

        #region Private Methods - STFT/ISTFT Processing

        private OrtTensor ComputeStft(float[,,] mixWaves)
        {
            int batchSize = mixWaves.GetLength(0);
            var tensor = new OrtTensor(new[] { batchSize, 4, _modelParams.DimF, _modelParams.DimT });

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

                        OwnAudioFft.Forward(frame);

                        for (int f = 0; f < Math.Min(_modelParams.DimF, _modelParams.NBins); f++)
                        {
                            tensor[b, ch * 2, f, t]     = (float)frame[f].Real;
                            tensor[b, ch * 2 + 1, f, t] = (float)frame[f].Imaginary;
                        }
                    }
                }
            }
            return tensor;
        }

        private float[,,] ComputeIstft(OrtTensor spectrum)
        {
            int batchSize = spectrum.Shape[0];
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
                            if (f < _modelParams.DimF && f < spectrum.Shape[2])
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

                        OwnAudioFft.Inverse(frame);

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
                                windowSum[targetIdx]     += windowValue * windowValue;
                            }
                        }
                    }

                    for (int i = 0; i < _modelParams.ChunkSize; i++)
                    {
                        int srcIdx = i + padSize;
                        if (srcIdx >= 0 && srcIdx < reconstructed.Length)
                        {
                            result[b, ch, i] = windowSum[srcIdx] > 1e-10
                                ? (float)(reconstructed[srcIdx] / windowSum[srcIdx])
                                : (float)reconstructed[srcIdx];
                        }
                    }
                }
            }
            return result;
        }

        #endregion

        #region Private Methods - Model Inference

        private OrtTensor RunModelInference(OrtTensor stftTensor)
        {
            if (_onnxSession == null)
                throw new InvalidOperationException("ONNX session not initialized");

            return RunModelInferenceWithSession(stftTensor, _onnxSession);
        }

        private OrtTensor RunModelInferenceWithSession(OrtTensor stftTensor, InferenceSession session)
        {
            if (!_options.DisableNoiseReduction)
            {
                var stftTensorNeg = new OrtTensor(stftTensor.Shape);
                for (int idx = 0; idx < stftTensor.Length; idx++)
                {
                    stftTensorNeg.SetValue(idx, -stftTensor.GetValue(idx));
                }

                var specPred    = OrtRunner.Run(session, stftTensor);
                var specPredNeg = OrtRunner.Run(session, stftTensorNeg);

                var result = new OrtTensor(specPred.Shape);

                for (int b = 0; b < specPred.Shape[0]; b++)
                    for (int c = 0; c < specPred.Shape[1]; c++)
                        for (int f = 0; f < specPred.Shape[2]; f++)
                            for (int t = 0; t < specPred.Shape[3]; t++)
                            {
                                result[b, c, f, t] =
                                    -specPredNeg[b, c, f, t] * 0.5f + specPred[b, c, f, t] * 0.5f;
                            }

                return result;
            }
            else
            {
                return OrtRunner.Run(session, stftTensor);
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
            int samples  = audio.GetLength(1);

            float maxVal = 0f;
            for (int ch = 0; ch < audio.GetLength(0); ch++)
                for (int i = 0; i < audio.GetLength(1); i++)
                    maxVal = Math.Max(maxVal, Math.Abs(audio[ch, i]));

            float scale = maxVal > 0.95f ? 0.95f / maxVal : 1.0f;

            var interleaved = new float[samples * channels];
            for (int i = 0; i < samples; i++)
                for (int ch = 0; ch < channels; ch++)
                    interleaved[i * channels + ch] = audio[ch, i] * scale;

            OwnaudioNET.RustNext.Recording.WaveFile.Create(filePath, interleaved, sampleRate, channels, 16);
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
        public static AudioSeparationService CreateDefaultService(string modelPath)
            => createDefaultService(modelPath, InternalModel.None);

        public static AudioSeparationService CreateDefaultService(InternalModel _model)
            => createDefaultService("", _model);

        public static AudioSeparationService CreatetService(string _modelPath, string _output)
            => createService(_modelPath, InternalModel.None, _output);

        public static AudioSeparationService CreatetService(InternalModel _model, string _output)
            => createService("", _model, _output);

        private static AudioSeparationService createDefaultService(string? modelPath, InternalModel inmodel)
        {
            var options = new SimpleSeparationOptions { ModelPath = modelPath, Model = inmodel };
            return new AudioSeparationService(options);
        }

        private static AudioSeparationService createService(string modelPath, InternalModel inmodel, string outputDirectory)
        {
            var options = new SimpleSeparationOptions { ModelPath = modelPath, Model = inmodel, OutputDirectory = outputDirectory };
            return new AudioSeparationService(options);
        }

        public static bool IsValidAudioFile(string filePath)
        {
            if (!File.Exists(filePath)) return false;
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return new[] { ".wav", ".mp3", ".flac" }.Contains(ext);
        }

        public static TimeSpan EstimateProcessingTime(string filePath)
        {
            if (!File.Exists(filePath)) return TimeSpan.Zero;
            var sizeMB = new FileInfo(filePath).Length / (1024.0 * 1024.0);
            return TimeSpan.FromMinutes(Math.Max(0.5, sizeMB * 1.5));
        }

        /// <summary>
        /// Loads the model bytes from the local models directory managed by
        /// <see cref="VocalRemoverModelManager"/>. Call
        /// <see cref="VocalRemoverModelManager.DownloadModelAsync"/> first if the file is missing.
        /// </summary>
        /// <param name="_model">The model whose bytes are to be loaded.</param>
        /// <returns>Raw ONNX model bytes.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <paramref name="_model"/> is <see cref="InternalModel.None"/>.
        /// </exception>
        public static byte[] LoadModelBytes(InternalModel _model)
        {
            if (_model == InternalModel.None)
                throw new InvalidOperationException("Model is not set.");

            var path = VocalRemoverModelManager.GetModelPath(_model);
            return File.ReadAllBytes(path);
        }
    }

    /// <summary>
    /// Simplified factory with Separator method
    /// </summary>
    public static class SimpleSeparator
    {
        public static (SimpleAudioSeparationService? service, string vocalPath, string instrumentPath) Separator(
            InternalModel model, string outputDirectory = "separated")
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
