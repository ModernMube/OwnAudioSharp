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
        /// Bands in the input parametric EQ, same count a PA2 gives you.
        /// </summary>
        public const int ParametricBands = 8;

        private float[] _graphicEQGains = new float[EqBands];
        private float[] _timeDelays = new float[AlignChannels];
        private bool[] _phaseInvert = new bool[AlignChannels];
        private float[] _outputGains = new float[AlignChannels];
        private ParametricBand[] _parametricEQ = ParametricBand.CreateDefaults();

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
        /// Subsonic high-pass on the input. Off by default so an existing preset
        /// sounds the way it did.
        /// </summary>
        public bool SubsonicEnabled { get; set; } = false;

        /// <summary>
        /// Corner of the subsonic high-pass in Hz, 24 dB/oct.
        /// </summary>
        public float SubsonicFrequency { get; set; } = 35.0f;

        /// <summary>
        /// Gets or sets whether the subharmonic synthesizer is enabled.
        /// </summary>
        public bool SubharmonicEnabled { get; set; } = false;

        /// <summary>
        /// How much synthesized sub gets added on top of the dry signal, 0 to 1.
        /// This is a parallel level now, not a dry/wet crossfade.
        /// </summary>
        public float SubharmonicMix { get; set; } = 0.0f;

        /// <summary>
        /// Level of the 24-36Hz divider band.
        /// </summary>
        public float SubharmonicLowLevel { get; set; } = 1.0f;

        /// <summary>
        /// Level of the 36-56Hz divider band.
        /// </summary>
        public float SubharmonicHighLevel { get; set; } = 1.0f;

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
        /// Soft knee width in dB. dbx would call this OverEasy.
        /// </summary>
        public float CompressorKnee { get; set; } = 6.0f;

        /// <summary>
        /// Runs the crossover section even without alignment delays, so the
        /// per-band trims and limiters come into play.
        /// </summary>
        public bool CrossoverEnabled { get; set; } = false;

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
        /// Per-band output trim in dB, main L / main R / sub. Only bites while the
        /// crossover section runs.
        /// </summary>
        public float[] OutputGains
        {
            get => _outputGains;
            set => _outputGains = _fit(value, AlignChannels);
        }

        /// <summary>
        /// Input parametric EQ, eight sweepable bands.
        /// </summary>
        public ParametricBand[] ParametricEQ
        {
            get => _parametricEQ;
            set => _parametricEQ = ParametricBand.Fit(value);
        }

        /// <summary>
        /// Driver protection limiter on the main band, dBFS. 0 leaves it open.
        /// </summary>
        public float MainLimiterThreshold { get; set; } = 0.0f;

        /// <summary>
        /// Driver protection limiter on the sub band, dBFS. 0 leaves it open.
        /// </summary>
        public float SubLimiterThreshold { get; set; } = 0.0f;

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
    }

    /// <summary>
    /// Shape a parametric band takes.
    /// </summary>
    public enum ParametricShape
    {
        /// <summary>
        /// Peaking bell.
        /// </summary>
        Bell = 0,

        /// <summary>
        /// Low shelf.
        /// </summary>
        LowShelf = 1,

        /// <summary>
        /// High shelf.
        /// </summary>
        HighShelf = 2
    }

    /// <summary>
    /// One sweepable band of the input parametric EQ. A band at 0 dB is skipped.
    /// </summary>
    public class ParametricBand
    {
        /// <summary>
        /// Where the bands sit on a fresh config, so a UI has sane starting points.
        /// </summary>
        private static readonly float[] DefaultFrequencies =
        {
            60f, 120f, 250f, 500f, 1000f, 2500f, 6000f, 12000f
        };

        /// <summary>
        /// Bell, low shelf or high shelf.
        /// </summary>
        public ParametricShape Shape { get; set; } = ParametricShape.Bell;

        /// <summary>
        /// Centre (or corner) frequency in Hz.
        /// </summary>
        public float Frequency { get; set; } = 1000.0f;

        /// <summary>
        /// Q, 0.1 to 16.
        /// </summary>
        public float Q { get; set; } = 1.4f;

        /// <summary>
        /// Gain in dB, +/-20.
        /// </summary>
        public float GainDb { get; set; } = 0.0f;

        /// <summary>
        /// Flat set of bands spread over the spectrum.
        /// </summary>
        public static ParametricBand[] CreateDefaults()
        {
            var bands = new ParametricBand[SmartMasterConfig.ParametricBands];
            for (int i = 0; i < bands.Length; i++)
                bands[i] = new ParametricBand { Frequency = DefaultFrequencies[i] };

            return bands;
        }

        /// <summary>
        /// Cuts or pads a hand edited array to the length the chain expects.
        /// </summary>
        public static ParametricBand[] Fit(ParametricBand[]? source)
        {
            if (source is not null && source.Length == SmartMasterConfig.ParametricBands)
                return source;

            var fitted = CreateDefaults();
            if (source is not null)
            {
                int count = Math.Min(source.Length, fitted.Length);
                for (int i = 0; i < count; i++)
                    if (source[i] is not null) fitted[i] = source[i];
            }

            return fitted;
        }
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
