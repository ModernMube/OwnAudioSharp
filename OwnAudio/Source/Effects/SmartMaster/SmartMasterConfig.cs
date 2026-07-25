using System;

namespace OwnaudioNET.Effects.SmartMaster
{
    /// <summary>
    /// Smart Master effect configuration class
    /// </summary>
    public class SmartMasterConfig
    {
        /// <summary>
        /// Bands of the graphic EQ: the ISO set from 20Hz to 16kHz the chain actually runs.
        /// </summary>
        public const int EqBands = 30;

        /// <summary>
        /// Channels the phase alignment stage handles - L, R and the summed sub.
        /// </summary>
        public const int AlignChannels = 3;

        /// <summary>
        /// Bands per parametric EQ branch.
        /// </summary>
        public const int ParametricBands = 10;

        private float[] _graphicEQGains = new float[EqBands];
        private float[] _timeDelays = new float[AlignChannels];
        private bool[] _phaseInvert = new bool[AlignChannels];
        private float[][] _parametricEQGains = _emptyParametric();

        /// <summary>
        /// Gets or sets the 30-band graphic EQ gains in dB. Default is 0 dB (flat).
        /// A shorter or longer array gets fitted to <see cref="EqBands"/>.
        /// </summary>
        public float[] GraphicEQGains
        {
            get => _graphicEQGains;
            set => _graphicEQGains = _fit(value, EqBands);
        }

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
        public float[] TimeDelays
        {
            get => _timeDelays;
            set => _timeDelays = _fit(value, AlignChannels);
        }

        /// <summary>
        /// Gets or sets the phase inversion flags for L, R, and Sub channels.
        /// </summary>
        public bool[] PhaseInvert
        {
            get => _phaseInvert;
            set => _phaseInvert = _fit(value, AlignChannels);
        }

        /// <summary>
        /// Gets or sets the parametric EQ gains for L, R, and Sub branches (10 bands each).
        /// Reserved - the chain does not run a parametric stage yet.
        /// </summary>
        public float[][] ParametricEQGains
        {
            get => _parametricEQGains;
            set => _parametricEQGains = _fitParametric(value);
        }

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
        /// Cuts or pads an array to the length the chain expects, so a hand edited
        /// or older preset can't silently disable a stage.
        /// </summary>
        internal static T[] _fit<T>(T[]? source, int length)
        {
            if (source is not null && source.Length == length)
                return source;

            var fitted = new T[length];
            if (source is not null) Array.Copy(source, fitted, Math.Min(source.Length, length));

            return fitted;
        }

        /// <summary>
        /// Same idea for the jagged parametric gains.
        /// </summary>
        private static float[][] _fitParametric(float[][]? source)
        {
            var fitted = new float[AlignChannels][];
            for (int i = 0; i < AlignChannels; i++)
                fitted[i] = _fit(source is not null && i < source.Length ? source[i] : null, ParametricBands);

            return fitted;
        }

        /// <summary>
        /// Flat parametric gains for a fresh config.
        /// </summary>
        private static float[][] _emptyParametric() => _fitParametric(null);
    }

    /// <summary>
    /// Storage for measurement results
    /// </summary>
    public class MeasurementResults
    {
        private float[] _channelLevels = new float[SmartMasterConfig.AlignChannels];
        private float[] _channelDelays = new float[SmartMasterConfig.AlignChannels];
        private float[] _frequencyResponse = new float[SmartMasterConfig.EqBands];
        private bool[] _channelPolarity = new bool[SmartMasterConfig.AlignChannels];

        /// <summary>
        /// Gets or sets the date and time when the measurement was performed.
        /// </summary>
        public DateTime MeasurementDate { get; set; }

        /// <summary>
        /// Gets or sets the measured channel levels in dB for L, R, and Sub channels.
        /// </summary>
        public float[] ChannelLevels
        {
            get => _channelLevels;
            set => _channelLevels = SmartMasterConfig._fit(value, SmartMasterConfig.AlignChannels);
        }

        /// <summary>
        /// Gets or sets the measured channel delays in milliseconds for L, R, and Sub channels.
        /// </summary>
        public float[] ChannelDelays
        {
            get => _channelDelays;
            set => _channelDelays = SmartMasterConfig._fit(value, SmartMasterConfig.AlignChannels);
        }

        /// <summary>
        /// Gets or sets the measured frequency response deviations in dB, one per EQ band.
        /// </summary>
        public float[] FrequencyResponse
        {
            get => _frequencyResponse;
            set => _frequencyResponse = SmartMasterConfig._fit(value, SmartMasterConfig.EqBands);
        }

        /// <summary>
        /// Gets or sets the channel polarity flags for L, R, and Sub channels (true = inverted).
        /// </summary>
        public bool[] ChannelPolarity
        {
            get => _channelPolarity;
            set => _channelPolarity = SmartMasterConfig._fit(value, SmartMasterConfig.AlignChannels);
        }

        /// <summary>
        /// Gets or sets the warning messages generated during measurement.
        /// </summary>
        public string[] Warnings { get; set; } = Array.Empty<string>();
    }
}
