using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Chorus presets for different musical and production scenarios
    /// </summary>
    public enum ChorusPreset
    {
        Default,
        VocalSubtle,
        VocalLush,
        GuitarClassic,
        GuitarShimmer,
        SynthPad,
        StringEnsemble,
        VintageAnalog,
        Extreme
    }

    /// <summary>
    /// High-quality Chorus effect with fractional delay interpolation
    /// </summary>
    public sealed class ChorusEffect : IEffectProcessor
    {
        private readonly Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        private AudioConfig? _config;

        // DSP State
        private readonly float[] _delayBuffer;
        private int _bufferIndex;
        private float _sampleRate;
        private float _lfoPhase;
        
        // Parameters
        private float _rate = 1.0f;
        private float _depth = 0.5f;
        private float _mix = 0.5f;
        private int _voices = 3;

        // Precalculated
        private float _lfoIncrement;
        private readonly float[] _voicePhases;

        public Guid Id => _id;
        public string Name { get => _name; set => _name = value ?? "Chorus"; }
        public bool Enabled { get => _enabled; set => _enabled = value; }

        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        public float Rate
        {
            get => _rate;
            set 
            {
                _rate = Math.Clamp(value, 0.1f, 10.0f);
                RecalculateIncrement();
            }
        }

        public float Depth
        {
            get => _depth;
            set => _depth = Math.Clamp(value, 0.0f, 1.0f);
        }

        public int Voices
        {
            get => _voices;
            set => _voices = Math.Clamp(value, 2, 6);
        }

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

        public ChorusEffect(ChorusPreset preset, int sampleRate = 44100) : this(1, 0.5f, 0.5f, 3, sampleRate)
        {
            SetPreset(preset);
        }

        private void RecalculateIncrement()
        {
            _lfoIncrement = (float)(2.0 * Math.PI * _rate / _sampleRate);
        }

        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            if (Math.Abs(_sampleRate - config.SampleRate) > 1.0f)
            {
                _sampleRate = config.SampleRate;
                RecalculateIncrement();
            }
        }

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

        public void Reset()
        {
            Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
            _bufferIndex = 0;
            _lfoPhase = 0.0f;
        }

        public void Dispose()
        {
            _disposed = true;
        }
        
        public override string ToString() => $"Chorus: Rate={_rate:F2}, Depth={_depth:F2}, Enabled={_enabled}";
    }
}
