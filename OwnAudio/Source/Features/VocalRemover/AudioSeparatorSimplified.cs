using MathNet.Numerics.IntegralTransforms;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Ownaudio;
using Ownaudio.Decoders;
using Ownaudio.Core;
using Logger;
using System.Numerics;
using System.Reflection;

namespace OwnaudioNET.Features.Vocalremover
{
    /// <summary>
    /// Configuration parameters for audio separation process
    /// </summary>
    public class SimpleSeparationOptions
    {
        /// <summary>
        /// ONNX model file path
        /// </summary>
        public string? ModelPath { get; set; }

        /// <summary>
        /// Gets or sets the type of separation model used in the operation.
        /// </summary>
        public InternalModel Model { get; set; } = InternalModel.Best;

        /// <summary>
        /// Output directory path
        /// </summary>
        public string OutputDirectory { get; set; } = "separated";

        /// <summary>
        /// Disable noise reduction (enabled by default)
        /// </summary>
        public bool DisableNoiseReduction { get; set; } = false;

        /// <summary>
        /// Margin size for overlapping chunks (in samples)
        /// </summary>
        public int Margin { get; set; } = 44100;

        /// <summary>
        /// Chunk size in seconds (0 = process entire file at once)
        /// </summary>
        public int ChunkSizeSeconds { get; set; } = 15;

        /// <summary>
        /// FFT size
        /// </summary>
        public int NFft { get; set; } = 6144;

        /// <summary>
        /// Temporal dimension parameter (as power of 2)
        /// </summary>
        public int DimT { get; set; } = 8;

        /// <summary>
        /// Frequency dimension parameter
        /// </summary>
        public int DimF { get; set; } = 2048;
    }

    /// <summary>
    /// Progress information for separation process
    /// </summary>
    public class SimpleSeparationProgress
    {
        /// <summary>
        /// Current file being processed
        /// </summary>
        public string CurrentFile { get; set; } = string.Empty;

        /// <summary>
        /// Overall progress percentage (0-100)
        /// </summary>
        public double OverallProgress { get; set; }

        /// <summary>
        /// Current processing step description
        /// </summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// Number of chunks processed
        /// </summary>
        public int ProcessedChunks { get; set; }

        /// <summary>
        /// Total number of chunks
        /// </summary>
        public int TotalChunks { get; set; }
    }

    /// <summary>
    /// Result of audio separation
    /// </summary>
    public class SimpleSeparationResult
    {
        /// <summary>
        /// Path to the vocals output file
        /// </summary>
        public string VocalsPath { get; set; } = string.Empty;

        /// <summary>
        /// Path to the instrumental output file
        /// </summary>
        public string InstrumentalPath { get; set; } = string.Empty;

        /// <summary>
        /// Processing duration
        /// </summary>
        public TimeSpan ProcessingTime { get; set; }
    }

    /// <summary>
    /// Simplified audio separation service without async operations
    /// Optimized for streaming processing with minimal memory footprint
    /// </summary>
    public class SimpleAudioSeparationService : IDisposable
    {
        #region Events

        /// <summary>
        /// Progress update event
        /// </summary>
        public event EventHandler<SimpleSeparationProgress>? ProgressChanged;

        /// <summary>
        /// Processing completed event
        /// </summary>
        public event EventHandler<SimpleSeparationResult>? ProcessingCompleted;

        #endregion

        #region Private Fields

        private readonly SimpleSeparationOptions _options;
        private ModelParameters _modelParams;
        private InferenceSession? _onnxSession;
        private bool _disposed = false;
        private const int TargetSampleRate = 44100;

        // Pre-calculated Hanning window for STFT/ISTFT optimization
        private float[]? _hanningWindow;

        #endregion

        #region Constructor

        /// <summary>
        /// Initialize audio separation service
        /// </summary>
        /// <param name="options">Separation configuration options</param>
        public SimpleAudioSeparationService(SimpleSeparationOptions options)
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

