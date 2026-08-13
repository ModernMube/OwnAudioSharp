using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Leslie cabinet setups per style.
    /// </summary>
    public enum RotaryPreset
    {
        /// <summary>
        /// Balanced starting point.
        /// </summary>
        Default,

        /// <summary>
        /// Classic Hammond cabinet.
        /// </summary>
        Hammond,

        /// <summary>
        /// Warm and expressive gospel movement.
        /// </summary>
        Gospel,

        /// <summary>
        /// Aggressive fast Leslie for rock.
        /// </summary>
        Rock,

        /// <summary>
        /// Gentle and refined, jazz combo.
        /// </summary>
        Jazz,

        /// <summary>
        /// Extreme, deliberately unnatural doppler.
        /// </summary>
        Psychedelic,

        /// <summary>
        /// Authentic slow cabinet.
        /// </summary>
        VintageSlow,

        /// <summary>
        /// Leslie 122 in tremolo: 6.7Hz horn, 5.2Hz rotor.
        /// </summary>
        VintageFast,

        /// <summary>
        /// Barely moving, background texture.
        /// </summary>
        Subtle
    }

    /// <summary>
    /// Rotary speaker sim. The signal is split at 800Hz, the horn takes the top and the
    /// rotor the bottom, each with its own doppler delay and tremolo.
    /// </summary>
    public sealed class RotaryEffect : IEffectProcessor
    {
        private Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        private AudioConfig _config = null!;

        /// <summary>
        /// Chorale to tremolo ratio of a Leslie 122: the horn goes from about 40rpm to 400,
        /// the bass drum is geared a bit lower.
        /// </summary>
        private const float FastHornRatio = 9.0f;
        private const float FastRotorRatio = 8.0f;

        private readonly int _sampleRate;
        private readonly float[] _hornDelayBuffer;
        private readonly float[] _rotorDelayBuffer;

        private float _hornSpeed = 0.8f;
        private float _rotorSpeed = 0.7f;
        private float _intensity = 0.7f;
        private float _mix = 1.0f;
        private bool _isFast = false;

        private float _hornPhase = 0.0f;
        private float _rotorPhase = 0.0f;
        private int _hornBufferIndex = 0;
        private int _rotorBufferIndex = 0;

        /// <summary>
        /// 800Hz crossover: one one-pole coefficient and a state for each side.
        /// </summary>
        private readonly float _xoverCoeff;
        private float _lpState;
        private float _hpState;

        /// <summary>
        /// Instance id.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// On/off switch.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// Horn chorale speed in Hz, 0.4 - 8. A real 122 idles around 0.8Hz here and the fast
        /// switch takes it to tremolo, roughly 6.7Hz.
        /// </summary>
        public float HornSpeed
        {
            get => _hornSpeed;
            set => _hornSpeed = Math.Clamp(value, 0.4f, 8.0f);
        }

        /// <summary>
        /// Rotor chorale speed in Hz, 0.3 - 6. The bass drum is slower than the horn, both
        /// standing still and in tremolo.
        /// </summary>
        public float RotorSpeed
        {
            get => _rotorSpeed;
            set => _rotorSpeed = Math.Clamp(value, 0.3f, 6.0f);
        }

        /// <summary>
        /// How deep the doppler and the tremolo go.
        /// </summary>
        public float Intensity
        {
            get => _intensity;
            set => _intensity = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Dry to wet balance. A cabinet is the whole sound, not a layer, so this belongs at
        /// 1.0 unless you deliberately want a parallel blend.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Fast/slow cabinet switch.
        /// </summary>
        public bool IsFast
        {
            get => _isFast;
            set => _isFast = value;
        }

        /// <summary>
        /// Builds the cabinet with hand picked values, a 122 sitting in chorale.
        /// </summary>
        public RotaryEffect(float hornSpeed = 0.8f, float rotorSpeed = 0.7f, float intensity = 0.7f, float mix = 1.0f, bool isFast = false, int sampleRate = 44100)
        {
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _id = Guid.NewGuid();
            _name = "Rotary";
            _enabled = true;

            _sampleRate = sampleRate;
            HornSpeed = hornSpeed;
            RotorSpeed = rotorSpeed;
            Intensity = intensity;
            Mix = mix;
            IsFast = isFast;

            int maxDelay = (int)(0.01 * sampleRate);
            _hornDelayBuffer = new float[maxDelay];
            _rotorDelayBuffer = new float[maxDelay];

            _xoverCoeff = (float)(2.0 * Math.PI * 800.0 / sampleRate);
        }

        /// <summary>
        /// Builds the cabinet from a preset.
        /// </summary>
        /// <param name="preset"></param>
        /// <param name="sampleRate"></param>
        public RotaryEffect(RotaryPreset preset, int sampleRate = 44100)
            : this(0.8f, 0.7f, 0.7f, 1.0f, false, sampleRate)
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Stores the engine config.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Loads one of the canned setups. Speeds are the pre-switch values, the fast
        /// presets get their real rate from the x3 / x2 multipliers.
        /// </summary>
        public void SetPreset(RotaryPreset preset)
        {
            switch (preset)
            {
                case RotaryPreset.Hammond:
                    HornSpeed = 0.80f; RotorSpeed = 0.70f; Intensity = 0.75f; Mix = 1.0f; IsFast = false;
                    break;

                case RotaryPreset.Gospel:
                    HornSpeed = 0.85f; RotorSpeed = 0.72f; Intensity = 0.85f; Mix = 1.0f; IsFast = true;
                    break;

                case RotaryPreset.Rock:
                    HornSpeed = 0.78f; RotorSpeed = 0.68f; Intensity = 0.90f; Mix = 1.0f; IsFast = true;
                    break;

                case RotaryPreset.Jazz:
                    HornSpeed = 0.70f; RotorSpeed = 0.60f; Intensity = 0.60f; Mix = 1.0f; IsFast = false;
                    break;

                case RotaryPreset.Psychedelic:
                    HornSpeed = 1.10f; RotorSpeed = 0.90f; Intensity = 1.0f; Mix = 1.0f; IsFast = true;
                    break;

                case RotaryPreset.VintageSlow:
                    HornSpeed = 0.75f; RotorSpeed = 0.65f; Intensity = 0.70f; Mix = 1.0f; IsFast = false;
                    break;

                case RotaryPreset.VintageFast:
                    HornSpeed = 0.75f; RotorSpeed = 0.65f; Intensity = 0.78f; Mix = 1.0f; IsFast = true;
                    break;

                case RotaryPreset.Subtle:
                    HornSpeed = 0.60f; RotorSpeed = 0.50f; Intensity = 0.35f; Mix = 0.65f; IsFast = false;
                    break;

                default:
                    HornSpeed = 0.80f; RotorSpeed = 0.70f; Intensity = 0.70f; Mix = 1.0f; IsFast = false;
                    break;
            }
        }

        /// <summary>
        /// Splits the band, sweeps both delay lines with their own LFO and sums them back.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null)
                throw new InvalidOperationException("Effect not initialized. Call Initialize() first.");

            if (!_enabled || _mix < 0.001f) return;

            int sampleCount = frameCount * _config.Channels;
            int hornLen = _hornDelayBuffer.Length;
            int rotorLen = _rotorDelayBuffer.Length;

            float intensity = _intensity;
            float mx = _mix;
            float k = _xoverCoeff;

            float hornInc = (float)(2.0 * Math.PI * (_isFast ? _hornSpeed * FastHornRatio : _hornSpeed) / _sampleRate);
            float rotorInc = (float)(2.0 * Math.PI * (_isFast ? _rotorSpeed * FastRotorRatio : _rotorSpeed) / _sampleRate);

            for (int i = 0; i < sampleCount; i++)
            {
                float input = buffer[i];

                _lpState += k * (input - _lpState);
                _hpState += k * (input - _hpState);
                float lowFreq = _lpState;
                float highFreq = input - _hpState;

                float hornLfo = MathF.Sin(_hornPhase);
                int hornDelay = Math.Clamp((int)((0.001f + 0.003f * hornLfo * intensity) * _sampleRate), 1, hornLen - 1);
                float hornOut = _hornDelayBuffer[(_hornBufferIndex - hornDelay + hornLen) % hornLen] * (0.8f + 0.2f * hornLfo * intensity);
                _hornDelayBuffer[_hornBufferIndex] = highFreq;

                float rotorLfo = MathF.Sin(_rotorPhase);
                int rotorDelay = Math.Clamp((int)((0.002f + 0.004f * rotorLfo * intensity) * _sampleRate), 1, rotorLen - 1);
                float rotorOut = _rotorDelayBuffer[(_rotorBufferIndex - rotorDelay + rotorLen) % rotorLen] * (0.9f + 0.1f * rotorLfo * intensity);
                _rotorDelayBuffer[_rotorBufferIndex] = lowFreq;

                buffer[i] = input * (1.0f - mx) + (hornOut + rotorOut) * mx;

                _hornBufferIndex = (_hornBufferIndex + 1) % hornLen;
                _rotorBufferIndex = (_rotorBufferIndex + 1) % rotorLen;

                _hornPhase += hornInc;
                _rotorPhase += rotorInc;

                if (_hornPhase >= 2.0 * Math.PI) _hornPhase -= (float)(2.0 * Math.PI);
                if (_rotorPhase >= 2.0 * Math.PI) _rotorPhase -= (float)(2.0 * Math.PI);
            }
        }

        /// <summary>
        /// Ticks up on every Reset, that is how the native twin hears about it.
        /// </summary>
        public int ResetGeneration { get; private set; }

        /// <summary>
        /// Empties both lines and the crossover, parameters stay.
        /// </summary>
        public void Reset()
        {
            ResetGeneration++;
            Array.Clear(_hornDelayBuffer, 0, _hornDelayBuffer.Length);
            Array.Clear(_rotorDelayBuffer, 0, _rotorDelayBuffer.Length);
            _hornBufferIndex = 0;
            _rotorBufferIndex = 0;
            _hornPhase = 0.0f;
            _rotorPhase = 0.0f;
            _lpState = 0.0f;
            _hpState = 0.0f;
        }

        /// <summary>
        /// Nothing unmanaged here.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString()
        {
            return $"{_name} (Enabled: {_enabled}, Mix: {_mix:F2}, Speed: {(_isFast ? "Fast" : "Slow")})";
        }
    }
}
