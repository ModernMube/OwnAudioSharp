namespace OwnaudioNET.Features.Vocalremover
{
    /// <summary>
    /// Configuration parameters for HTDemucs audio separation
    /// </summary>
    public class HTDemucsSeparationOptions
    {
        /// <summary>ONNX model file path (optional if using InternalModel)</summary>
        public string? ModelPath { get; set; }

        /// <summary>Internal embedded model to use (if ModelPath is not specified)</summary>
        public InternalModel Model { get; set; } = InternalModel.None;

        /// <summary>Output directory path</summary>
        public string OutputDirectory { get; set; } = "separated_htdemucs";

        /// <summary>Chunk size in seconds (10-30 seconds recommended for HTDemucs)</summary>
        public int ChunkSizeSeconds { get; set; } = 10;

        /// <summary>Overlap factor for chunk processing (0.0-0.5, recommended 0.25)</summary>
        public float OverlapFactor { get; set; } = 0.25f;

        /// <summary>Enable GPU acceleration (CUDA/DirectML)</summary>
        public bool EnableGPU { get; set; } = true;

        /// <summary>Target stems to extract</summary>
        public HTDemucsStem TargetStems { get; set; } = HTDemucsStem.All;

        /// <summary>Target sample rate (default: 44100 Hz)</summary>
        public int TargetSampleRate { get; set; } = 44100;

        /// <summary>Model-specific segment length override (0 = auto-detect from model)</summary>
        public int SegmentLength { get; set; } = 0;

        /// <summary>
        /// Margin to trim from chunk edges in seconds (to remove model warm-up artifacts).
        /// Default: 0.5 seconds
        /// </summary>
        public float MarginSeconds { get; set; } = 0.5f;

        /// <summary>
        /// Crossfade duration for valid regions in seconds.
        /// Default: 0.05 seconds (50ms)
        /// </summary>
        public float CrossfadeSeconds { get; set; } = 0.05f;
    }

    /// <summary>
    /// HTDemucs stem types
    /// </summary>
    [Flags]
    public enum HTDemucsStem
    {
        /// <summary>Vocal stem</summary>
        Vocals = 1,

        /// <summary>Drums stem</summary>
        Drums = 2,

        /// <summary>Bass stem</summary>
        Bass = 4,

        /// <summary>Other instruments stem</summary>
        Other = 8,

        /// <summary>All stems</summary>
        All = Vocals | Drums | Bass | Other
    }

    /// <summary>
    /// Progress information for HTDemucs separation process
    /// </summary>
    public class HTDemucsSeparationProgress
    {
        /// <summary>Current file being processed</summary>
        public string CurrentFile { get; set; } = string.Empty;

        /// <summary>Overall progress percentage (0-100)</summary>
        public double OverallProgress { get; set; }

        /// <summary>Current processing step description</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>Number of chunks processed</summary>
        public int ProcessedChunks { get; set; }

        /// <summary>Total number of chunks</summary>
        public int TotalChunks { get; set; }

        /// <summary>Current stem being processed</summary>
        public HTDemucsStem? CurrentStem { get; set; }
    }

    /// <summary>
    /// Result of HTDemucs audio separation
    /// </summary>
    public class HTDemucsSeparationResult
    {
        /// <summary>Paths to separated stem files</summary>
        public Dictionary<HTDemucsStem, string> StemPaths { get; set; } = new();

        /// <summary>Processing duration</summary>
        public TimeSpan ProcessingTime { get; set; }

        /// <summary>Number of stems extracted</summary>
        public int StemCount => StemPaths.Count;

        /// <summary>Source audio duration</summary>
        public TimeSpan AudioDuration { get; set; }
    }
}