        /// <summary>
        /// Initialize the ONNX model session
        /// </summary>
        public void Initialize()
        {
            if (!File.Exists(_options.ModelPath) && _options.Model == InternalModel.None)
            {
                throw new FileNotFoundException($"Model file not found: {_options.ModelPath}");
            }

            var sessionOptions = new SessionOptions
            {
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
            };

            try
            {
                sessionOptions.AppendExecutionProvider_CUDA();
                Log.Info("CUDA execution provider enabled.");
            }
            catch
            {
                sessionOptions.AppendExecutionProvider_CPU();
                Log.Info("Using CPU execution provider.");
            }

            if (File.Exists(_options.ModelPath))
            {
                _onnxSession = new InferenceSession(_options.ModelPath, sessionOptions);
            }
            else
            {
                var modelBytes = AudioSeparationExtensions.LoadModelBytes(_options.Model);
                _onnxSession = new InferenceSession(modelBytes, sessionOptions);
            }

            AutoDetectModelDimensions();
            Log.Info($"Model parameters: DimF={_modelParams.DimF}, DimT={_modelParams.DimT}, NFft={_modelParams.NFft}");

            // Pre-calculate Hanning window for performance
            PreCalculateHanningWindow();
        }

        /// <summary>
        /// Separate audio file into vocals and instrumental tracks
        /// </summary>
        /// <param name="inputFilePath">Input audio file path</param>
        /// <returns>Separation result</returns>
        public SimpleSeparationResult Separate(string inputFilePath)
        {
            if (_onnxSession == null)
                throw new InvalidOperationException("Service not initialized. Call Initialize first.");

            if (!File.Exists(inputFilePath))
                throw new FileNotFoundException($"Input file not found: {inputFilePath}");

            var startTime = DateTime.Now;
            var filename = Path.GetFileNameWithoutExtension(inputFilePath);

            ReportProgress(new SimpleSeparationProgress
            {
                CurrentFile = Path.GetFileName(inputFilePath),
                Status = "Loading audio file...",
                OverallProgress = 0
            });

            // Use streaming processing instead of loading entire file
            var (vocals, instrumental) = ProcessAudioStreaming(inputFilePath);

            ReportProgress(new SimpleSeparationProgress
            {
                CurrentFile = Path.GetFileName(inputFilePath),
                Status = "Saving results...",
                OverallProgress = 90
            });

            Directory.CreateDirectory(_options.OutputDirectory);

            var modelName = Path.GetFileName(_options.ModelPath ?? "").ToUpper();
            if (modelName == "")
                modelName = _options.Model.ToString().ToUpper();

            var (vocalsPath, instrumentalPath) = SaveResults(filename, vocals, instrumental, TargetSampleRate, modelName);

            var result = new SimpleSeparationResult
            {
                VocalsPath = vocalsPath,
                InstrumentalPath = instrumentalPath,
                ProcessingTime = DateTime.Now - startTime
            };

            ReportProgress(new SimpleSeparationProgress
            {
                CurrentFile = Path.GetFileName(inputFilePath),
                Status = "Completed",
                OverallProgress = 100
            });

            ProcessingCompleted?.Invoke(this, result);
            return result;
        }

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _onnxSession?.Dispose();
                _disposed = true;
            }
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

