

namespace OwnaudioNET.Features.Matchering
{
    #region Data Classes

    /// <summary>
    /// Spectrum analysis result of one track.
    /// </summary>
    public class AudioSpectrum
    {
        /// <summary>
        /// Energy per ISO band, 30 of them.
        /// </summary>
        public float[] FrequencyBands { get; set; } = new float[30];

        /// <summary>
        /// RMS level, linear.
        /// </summary>
        public float RMSLevel { get; set; }

        /// <summary>
        /// Peak level, linear.
        /// </summary>
        public float PeakLevel { get; set; }

        /// <summary>
        /// Peak to RMS in dB.
        /// </summary>
        public float DynamicRange { get; set; }

        /// <summary>
        /// Loudness in dBFS.
        /// </summary>
        public float Loudness { get; set; }
    }

    /// <summary>
    /// Level/dynamics readings of a buffer.
    /// </summary>
    public class DynamicsInfo
    {
        /// <summary>
        /// RMS level.
        /// </summary>
        public float RMS { get; set; }

        /// <summary>
        /// Peak level.
        /// </summary>
        public float Peak { get; set; }

        /// <summary>
        /// Crest factor in dB.
        /// </summary>
        public float DynamicRange { get; set; }

        /// <summary>
        /// Loudness in dBFS.
        /// </summary>
        public float Loudness { get; set; }
    }

    /// <summary>
    /// Compressor knobs of a preset.
    /// </summary>
    public class CompressionSettings
    {
        /// <summary>
        /// Threshold in dB.
        /// </summary>
        public float Threshold { get; set; }

        /// <summary>
        /// Ratio, x:1.
        /// </summary>
        public float Ratio { get; set; }

        /// <summary>
        /// Attack in ms.
        /// </summary>
        public float AttackTime { get; set; }

        /// <summary>
        /// Release in ms.
        /// </summary>
        public float ReleaseTime { get; set; }

        /// <summary>
        /// Makeup gain in dB.
        /// </summary>
        public float MakeupGain { get; set; }
    }

    /// <summary>
    /// AGC settings.
    /// </summary>
    public class DynamicAmpSettings
    {
        /// <summary>
        /// Level the AGC is chasing, dB.
        /// </summary>
        public float TargetLevel { get; set; }

        /// <summary>
        /// Attack in seconds.
        /// </summary>
        public float AttackTime { get; set; }

        /// <summary>
        /// Release in seconds.
        /// </summary>
        public float ReleaseTime { get; set; }

        /// <summary>
        /// Gain ceiling in dB.
        /// </summary>
        public float MaxGain { get; set; }
    }

    /// <summary>
    /// One slice of audio plus the metadata the analyzer needs.
    /// </summary>
    public class AudioSegment
    {
        /// <summary>
        /// The samples of this slice.
        /// </summary>
        public float[] Data { get; set; } = null!;

        /// <summary>
        /// Offset from the start of the track, seconds.
        /// </summary>
        public float StartTime { get; set; }

        /// <summary>
        /// Length in seconds.
        /// </summary>
        public float Duration { get; set; }

        /// <summary>
        /// Energy in dBFS, used by the quiet-segment gate.
        /// </summary>
        public float EnergyLevel { get; set; }

        /// <summary>
        /// Sample rate in Hz.
        /// </summary>
        public int SampleRate { get; set; }
    }

    /// <summary>
    /// What came out of analyzing a single segment.
    /// </summary>
    public class SegmentAnalysis
    {
        /// <summary>
        /// Index in the original segment list.
        /// </summary>
        public int SegmentIndex { get; set; }

        /// <summary>
        /// Offset from the start of the track, seconds.
        /// </summary>
        public float StartTime { get; set; }

        /// <summary>
        /// Length in seconds.
        /// </summary>
        public float Duration { get; set; }

        /// <summary>
        /// Energy in dBFS.
        /// </summary>
        public float EnergyLevel { get; set; }

        /// <summary>
        /// Per band energy of this segment.
        /// </summary>
        public float[] FrequencySpectrum { get; set; } = null!;

        /// <summary>
        /// Level readings of this segment.
        /// </summary>
        public DynamicsInfo Dynamics { get; set; } = null!;

        /// <summary>
        /// How much this segment counts in the final average.
        /// </summary>
        public float Weight { get; set; }

        /// <summary>
        /// How many bands this segment is an outlier in.
        /// </summary>
        public float OutlierScore { get; set; }
    }

    /// <summary>
    /// Knobs for the segmented analysis.
    /// </summary>
    public class SegmentedAnalysisConfig
    {
        /// <summary>
        /// Segment length in seconds.
        /// </summary>
        public float SegmentLengthSeconds { get; set; } = 10.0f;

        /// <summary>
        /// Overlap between consecutive segments, 0.2 = 20%.
        /// </summary>
        public float OverlapRatio { get; set; } = 0.2f;

        /// <summary>
        /// Outlier cutoff in standard deviations.
        /// </summary>
        public float OutlierThreshold { get; set; } = 2.5f;

        /// <summary>
        /// Weighted averaging switch.
        /// </summary>
        public bool UseWeightedAveraging { get; set; } = true;

        /// <summary>
        /// Segments quieter than this dBFS get skipped.
        /// </summary>
        public float MinSegmentEnergyThreshold { get; set; } = -60.0f;
    }

    #endregion
}
