using Microsoft.ML.OnnxRuntime;
using Logger;

namespace OwnaudioNET.Features.Vocalremover
{
    /// <summary>
    /// HTDemucs-based audio separation service for stem extraction.
    /// Optimized for streaming processing with minimal memory footprint.
    /// Uses OwnAudioEngine converters for high-performance audio processing.
    /// </summary>
    /// <remarks>
    /// HTDemucs (Hybrid Transformer Demucs) separates audio into 4 stems:
    /// - Vocals: singing and speech
    /// - Drums: percussion instruments
    /// - Bass: bass guitar and low-frequency instruments
    /// - Other: all other instruments
    ///
    /// Performance characteristics:
    /// - CPU processing: ~10-15x realtime (16 cores)
    /// - GPU processing: ~50-100x realtime (CUDA)
    /// - Memory footprint: ~500-800 MB for 10s chunks
    /// </remarks>
    public partial class HTDemucsAudioSeparator : IDisposable
    {
        #region Events

        /// <summary>Progress update event</summary>
        public event EventHandler<HTDemucsSeparationProgress>? ProgressChanged;

        /// <summary>Processing completed event</summary>
        public event EventHandler<HTDemucsSeparationResult>? ProcessingCompleted;

        #endregion

        #region Private Fields

        private readonly HTDemucsSeparationOptions _options;
        private InferenceSession? _onnxSession;
        private bool _disposed = false;

        // STFT processor for spectrogram generation
        private STFTProcessor? _stftProcessor;

        // Model parameters (auto-detected or configured)
        private int _modelSegmentLength = 0;
        private int _modelSampleRate = 44100;
        private int _modelChannels = 2;
        private int _modelStemCount = 4;

        // Stem name mapping (order from model output)
        private readonly string[] _stemNames = { "vocals", "drums", "bass", "other" };

        #endregion

        #region Constructor

        /// <summary>
        /// Initialize HTDemucs audio separation service
        /// </summary>
        /// <param name="options">Separation configuration options</param>
        public HTDemucsAudioSeparator(HTDemucsSeparationOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Initialize the ONNX model session
        /// </summary>
        public void Initialize()
        {
            bool useEmbeddedModel = string.IsNullOrEmpty(_options.ModelPath) || !File.Exists(_options.ModelPath);

            if (useEmbeddedModel && _options.Model == InternalModel.None)
            {
                throw new InvalidOperationException("Either ModelPath must be a valid file or Model must be set to a valid InternalModel value.");
            }

            var sessionOptions = new SessionOptions
            {
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
            };

            // Configure execution providers
            if (_options.EnableGPU)
            {
#if MACOS
                try
                {
                    Log.Info("Attempting to enable CoreML execution provider for HTDemucs...");

                    try
                    {
                        var coremlOptions = new Dictionary<string, string>
                        {
                            ["ModelFormat"] = "MLProgram",
                            ["MLComputeUnits"] = "ALL",
                            ["RequireStaticInputShapes"] = "0"
                        };

                        sessionOptions.AppendExecutionProvider("CoreML", coremlOptions);
                        Log.Info("✅ CoreML enabled for HTDemucs (MLProgram format, ALL compute units).");
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"   MLProgram failed, trying NeuralNetwork format...");

                        try
                        {
                            var coremlOptions = new Dictionary<string, string>
                            {
                                ["ModelFormat"] = "NeuralNetwork",
                                ["MLComputeUnits"] = "ALL"
                            };

                            sessionOptions.AppendExecutionProvider("CoreML", coremlOptions);
                            Log.Info("✅ CoreML enabled for HTDemucs (NeuralNetwork format fallback).");
                        }
                        catch (Exception ex2)
                        {
                            Log.Warning($"❌ Both CoreML formats failed.");
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to enable CoreML: {ex.Message}");
                    sessionOptions.AppendExecutionProvider_CPU();
                    Log.Info("Using CPU execution provider for HTDemucs.");
                }
#else
                try
                {
                    sessionOptions.AppendExecutionProvider_CUDA();
                    Log.Info("CUDA execution provider enabled for HTDemucs.");
                }
                catch
                {
                    try
                    {
                        sessionOptions.AppendExecutionProvider_DML();
                        Log.Info("DirectML execution provider enabled for HTDemucs.");
                    }
                    catch
                    {
                        sessionOptions.AppendExecutionProvider_CPU();
                        Log.Info("Using CPU execution provider for HTDemucs.");
                    }
                }
#endif
            }
            else
            {
                sessionOptions.AppendExecutionProvider_CPU();
                Log.Info("Using CPU execution provider for HTDemucs.");
            }

            // Load model from file or embedded resource
            if (useEmbeddedModel)
            {
                Log.Info($"Loading embedded HTDemucs model: {_options.Model}");
                var modelBytes = AudioSeparationExtensions.LoadModelBytes(_options.Model);
                _onnxSession = new InferenceSession(modelBytes, sessionOptions);
            }
            else
            {
                Log.Info($"Loading HTDemucs model from file: {_options.ModelPath}");
                _onnxSession = new InferenceSession(_options.ModelPath, sessionOptions);
            }

            AutoDetectModelParameters();

            // Initialize STFT processor for HTDemucs (n_fft=4096, hop_length=1024)
            _stftProcessor = new STFTProcessor(nFft: 4096, hopLength: 1024);

            Log.Info($"HTDemucs model initialized: SegmentLength={_modelSegmentLength}, " +
                     $"SampleRate={_modelSampleRate}, Stems={_modelStemCount}");
        }

        /// <summary>
        /// Separate audio file into stems
        /// </summary>
        /// <param name="inputFilePath">Input audio file path</param>
        /// <returns>Separation result with stem file paths</returns>
        public HTDemucsSeparationResult Separate(string inputFilePath)
        {
            if (_onnxSession == null)
                throw new InvalidOperationException("Service not initialized. Call Initialize() first.");

            if (!File.Exists(inputFilePath))
                throw new FileNotFoundException($"Input file not found: {inputFilePath}");

            var startTime = DateTime.Now;
            var filename = Path.GetFileNameWithoutExtension(inputFilePath);

            ReportProgress(new HTDemucsSeparationProgress
            {
                CurrentFile = Path.GetFileName(inputFilePath),
                Status = "Loading audio file...",
                OverallProgress = 0
            });

            var (stems, audioDuration) = ProcessAudioStreaming(inputFilePath);

            ReportProgress(new HTDemucsSeparationProgress
            {
                CurrentFile = Path.GetFileName(inputFilePath),
                Status = "Saving stems...",
                OverallProgress = 90
            });

            Directory.CreateDirectory(_options.OutputDirectory);

            var stemPaths = SaveResults(filename, stems, _options.TargetSampleRate);

            var result = new HTDemucsSeparationResult
            {
                StemPaths = stemPaths,
                ProcessingTime = DateTime.Now - startTime,
                AudioDuration = audioDuration
            };

            ReportProgress(new HTDemucsSeparationProgress
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

        private void ReportProgress(HTDemucsSeparationProgress progress)
        {
            ProgressChanged?.Invoke(this, progress);
        }

        #endregion
    }
}