                        if (expectedFreq != _modelParams.DimF || expectedTime != _modelParams.DimT)
                        {
                            Log.Info("Auto-adjusting model parameters to match ONNX model...");
                            int newDimT = (int)Math.Log2(expectedTime);

                            _modelParams = new ModelParameters(
                                dimF: expectedFreq,
                                dimT: newDimT,
                                nFft: _options.NFft
                            );
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not auto-detect model dimensions: {ex.Message}");
            }
        }

        #endregion

        #region Private Methods - Audio Processing

        private void ReportProgress(SimpleSeparationProgress progress)
        {
            ProgressChanged?.Invoke(this, progress);
        }

        /// <summary>
        /// Pre-calculate Hanning window for STFT/ISTFT optimization
        /// </summary>
        private void PreCalculateHanningWindow()
        {
            _hanningWindow = new float[_modelParams.NFft];
            for (int i = 0; i < _modelParams.NFft; i++)
            {
                _hanningWindow[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / _modelParams.NFft)));
            }
        }

        /// <summary>
        /// Process audio file using streaming approach to minimize memory usage
        /// </summary>
        private (float[,] vocals, float[,] instrumental) ProcessAudioStreaming(string inputFilePath)
        {
            using var decoder = AudioDecoderFactory.Create(
                inputFilePath,
                targetSampleRate: TargetSampleRate,
                targetChannels: 2
            );

            AudioStreamInfo info = decoder.StreamInfo;
            int totalFrames = (int)(info.Duration.TotalSeconds * TargetSampleRate);

            // Allocate output buffers
            var vocals = new float[2, totalFrames];
            var instrumental = new float[2, totalFrames];

            int margin = _options.Margin;
            int chunkSize = _options.ChunkSizeSeconds * TargetSampleRate;

            if (margin == 0) throw new ArgumentException("Margin cannot be zero!");
            if (chunkSize != 0 && margin > chunkSize) margin = chunkSize;
            if (_options.ChunkSizeSeconds == 0 || totalFrames < chunkSize) chunkSize = totalFrames;

            // Processing context for buffer reuse
            using var context = new ProcessingContext(_modelParams);

            int processedFrames = 0;
            int chunkIndex = 0;
            int totalChunks = (int)Math.Ceiling((double)totalFrames / chunkSize);

            // Read and process in chunks
            int framesPerBuffer = Math.Min(chunkSize, 8192);
            int bufferSizeInBytes = framesPerBuffer * 2 * sizeof(float); // 2 channels
            byte[] readBuffer = new byte[bufferSizeInBytes];

            var chunkAccumulator = new List<float>();

            while (processedFrames < totalFrames)
            {
                // Read audio data
                var result = decoder.ReadFrames(readBuffer);

                if (!result.IsSucceeded || result.FramesRead == 0)
                {
                    if (result.IsEOF) break;
                    continue;
                }

                // Convert byte buffer to float using MemoryMarshal
                int bytesRead = result.FramesRead * 2 * sizeof(float);
                var floatSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                    readBuffer.AsSpan(0, bytesRead)
                );

                // Accumulate samples for chunk processing
                chunkAccumulator.AddRange(floatSpan.ToArray());

                // Process when we have enough data for a chunk
                while (chunkAccumulator.Count >= chunkSize * 2)
                {
                    // Extract chunk
                    var chunkData = new float[2, chunkSize];
                    for (int i = 0; i < chunkSize; i++)
                    {
                        chunkData[0, i] = chunkAccumulator[i * 2];
                        chunkData[1, i] = chunkAccumulator[i * 2 + 1];
                    }

                    // Remove processed samples
                    chunkAccumulator.RemoveRange(0, chunkSize * 2);

                    // Process chunk
                    var separated = ProcessSingleChunkOptimized(chunkData, context);

                    // Calculate vocals and instrumental
                    for (int ch = 0; ch < 2; ch++)
                    {
                        for (int i = 0; i < chunkSize; i++)
                        {
                            int outIdx = processedFrames + i;
                            if (outIdx < totalFrames)
                            {
                                instrumental[ch, outIdx] = separated[ch, i];
                                vocals[ch, outIdx] = chunkData[ch, i] - separated[ch, i];
                            }
                        }
                    }

                    processedFrames += chunkSize;
                    chunkIndex++;

                    // Report progress
                    ReportProgress(new SimpleSeparationProgress
                    {
                        Status = $"Processing chunk {chunkIndex}/{totalChunks}",
                        ProcessedChunks = chunkIndex,
                        TotalChunks = totalChunks,
                        OverallProgress = 20 + ((double)chunkIndex / totalChunks * 70)
                    });
                }
            }

            // Process remaining samples
            if (chunkAccumulator.Count > 0)
            {
                int remainingFrames = chunkAccumulator.Count / 2;
                var chunkData = new float[2, remainingFrames];
                for (int i = 0; i < remainingFrames; i++)
                {
                    chunkData[0, i] = chunkAccumulator[i * 2];
                    chunkData[1, i] = chunkAccumulator[i * 2 + 1];
                }

                var separated = ProcessSingleChunkOptimized(chunkData, context);

                for (int ch = 0; ch < 2; ch++)
                {
                    for (int i = 0; i < remainingFrames; i++)
                    {
                        int outIdx = processedFrames + i;
                        if (outIdx < totalFrames)
                        {
                            instrumental[ch, outIdx] = separated[ch, i];
                            vocals[ch, outIdx] = chunkData[ch, i] - separated[ch, i];
                        }
                    }
                }
            }

            return (vocals, instrumental);
        }

        #endregion

        #region Private Methods - Chunk Processing

        /// <summary>
        /// Processing context to reuse buffers and reduce GC pressure
        /// </summary>
        private class ProcessingContext : IDisposable
        {
            public float[] PaddedSignalL { get; }
            public float[] PaddedSignalR { get; }
            public Complex[] FftFrame { get; }
            public double[] ReconstructedL { get; }
            public double[] ReconstructedR { get; }
            public double[] WindowSumL { get; }
            public double[] WindowSumR { get; }

            public ProcessingContext(ModelParameters modelParams)
            {
                int padSize = modelParams.NFft / 2;
                int maxChunkSize = modelParams.ChunkSize + 2 * padSize;

                PaddedSignalL = new float[maxChunkSize];
                PaddedSignalR = new float[maxChunkSize];
                FftFrame = new Complex[modelParams.NFft];
                ReconstructedL = new double[maxChunkSize];
                ReconstructedR = new double[maxChunkSize];
                WindowSumL = new double[maxChunkSize];
                WindowSumR = new double[maxChunkSize];
            }

            public void Clear()
            {
                Array.Clear(PaddedSignalL, 0, PaddedSignalL.Length);
                Array.Clear(PaddedSignalR, 0, PaddedSignalR.Length);
                Array.Clear(ReconstructedL, 0, ReconstructedL.Length);
                Array.Clear(ReconstructedR, 0, ReconstructedR.Length);
                Array.Clear(WindowSumL, 0, WindowSumL.Length);
                Array.Clear(WindowSumR, 0, WindowSumR.Length);
            }

            public void Dispose()
            {
                // Nothing to dispose, arrays are managed
            }
        }

        #endregion

        #region Private Methods - Single Chunk Processing

        /// <summary>
        /// Optimized single chunk processing with buffer reuse
        /// </summary>
        private float[,] ProcessSingleChunkOptimized(float[,] mixChunk, ProcessingContext context)
        {
            int nSample = mixChunk.GetLength(1);
            int trim = _modelParams.NFft / 2;
            int genSize = _modelParams.ChunkSize - 2 * trim;

            if (genSize <= 0)
                throw new ArgumentException($"Invalid genSize: {genSize}. Check FFT parameters.");

            // Padding
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

            // STFT -> Model -> ISTFT (optimized with pre-calculated window)
            var stftTensor = ComputeStftOptimized(mixWaves, context);
            var outputTensor = RunModelInference(stftTensor);
            var resultWaves = ComputeIstftOptimized(outputTensor, context);

            // Extract signal
            return ExtractSignal(resultWaves, nSample, trim, genSize);
        }

        #endregion

        #region Private Methods - STFT/ISTFT Processing

        /// <summary>
        /// Optimized STFT computation with pre-calculated Hanning window
        /// </summary>
        private DenseTensor<float> ComputeStftOptimized(float[,,] mixWaves, ProcessingContext context)
        {
            int batchSize = mixWaves.GetLength(0);
            var tensor = new DenseTensor<float>(new[] { batchSize, 4, _modelParams.DimF, _modelParams.DimT });

            for (int b = 0; b < batchSize; b++)
            {
                for (int ch = 0; ch < 2; ch++)
                {
                    int padSize = _modelParams.NFft / 2;
                    var paddedSignal = ch == 0 ? context.PaddedSignalL : context.PaddedSignalR;

                    // Clear and prepare padded signal
                    Array.Clear(paddedSignal, 0, paddedSignal.Length);

                    // Reflection padding
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

                    // STFT computation with pre-calculated window
                    for (int t = 0; t < _modelParams.DimT; t++)
                    {
                        int frameStart = t * _modelParams.Hop;

                        // Apply window and prepare for FFT
                        for (int i = 0; i < _modelParams.NFft; i++)
                        {
                            if (frameStart + i < paddedSignal.Length)
                            {
                                context.FftFrame[i] = new Complex(paddedSignal[frameStart + i] * _hanningWindow![i], 0);
                            }
                            else
                            {
                                context.FftFrame[i] = Complex.Zero;
                            }
                        }

                        Fourier.Forward(context.FftFrame, FourierOptions.NoScaling);

                        for (int f = 0; f < Math.Min(_modelParams.DimF, _modelParams.NBins); f++)
                        {
                            tensor[b, ch * 2, f, t] = (float)context.FftFrame[f].Real;
                            tensor[b, ch * 2 + 1, f, t] = (float)context.FftFrame[f].Imaginary;
                        }
                    }
                }
            }
            return tensor;
        }

        /// <summary>
        /// Optimized ISTFT computation with pre-calculated Hanning window and buffer reuse
        /// </summary>
        private float[,,] ComputeIstftOptimized(Tensor<float> spectrum, ProcessingContext context)
        {
            int batchSize = spectrum.Dimensions[0];
            var result = new float[batchSize, 2, _modelParams.ChunkSize];

            for (int b = 0; b < batchSize; b++)
            {
                for (int ch = 0; ch < 2; ch++)
                {
                    int padSize = _modelParams.NFft / 2;
                    var reconstructed = ch == 0 ? context.ReconstructedL : context.ReconstructedR;
                    var windowSum = ch == 0 ? context.WindowSumL : context.WindowSumR;

                    // Clear buffers
                    Array.Clear(reconstructed, 0, reconstructed.Length);
                    Array.Clear(windowSum, 0, windowSum.Length);

                    int realIdx = ch * 2;
                    int imagIdx = ch * 2 + 1;

                    for (int t = 0; t < _modelParams.DimT; t++)
                    {
                        // Prepare FFT frame
                        for (int f = 0; f < _modelParams.NBins && f < _modelParams.NFft; f++)
                        {
                            if (f < _modelParams.DimF && f < spectrum.Dimensions[2])
                            {
                                context.FftFrame[f] = new Complex(spectrum[b, realIdx, f, t], spectrum[b, imagIdx, f, t]);
                            }
                            else
                            {
                                context.FftFrame[f] = Complex.Zero;
                            }
                        }

                        // Hermitian symmetry
                        for (int f = 1; f < _modelParams.NFft / 2; f++)
                        {
                            if (_modelParams.NFft - f < context.FftFrame.Length)
                            {
                                context.FftFrame[_modelParams.NFft - f] = Complex.Conjugate(context.FftFrame[f]);
                            }
                        }

                        Fourier.Inverse(context.FftFrame, FourierOptions.NoScaling);

                        // Normalize
                        for (int i = 0; i < _modelParams.NFft; i++)
                        {
                            context.FftFrame[i] /= _modelParams.NFft;
                        }

                        // Overlap-add with pre-calculated window
                        int frameStart = t * _modelParams.Hop;
                        for (int i = 0; i < _modelParams.NFft; i++)
                        {
                            int targetIdx = frameStart + i;
                            if (targetIdx >= 0 && targetIdx < reconstructed.Length)
                            {
                                float windowValue = _hanningWindow![i];
                                reconstructed[targetIdx] += context.FftFrame[i].Real * windowValue;
                                windowSum[targetIdx] += windowValue * windowValue;
                            }
                        }
                    }

                    // Extract final result
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

            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input", stftTensor) };

            if (!_options.DisableNoiseReduction)
            {
                // Denoise logic
                var stftTensorNeg = new DenseTensor<float>(stftTensor.Dimensions);
                for (int idx = 0; idx < stftTensor.Length; idx++)
                {
                    stftTensorNeg.SetValue(idx, -stftTensor.GetValue(idx));
                }
                var inputsNeg = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input", stftTensorNeg) };

                using var outputs = _onnxSession.Run(inputs);
                using var outputsNeg = _onnxSession.Run(inputsNeg);

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
                using var outputs = _onnxSession.Run(inputs);
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

        private (string vocalsPath, string instrumentalPath) SaveResults(
            string filename, float[,] vocals, float[,] instrumental, int sampleRate, string modelName)
        {
            var vocalsPath = Path.Combine(_options.OutputDirectory, $"{filename}_vocals.wav");
            var instrumentalPath = Path.Combine(_options.OutputDirectory, $"{filename}_music.wav");

            if (modelName.CompareTo("DEFAULT") < 0)
            {
                SaveAudio(instrumentalPath, instrumental, sampleRate);
                SaveAudio(vocalsPath, vocals, sampleRate);
            }
            else
            {
                SaveAudio(instrumentalPath, vocals, sampleRate);
                SaveAudio(vocalsPath, instrumental, sampleRate);
            }

            return (vocalsPath, instrumentalPath);
        }

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
    }

    /// <summary>
    /// Represents the type of model used in a specific operation or context.
    /// </summary>
    public enum InternalModel
    {
        None,
        Default,
        Best,
        Karaoke,
        HTDemucs
    }

    /// <summary>
    /// Model parameters for STFT processing (internal use)
    /// </summary>
    internal class ModelParameters
    {
        /// <summary>
        /// Number of channels (always 4 for complex stereo)
        /// </summary>
        public int DimC { get; } = 4;

        /// <summary>
        /// Frequency dimension size
        /// </summary>
        public int DimF { get; set; }

        /// <summary>
        /// Time dimension size (calculated as power of 2)
        /// </summary>
        public int DimT { get; set; }

        /// <summary>
        /// FFT size
        /// </summary>
        public int NFft { get; }

        /// <summary>
        /// Hop size for STFT
        /// </summary>
        public int Hop { get; }

        /// <summary>
        /// Number of frequency bins (NFft/2 + 1)
        /// </summary>
        public int NBins { get; }

        /// <summary>
        /// Processing chunk size in samples
        /// </summary>
        public int ChunkSize { get; set; }

        /// <summary>
        /// Initialize model parameters
        /// </summary>
        /// <param name="dimF">Frequency dimension</param>
        /// <param name="dimT">Time dimension (as power of 2)</param>
        /// <param name="nFft">FFT size</param>
        /// <param name="hop">Hop size (default 1024)</param>
        public ModelParameters(int dimF, int dimT, int nFft, int hop = 1024)
        {
            DimF = dimF;
            DimT = (int)Math.Pow(2, dimT);
            NFft = nFft;
            Hop = hop;
            NBins = NFft / 2 + 1;
            ChunkSize = hop * (DimT - 1);
        }
    }

    /// <summary>
    /// Helper extension methods for easier usage
    /// </summary>
    public static class AudioSeparationExtensions
    {
        /// <summary>
        /// Creates a default instance of the <see cref="AudioSeparationService"/> using the specified model path.
        /// </summary>
        /// <param name="modelPath">The file path to the model used for audio separation. This must be a valid path to a supported model file.</param>
        /// <returns>A new instance of <see cref="AudioSeparationService"/> configured with the specified model.</returns>
        public static AudioSeparationService CreateDefaultService(string modelPath)
        {
            return createDefaultService(modelPath, InternalModel.None);
        }

        /// <summary>
        /// Creates a default instance of the <see cref="AudioSeparationService"/> using the specified model.
        /// </summary>
        /// <param name="_model">The internal model to be used for audio separation. Cannot be null.</param>
        /// <returns>A new instance of <see cref="AudioSeparationService"/> configured with the specified model.</returns>
        public static AudioSeparationService CreateDefaultService(InternalModel _model)
        {
            return createDefaultService("", _model);
        }

        /// <summary>
        /// Creates an instance of the <see cref="AudioSeparationService"/> using the specified model path and output
        /// directory.
        /// </summary>
        /// <param name="_modelPath">The file path to the model used for audio separation. Cannot be null or empty.</param>
        /// <param name="_output">The directory where the output files will be saved. Cannot be null or empty.</param>
        /// <returns>An instance of <see cref="AudioSeparationService"/> configured with the specified model and output
        /// directory.</returns>
        public static AudioSeparationService CreatetService(string _modelPath, string _output)
        {
            return createService(_modelPath, InternalModel.None, _output);
        }

        /// <summary>
        /// Creates an instance of the <see cref="AudioSeparationService"/> using the specified model and output path.
        /// </summary>
        /// <param name="_model">The internal model to be used for audio separation.</param>
        /// <param name="_output">The output path where the results of the audio separation will be stored. Cannot be null or empty.</param>
        /// <returns>An instance of <see cref="AudioSeparationService"/> configured with the specified model and output path.</returns>
        public static AudioSeparationService CreatetService(InternalModel _model, string _output)
        {
            return createService("", _model, _output);
        }

        /// <summary>
        /// Create service with default options
        /// </summary>
        /// <param name="modelPath">Path to ONNX model file</param>
        /// <returns>Configured AudioSeparationService</returns>
        private static AudioSeparationService createDefaultService(string? modelPath, InternalModel inmodel)
        {
            var options = new SimpleSeparationOptions
            {
                ModelPath = modelPath,
                Model = inmodel,
            };
            return new AudioSeparationService(options);
        }

        /// <summary>
        /// Create service with custom output directory
        /// </summary>
        /// <param name="modelPath">Path to ONNX model file</param>
        /// <param name="outputDirectory">Output directory path</param>
        /// <returns>Configured AudioSeparationService</returns>
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

        /// <summary>
        /// Validate audio file format
        /// </summary>
        /// <param name="filePath">Audio file path</param>
        /// <returns>True if supported format</returns>
        public static bool IsValidAudioFile(string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var supportedFormats = new[] { ".wav", ".mp3", ".flac" };

            return supportedFormats.Contains(extension);
        }

        /// <summary>
        /// Get estimated processing time based on file size
        /// </summary>
        /// <param name="filePath">Audio file path</param>
        /// <returns>Estimated processing time</returns>
        public static TimeSpan EstimateProcessingTime(string filePath)
        {
            if (!File.Exists(filePath))
                return TimeSpan.Zero;

            var fileInfo = new FileInfo(filePath);
            var fileSizeMB = fileInfo.Length / (1024.0 * 1024.0);

            // Rough estimate: ~1-2 minutes per MB for CPU processing
            var estimatedMinutes = fileSizeMB * 1.5;
            return TimeSpan.FromMinutes(Math.Max(0.5, estimatedMinutes));
        }

        /// <summary>
        /// Loads the model data as a byte array from the embedded resource.
        /// </summary>
        /// <remarks>This method retrieves the model file from the assembly's embedded resources using the
        /// specified resource name. The resource name is matched against the assembly's manifest resource names, and
        /// the corresponding resource is loaded.</remarks>
        /// <param name="_model">The internal model instance used to determine the resource to load. This parameter is currently unused.</param>
        /// <returns>A byte array containing the model data extracted from the embedded resource.</returns>
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
    /// Main audio separation service with parallel processing support
    /// </summary>
    public class AudioSeparationService : IDisposable
    {
        #region Events

        /// <summary>
        /// Progress update event
        /// </summary>
        public event EventHandler<SimpleSeparationProgress>? ProgressChanged;

        /// <summary>
        /// Processing started event
        /// </summary>
#pragma warning disable CS0067 // Event is never used
        public event EventHandler<string>? ProcessingStarted;

        /// <summary>
        /// Processing completed event
        /// </summary>
        public event EventHandler<SimpleSeparationResult>? ProcessingCompleted;

        /// <summary>
        /// Error occurred event
        /// </summary>
        public event EventHandler<Exception>? ErrorOccurred;
#pragma warning restore CS0067

        #endregion

        #region Private Fields

        /// <summary>
        /// Configuration options for separation
        /// </summary>
        private readonly SimpleSeparationOptions _options;

        /// <summary>
        /// Model parameters for STFT processing
        /// </summary>
        private ModelParameters _modelParams;

        /// <summary>
        /// ONNX Runtime session for model inference (traditional mode)
        /// </summary>
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value
        private InferenceSession? _onnxSession;

        /// <summary>
        /// Semaphore for controlling concurrent session usage
        /// </summary>
        private SemaphoreSlim? _sessionSemaphore;

        /// <summary>
        /// Memory monitoring timer
        /// </summary>
        private Timer? _memoryMonitorTimer;
#pragma warning restore CS0649

        /// <summary>
        /// Current memory pressure flag
        /// </summary>
#pragma warning disable CS0414 // Field is assigned but its value is never used
        private volatile bool _isMemoryPressureHigh = false;
#pragma warning restore CS0414

        /// <summary>
        /// Flag indicating if the object has been disposed
        /// </summary>
        private bool _disposed = false;

        /// <summary>
        /// Target sample rate for audio processing
        /// </summary>
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

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion

        #region Private Methods - Initialization

        /// <summary>
        /// Automatically detect model dimensions from ONNX metadata
        /// </summary>
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
                        // Expected shape: [batch, channels, frequency, time]
                        int expectedFreq = (int)inputShape[2];
                        int expectedTime = (int)inputShape[3];

                        Log.Info($"Model expects: Frequency={expectedFreq}, Time={expectedTime}");
                        Log.Info($"Current config: Frequency={_modelParams.DimF}, Time={_modelParams.DimT}");

                        // Update model parameters if they don't match
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

        /// <summary>
        /// Report progress to subscribers
        /// </summary>
        /// <param name="progress">Progress information</param>
        private void ReportProgress(SimpleSeparationProgress progress)
        {
            ProgressChanged?.Invoke(this, progress);
        }
        #endregion

        #region Private Methods - Single Chunk Processing

        /// <summary>
        /// Process a single audio chunk through STFT, model inference, and ISTFT (traditional mode)
        /// </summary>
        /// <param name="mixChunk">Audio chunk to process</param>
        /// <param name="chunkKey">Position key of the chunk</param>
        /// <param name="allKeys">All chunk position keys</param>
        /// <param name="margin">Margin size for trimming</param>
        /// <returns>Processed audio chunk</returns>
        private float[,] ProcessSingleChunk(float[,] mixChunk, long chunkKey, List<long> allKeys, int margin)
        {
            int nSample = mixChunk.GetLength(1);
            int trim = _modelParams.NFft / 2;
            int genSize = _modelParams.ChunkSize - 2 * trim;

            if (genSize <= 0)
                throw new ArgumentException($"Invalid genSize: {genSize}. Check FFT parameters.");

            // Padding
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

            // STFT -> Model -> ISTFT
            var stftTensor = ComputeStft(mixWaves);
            var outputTensor = RunModelInference(stftTensor);
            var resultWaves = ComputeIstft(outputTensor);

            // Extract and apply margin
            var result = ExtractSignal(resultWaves, nSample, trim, genSize);
            return ApplyMargin(result, chunkKey, allKeys, margin);
        }
        #endregion

        #region Private Methods - STFT/ISTFT Processing

        /// <summary>
        /// Compute Short-Time Fourier Transform (STFT) for audio waves
        /// </summary>
        /// <param name="mixWaves">Audio waves to transform</param>
        /// <returns>STFT tensor in model input format</returns>
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

                    // Reflection padding
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

                    // STFT computation
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

        /// <summary>
        /// Compute Inverse Short-Time Fourier Transform (ISTFT) from spectrum
        /// </summary>
        /// <param name="spectrum">Frequency domain spectrum</param>
        /// <returns>Time domain audio waves</returns>
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

                        // Hermitian symmetry
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

        /// <summary>
        /// Run model inference on STFT tensor (traditional mode)
        /// </summary>
        /// <param name="stftTensor">Input STFT tensor</param>
        /// <returns>Output tensor from model</returns>
        private Tensor<float> RunModelInference(DenseTensor<float> stftTensor)
        {
            if (_onnxSession == null)
                throw new InvalidOperationException("ONNX session not initialized");

            return RunModelInferenceWithSession(stftTensor, _onnxSession);
        }

        /// <summary>
        /// Run model inference with specific session
        /// </summary>
        /// <param name="stftTensor">Input STFT tensor</param>
        /// <param name="session">ONNX session to use</param>
        /// <returns>Output tensor from model</returns>
        private Tensor<float> RunModelInferenceWithSession(DenseTensor<float> stftTensor, InferenceSession session)
        {
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input", stftTensor) };

            if (!_options.DisableNoiseReduction)
            {
                // Denoise logic with specific session
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

                // Create a copy to avoid disposal issues
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

        /// <summary>
        /// Extract processed signal from wave frames
        /// </summary>
        /// <param name="waves">Processed wave frames</param>
        /// <param name="nSample">Number of samples to extract</param>
        /// <param name="trim">Trim size from frame edges</param>
        /// <param name="genSize">Generation size per frame</param>
        /// <returns>Extracted signal</returns>
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

        /// <summary>
        /// Apply margin trimming to processed signal
        /// </summary>
        /// <param name="signal">Processed signal</param>
        /// <param name="chunkKey">Current chunk position key</param>
        /// <param name="allKeys">All chunk position keys</param>
        /// <param name="margin">Margin size for trimming</param>
        /// <returns>Margin-trimmed signal</returns>
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

        /// <summary>
        /// Concatenate processed audio chunks into single array
        /// </summary>
        /// <param name="chunks">List of processed audio chunks</param>
        /// <returns>Concatenated audio array</returns>
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

        #region Private Methods - Statistics and File I/O

        /// <summary>
        /// Save audio data to WAV file with normalization
        /// </summary>
        /// <param name="filePath">Output file path</param>
        /// <param name="audio">Audio data to save</param>
        /// <param name="sampleRate">Audio sample rate</param>
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

            // Normalize +/-1.0 
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

        /// <summary>
        /// Dispose resources
        /// </summary>
        /// <param name="disposing">True if disposing</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _memoryMonitorTimer?.Dispose();
                _sessionSemaphore?.Dispose();

                // Dispose original session if it exists
                _onnxSession?.Dispose();
                _disposed = true;
            }
        }

        #endregion
    }

    /// <summary>
    /// Simplified factory with Separator method
    /// </summary>
    public static class SimpleSeparator
    {
        /// <summary>
        /// Creates and initializes a simple audio separator service
        /// </summary>
        /// <param name="model">Internal model to use</param>
        /// <param name="outputDirectory">Output directory path</param>
        /// <returns>Tuple containing initialized service, vocals path placeholder, and instrumental path placeholder</returns>
        public static (SimpleAudioSeparationService? service, string vocalPath, string instrumentPath) Separator(
            InternalModel model, string outputDirectory)
        {
            var options = new SimpleSeparationOptions
            {
                Model = model,
                OutputDirectory = outputDirectory
            };

            var service = new SimpleAudioSeparationService(options);
            service.Initialize();

            return (service, string.Empty, string.Empty);
        }
    }
}
