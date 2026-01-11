using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Predefined chorus presets optimized for different musical and production scenarios.
    /// </summary>
    public enum ChorusPreset
    {
        /// <summary>
        /// Balanced preset suitable for general purpose use.
        /// </summary>
        Default,

        /// <summary>
        /// Subtle chorus effect optimized for vocals with minimal modulation.
        /// </summary>
        VocalSubtle,

        /// <summary>
        /// Rich, lush chorus effect for vocals with pronounced depth.
        /// </summary>
        VocalLush,

        /// <summary>
        /// Classic chorus sound for guitar with moderate modulation.
        /// </summary>
        GuitarClassic,

        /// <summary>
        /// Shimmering chorus effect for guitar with fast rate and high depth.
        /// </summary>
        GuitarShimmer,

        /// <summary>
        /// Wide, slow chorus optimized for synthesizer pads with maximum voices.
        /// </summary>
        SynthPad,

        /// <summary>
        /// String ensemble chorus effect simulating multiple instruments.
        /// </summary>
        StringEnsemble,

        /// <summary>
        /// Vintage analog chorus emulation.
        /// </summary>
        VintageAnalog,

        /// <summary>
        /// Extreme chorus effect with aggressive modulation.
        /// </summary>
        Extreme
    }

    /// <summary>
    /// High-quality chorus effect using multiple delayed voices with LFO modulation and fractional delay interpolation.
    /// Creates a rich, detuned sound by combining the original signal with multiple time-varying delayed copies.
    /// </summary>
    public sealed class ChorusEffect : IEffectProcessor
    {
        /// <summary>
        /// Unique identifier for this effect instance.
        /// </summary>
        private readonly Guid _id;

        /// <summary>
        /// User-friendly name of the effect.
        /// </summary>
        private string _name;

        /// <summary>
        /// Indicates whether the effect is currently enabled.
        /// </summary>
        private bool _enabled;

        /// <summary>
        /// Audio configuration including sample rate and channel count.
        /// </summary>
        private AudioConfig? _config;

        /// <summary>
        /// Circular delay buffer for storing audio samples (50ms capacity).
        /// </summary>
        private readonly float[] _delayBuffer;

        /// <summary>
        /// Current write position in the delay buffer.
        /// </summary>
        private int _bufferIndex;

        /// <summary>
        /// Audio sample rate in Hz.
        /// </summary>
        private float _sampleRate;

        /// <summary>
        /// Current phase of the low-frequency oscillator (LFO) in radians.
        /// </summary>
        private float _lfoPhase;

        /// <summary>
        /// LFO modulation rate in Hz (0.1 to 10.0).
        /// </summary>
        private float _rate = 1.0f;

        /// <summary>
        /// Modulation depth (0.0 to 1.0). Controls how much the delay time varies.
        /// </summary>
        private float _depth = 0.5f;

        /// <summary>
        /// Wet/dry mix ratio (0.0 = dry only, 1.0 = wet only).
        /// </summary>
        private float _mix = 0.5f;

        /// <summary>
        /// Number of chorus voices (2 to 6). More voices create a richer effect.
        /// </summary>
        private int _voices = 3;

        /// <summary>
        /// Precalculated LFO phase increment per sample for performance.
        /// </summary>
        private float _lfoIncrement;

        /// <summary>
        /// Phase offsets for each voice to create independent modulation patterns.
        /// </summary>
        private readonly float[] _voicePhases;

        /// <summary>
        /// Gets the unique identifier for this effect instance.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Gets or sets the user-friendly name of the effect.
        /// </summary>
        public string Name { get => _name; set => _name = value ?? "Chorus"; }

        /// <summary>
        /// Gets or sets whether the effect is currently enabled.
        /// </summary>
        public bool Enabled { get => _enabled; set => _enabled = value; }

        /// <summary>
        /// Gets or sets the wet/dry mix ratio. Valid range: 0.0 (dry only) to 1.0 (wet only).
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Gets or sets the LFO modulation rate in Hz. Valid range: 0.1 to 10.0 Hz.
        /// Higher rates create faster pitch variations.
        /// </summary>
        public float Rate
        {
            get => _rate;
            set
            {
                _rate = Math.Clamp(value, 0.1f, 10.0f);
                RecalculateIncrement();
            }
        }

        /// <summary>
        /// Gets or sets the modulation depth. Valid range: 0.0 to 1.0.
        /// Controls how much the delay time varies, affecting the intensity of the detuning effect.
        /// </summary>
        public float Depth
        {
            get => _depth;
            set => _depth = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Gets or sets the number of chorus voices. Valid range: 2 to 6.
        /// More voices create a richer, more complex chorus effect but increase CPU usage.
        /// </summary>
        public int Voices
        {
            get => _voices;
            set => _voices = Math.Clamp(value, 2, 6);
        }

        /// <summary>
        /// Initializes a new instance of the ChorusEffect with custom parameters.
        /// </summary>
        /// <param name="rate">LFO modulation rate in Hz (default: 1.0).</param>
        /// <param name="depth">Modulation depth 0.0 to 1.0 (default: 0.5).</param>
        /// <param name="mix">Wet/dry mix ratio 0.0 to 1.0 (default: 0.5).</param>
        /// <param name="voices">Number of chorus voices, 2 to 6 (default: 3).</param>
        /// <param name="sampleRate">Audio sample rate in Hz (default: 44100).</param>
        public ChorusEffect(float rate = 1.0f, float depth = 0.5f, float mix = 0.5f, int voices = 3, int sampleRate = 44100)
        {
            _id = Guid.NewGuid();
            _name = "Chorus";
            _enabled = true;
            _sampleRate = sampleRate;

            _rate = rate;
            _depth = depth;
            _mix = mix;
            _voices = voices;

            // 50ms buffer is usually enough for chorus
            int bufferSize = (int)(0.05f * sampleRate);
            _delayBuffer = new float[bufferSize];
            _voicePhases = new float[6];

            // Distribute phases evenly
            for (int i = 0; i < 6; i++)
                _voicePhases[i] = (float)(i * Math.PI * 2.0 / 6.0);

            RecalculateIncrement();
        }

        /// <summary>
        /// Initializes a new instance of the ChorusEffect using a predefined preset.
        /// </summary>
        /// <param name="preset">The preset configuration to use.</param>
        /// <param name="sampleRate">Audio sample rate in Hz (default: 44100).</param>
        public ChorusEffect(ChorusPreset preset, int sampleRate = 44100) : this(1, 0.5f, 0.5f, 3, sampleRate)
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Recalculates the LFO phase increment based on the current rate and sample rate.
        /// Called automatically when the Rate property changes.
        /// </summary>
        private void RecalculateIncrement()
        {
            _lfoIncrement = (float)(2.0 * Math.PI * _rate / _sampleRate);
        }

        /// <summary>
        /// Initializes the effect with the specified audio configuration.
        /// Updates the sample rate and recalculates LFO parameters if necessary.
        /// </summary>
        /// <param name="config">The audio configuration including sample rate and channel count.</param>
        /// <exception cref="ArgumentNullException">Thrown when config is null.</exception>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            if (Math.Abs(_sampleRate - config.SampleRate) > 1.0f)
            {
                _sampleRate = config.SampleRate;
                RecalculateIncrement();
            }
        }

        /// <summary>
        /// Processes the audio buffer applying the chorus effect.
        /// Uses multiple delayed voices with LFO-modulated delay times and linear interpolation
        /// to create a rich, detuned sound.
        /// </summary>
        /// <param name="buffer">The audio buffer to process (interleaved samples).</param>
        /// <param name="frameCount">The number of audio frames in the buffer.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null || !_enabled || _mix < 0.001f) return;

            int channels = _config.Channels;
            int totalSamples = frameCount * channels;
            int bufLen = _delayBuffer.Length;

            // Cache locally
            float currentDepth = _depth;
            float currentMix = _mix;
            int currentVoices = _voices;
            float lfoInc = _lfoIncrement;

            // Base delay = 15ms? Chorus typically 10-30ms
            float baseDelaySamples = 0.015f * _sampleRate;
            float modDepthSamples = 0.005f * _sampleRate * currentDepth;

            for (int i = 0; i < totalSamples; i++)
            {
                float input = buffer[i];

                // Write to delay line
                _delayBuffer[_bufferIndex] = input;

                float wetSignal = 0.0f;
                // Accumulate voices
                for (int v = 0; v < currentVoices; v++)
                {
                    // LFO Modulation
                    // Use FastSin approximation if possible, but MathF.Sin is hardware accelerated mostly
                    float phase = _lfoPhase + _voicePhases[v];
                    float lfo = MathF.Sin(phase);

                    // Modulated delay time in samples
                    float delayOffset = baseDelaySamples + (lfo * modDepthSamples);

                    // Read position (fractional)
                    float readPos = _bufferIndex - delayOffset;

                    // Wrap logic for float
                    while (readPos < 0) readPos += bufLen;
                    while (readPos >= bufLen) readPos -= bufLen;

                    // Linear Interpolation
                    // pos = intPart + frac
                    int idxA = (int)readPos;
                    int idxB = (idxA + 1);
                    if (idxB >= bufLen) idxB = 0;

                    float frac = readPos - idxA;

                    float sampleA = _delayBuffer[idxA];
                    float sampleB = _delayBuffer[idxB];

                    // Lerp
                    wetSignal += sampleA + frac * (sampleB - sampleA);
                }

                wetSignal /= currentVoices;

                // Mix
                buffer[i] = input * (1.0f - currentMix) + wetSignal * currentMix;

                // Increment state
                _bufferIndex++;
                if (_bufferIndex >= bufLen) _bufferIndex = 0;

                // Update LFO (Global)
                // For stereo, usually we might want LFO to be per channel or simply shared
                // Here we share LFO phase per sample iteration, effectively running LFO at audio rate which is fine
                _lfoPhase += lfoInc;
                if (_lfoPhase >= Math.PI * 2) _lfoPhase -= (float)(Math.PI * 2);
            }
        }

        /// <summary>
        /// Applies a predefined configuration preset to the effect.
        /// </summary>
        /// <param name="preset">The preset to apply.</param>
        public void SetPreset(ChorusPreset preset)
        {
            // Simple mapping to existing logic
            switch (preset)
            {
                case ChorusPreset.Default: Rate=1.0f; Depth=0.5f; Mix=0.5f; Voices=3; break;
                case ChorusPreset.VocalSubtle: Rate=0.3f; Depth=0.2f; Mix=0.3f; Voices=2; break;
                case ChorusPreset.VocalLush: Rate=0.8f; Depth=0.6f; Mix=0.6f; Voices=4; break;
                case ChorusPreset.GuitarClassic: Rate=0.5f; Depth=0.4f; Mix=0.5f; Voices=3; break;
                case ChorusPreset.GuitarShimmer: Rate=2.0f; Depth=0.8f; Mix=0.7f; Voices=5; break;
                case ChorusPreset.SynthPad: Rate=0.15f; Depth=0.9f; Mix=0.8f; Voices=6; break;
                default: Rate=1.0f; Depth=0.5f; Mix=0.5f; Voices=3; break;
            }
        }

        /// <summary>
        /// Resets the effect state to initial values.
        /// Clears the delay buffer and resets LFO phase and buffer index.
        /// </summary>
        public void Reset()
        {
            Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
            _bufferIndex = 0;
            _lfoPhase = 0.0f;
        }

        /// <summary>
        /// Disposes the effect and releases resources.
        /// </summary>
        public void Dispose()
        {
        }

        /// <summary>
        /// Returns a string representation of the effect's current state.
        /// </summary>
        /// <returns>A string containing rate, depth, and enabled status.</returns>
        public override string ToString() => $"Chorus: Rate={_rate:F2}, Depth={_depth:F2}, Enabled={_enabled}";
    }
}
