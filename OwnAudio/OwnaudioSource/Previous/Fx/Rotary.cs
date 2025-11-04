using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Rotary speaker presets for different musical styles and applications
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public enum RotaryPreset
    {
        /// <summary>
        /// Default preset - Classic Hammond organ sound
        /// Traditional Leslie cabinet emulation with balanced parameters
        /// </summary>
        Default,

        /// <summary>
        /// Classic Hammond organ sound - traditional Leslie cabinet emulation
        /// Medium speeds, balanced intensity for authentic organ tones
        /// </summary>
        Hammond,

        /// <summary>
        /// Gospel organ style - warm, soulful rotary movement
        /// Moderate speeds with higher intensity for expressive playing
        /// </summary>
        Gospel,

        /// <summary>
        /// Rock organ - aggressive, cutting rotary sound
        /// Higher speeds and intensity for rock and prog applications
        /// </summary>
        Rock,

        /// <summary>
        /// Jazz combo - subtle, sophisticated rotary movement
        /// Lower speeds and gentler intensity for jazz settings
        /// </summary>
        Jazz,

        /// <summary>
        /// Psychedelic - extreme modulation for atmospheric effects
        /// High speeds and maximum intensity for experimental sounds
        /// </summary>
        Psychedelic,

        /// <summary>
        /// Vintage slow - authentic slow Leslie cabinet sound
        /// Traditional slow speeds with authentic character
        /// </summary>
        VintageSlow,

        /// <summary>
        /// Vintage fast - authentic fast Leslie cabinet sound  
        /// Traditional fast speeds with classic tremolo and vibrato
        /// </summary>
        VintageFast,

        /// <summary>
        /// Subtle - gentle rotary effect for ambient applications
        /// Very low speeds and intensity for background texture
        /// </summary>
        Subtle
    }

    /// <summary>
    /// Rotary speaker simulation with horn and rotor
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public class Rotary : SampleProcessorBase
    {
        private readonly int _sampleRate;
        private readonly float[] _hornDelayBuffer;
        private readonly float[] _rotorDelayBuffer;

        private float _hornSpeed = 6.0f;
        private float _rotorSpeed = 1.0f;
        private float _intensity = 0.7f;
        private float _mix = 1.0f;
        private bool _isFast = false;

        private float _hornPhase = 0.0f;
        private float _rotorPhase = 0.0f;
        private int _hornBufferIndex = 0;
        private int _rotorBufferIndex = 0;

        // Crossover filters
        private LowPassFilter _lowPassFilter;
        private HighPassFilter _highPassFilter;

        /// <summary>
        /// Horn rotation speed in Hz (2.0 - 15.0).
        /// </summary>
        public float HornSpeed
        {
            get => _hornSpeed;
            set => _hornSpeed = Math.Clamp(value, 2.0f, 15.0f);
        }

        /// <summary>
        /// Rotor rotation speed in Hz (0.5 - 5.0).
        /// </summary>
        public float RotorSpeed
        {
            get => _rotorSpeed;
            set => _rotorSpeed = Math.Clamp(value, 0.5f, 5.0f);
        }

        /// <summary>
        /// Effect intensity (0.0 - 1.0).
        /// </summary>
        public float Intensity
        {
            get => _intensity;
            set => _intensity = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Mix between dry and wet signal (0.0 - 1.0).
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Fast/slow speed switch.
        /// </summary>
        public bool IsFast
        {
            get => _isFast;
            set => _isFast = value;
        }

        /// <summary>
        /// Initialize Rotary Processor with all parameters and default values.
        /// </summary>
        /// <param name="hornSpeed">Horn speed in Hz (2.0 - 15.0)</param>
        /// <param name="rotorSpeed">Rotor speed in Hz (0.5 - 5.0)</param>
        /// <param name="intensity">Effect intensity (0.0 - 1.0)</param>
        /// <param name="mix">Dry/wet mix (0.0 - 1.0)</param>
        /// <param name="isFast">Fast/slow speed switch</param>
        /// <param name="sampleRate">Sample rate</param>
        public Rotary(float hornSpeed = 6.0f, float rotorSpeed = 1.0f, float intensity = 0.7f, float mix = 1.0f, bool isFast = false, int sampleRate = 44100)
        {
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _sampleRate = sampleRate;
            HornSpeed = hornSpeed;
            RotorSpeed = rotorSpeed;
            Intensity = intensity;
            Mix = mix;
            IsFast = isFast;

            int maxDelay = (int)(0.01 * sampleRate); // 10ms max delay
            _hornDelayBuffer = new float[maxDelay];
            _rotorDelayBuffer = new float[maxDelay];

            _lowPassFilter = new LowPassFilter(800.0f, sampleRate);
            _highPassFilter = new HighPassFilter(800.0f, sampleRate);
        }

        /// <summary>
        /// Initialize Rotary Processor with preset selection.
        /// </summary>
        /// <param name="preset">Rotary preset to use</param>
        /// <param name="sampleRate">Sample rate</param>
        public Rotary(RotaryPreset preset, int sampleRate = 44100)
        {
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _sampleRate = sampleRate;

            // Initialize with default values first
            _hornSpeed = 6.0f;
            _rotorSpeed = 1.0f;
            _intensity = 0.7f;
            _mix = 1.0f;
            _isFast = false;

            int maxDelay = (int)(0.01 * sampleRate); // 10ms max delay
            _hornDelayBuffer = new float[maxDelay];
            _rotorDelayBuffer = new float[maxDelay];

            _lowPassFilter = new LowPassFilter(800.0f, sampleRate);
            _highPassFilter = new HighPassFilter(800.0f, sampleRate);

            // Apply the selected preset
            SetPreset(preset);
        }

        /// <summary>
        /// Set rotary parameters using predefined presets
        /// </summary>
        public void SetPreset(RotaryPreset preset)
        {
            switch (preset)
            {
                case RotaryPreset.Default:
                    // Default preset - Classic Hammond organ sound with balanced parameters
                    HornSpeed = 6.0f;      // Default horn speed
                    RotorSpeed = 1.0f;     // Default rotor speed  
                    Intensity = 0.7f;      // Default modulation depth
                    Mix = 1.0f;            // Full wet signal
                    IsFast = false;        // Start in slow mode
                    break;

                case RotaryPreset.Hammond:
                    // Classic Hammond organ Leslie cabinet sound
                    // Authentic speeds and balanced intensity for traditional organ tones
                    HornSpeed = 6.7f;      // Classic Leslie horn speed
                    RotorSpeed = 1.2f;     // Traditional rotor speed  
                    Intensity = 0.75f;     // Balanced modulation depth
                    Mix = 1.0f;            // Full wet signal for authentic sound
                    IsFast = false;        // Start in slow mode
                    break;

                case RotaryPreset.Gospel:
                    // Warm, soulful gospel organ rotary
                    // Moderate speeds with expressive modulation for emotional playing
                    HornSpeed = 7.5f;      // Slightly faster horn for expression
                    RotorSpeed = 1.4f;     // Warm rotor movement
                    Intensity = 0.85f;     // Higher intensity for expressiveness
                    Mix = 0.95f;           // Almost full wet for immersive sound
                    IsFast = false;        // Expressive slow speeds
                    break;

                case RotaryPreset.Rock:
                    // Aggressive rock organ sound
                    // Higher speeds and intensity for cutting through dense mixes
                    HornSpeed = 9.0f;      // Faster horn for aggression
                    RotorSpeed = 2.0f;     // Driving rotor speed
                    Intensity = 0.90f;     // High intensity for presence
                    Mix = 1.0f;            // Full effect for maximum impact
                    IsFast = true;         // Start in fast mode for rock energy
                    break;

                case RotaryPreset.Jazz:
                    // Subtle, sophisticated jazz combo sound
                    // Gentler speeds and lower intensity for refined musical settings
                    HornSpeed = 5.5f;      // Gentler horn movement
                    RotorSpeed = 0.9f;     // Subtle rotor speed
                    Intensity = 0.60f;     // Refined modulation depth
                    Mix = 0.85f;           // Blend with dry signal for subtlety
                    IsFast = false;        // Smooth slow speeds
                    break;

                case RotaryPreset.Psychedelic:
                    // Extreme modulation for experimental and atmospheric sounds
                    // Maximum parameters for trippy, otherworldly effects
                    HornSpeed = 12.0f;     // Very fast horn for intense modulation
                    RotorSpeed = 3.5f;     // Rapid rotor movement
                    Intensity = 1.0f;      // Maximum intensity for extreme effect
                    Mix = 1.0f;            // Full wet for maximum impact
                    IsFast = true;         // Fast mode for intensity
                    break;

                case RotaryPreset.VintageSlow:
                    // Authentic vintage Leslie slow cabinet sound
                    // Traditional slow speeds with classic character
                    HornSpeed = 6.0f;      // Classic slow horn speed
                    RotorSpeed = 1.0f;     // Traditional slow rotor
                    Intensity = 0.70f;     // Vintage modulation depth
                    Mix = 1.0f;            // Pure rotary sound
                    IsFast = false;        // Authentic slow mode
                    break;

                case RotaryPreset.VintageFast:
                    // Authentic vintage Leslie fast cabinet sound
                    // Traditional fast speeds with classic tremolo and vibrato
                    HornSpeed = 6.0f;      // Base speed (will be multiplied by fast mode)
                    RotorSpeed = 1.0f;     // Base speed (will be multiplied by fast mode)
                    Intensity = 0.75f;     // Classic fast intensity
                    Mix = 1.0f;            // Pure rotary sound
                    IsFast = true;         // Authentic fast mode (18Hz horn, 2Hz rotor)
                    break;

                case RotaryPreset.Subtle:
                    // Gentle rotary effect for ambient and background applications
                    // Very low speeds and intensity for texture without distraction
                    HornSpeed = 4.0f;      // Very gentle horn movement
                    RotorSpeed = 0.7f;     // Minimal rotor speed
                    Intensity = 0.40f;     // Low intensity for subtlety
                    Mix = 0.60f;           // Heavy blend with dry signal
                    IsFast = false;        // Slow, ambient movement
                    break;
            }
        }

        /// <summary>
        /// Process samples with rotary speaker effect.
        /// </summary>
        /// <param name="samples">Input samples</param>
        public override void Process(Span<float> samples)
        {
            float currentHornSpeed = _isFast ? HornSpeed * 3.0f : HornSpeed;
            float currentRotorSpeed = _isFast ? RotorSpeed * 2.0f : RotorSpeed;

            float hornIncrement = (float)(2.0 * Math.PI * currentHornSpeed / _sampleRate);
            float rotorIncrement = (float)(2.0 * Math.PI * currentRotorSpeed / _sampleRate);

            for (int i = 0; i < samples.Length; i++)
            {
                float input = samples[i];

                float lowFreq = _lowPassFilter.Process(input);
                float highFreq = _highPassFilter.Process(input);

                float hornLfo = (float)Math.Sin(_hornPhase);
                float hornDelay = 0.001f + (0.003f * hornLfo * Intensity);
                int hornDelaySamples = (int)(hornDelay * _sampleRate);
                hornDelaySamples = Math.Clamp(hornDelaySamples, 1, _hornDelayBuffer.Length - 1);

                int hornReadIndex = (_hornBufferIndex - hornDelaySamples + _hornDelayBuffer.Length) % _hornDelayBuffer.Length;
                float hornOutput = _hornDelayBuffer[hornReadIndex];

                hornOutput *= (0.8f + 0.2f * hornLfo * Intensity);

                _hornDelayBuffer[_hornBufferIndex] = highFreq;

                float rotorLfo = (float)Math.Sin(_rotorPhase);
                float rotorDelay = 0.002f + (0.004f * rotorLfo * Intensity);
                int rotorDelaySamples = (int)(rotorDelay * _sampleRate);
                rotorDelaySamples = Math.Clamp(rotorDelaySamples, 1, _rotorDelayBuffer.Length - 1);

                int rotorReadIndex = (_rotorBufferIndex - rotorDelaySamples + _rotorDelayBuffer.Length) % _rotorDelayBuffer.Length;
                float rotorOutput = _rotorDelayBuffer[rotorReadIndex];

                rotorOutput *= (0.9f + 0.1f * rotorLfo * Intensity);

                _rotorDelayBuffer[_rotorBufferIndex] = lowFreq;

                float processed = hornOutput + rotorOutput;

                samples[i] = (input * (1.0f - Mix)) + (processed * Mix);

                _hornBufferIndex = (_hornBufferIndex + 1) % _hornDelayBuffer.Length;
                _rotorBufferIndex = (_rotorBufferIndex + 1) % _rotorDelayBuffer.Length;

                _hornPhase += hornIncrement;
                _rotorPhase += rotorIncrement;

                if (_hornPhase >= 2.0 * Math.PI)
                    _hornPhase -= (float)(2.0 * Math.PI);
                if (_rotorPhase >= 2.0 * Math.PI)
                    _rotorPhase -= (float)(2.0 * Math.PI);
            }
        }

        /// <summary>
        /// Reset rotary effect state without changing parameters.
        /// </summary>
        public override void Reset()
        {
            Array.Clear(_hornDelayBuffer, 0, _hornDelayBuffer.Length);
            Array.Clear(_rotorDelayBuffer, 0, _rotorDelayBuffer.Length);
            _hornBufferIndex = 0;
            _rotorBufferIndex = 0;
            _hornPhase = 0.0f;
            _rotorPhase = 0.0f;
            _lowPassFilter.Reset();
            _highPassFilter.Reset();
        }

        /// <summary>
        /// Simple low-pass filter for crossover
        /// </summary>
        private class LowPassFilter
        {
            private float _cutoff;
            private float _state;

            public LowPassFilter(float cutoffFreq, int sampleRate)
            {
                _cutoff = (float)(2.0 * Math.PI * cutoffFreq / sampleRate);
                _state = 0.0f;
            }

            public float Process(float input)
            {
                _state += _cutoff * (input - _state);
                return _state;
            }

            public void Reset()
            {
                _state = 0.0f;
            }
        }

        /// <summary>
        /// Simple high-pass filter for crossover
        /// </summary>
        private class HighPassFilter
        {
            private readonly LowPassFilter _lowPass;

            public HighPassFilter(float cutoffFreq, int sampleRate)
            {
                _lowPass = new LowPassFilter(cutoffFreq, sampleRate);
            }

            public float Process(float input)
            {
                return input - _lowPass.Process(input);
            }

            public void Reset()
            {
                _lowPass.Reset();
            }
        }
    }
}
