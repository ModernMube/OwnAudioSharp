using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Preset configurations for the DelayEffect.
    /// </summary>
    public enum DelayPreset
    {
        /// <summary>
        /// Balanced delay suitable for general use.
        /// </summary>
        Default,
        
        /// <summary>
        /// Short delay (80ms) for slapback echo effect.
        /// </summary>
        SlapBack,
        
        /// <summary>
        /// Classic echo with moderate delay and feedback.
        /// </summary>
        ClassicEcho,
        
        /// <summary>
        /// Long delay with high feedback for ambient soundscapes.
        /// </summary>
        Ambient,
        
        /// <summary>
        /// Rhythmic delay synchronized to musical timing.
        /// </summary>
        Rhythmic,
        
        /// <summary>
        /// Ping-pong delay bouncing between left and right channels.
        /// </summary>
        PingPong,
        
        /// <summary>
        /// Warm tape echo emulation with high damping.
        /// </summary>
        TapeEcho,
        
        /// <summary>
        /// Dub-style delay with long time and high feedback.
        /// </summary>
        Dub,
        
        /// <summary>
        /// Very short delay for thickening/doubling effect.
        /// </summary>
        Thickening
    }

    /// <summary>
    /// Professional stereo delay effect with ping-pong capability and damping control.
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
        /// Gets the unique identifier for this effect instance.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Gets or sets the name of this effect instance.
        /// </summary>
        public string Name { get => _name; set => _name = value ?? "Delay"; }

        /// <summary>
        /// Gets or sets whether this effect is enabled.
        /// </summary>
        public bool Enabled { get => _enabled; set => _enabled = value; }

        /// <summary>
        /// Gets or sets the delay time in milliseconds (1 to 5000ms).
        /// </summary>
        public int Time
        {
            get => _timeMs;
            set { _timeMs = Math.Clamp(value, 1, 5000); UpdateDelaySamples(); }
        }

        /// <summary>
        /// Gets or sets the feedback/repeat amount (0.0 to 1.0).
        /// </summary>
        public float Repeat
        {
            get => _repeat;
            set => _repeat = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>
        /// Gets or sets the wet/dry mix (0.0 = dry, 1.0 = wet).
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>
        /// Gets or sets the damping amount for feedback low-pass filtering (0.0 to 1.0).
        /// Higher values create darker, warmer repeats.
        /// </summary>
        public float Damping
        {
            get => _damping;
            set => _damping = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>
        /// Gets or sets whether ping-pong mode is enabled (feedback crosses channels).
        /// </summary>
        public bool PingPong
        {
            get => _pingPong;
            set => _pingPong = value;
        }

        /// <summary>
        /// Gets or sets the sample rate and reinitializes buffers if changed.
        /// </summary>
        public int SampleRate
        {
            get => (int)_sampleRate;
            set { _sampleRate = Math.Clamp(value, 8000, 192000); UpdateDelaySamples(); InitializeBuffer(); }
        }

        /// <summary>
        /// Initializes a new instance of the DelayEffect with custom parameters.
        /// </summary>
        /// <param name="time">Delay time in milliseconds (default: 375).</param>
        /// <param name="repeat">Feedback amount (default: 0.35).</param>
        /// <param name="mix">Wet/dry mix (default: 0.3).</param>
        /// <param name="damping">Damping amount (default: 0.25).</param>
        /// <param name="sampleRate">Sample rate (default: 44100).</param>
        /// <param name="pingPong">Enable ping-pong mode (default: false).</param>
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
            
            UpdateDelaySamples();
            InitializeBuffer();
        }

        /// <summary>
        /// Initializes a new instance of the DelayEffect using a preset configuration.
        /// </summary>
        /// <param name="preset">The preset configuration to use.</param>
        public DelayEffect(DelayPreset preset) : this()
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Updates the delay time in samples based on current time and sample rate.
        /// </summary>
        private void UpdateDelaySamples()
        {
            _delaySamples = (_timeMs / 1000.0f) * _sampleRate;
        }

        /// <summary>
        /// Initializes or clears the delay buffers.
        /// </summary>
        private void InitializeBuffer()
        {          
            int needed = (int)(5.0f * _sampleRate); // Max 5s capacity
            if (_delayBufferL == null || _delayBufferL.Length != needed)
            {
                _delayBufferL = new float[needed];
                _delayBufferR = new float[needed];
            }
            else
            {
                Array.Clear(_delayBufferL, 0, _delayBufferL.Length);
                Array.Clear(_delayBufferR, 0, _delayBufferR.Length);
            }
            _writeIndex = 0;
            _lastOutputL = 0.0f;
            _lastOutputR = 0.0f;
        }
        
        /// <summary>
        /// Initializes the effect with the specified audio configuration.
        /// </summary>
        /// <param name="config">The audio configuration.</param>
        /// <exception cref="ArgumentNullException">Thrown when config is null.</exception>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            if (Math.Abs(_sampleRate - config.SampleRate) > 1.0f)
            {
                SampleRate = config.SampleRate;
            }
        }

        /// <summary>
        /// Processes the audio buffer with delay effect.
        /// </summary>
        /// <param name="buffer">The interleaved stereo audio buffer to process.</param>
        /// <param name="frameCount">The number of frames in the buffer.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null || !_enabled || _mix < 0.001f || _delayBufferL == null || _delayBufferR == null) return;

            int channels = _config.Channels;
            if (channels != 2) return; // Only stereo supported
            
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
                int idxR = frame * 2 + 1;
                
                float inputL = buffer[idxL];
                float inputR = buffer[idxR];
                
                // Calculate read position with interpolation
                float readPos = _writeIndex - ds;
                if (readPos < 0) readPos += bufLen;
                
                int readIdxA = (int)readPos;
                int readIdxB = readIdxA + 1;
                if (readIdxB >= bufLen) readIdxB = 0;
                
                float frac = readPos - readIdxA;
                
                // Read delayed samples with linear interpolation
                float delayedL = _delayBufferL[readIdxA] + frac * (_delayBufferL[readIdxB] - _delayBufferL[readIdxA]);
                float delayedR = _delayBufferR[readIdxA] + frac * (_delayBufferR[readIdxB] - _delayBufferR[readIdxA]);
                
                // Apply damping (one-pole low-pass filter)
                float dampedL = lastL + damp * (delayedL - lastL);
                float dampedR = lastR + damp * (delayedR - lastR);
                lastL = dampedL;
                lastR = dampedR;
                
                // Calculate feedback
                float feedbackL = dampedL * rep;
                float feedbackR = dampedR * rep;
                
                // Hard limit feedback to prevent runaway
                feedbackL = Math.Clamp(feedbackL, -1.0f, 1.0f);
                feedbackR = Math.Clamp(feedbackR, -1.0f, 1.0f);
                
                // Write to buffers (with ping-pong cross-feeding if enabled)
                if (pp)
                {
                    _delayBufferL[_writeIndex] = inputL + feedbackR; // Right feedback to left
                    _delayBufferR[_writeIndex] = inputR + feedbackL; // Left feedback to right
                }
                else
                {
                    _delayBufferL[_writeIndex] = inputL + feedbackL;
                    _delayBufferR[_writeIndex] = inputR + feedbackR;
                }
                
                // Mix dry and wet signals
                buffer[idxL] = inputL * (1.0f - mx) + dampedL * mx;
                buffer[idxR] = inputR * (1.0f - mx) + dampedR * mx;
                
                _writeIndex++;
                if (_writeIndex >= bufLen) _writeIndex = 0;
            }
            
            _lastOutputL = lastL;
            _lastOutputR = lastR;
        }
        
        /// <summary>
        /// Applies a preset configuration to the effect.
        /// </summary>
        /// <param name="preset">The preset to apply.</param>
        public void SetPreset(DelayPreset preset)
        {
            switch (preset)
            {
                case DelayPreset.Default: Time=375; Repeat=0.35f; Mix=0.3f; Damping=0.25f; PingPong=false; break;
                case DelayPreset.SlapBack: Time=80; Repeat=0.15f; Mix=0.25f; Damping=0.1f; PingPong=false; break;
                case DelayPreset.ClassicEcho: Time=375; Repeat=0.35f; Mix=0.3f; Damping=0.25f; PingPong=false; break;
                case DelayPreset.Ambient: Time=650; Repeat=0.55f; Mix=0.45f; Damping=0.4f; PingPong=false; break;
                case DelayPreset.Rhythmic: Time=250; Repeat=0.4f; Mix=0.35f; Damping=0.2f; PingPong=false; break;
                case DelayPreset.PingPong: Time=300; Repeat=0.45f; Mix=0.4f; Damping=0.15f; PingPong=true; break;
                case DelayPreset.TapeEcho: Time=400; Repeat=0.5f; Mix=0.38f; Damping=0.6f; PingPong=false; break;
                case DelayPreset.Dub: Time=500; Repeat=0.7f; Mix=0.5f; Damping=0.45f; PingPong=false; break;
                case DelayPreset.Thickening: Time=15; Repeat=0.05f; Mix=0.15f; Damping=0.05f; PingPong=false; break;
            }
        }

        /// <summary>
        /// Resets the effect state to initial values.
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
        /// Disposes the effect and releases resources.
        /// </summary>
        public void Dispose()
        {
        }

        /// <summary>
        /// Returns a string representation of the effect's current state.
        /// </summary>
        /// <returns>A string describing the effect state.</returns>
        public override string ToString()
        {
            return $"Delay: Time={_timeMs}ms, Repeats={_repeat:F2}, PingPong={_pingPong}, Enabled={_enabled}";
        }
    }
}
