

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
        public float[] FrequencyBands { get; set; } = new float[10];

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

    #endregion
}
