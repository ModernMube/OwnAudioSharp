using System;

namespace OwnaudioNET.Effects.SmartMaster
{
    /// <summary>
    /// Smart Master effect configuration class
    /// </summary>
    public class SmartMasterConfig
    {
        /// <summary>
        /// Gets or sets the 31-band graphic EQ gains in dB. Default is 0 dB (flat).
        /// </summary>
        public float[] GraphicEQGains { get; set; } = new float[31];
        
        /// <summary>
        /// Gets or sets whether the subharmonic synthesizer is enabled.
        /// </summary>
        public bool SubharmonicEnabled { get; set; } = false;
        
        /// <summary>
        /// Gets or sets the subharmonic mix level (0.0 = dry, 1.0 = full effect).
        /// </summary>
        public float SubharmonicMix { get; set; } = 0.0f;
        
        /// <summary>
        /// Gets or sets the maximum frequency for subharmonic generation in Hz.
        /// </summary>
        public float SubharmonicFreqRange { get; set; } = 60.0f;
        
        /// <summary>
        /// Gets or sets whether the compressor is enabled.
        /// </summary>
        public bool CompressorEnabled { get; set; } = false;
        
        /// <summary>
        /// Gets or sets the compressor threshold (0.0 - 1.0 linear).
        /// </summary>
        public float CompressorThreshold { get; set; } = 0.5f;
        
        /// <summary>
        /// Gets or sets the compressor ratio (e.g., 4.0 = 4:1 compression).
        /// </summary>
        public float CompressorRatio { get; set; } = 4.0f;
        
        /// <summary>
        /// Gets or sets the compressor attack time in milliseconds.
        /// </summary>
        public float CompressorAttack { get; set; } = 10.0f;
        
        /// <summary>
        /// Gets or sets the compressor release time in milliseconds.
        /// </summary>
        public float CompressorRelease { get; set; } = 100.0f;
        
        /// <summary>
        /// Gets or sets the crossover frequency in Hz for splitting high/low frequencies.
        /// </summary>
        public float CrossoverFrequency { get; set; } = 80.0f;
        
        /// <summary>
        /// Gets or sets the time delays in milliseconds for L, R, and Sub channels.
        /// </summary>
        public float[] TimeDelays { get; set; } = new float[3];
        
        /// <summary>
        /// Gets or sets the phase inversion flags for L, R, and Sub channels.
        /// </summary>
        public bool[] PhaseInvert { get; set; } = new bool[3];
        
        /// <summary>
        /// Gets or sets the parametric EQ gains for L, R, and Sub branches (10 bands each).
        /// </summary>
        public float[][] ParametricEQGains { get; set; } = new float[3][];
        
        /// <summary>
        /// Gets or sets the limiter threshold in dBFS.
        /// </summary>
        public float LimiterThreshold { get; set; } = -0.1f;
        
        /// <summary>
        /// Gets or sets the limiter ceiling in dBFS.
        /// </summary>
        public float LimiterCeiling { get; set; } = -0.1f;
        
        /// <summary>
        /// Gets or sets the limiter release time in milliseconds.
        /// </summary>
        public float LimiterRelease { get; set; } = 50.0f;
        
        /// <summary>
        /// Gets or sets the microphone input gain (0.0 - 2.0, where 1.0 = unity gain).
        /// </summary>
        public float MicInputGain { get; set; } = 1.0f;
        
        /// <summary>
        /// Gets or sets the last measurement results, if available.
        /// </summary>
        public MeasurementResults? LastMeasurement { get; set; }
        
        /// <summary>
        /// Initializes a new instance of the <see cref="SmartMasterConfig"/> class.
        /// </summary>
        public SmartMasterConfig()
        {
            for (int i = 0; i < 3; i++)
            {
                ParametricEQGains[i] = new float[10];
            }
        }
    }
    
    /// <summary>
    /// Storage for measurement results
    /// </summary>
    public class MeasurementResults
    {
        /// <summary>
        /// Gets or sets the date and time when the measurement was performed.
        /// </summary>
        public DateTime MeasurementDate { get; set; }
        
        /// <summary>
        /// Gets or sets the measured channel levels in dB for L, R, and Sub channels.
        /// </summary>
        public float[] ChannelLevels { get; set; } = new float[3];
        
        /// <summary>
        /// Gets or sets the measured channel delays in milliseconds for L, R, and Sub channels.
        /// </summary>
        public float[] ChannelDelays { get; set; } = new float[3];
        
        /// <summary>
        /// Gets or sets the measured frequency response deviations in dB for 31 bands.
        /// </summary>
        public float[] FrequencyResponse { get; set; } = new float[31];
        
        /// <summary>
        /// Gets or sets the channel polarity flags for L, R, and Sub channels (true = inverted).
        /// </summary>
        public bool[] ChannelPolarity { get; set; } = new bool[3];
        
        /// <summary>
        /// Gets or sets the warning messages generated during measurement.
        /// </summary>
        public string[] Warnings { get; set; } = Array.Empty<string>();
    }
}
