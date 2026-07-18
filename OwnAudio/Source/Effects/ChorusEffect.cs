using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Chorus setups for the usual sources.
    /// </summary>
    public enum ChorusPreset
    {
        /// <summary>
        /// Safe middle ground.
        /// </summary>
        Default,

        /// <summary>
        /// Just a hint of doubling on vocals.
        /// </summary>
        VocalSubtle,

        /// <summary>
        /// Thick vocal layering.
        /// </summary>
        VocalLush,

        /// <summary>
        /// CE-1 flavoured guitar chorus.
        /// </summary>
        GuitarClassic,

        /// <summary>
        /// Fast and sparkly, clean guitar.
        /// </summary>
        GuitarShimmer,

        /// <summary>
        /// Slow, wide, dreamy. All voices in.
        /// </summary>
        SynthPad,

        /// <summary>
        /// Section-like detune spread.
        /// </summary>
        StringEnsemble,

        /// <summary>
        /// BBD style, warm and a bit seasick.
        /// </summary>
        VintageAnalog,

        /// <summary>
        /// Over the top detune/vibrato.
        /// </summary>
        Extreme
    }

    /// <summary>
    /// Multi voice chorus. LFO modulated delay taps with fractional read, mixed back over the dry.
    /// </summary>
    public sealed class ChorusEffect : IEffectProcessor
    {
        private readonly Guid _id;
        private string _name;
        private bool _enabled;
        private AudioConfig? _config;

        /// <summary>
        /// 50ms circular delay line, plenty for chorus.
        /// </summary>
        private readonly float[] _delayBuffer;
        private int _bufferIndex;
        private float _sampleRate;

        private float _lfoPhase;
        private float _rate = 1.0f;
        private float _depth = 0.5f;
        private float _mix = 0.5f;
        private int _voices = 3;

        /// <summary>
        /// Phase step per sample, recalculated when rate changes.
        /// </summary>
        private float _lfoIncrement;

        /// <summary>
        /// Fixed phase offset per voice so they don't move together.
        /// </summary>
        private readonly float[] _voicePhases;

        /// <summary>
        /// Instance id.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name { get => _name; set => _name = value ?? "Chorus"; }

        /// <summary>
        /// On/off switch.
        /// </summary>
        public bool Enabled { get => _enabled; set => _enabled = value; }

        /// <summary>
        /// Dry to wet balance, 0 - 1.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// LFO speed in Hz, 0.1 - 10.
        /// </summary>
        public float Rate
        {
            get => _rate;
            set
            {
                _rate = Math.Clamp(value, 0.1f, 10.0f);
                _recalcIncrement();
            }
        }

        /// <summary>
        /// How far the delay time swings, 0 - 1.
        /// </summary>
        public float Depth
        {
            get => _depth;
            set => _depth = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Voice count, 2 - 6. More voices = thicker but costs CPU.
        /// </summary>
        public int Voices
        {
            get => _voices;
            set => _voices = Math.Clamp(value, 2, 6);
        }

        /// <summary>
        /// Builds the chorus. Sample rate only sizes the delay line, Initialize can override it.
        /// </summary>
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

            _delayBuffer = new float[(int)(0.05f * sampleRate)];
            _voicePhases = new float[6];

            for (int i = 0; i < 6; i++)
                _voicePhases[i] = (float)(i * Math.PI * 2.0 / 6.0);

            _recalcIncrement();
        }

        /// <summary>
        /// Builds the chorus from a preset.
        /// </summary>
        /// <param name="preset"></param>
        /// <param name="sampleRate"></param>
        public ChorusEffect(ChorusPreset preset, int sampleRate = 44100) : this(1, 0.5f, 0.5f, 3, sampleRate)
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Phase step per sample from rate and sample rate.
        /// </summary>
        private void _recalcIncrement()
        {
            _lfoIncrement = (float)(2.0 * Math.PI * _rate / _sampleRate);
        }

        /// <summary>
        /// Takes the engine config, retunes the LFO if the rate moved.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            if (Math.Abs(_sampleRate - config.SampleRate) > 1.0f)
            {
                _sampleRate = config.SampleRate;
                _recalcIncrement();
            }
        }

        /// <summary>
        /// Reads every voice out of the delay line with linear interpolation, then blends with the dry.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null || !_enabled || _mix < 0.001f) return;

            int totalSamples = frameCount * _config.Channels;
            int bufLen = _delayBuffer.Length;

            float currentDepth = _depth;
            float currentMix = _mix;
            int currentVoices = _voices;
            float lfoInc = _lfoIncrement;

            float baseDelaySamples = 0.015f * _sampleRate;
            float modDepthSamples = 0.005f * _sampleRate * currentDepth;

            for (int i = 0; i < totalSamples; i++)
            {
                float input = buffer[i];
                _delayBuffer[_bufferIndex] = input;

                float wet = 0.0f;
                for (int v = 0; v < currentVoices; v++)
                {
                    float lfo = MathF.Sin(_lfoPhase + _voicePhases[v]);
                    float readPos = _bufferIndex - (baseDelaySamples + lfo * modDepthSamples);

                    while (readPos < 0) readPos += bufLen;
                    while (readPos >= bufLen) readPos -= bufLen;

                    int idxA = (int)readPos;
                    int idxB = idxA + 1;
                    if (idxB >= bufLen) idxB = 0;

                    float frac = readPos - idxA;
                    float a = _delayBuffer[idxA];
                    wet += a + frac * (_delayBuffer[idxB] - a);
                }

                wet /= currentVoices;
                buffer[i] = input * (1.0f - currentMix) + wet * currentMix;

                _bufferIndex++;
                if (_bufferIndex >= bufLen) _bufferIndex = 0;

                _lfoPhase += lfoInc;
                if (_lfoPhase >= Math.PI * 2) _lfoPhase -= (float)(Math.PI * 2);
            }
        }

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(ChorusPreset preset)
        {
            switch (preset)
            {
                case ChorusPreset.VocalSubtle:
                    Rate=0.25f; Depth=0.15f; Mix=0.25f; Voices=2; break;
                case ChorusPreset.VocalLush:
                    Rate=0.65f; Depth=0.55f; Mix=0.50f; Voices=4; break;
                case ChorusPreset.GuitarClassic:
                    Rate=0.50f; Depth=0.35f; Mix=0.45f; Voices=3; break;
                case ChorusPreset.GuitarShimmer:
                    Rate=1.50f; Depth=0.70f; Mix=0.60f; Voices=5; break;
                case ChorusPreset.SynthPad:
                    Rate=0.12f; Depth=0.75f; Mix=0.65f; Voices=6; break;
                case ChorusPreset.StringEnsemble:
                    Rate=0.35f; Depth=0.65f; Mix=0.55f; Voices=5; break;
                case ChorusPreset.VintageAnalog:
                    Rate=0.65f; Depth=0.42f; Mix=0.48f; Voices=3; break;
                case ChorusPreset.Extreme:
                    Rate=3.0f; Depth=0.90f; Mix=0.70f; Voices=6; break;
                default:
                    Rate=0.8f; Depth=0.40f; Mix=0.40f; Voices=3; break;
            }
        }

        /// <summary>
        /// Empties the delay line and parks the LFO.
        /// </summary>
        public void Reset()
        {
            Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
            _bufferIndex = 0;
            _lfoPhase = 0.0f;
        }

        /// <summary>
        /// Nothing to release.
        /// </summary>
        public void Dispose()
        {
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString() => $"Chorus: Rate={_rate:F2}, Depth={_depth:F2}, Enabled={_enabled}";
    }
}
