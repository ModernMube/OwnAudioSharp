using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Delay setups from slapback to dub.
    /// </summary>
    public enum DelayPreset
    {
        /// <summary>
        /// Dotted 8th around 120 BPM, musical everywhere.
        /// </summary>
        Default,

        /// <summary>
        /// 85ms rockabilly slap.
        /// </summary>
        SlapBack,

        /// <summary>
        /// Quarter note echo, clearly audible repeats.
        /// </summary>
        ClassicEcho,

        /// <summary>
        /// Long and dense, almost reverb.
        /// </summary>
        Ambient,

        /// <summary>
        /// 8th note, groove locked.
        /// </summary>
        Rhythmic,

        /// <summary>
        /// Repeats bounce between the two sides.
        /// </summary>
        PingPong,

        /// <summary>
        /// Warm tape flavour, darker repeats.
        /// </summary>
        TapeEcho,

        /// <summary>
        /// Long and high feedback, close to self oscillation.
        /// </summary>
        Dub,

        /// <summary>
        /// ADT style doubling, you don't hear it as an echo.
        /// </summary>
        Thickening
    }

    /// <summary>
    /// Stereo delay with damped feedback and optional ping-pong. Stereo only.
    /// </summary>
    public sealed class DelayEffect : IEffectProcessor
    {
        private readonly Guid _id;
        private string _name;
        private bool _enabled;
        private AudioConfig? _config;
        private readonly NativeEffectEngine _native = new NativeEffectEngine();

        private float _sampleRate;

        private int _timeMs;
        private float _repeat;
        private float _mix;
        private float _damping;
        private bool _pingPong;

        /// <summary>
        /// Instance id.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name { get => _name; set => _name = value ?? "Delay"; }

        /// <summary>
        /// On/off switch.
        /// </summary>
        public bool Enabled { get => _enabled; set => _enabled = value; }

        /// <summary>
        /// Delay time in ms, 1 - 5000.
        /// </summary>
        public int Time
        {
            get => _timeMs;
            set => _timeMs = Math.Clamp(value, 1, 5000);
        }

        /// <summary>
        /// Feedback amount, 0 - 1.
        /// </summary>
        public float Repeat
        {
            get => _repeat;
            set => _repeat = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>
        /// Dry to wet balance.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>
        /// Tracking coefficient of the one-pole low-pass in the feedback path, so against
        /// what the name suggests higher = brighter repeats and lower = darker. At 0 the
        /// wet path collapses to silence. Same behaviour as the native delay, kept as is
        /// so existing presets sound the same.
        /// </summary>
        public float Damping
        {
            get => _damping;
            set => _damping = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>
        /// Cross feeds the sides so the repeats bounce.
        /// </summary>
        public bool PingPong
        {
            get => _pingPong;
            set => _pingPong = value;
        }

        /// <summary>
        /// Working sample rate, setting it reallocates the delay lines.
        /// </summary>
        public int SampleRate
        {
            get => (int)_sampleRate;
            set => _sampleRate = Math.Clamp(value, 8000, 192000);
        }

        /// <summary>
        /// Builds the delay with hand picked values: a dotted 8th at 120 BPM, three or four
        /// audible repeats and a quarter of wet, which is the usual starting point on an
        /// insert. Same numbers as the Default preset.
        /// </summary>
        /// <param name="repeat">Feedback amount.</param>
        /// <param name="damping">Feedback low-pass tracking coefficient, higher = brighter.</param>
        public DelayEffect(int time = 375, float repeat = 0.32f, float mix = 0.25f, float damping = 0.22f, int sampleRate = 44100, bool pingPong = false)
        {
            _id = Guid.NewGuid();
            _name = "Delay";
            _enabled = true;

            _sampleRate = sampleRate;
            _timeMs = time;
            _repeat = repeat;
            _mix = mix;
            _damping = damping;
            _pingPong = pingPong;
        }

        /// <summary>
        /// Builds the delay from a preset.
        /// </summary>
        /// <param name="preset"></param>
        public DelayEffect(DelayPreset preset) : this()
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Takes the engine config and builds the native line on it.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            if (Math.Abs(_sampleRate - config.SampleRate) > 1.0f)
                SampleRate = config.SampleRate;
            _native.Initialize(this, config);
        }

        /// <summary>
        /// Same DSP the mixer twin runs, on this instance's native handle.
        /// </summary>
        public void Process(Span<float> buffer, int frameCount)
        {
            _native.Process(this, buffer, frameCount);
        }

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(DelayPreset preset)
        {
            switch (preset)
            {
                case DelayPreset.Default:     Time=375; Repeat=0.32f; Mix=0.25f; Damping=0.22f; PingPong=false; break;
                case DelayPreset.SlapBack:    Time=85;  Repeat=0.10f; Mix=0.20f; Damping=0.10f; PingPong=false; break;
                case DelayPreset.ClassicEcho: Time=500; Repeat=0.38f; Mix=0.26f; Damping=0.25f; PingPong=false; break;
                case DelayPreset.Ambient:     Time=680; Repeat=0.58f; Mix=0.35f; Damping=0.40f; PingPong=false; break;
                case DelayPreset.Rhythmic:    Time=250; Repeat=0.36f; Mix=0.24f; Damping=0.20f; PingPong=false; break;
                case DelayPreset.PingPong:    Time=320; Repeat=0.45f; Mix=0.30f; Damping=0.18f; PingPong=true;  break;
                case DelayPreset.TapeEcho:    Time=420; Repeat=0.48f; Mix=0.28f; Damping=0.45f; PingPong=false; break;
                case DelayPreset.Dub:         Time=520; Repeat=0.70f; Mix=0.38f; Damping=0.48f; PingPong=false; break;
                case DelayPreset.Thickening:  Time=18;  Repeat=0.04f; Mix=0.30f; Damping=0.05f; PingPong=false; break;
            }
        }

        /// <summary>
        /// Ticks up on every Reset, that is how the native twin hears about it.
        /// </summary>
        public int ResetGeneration { get; private set; }

        /// <summary>
        /// Empties the native lines and the filter state.
        /// </summary>
        public void Reset()
        {
            ResetGeneration++;
            _native.Reset();
        }

        /// <summary>
        /// Nothing to release.
        /// </summary>
        public void Dispose()
        {
            _native.Dispose();
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString()
        {
            return $"Delay: Time={_timeMs}ms, Repeats={_repeat:F2}, PingPong={_pingPong}, Enabled={_enabled}";
        }
    }
}
