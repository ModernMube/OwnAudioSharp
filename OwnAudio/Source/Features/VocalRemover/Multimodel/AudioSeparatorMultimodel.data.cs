namespace OwnaudioNET.Features.Vocalremover
{
    /// <summary>
    /// Specifies what the model outputs
    /// </summary>
    public enum ModelOutputType
    {
        /// <summary>Model outputs the instrumental track (vocals will be calculated as original - instrumental)</summary>
        Instrumental,

        /// <summary>Model outputs the vocal track (instrumental will be calculated as original - vocals)</summary>
        Vocals
    }

    /// <summary>
    /// Information about a single model in the pipeline
    /// </summary>
    public class MultiModelInfo
    {
        /// <summary>Model display name</summary>
        public string Name { get; set; } = "Model";

        /// <summary>ONNX model file path (optional if using InternalModel)</summary>
        public string? ModelPath { get; set; }

        /// <summary>Internal embedded model to use</summary>
        public InternalModel Model { get; set; } = InternalModel.None;

        /// <summary>FFT size for this model (0 = auto-detect)</summary>
        public int NFft { get; set; } = 6144;

        /// <summary>Temporal dimension parameter (as power of 2)</summary>
        public int DimT { get; set; } = 8;

        /// <summary>Frequency dimension parameter</summary>
        public int DimF { get; set; } = 2048;

        /// <summary>Disable noise reduction for this model</summary>
        public bool DisableNoiseReduction { get; set; } = false;

        /// <summary>Save intermediate output from this model (for debugging)</summary>
        public bool SaveIntermediateOutput { get; set; } = false;

        /// <summary>
        /// Specifies what this model outputs (Vocals or Instrumental).
        /// If null, the system will try to auto-detect from model metadata or filename.
        /// Default is Instrumental (most common for UVR models)
        /// </summary>
        public ModelOutputType? OutputType { get; set; } = null;
    }

    /// <summary>
    /// Configuration parameters for multi-model audio separation
    /// </summary>
    public class MultiModelSeparationOptions
    {
        /// <summary>List of models to process in sequence</summary>
        public List<MultiModelInfo> Models { get; set; } = new();

        /// <summary>Output directory path</summary>
        public string OutputDirectory { get; set; } = "separated_multimodel";

        /// <summary>Enable GPU acceleration (CoreML on macOS, CUDA on other platforms)</summary>
        public bool EnableGPU { get; set; } = true;

        /// <summary>Margin size for overlapping chunks (in samples)</summary>
        public int Margin { get; set; } = 44100;

        /// <summary>Chunk size in seconds (0 = process entire file at once)</summary>
        public int ChunkSizeSeconds { get; set; } = 15;

        /// <summary>Save intermediate results after each model (for debugging)</summary>
        public bool SaveAllIntermediateResults { get; set; } = false;
    }

    /// <summary>
    /// Progress information for multi-model separation
    /// </summary>
    public class MultiModelSeparationProgress
    {
        /// <summary>Current file being processed</summary>
        public string CurrentFile { get; set; } = string.Empty;

        /// <summary>Overall progress percentage (0-100)</summary>
        public double OverallProgress { get; set; }

        /// <summary>Current processing step description</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>Current model being processed (1-based index)</summary>
        public int CurrentModelIndex { get; set; }

        /// <summary>Total number of models in pipeline</summary>
        public int TotalModels { get; set; }

        /// <summary>Current model name</summary>
        public string CurrentModelName { get; set; } = string.Empty;

        /// <summary>Number of chunks processed for current model</summary>
        public int ProcessedChunks { get; set; }

        /// <summary>Total number of chunks for current model</summary>
        public int TotalChunks { get; set; }
    }

    /// <summary>
    /// Result of multi-model audio separation
    /// </summary>
    public class MultiModelSeparationResult
    {
        /// <summary>Path to the final separated output file (averaged instrumental from all models)</summary>
        public string OutputPath { get; set; } = string.Empty;

        /// <summary>Path to the vocals file (averaged vocals from all models)</summary>
        public string VocalsPath { get; set; } = string.Empty;

        /// <summary>Path to the instrumental file (averaged instrumental from all models)</summary>
        public string InstrumentalPath { get; set; } = string.Empty;

        /// <summary>Paths to intermediate results (if saved)</summary>
        public Dictionary<string, string> IntermediatePaths { get; set; } = new();

        /// <summary>Processing duration</summary>
        public TimeSpan ProcessingTime { get; set; }

        /// <summary>Number of models processed</summary>
        public int ModelsProcessed { get; set; }
    }
}
