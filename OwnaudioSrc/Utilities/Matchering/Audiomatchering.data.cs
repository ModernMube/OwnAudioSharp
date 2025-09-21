

namespace Ownaudio.Utilities.Matchering
{
    #region Data Classes

    /// <summary>
    /// Contains audio spectrum analysis results
    /// </summary>
    public class AudioSpectrum
    {
        /// <summary>
        /// Frequency band energy levels
        /// </summary>
        public float[] FrequencyBands { get; set; } = new float[30];

        /// <summary>
        /// RMS level of the audio
        /// </summary>
        public float RMSLevel { get; set; }

        /// <summary>
        /// Peak level of the audio
        /// </summary>
        public float PeakLevel { get; set; }

        /// <summary>
        /// Dynamic range in dB
        /// </summary>
        public float DynamicRange { get; set; }

        /// <summary>
        /// Perceived loudness in LUFS
        /// </summary>
        public float Loudness { get; set; }
    }

    /// <summary>
    /// Contains audio dynamics analysis information
    /// </summary>
    public class DynamicsInfo
    {
        /// <summary>
        /// RMS (Root Mean Square) level
        /// </summary>
        public float RMS { get; set; }

        /// <summary>
        /// Peak level
        /// </summary>
        public float Peak { get; set; }

        /// <summary>
        /// Dynamic range in dB
        /// </summary>
        public float DynamicRange { get; set; }

        /// <summary>
        /// Loudness in LUFS
        /// </summary>
        public float Loudness { get; set; }
    }

    /// <summary>
    /// Compression settings for audio processing
    /// </summary>
    public class CompressionSettings
    {
        /// <summary>
        /// Compression threshold in dB
        /// </summary>
        public float Threshold { get; set; }

        /// <summary>
        /// Compression ratio
        /// </summary>
        public float Ratio { get; set; }

        /// <summary>
        /// Attack time in milliseconds
        /// </summary>
        public float AttackTime { get; set; }

        /// <summary>
        /// Release time in milliseconds
        /// </summary>
        public float ReleaseTime { get; set; }

        /// <summary>
        /// Makeup gain in dB
        /// </summary>
        public float MakeupGain { get; set; }
    }

    /// <summary>
    /// Dynamic amplification settings
    /// </summary>
    public class DynamicAmpSettings
    {
        /// <summary>
        /// Target level in dB
        /// </summary>
        public float TargetLevel { get; set; }

        /// <summary>
        /// Attack time in seconds
        /// </summary>
        public float AttackTime { get; set; }

        /// <summary>
        /// Release time in seconds
        /// </summary>
        public float ReleaseTime { get; set; }

        /// <summary>
        /// Maximum gain in dB
        /// </summary>
        public float MaxGain { get; set; }
    }

    /// <summary>
    /// Represents an audio segment with associated metadata for analysis.
    /// Contains the audio data along with timing and energy information.
    /// </summary>
    public class AudioSegment
    {
        /// <summary>
        /// Gets or sets the audio data for this segment.
        /// </summary>
        public float[] Data { get; set; }

        /// <summary>
        /// Gets or sets the start time of this segment in seconds.
        /// </summary>
        public float StartTime { get; set; }

        /// <summary>
        /// Gets or sets the duration of this segment in seconds.
        /// </summary>
        public float Duration { get; set; }

        /// <summary>
        /// Gets or sets the energy level of this segment in dBFS.
        /// </summary>
        public float EnergyLevel { get; set; }

        /// <summary>
        /// Gets or sets the sample rate for this segment in Hz.
        /// </summary>
        public int SampleRate { get; set; }
    }

    /// <summary>
    /// Contains the analysis results for a single audio segment.
    /// Includes frequency spectrum, dynamics, and weighting information.
    /// </summary>
    public class SegmentAnalysis
    {
        /// <summary>
        /// Gets or sets the index of this segment in the original sequence.
        /// </summary>
        public int SegmentIndex { get; set; }

        /// <summary>
        /// Gets or sets the start time of this segment in seconds.
        /// </summary>
        public float StartTime { get; set; }

        /// <summary>
        /// Gets or sets the duration of this segment in seconds.
        /// </summary>
        public float Duration { get; set; }

        /// <summary>
        /// Gets or sets the energy level of this segment in dBFS.
        /// </summary>
        public float EnergyLevel { get; set; }

        /// <summary>
        /// Gets or sets the frequency spectrum analysis for this segment.
        /// </summary>
        public float[] FrequencySpectrum { get; set; }

        /// <summary>
        /// Gets or sets the dynamic analysis information for this segment.
        /// </summary>
        public DynamicsInfo Dynamics { get; set; }

        /// <summary>
        /// Gets or sets the calculated weight for this segment in final averaging.
        /// </summary>
        public float Weight { get; set; }

        /// <summary>
        /// Gets or sets the outlier score for this segment (used for filtering).
        /// </summary>
        public float OutlierScore { get; set; }
    }

    /// <summary>
    /// Configuration parameters for segmented audio analysis approach.
    /// Defines how audio files are divided into segments for more accurate analysis.
    /// </summary>
    public class SegmentedAnalysisConfig
    {
        /// <summary>
        /// Gets or sets the length of each audio segment in seconds.
        /// Default value is 10.0 seconds.
        /// </summary>
        public float SegmentLengthSeconds { get; set; } = 10.0f;

        /// <summary>
        /// Gets or sets the overlap ratio between consecutive segments.
        /// Default value is 0.2 (20% overlap).
        /// </summary>
        public float OverlapRatio { get; set; } = 0.2f;

        /// <summary>
        /// Gets or sets the threshold for outlier detection in standard deviations.
        /// Segments exceeding this threshold will be considered outliers.
        /// Default value is 2.5 standard deviations.
        /// </summary>
        public float OutlierThreshold { get; set; } = 2.5f;

        /// <summary>
        /// Gets or sets whether to use weighted averaging for final spectrum calculation.
        /// Default value is true.
        /// </summary>
        public bool UseWeightedAveraging { get; set; } = true;

        /// <summary>
        /// Gets or sets the minimum energy threshold in dBFS for segment inclusion.
        /// Segments quieter than this threshold will be skipped.
        /// Default value is -60.0 dBFS.
        /// </summary>
        public float MinSegmentEnergyThreshold { get; set; } = -60.0f;
    }

    #endregion
}
