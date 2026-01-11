namespace OwnaudioNET.Effects.SmartMaster
{
    /// <summary>
    /// Measurement process status
    /// </summary>
    public enum MeasurementStatus
    {
        Idle,                    // No measurement
        Initializing,            // Initialization
        CheckingRightChannel,    // Checking right channel
        CheckingLeftChannel,     // Checking left channel
        CheckingSubwoofer,       // Checking subwoofer
        AnalyzingSpectrum,       // Spectrum analysis
        CalculatingCorrection,   // Calculating correction
        Completed,               // Successfully completed
        Error                    // Error occurred
    }
    
    /// <summary>
    /// Measurement status information
    /// </summary>
    public class MeasurementStatusInfo
    {
        public MeasurementStatus Status { get; set; } = MeasurementStatus.Idle;
        public float Progress { get; set; } = 0.0f; // 0.0 - 1.0
        public string CurrentStep { get; set; } = "";
        public string? ErrorMessage { get; set; }
        public string[] Warnings { get; set; } = System.Array.Empty<string>();
    }
}
