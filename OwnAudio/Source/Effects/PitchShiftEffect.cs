using System;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Realtime pitch shift, WSOLA under the hood. Tempo is untouched, only the pitch
    /// moves.
    /// </summary>
    /// <remarks>
    /// The pipeline delays the signal by a fixed amount and reports it, so the mixer's
    /// delay compensation can keep a shifted track lined up with the rest. That delay is
    /// there even at 0 semitones — the reported latency has to stay constant while the
    /// effect sits in a chain.
    /// </remarks>
    public sealed class PitchShiftEffect : IEffectProcessor
    {
        private Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        private readonly NativeEffectEngine _native = new NativeEffectEngine();
        private AudioConfig _config = null!;
        private readonly int _sampleRate;

        private float _semitones;
        private float _mix = 1.0f;

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
        /// Shift in semitones, -12 - 12. Fractional values are fine, 0.5 is a quarter tone
        /// and 0.01 is about a cent.
        /// </summary>
        public float Semitones
        {
            get => _semitones;
            set => _semitones = Math.Clamp(value, -12.0f, 12.0f);
        }

        /// <summary>
        /// Shifted to dry balance. Anything under 1.0 gives you a detuned double rather
        /// than a transposition — the dry side is delay aligned, so it does not comb.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = Math.Clamp(value, 0.0f, 1.0f);
        }

        /// <summary>
        /// Working sample rate.
        /// </summary>
        public int SampleRate => _sampleRate;

        /// <summary>
        /// Pipeline delay in frames, for the mixer's PDC. Zero until Initialize, since the
        /// engine measures it from the real sample rate.
        /// </summary>
        public int LatencySamples => _native.IsReady ? _native.LatencySamples : 0;

        /// <summary>
        /// Builds the shifter. Defaults to unity, which still costs the pipeline delay.
        /// </summary>
        public PitchShiftEffect(float semitones = 0.0f, float mix = 1.0f, int sampleRate = 44100)
        {
            if (sampleRate <= 0)
                throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));

            _id = Guid.NewGuid();
            _name = "PitchShift";
            _enabled = true;
            _sampleRate = sampleRate;

            Semitones = semitones;
            Mix = mix;
        }

        /// <summary>
        /// Stores the engine config.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config;
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
        /// Ticks up on every Reset, that is how the native twin hears about it.
        /// </summary>
        public int ResetGeneration { get; private set; }

        /// <summary>
        /// Flushes the WSOLA buffers and the dry line, parameters stay.
        /// </summary>
        public void Reset()
        {
            ResetGeneration++;
            _native.Reset();
        }

        /// <summary>
        /// Nothing unmanaged here.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            _native.Dispose();
            _disposed = true;
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString()
        {
            return $"{_name} (Enabled: {_enabled}, Semitones: {_semitones:F2}, Mix: {_mix:F2})";
        }
    }
}
