using Microsoft.ML.OnnxRuntime;
using Logger;

namespace OwnaudioNET.Features.Vocalremover
{
    /// <summary>
    /// Multi-model audio separation service with averaging across multiple UVR MDX models.
    /// Processes audio through multiple models in parallel and averages their outputs.
    /// Each model processes the original audio independently, then vocals and instrumentals
    /// are averaged separately across all models.
    ///
    /// Example use case:
    /// Model 1: High-quality vocal separation (outputs vocals)
    /// Model 2: Another vocal model with different characteristics (outputs vocals)
    /// Model 3: Instrumental-focused model (outputs instrumental)
    /// Result: Averaged vocals and averaged instrumental with better quality than any single model
    ///
    /// Important: Configure each model's OutputType (Vocals or Instrumental) to handle
    /// models that output different stems.
    /// </summary>
    public partial class MultiModelAudioSeparator : IDisposable
    {
        #region Events

        /// <summary>Progress update event</summary>
        public event EventHandler<MultiModelSeparationProgress>? ProgressChanged;

        /// <summary>Processing completed event</summary>
        public event EventHandler<MultiModelSeparationResult>? ProcessingCompleted;

        #endregion

        #region Private Fields

        private readonly MultiModelSeparationOptions _options;
        private readonly List<ModelSession> _modelSessions = new();
        private bool _disposed = false;
        private const int TargetSampleRate = 44100;

        // Pre-calculated Hanning windows for each model (keyed by NFft size)
        private readonly Dictionary<int, float[]> _hanningWindows = new();

        #endregion

        #region Constructor

        /// <summary>
        /// Initialize multi-model audio separation service
        /// </summary>
        /// <param name="options">Separation configuration options</param>
        public MultiModelAudioSeparator(MultiModelSeparationOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            if (_options.Models.Count == 0)
            {
                throw new ArgumentException("At least one model must be specified in the pipeline.");
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Initialize all ONNX model sessions
        /// </summary>
        public void Initialize()
        {
            Log.Info($"Initializing multi-model pipeline with {_options.Models.Count} models...");

            foreach (var modelInfo in _options.Models)
            {
                InitializeModel(modelInfo);
            }

            Log.Info($"Multi-model pipeline initialized successfully.");
        }

        /// <summary>
        /// Separate audio file through the multi-model pipeline using streaming
        /// </summary>
        /// <param name="inputFilePath">Input audio file path</param>
        /// <returns>Separation result</returns>
        public MultiModelSeparationResult Separate(string inputFilePath)
        {
            if (_modelSessions.Count == 0)
                throw new InvalidOperationException("Service not initialized. Call Initialize() first.");

            if (!File.Exists(inputFilePath))
                throw new FileNotFoundException($"Input file not found: {inputFilePath}");

            var startTime = DateTime.Now;
            var filename = Path.GetFileNameWithoutExtension(inputFilePath);

            ReportProgress(new MultiModelSeparationProgress
            {
                CurrentFile = Path.GetFileName(inputFilePath),
                Status = "Starting multi-model streaming pipeline...",
                OverallProgress = 0,
                TotalModels = _modelSessions.Count
            });

            Log.Info($"Processing audio with streaming pipeline: {inputFilePath}");
            var finalAudio = ProcessAudioStreamingPipeline(inputFilePath, filename, out var intermediatePaths);

            ReportProgress(new MultiModelSeparationProgress
            {
                CurrentFile = Path.GetFileName(inputFilePath),
                Status = "Saving final results...",
                OverallProgress = 95
            });

            Directory.CreateDirectory(_options.OutputDirectory);

            string instrumentalPath = Path.Combine(_options.OutputDirectory, $"{filename}_instrumental.wav");
            SaveAudio(instrumentalPath, finalAudio, TargetSampleRate);
            Log.Info($"Saved final instrumental: {instrumentalPath}");

            string vocalsPath = intermediatePaths.ContainsKey("Vocals")
                ? intermediatePaths["Vocals"]
                : string.Empty;

            var result = new MultiModelSeparationResult
            {
                OutputPath = instrumentalPath,
                VocalsPath = vocalsPath,
                InstrumentalPath = instrumentalPath,
                IntermediatePaths = intermediatePaths,
                ProcessingTime = DateTime.Now - startTime,
                ModelsProcessed = _modelSessions.Count
            };

            ReportProgress(new MultiModelSeparationProgress
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
                foreach (var session in _modelSessions)
                {
                    session.Session?.Dispose();
                }
                _modelSessions.Clear();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        #endregion

        #region Private Methods - Progress

        private void ReportProgress(MultiModelSeparationProgress progress)
        {
            ProgressChanged?.Invoke(this, progress);
        }

        #endregion
    }
}
