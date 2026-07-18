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

        private float[]? _delayBufferL;
        private float[]? _delayBufferR;
        private int _writeIndex;

        /// <summary>
        /// One-pole damping filter state per side.
        /// </summary>
        private float _lastOutputL;
        private float _lastOutputR;
        private float _sampleRate;

        private int _timeMs;
        private float _repeat;
        private float _mix;
        private float _damping;
        private bool _pingPong;

        private float _delaySamples;

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
            set { _timeMs = Math.Clamp(value, 1, 5000); _updateDelaySamples(); }
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
        /// Low-pass in the feedback path, higher = darker repeats.
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
            set { _sampleRate = Math.Clamp(value, 8000, 192000); _updateDelaySamples(); _initBuffer(); }
        }

        /// <summary>
        /// Builds the delay with hand picked values.
        /// </summary>
        /// <param name="repeat">Feedback amount.</param>
        /// <param name="damping">How dark the repeats get.</param>
        public DelayEffect(int time = 375, float repeat = 0.35f, float mix = 0.3f, float damping = 0.25f, int sampleRate = 44100, bool pingPong = false)
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

            _updateDelaySamples();
            _initBuffer();
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
        /// ms to samples.
        /// </summary>
        private void _updateDelaySamples()
        {
            _delaySamples = (_timeMs / 1000.0f) * _sampleRate;
        }

        /// <summary>
        /// Allocates the 5s delay lines, or just wipes them if the size already fits.
        /// </summary>
        private void _initBuffer()
        {
            int _needed = (int)(5.0f * _sampleRate);
            if (_delayBufferL == null || _delayBufferL.Length != _needed)
            {
                _delayBufferL = new float[_needed];
                _delayBufferR = new float[_needed];
            }
            else
            {
#nullable disable
                Array.Clear(_delayBufferL, 0, _delayBufferL.Length);
                Array.Clear(_delayBufferR, 0, _delayBufferR.Length);
#nullable restore
            }
            _writeIndex = 0;
            _lastOutputL = 0.0f;
            _lastOutputR = 0.0f;
        }

        /// <summary>
        /// Takes the engine config, rebuilds the lines on a rate change.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            if (Math.Abs(_sampleRate - config.SampleRate) > 1.0f)
                SampleRate = config.SampleRate;
        }

        /// <summary>
        /// Reads back the delayed signal with interpolation, damps the feedback and mixes it in.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null || !_enabled || _mix < 0.001f || _delayBufferL == null || _delayBufferR == null) return;
            if (_config.Channels != 2) return;

            int bufLen = _delayBufferL.Length;

            float rep = _repeat;
            float mx = _mix;
            float damp = _damping;
            float ds = _delaySamples;
            bool pp = _pingPong;

            float lastL = _lastOutputL;
            float lastR = _lastOutputR;

            for (int frame = 0; frame < frameCount; frame++)
            {
                int idxL = frame * 2;
                int idxR = idxL + 1;

                float inputL = buffer[idxL];
                float inputR = buffer[idxR];

                float readPos = _writeIndex - ds;
                if (readPos < 0) readPos += bufLen;

                int readIdxA = (int)readPos;
                int readIdxB = readIdxA + 1;
                if (readIdxB >= bufLen) readIdxB = 0;

                float frac = readPos - readIdxA;

                float delayedL = _delayBufferL[readIdxA] + frac * (_delayBufferL[readIdxB] - _delayBufferL[readIdxA]);
                float delayedR = _delayBufferR[readIdxA] + frac * (_delayBufferR[readIdxB] - _delayBufferR[readIdxA]);

                lastL += damp * (delayedL - lastL);
                lastR += damp * (delayedR - lastR);

                float fbL = Math.Clamp(lastL * rep, -1.0f, 1.0f);
                float fbR = Math.Clamp(lastR * rep, -1.0f, 1.0f);

                if (pp)
                {
                    _delayBufferL[_writeIndex] = inputL + fbR;
                    _delayBufferR[_writeIndex] = inputR + fbL;
                }
                else
                {
                    _delayBufferL[_writeIndex] = inputL + fbL;
                    _delayBufferR[_writeIndex] = inputR + fbR;
                }

                buffer[idxL] = inputL * (1.0f - mx) + lastL * mx;
                buffer[idxR] = inputR * (1.0f - mx) + lastR * mx;

                _writeIndex++;
                if (_writeIndex >= bufLen) _writeIndex = 0;
            }

            _lastOutputL = lastL;
            _lastOutputR = lastR;
        }

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(DelayPreset preset)
        {
            switch (preset)
            {
                case DelayPreset.Default:     Time=375; Repeat=0.35f; Mix=0.28f; Damping=0.20f; PingPong=false; break;
                case DelayPreset.SlapBack:    Time=85;  Repeat=0.12f; Mix=0.22f; Damping=0.08f; PingPong=false; break;
                case DelayPreset.ClassicEcho: Time=500; Repeat=0.42f; Mix=0.32f; Damping=0.22f; PingPong=false; break;
                case DelayPreset.Ambient:     Time=680; Repeat=0.60f; Mix=0.50f; Damping=0.35f; PingPong=false; break;
                case DelayPreset.Rhythmic:    Time=250; Repeat=0.40f; Mix=0.33f; Damping=0.18f; PingPong=false; break;
                case DelayPreset.PingPong:    Time=320; Repeat=0.48f; Mix=0.42f; Damping=0.12f; PingPong=true;  break;
                case DelayPreset.TapeEcho:    Time=420; Repeat=0.52f; Mix=0.38f; Damping=0.42f; PingPong=false; break;
                case DelayPreset.Dub:         Time=520; Repeat=0.72f; Mix=0.52f; Damping=0.40f; PingPong=false; break;
                case DelayPreset.Thickening:  Time=18;  Repeat=0.04f; Mix=0.18f; Damping=0.03f; PingPong=false; break;
            }
        }

        /// <summary>
        /// Empties the lines and the filter state.
        /// </summary>
        public void Reset()
        {
            if (_delayBufferL != null) Array.Clear(_delayBufferL, 0, _delayBufferL.Length);
            if (_delayBufferR != null) Array.Clear(_delayBufferR, 0, _delayBufferR.Length);
            _writeIndex = 0;
            _lastOutputL = 0.0f;
            _lastOutputR = 0.0f;
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
        public override string ToString()
        {
            return $"Delay: Time={_timeMs}ms, Repeats={_repeat:F2}, PingPong={_pingPong}, Enabled={_enabled}";
        }
    }
}
