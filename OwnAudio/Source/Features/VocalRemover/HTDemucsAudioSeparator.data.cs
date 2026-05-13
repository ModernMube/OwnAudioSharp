namespace OwnaudioNET.Features.Vocalremover
{
    public class HTDemucsSeparationOptions
    {
        public string? ModelPath { get; set; }
        public InternalModel Model { get; set; } = InternalModel.None;
        public string OutputDirectory { get; set; } = "separated_htdemucs";
        public int ChunkSizeSeconds { get; set; } = 10;
        public float OverlapFactor { get; set; } = 0.25f;
        public bool EnableGPU { get; set; } = true;
        public HTDemucsStem TargetStems { get; set; } = HTDemucsStem.All;
        public int TargetSampleRate { get; set; } = 44100;
        public int SegmentLength { get; set; } = 0;
        public float MarginSeconds { get; set; } = 0.5f;
        public float CrossfadeSeconds { get; set; } = 0.05f;
    }

    [Flags]
    public enum HTDemucsStem
    {
        Vocals = 1,
        Drums  = 2,
        Bass   = 4,
        Other  = 8,
        All    = Vocals | Drums | Bass | Other
    }

    public class HTDemucsSeparationProgress
    {
        public string CurrentFile { get; set; } = string.Empty;
        public double OverallProgress { get; set; }
        public string Status { get; set; } = string.Empty;
        public int ProcessedChunks { get; set; }
        public int TotalChunks { get; set; }
        public HTDemucsStem? CurrentStem { get; set; }
    }

    public class HTDemucsSeparationResult
    {
        public Dictionary<HTDemucsStem, string> StemPaths { get; set; } = new();
        public TimeSpan ProcessingTime { get; set; }
        public int StemCount => StemPaths.Count;
        public TimeSpan AudioDuration { get; set; }
    }

    public enum InternalModel
    {
        None,
        Default,
        Best,
        Karaoke,
        HTDemucs
    }

    public enum ModelOutputType
    {
        Instrumental,
        Vocals
    }

    public class MultiModelInfo
    {
        public string Name { get; set; } = "Model";
        public string? ModelPath { get; set; }
        public InternalModel Model { get; set; } = InternalModel.None;
        public int NFft { get; set; } = 6144;
        public int DimT { get; set; } = 8;
        public int DimF { get; set; } = 2048;
        public bool DisableNoiseReduction { get; set; } = false;
        public bool SaveIntermediateOutput { get; set; } = false;
        public ModelOutputType? OutputType { get; set; } = null;
    }

    public class MultiModelSeparationOptions
    {
        public List<MultiModelInfo> Models { get; set; } = new();
        public string OutputDirectory { get; set; } = "separated_multimodel";
        public bool EnableGPU { get; set; } = true;
        public int Margin { get; set; } = 44100;
        public int ChunkSizeSeconds { get; set; } = 15;
        public bool SaveAllIntermediateResults { get; set; } = false;
    }

    public class MultiModelSeparationProgress
    {
        public string CurrentFile { get; set; } = string.Empty;
        public double OverallProgress { get; set; }
        public string Status { get; set; } = string.Empty;
        public int CurrentModelIndex { get; set; }
        public int TotalModels { get; set; }
        public string CurrentModelName { get; set; } = string.Empty;
        public int ProcessedChunks { get; set; }
        public int TotalChunks { get; set; }
    }

    public class MultiModelSeparationResult
    {
        public string OutputPath { get; set; } = string.Empty;
        public string VocalsPath { get; set; } = string.Empty;
        public string InstrumentalPath { get; set; } = string.Empty;
        public Dictionary<string, string> IntermediatePaths { get; set; } = new();
        public TimeSpan ProcessingTime { get; set; }
        public int ModelsProcessed { get; set; }
    }

    public class SimpleSeparationOptions
    {
        public string? ModelPath { get; set; }
        public InternalModel Model { get; set; } = InternalModel.Best;
        public string OutputDirectory { get; set; } = "separated";
        public bool EnableGPU { get; set; } = true;
        public bool DisableNoiseReduction { get; set; } = false;
        public int Margin { get; set; } = 44100;
        public int ChunkSizeSeconds { get; set; } = 15;
        public int NFft { get; set; } = 6144;
        public int DimT { get; set; } = 8;
        public int DimF { get; set; } = 2048;
    }

    public class SimpleSeparationProgress
    {
        public string CurrentFile { get; set; } = string.Empty;
        public double OverallProgress { get; set; }
        public string Status { get; set; } = string.Empty;
        public int ProcessedChunks { get; set; }
        public int TotalChunks { get; set; }
    }

    public class SimpleSeparationResult
    {
        public string VocalsPath { get; set; } = string.Empty;
        public string InstrumentalPath { get; set; } = string.Empty;
        public TimeSpan ProcessingTime { get; set; }
    }
}
