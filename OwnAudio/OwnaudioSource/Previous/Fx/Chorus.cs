using Ownaudio.Processors;
using System;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Chorus presets for different musical and production scenarios
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public enum ChorusPreset
    {
        /// <summary>
        /// Default chorus settings - balanced parameters for general use
        /// </summary>
        Default,

        /// <summary>
        /// Subtle vocal doubling - gentle thickening for lead vocals
        /// Slow modulation, shallow depth, moderate mix for natural enhancement
        /// </summary>
        VocalSubtle,

        /// <summary>
        /// Lush vocal chorus - rich, wide vocal texture
        /// Medium speed, deep modulation, high mix for ethereal vocal sounds
        /// </summary>
        VocalLush,

        /// <summary>
        /// Classic guitar chorus - warm, swirling guitar tone
        /// Medium-slow rate, moderate depth, balanced mix for classic rock/pop sound
        /// </summary>
        GuitarClassic,

        /// <summary>
        /// Shimmer guitar - bright, shimmering lead guitar effect
        /// Fast modulation, deep depth, high mix for ambient and shoegaze styles
        /// </summary>
        GuitarShimmer,

        /// <summary>
        /// Synth pad enhancement - wide, evolving synthesizer textures
        /// Very slow rate, deep modulation, many voices for ambient soundscapes
        /// </summary>
        SynthPad,

        /// <summary>
        /// String ensemble - orchestral string section simulation
        /// Slow rate, moderate depth, multiple voices for realistic ensemble feel
        /// </summary>
        StringEnsemble,

        /// <summary>
        /// Vintage analog - emulates classic analog chorus pedals
        /// Medium settings with characteristic analog warmth and movement
        /// </summary>
        VintageAnalog,

        /// <summary>
        /// Extreme modulation - intense, dramatic chorus effect
        /// Fast rate, maximum depth, high mix for special effects and transitions
        /// </summary>
        Extreme
    }

    /// <summary>
    /// Chorus effect with multiple delayed voices
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public class Chorus : SampleProcessorBase
    {
        private readonly float[] _delayBuffer;
        private readonly int _sampleRate;
        private int _bufferIndex;

        private float _rate = 1.0f;
        private float _depth = 0.5f;
        private float _mix = 0.5f;
        private int _voices = 3;

        private float _lfoPhase = 0.0f;
        private readonly float[] _voicePhases;

        /// <summary>
        /// LFO rate in Hz (0.1 - 10.0).
        /// </summary>
        public float Rate
        {
            get => _rate;
            set => _rate = Math.Clamp(value, 0.1f, 10.0f);
        }

        /// <summary>
        /// Modulation depth (0.0 - 1.0).
        /// </summary>
        public float Depth
        {
            get => _depth;
            set => _depth = Math.Clamp(value, 0.0f, 1.0f);
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
        /// Number of chorus voices (2 - 6).
        /// </summary>
        public int Voices
        {
            get => _voices;
            set => _voices = Math.Clamp(value, 2, 6);
        }

        /// <summary>
        /// Initialize Chorus Processor with default settings.
        /// </summary>
        public Chorus() : this(1.0f, 0.5f, 0.5f, 3, 44100)
        {
        }

        /// <summary>
        /// Initialize Chorus Processor with preset.
        /// </summary>
        /// <param name="preset">Chorus preset to use</param>
        /// <param name="sampleRate">Sample rate</param>
        public Chorus(ChorusPreset preset, int sampleRate = 44100)
        {
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _sampleRate = sampleRate;

            // Initialize with default values first
            _rate = 1.0f;
            _depth = 0.5f;
            _mix = 0.5f;
            _voices = 3;

            int maxDelaySamples = (int)(0.05 * sampleRate);
            _delayBuffer = new float[maxDelaySamples];
            _bufferIndex = 0;

            _voicePhases = new float[6]; // Max voices
            for (int i = 0; i < _voicePhases.Length; i++)
            {
                _voicePhases[i] = (float)(i * 2.0 * Math.PI / 6.0);
            }

            // Apply preset
            SetPreset(preset);
        }

        /// <summary>
        /// Initialize Chorus Processor with custom parameters.
        /// </summary>
        /// <param name="rate">LFO rate in Hz (0.1 - 10.0)</param>
        /// <param name="depth">Modulation depth (0.0 - 1.0)</param>
        /// <param name="mix">Dry/wet mix (0.0 - 1.0)</param>
        /// <param name="voices">Number of voices (2 - 6)</param>
        /// <param name="sampleRate">Sample rate</param>
        public Chorus(float rate, float depth, float mix, int voices, int sampleRate)
        {
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _sampleRate = sampleRate;
            Rate = rate;
            Depth = depth;
            Mix = mix;
            Voices = voices;

            int maxDelaySamples = (int)(0.05 * sampleRate);
            _delayBuffer = new float[maxDelaySamples];
            _bufferIndex = 0;

            _voicePhases = new float[6]; // Max voices
            for (int i = 0; i < _voicePhases.Length; i++)
            {
                _voicePhases[i] = (float)(i * 2.0 * Math.PI / 6.0);
            }
        }

        /// <summary>
        /// Set chorus parameters using predefined presets
        /// </summary>
        /// <param name="preset">Chorus preset to apply</param>
        public void SetPreset(ChorusPreset preset)
        {
            switch (preset)
            {
                case ChorusPreset.Default:
                    // Default balanced settings for general use
                    Rate = 1.0f;      // 1.0 Hz - moderate speed
                    Depth = 0.5f;     // 50% - balanced modulation
                    Mix = 0.5f;       // 50% - equal dry/wet balance
                    Voices = 3;       // 3 voices - good complexity
                    break;

                case ChorusPreset.VocalSubtle:
                    // Gentle vocal doubling for natural enhancement
                    // Very slow modulation preserves vocal intelligibility
                    Rate = 0.3f;      // 0.3 Hz - very slow, natural movement
                    Depth = 0.2f;     // 20% - subtle modulation depth
                    Mix = 0.3f;       // 30% - gentle blend with original
                    Voices = 2;       // 2 voices - simple doubling effect
                    break;

                case ChorusPreset.VocalLush:
                    // Rich, wide vocal texture for ethereal sounds
                    // Medium speed with deeper modulation for spacious feel
                    Rate = 0.8f;      // 0.8 Hz - noticeable but musical movement
                    Depth = 0.6f;     // 60% - rich modulation for width
                    Mix = 0.6f;       // 60% - prominent chorus effect
                    Voices = 4;       // 4 voices - lush harmonic content
                    break;

                case ChorusPreset.GuitarClassic:
                    // Warm, swirling guitar tone for classic rock/pop
                    // Balanced settings for the iconic chorus guitar sound
                    Rate = 0.5f;      // 0.5 Hz - classic chorus speed
                    Depth = 0.4f;     // 40% - moderate depth for warmth
                    Mix = 0.5f;       // 50% - balanced wet/dry mix
                    Voices = 3;       // 3 voices - classic thickness
                    break;

                case ChorusPreset.GuitarShimmer:
                    // Bright, shimmering lead guitar for ambient styles
                    // Fast modulation with deep depth for sparkling texture
                    Rate = 2.0f;      // 2.0 Hz - fast, shimmering movement
                    Depth = 0.8f;     // 80% - deep modulation for sparkle
                    Mix = 0.7f;       // 70% - prominent effect
                    Voices = 5;       // 5 voices - complex harmonic content
                    break;

                case ChorusPreset.SynthPad:
                    // Wide, evolving synthesizer textures for ambient music
                    // Very slow, deep modulation with maximum voices
                    Rate = 0.15f;     // 0.15 Hz - ultra-slow evolution
                    Depth = 0.9f;     // 90% - maximum depth for wide stereo image
                    Mix = 0.8f;       // 80% - heavily processed sound
                    Voices = 6;       // 6 voices - maximum complexity
                    break;

                case ChorusPreset.StringEnsemble:
                    // Orchestral string section simulation
                    // Slow rate with moderate depth for realistic ensemble feel
                    Rate = 0.4f;      // 0.4 Hz - natural string vibrato speed
                    Depth = 0.3f;     // 30% - subtle like real string variations
                    Mix = 0.4f;       // 40% - maintains string character
                    Voices = 4;       // 4 voices - ensemble size simulation
                    break;

                case ChorusPreset.VintageAnalog:
                    // Classic analog chorus pedal emulation
                    // Medium settings with characteristic analog warmth
                    Rate = 0.7f;      // 0.7 Hz - vintage pedal speed
                    Depth = 0.5f;     // 50% - classic analog depth
                    Mix = 0.45f;      // 45% - vintage pedal mix ratio
                    Voices = 3;       // 3 voices - typical analog design
                    break;

                case ChorusPreset.Extreme:
                    // Intense, dramatic chorus for special effects
                    // Maximum settings for obvious, dramatic modulation
                    Rate = 5.0f;      // 5.0 Hz - very fast modulation
                    Depth = 1.0f;     // 100% - maximum depth
                    Mix = 0.9f;       // 90% - almost completely wet
                    Voices = 6;       // 6 voices - maximum complexity
                    break;
            }
        }

        /// <summary>
        /// Process samples with chorus effect.
        /// </summary>
        /// <param name="samples">Input samples</param>
        public override void Process(Span<float> samples)
        {
            float lfoIncrement = (float)(2.0 * Math.PI * Rate / _sampleRate);

            for (int i = 0; i < samples.Length; i++)
            {
                float input = samples[i];

                _delayBuffer[_bufferIndex] = input;

                float chorusOutput = 0.0f;

                for (int voice = 0; voice < Voices; voice++)
                {
                    float lfoValue = (float)Math.Sin(_lfoPhase + _voicePhases[voice]);

                    float delayTime = 0.01f + (0.015f * (1.0f + lfoValue * Depth));
                    int delaySamples = (int)(delayTime * _sampleRate);

                    int readIndex = (_bufferIndex - delaySamples + _delayBuffer.Length) % _delayBuffer.Length;
                    float delayedSample = _delayBuffer[readIndex];

                    chorusOutput += delayedSample;
                }

                chorusOutput /= Voices;

                samples[i] = (input * (1.0f - Mix)) + (chorusOutput * Mix);

                _bufferIndex = (_bufferIndex + 1) % _delayBuffer.Length;
                _lfoPhase += lfoIncrement;

                if (_lfoPhase >= 2.0 * Math.PI)
                    _lfoPhase -= (float)(2.0 * Math.PI);
            }
        }

        /// <summary>
        /// Reset chorus effect state but preserve current parameter settings.
        /// </summary>
        public override void Reset()
        {
            // Clear delay buffer and reset internal state
            Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
            _bufferIndex = 0;
            _lfoPhase = 0.0f;

            // Do NOT reset parameters - preserve current settings
            // as per OwnEffectsDescription requirements
        }
    }
}
