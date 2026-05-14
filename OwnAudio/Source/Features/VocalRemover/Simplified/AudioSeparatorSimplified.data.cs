namespace OwnaudioNET.Features.Vocalremover
{
    /// <summary>
    /// Configuration parameters for audio separation process
    /// </summary>
    public class SimpleSeparationOptions
    {
        /// <summary>ONNX model file path</summary>
        public string? ModelPath { get; set; }

        /// <summary>Gets or sets the type of separation model used in the operation.</summary>
        public InternalModel Model { get; set; } = InternalModel.Best;

        /// <summary>Output directory path</summary>
        public string OutputDirectory { get; set; } = "separated";

        /// <summary>Enable GPU acceleration (CoreML on macOS, CUDA on other platforms)</summary>
        public bool EnableGPU { get; set; } = true;

        /// <summary>Disable noise reduction (enabled by default)</summary>
        public bool DisableNoiseReduction { get; set; } = false;

        /// <summary>Margin size for overlapping chunks (in samples)</summary>
        public int Margin { get; set; } = 44100;

        /// <summary>Chunk size in seconds (0 = process entire file at once)</summary>
        public int ChunkSizeSeconds { get; set; } = 15;

        /// <summary>FFT size</summary>
        public int NFft { get; set; } = 6144;

        /// <summary>Temporal dimension parameter (as power of 2)</summary>
        public int DimT { get; set; } = 8;

        /// <summary>Frequency dimension parameter</summary>
        public int DimF { get; set; } = 2048;
    }

    /// <summary>
    /// Progress information for separation process
    /// </summary>
    public class SimpleSeparationProgress
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
    }

    /// <summary>
    /// Result of audio separation
    /// </summary>
    public class SimpleSeparationResult
    {
        /// <summary>Path to the vocals output file</summary>
        public string VocalsPath { get; set; } = string.Empty;

        /// <summary>Path to the instrumental output file</summary>
        public string InstrumentalPath { get; set; } = string.Empty;

        /// <summary>Processing duration</summary>
        public TimeSpan ProcessingTime { get; set; }
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
        /// <summary>Number of channels (always 4 for complex stereo)</summary>
        public int DimC { get; } = 4;

        /// <summary>Frequency dimension size</summary>
        public int DimF { get; set; }

        /// <summary>Time dimension size (calculated as power of 2)</summary>
        public int DimT { get; set; }

        /// <summary>FFT size</summary>
        public int NFft { get; }

        /// <summary>Hop size for STFT</summary>
        public int Hop { get; }

        /// <summary>Number of frequency bins (NFft/2 + 1)</summary>
        public int NBins { get; }

        /// <summary>Processing chunk size in samples</summary>
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
}
