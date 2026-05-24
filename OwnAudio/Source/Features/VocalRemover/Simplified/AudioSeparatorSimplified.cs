using Microsoft.ML.OnnxRuntime;
using Ownaudio;
using Ownaudio.Decoders;
using Ownaudio.Core;
using Logger;

namespace OwnaudioNET.Features.Vocalremover
{
    /// <summary>
    /// Simplified audio separation service without async operations.
    /// Optimized for streaming processing with minimal memory footprint.
    /// </summary>
    public partial class SimpleAudioSeparationService : IDisposable
    {
        #region Events

        /// <summary>Progress update event</summary>
        public event EventHandler<SimpleSeparationProgress>? ProgressChanged;

        /// <summary>Processing completed event</summary>
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
            var sessionOptions = new SessionOptions
            {
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
            };

            if (_options.EnableGPU)
            {
#if MACOS
                try
                {
                    Log.Info("Attempting to enable CoreML execution provider...");

                    try
                    {
                        var coremlOptions = new Dictionary<string, string>
                        {
                            ["ModelFormat"] = "MLProgram",
                            ["MLComputeUnits"] = "ALL",
                            ["RequireStaticInputShapes"] = "0"
                        };

                        sessionOptions.AppendExecutionProvider("CoreML", coremlOptions);
                        Log.Info("CoreML enabled (MLProgram format, ALL compute units).");
                    }
                    catch (Exception)
                    {
                        Log.Info("   MLProgram failed, trying NeuralNetwork format...");

                        try
                        {
                            var coremlOptions = new Dictionary<string, string>
                            {
                                ["ModelFormat"] = "NeuralNetwork",
                                ["MLComputeUnits"] = "ALL"
                            };

                            sessionOptions.AppendExecutionProvider("CoreML", coremlOptions);
                            Log.Info("CoreML enabled (NeuralNetwork format fallback).");
                        }
                        catch (Exception)
                        {
                            Log.Warning("Both CoreML formats failed.");
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to enable CoreML: {ex.Message}");
                    sessionOptions.AppendExecutionProvider_CPU();
                }
#else
                try
                {
                    Log.Info("Attempting to enable CUDA execution provider...");
                    sessionOptions.AppendExecutionProvider_CUDA();
                    Log.Info("CUDA execution provider enabled.");
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to enable CUDA: {ex.Message}");
                    sessionOptions.AppendExecutionProvider_CPU();
                }
#endif
            }
            else
            {
                sessionOptions.AppendExecutionProvider_CPU();
                Log.Info("Using CPU execution provider (GPU disabled in options).");
            }

            string modelPath;
            if (!string.IsNullOrEmpty(_options.ModelPath) && File.Exists(_options.ModelPath))
            {
                modelPath = _options.ModelPath;
            }
            else if (_options.Model != InternalModel.None)
            {
                modelPath = VocalRemoverModelManager.GetModelPath(_options.Model);
            }
            else
            {
                throw new InvalidOperationException(
                    "Either ModelPath must point to a valid file or Model must be set to a valid InternalModel value. " +
                    "Use VocalRemoverModelManager.DownloadModelAsync() to fetch missing models.");
            }

            _onnxSession = new InferenceSession(modelPath, sessionOptions);

            AutoDetectModelDimensions();
            Log.Info($"Model parameters: DimF={_modelParams.DimF}, DimT={_modelParams.DimT}, NFft={_modelParams.NFft}");

            LogExecutionProviders(_onnxSession);
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

        #region Private Methods - Progress

        private void ReportProgress(SimpleSeparationProgress progress)
        {
            ProgressChanged?.Invoke(this, progress);
        }

        #endregion
    }
}
