using System;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Ready made spaces for the FDN reverb. Every one of them is a full setup, not just
    /// a size tweak - decay, damping, modulation and the ducker all move together.
    /// </summary>
    public enum OwnReverbPreset
    {
        /// <summary>
        /// Medium hall, the safe starting point.
        /// </summary>
        Default,

        /// <summary>
        /// Tight room, short tail, early reflections doing most of the work.
        /// </summary>
        SmallRoom,

        /// <summary>
        /// Wooden chamber, the classic mid sized space.
        /// </summary>
        Chamber,

        /// <summary>
        /// Concert hall, wide and long.
        /// </summary>
        LargeHall,

        /// <summary>
        /// Huge stone space, very long tail with open highs.
        /// </summary>
        Cathedral,

        /// <summary>
        /// EMT 140 flavour: dense, bright, no early reflections at all.
        /// </summary>
        Plate,

        /// <summary>
        /// Bright plate tuned for lead vocals, ducks a little under the voice.
        /// </summary>
        VocalPlate,

        /// <summary>
        /// Punchy live room, early reflections pushed up for the transients.
        /// </summary>
        DrumRoom,

        /// <summary>
        /// 80s gated sound, short burst with heavy damping.
        /// </summary>
        Gated,

        /// <summary>
        /// The signature one: slow, deeply modulated tail that swells in behind the source.
        /// </summary>
        Bloom,

        /// <summary>
        /// Endless wash for pads, deep modulation and a lot of wet.
        /// </summary>
        AmbientPad,

        /// <summary>
        /// Big tail that gets out of the way - hard ducking for dense mixes.
        /// </summary>
        DuckedVocal,

        /// <summary>
        /// Tank set up for holding - 20 s tail, wide and slow. Flip Freeze on top of it
        /// once something is in there and it stays put forever.
        /// </summary>
        InfiniteHold,

        /// <summary>
        /// Glue only, you shouldn't really be able to point at it.
        /// </summary>
        Subtle
    }

    /// <summary>
    /// 16 line FDN reverb: input diffusion, per line damping, LFO modulated taps and a
    /// sidechain ducker. The DSP lives in the Rust engine - this class is the parameter
    /// model the mixer mirrors onto its native twin, so Process does nothing here.
    /// </summary>
    public sealed class OwnReverbEffect : IEffectProcessor
    {
        private readonly Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        private readonly NativeEffectEngine _native = new NativeEffectEngine();

        private float _mix = 0.30f;
        private float _preDelay = 20.0f;
        private float _decay = 2.5f;
        private float _size = 1.0f;
        private float _damping = 0.5f;
        private float _lowDamping = 0.15f;
        private float _diffusion = 0.7f;
        private float _modRate = 0.8f;
        private float _modDepth = 0.4f;
        private float _width = 1.2f;
        private float _earlyLevel = 0.35f;
        private float _lateLevel = 1.0f;
        private float _duckDepth;
        private float _duckAttack = 12.0f;
        private float _duckRelease = 250.0f;
        private bool _freeze;

        /// <summary>
        /// Builds the reverb on its medium hall defaults. Use the other ctor if you want to
        /// start from one of the presets instead.
        /// </summary>
        public OwnReverbEffect()
        {
            _id = Guid.NewGuid();
            _name = "OwnReverb";
            _enabled = true;
        }

        /// <summary>
        /// Builds the reverb straight from a preset.
        /// </summary>
        /// <param name="preset"></param>
        public OwnReverbEffect(OwnReverbPreset preset) : this()
        {
            SetPreset(preset);
        }

        #region Propertyes

        /// <summary>
        /// Instance id.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name { get => _name; set => _name = value ?? "OwnReverb"; }

        /// <summary>
        /// On/off switch.
        /// </summary>
        public bool Enabled { get => _enabled; set => _enabled = value; }

        /// <summary>
        /// Send amount, 0.0 - 1.0. The dry never dips, so this behaves like an insert send:
        /// under 0.1 is glue, a quarter is a normal room, half is a deliberate wash.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = FastClamp(value, 0f, 1f);
        }

        /// <summary>Pre-delay in ms, 0 - 250.</summary>
        public float PreDelay
        {
            get => _preDelay;
            set => _preDelay = FastClamp(value, 0f, 250f);
        }

        /// <summary>
        /// RT60 tail length in seconds, 0.1 - 20. This is the undamped figure, the two
        /// damping controls shorten what you actually hear on top of it.
        /// </summary>
        public float Decay
        {
            get => _decay;
            set => _decay = FastClamp(value, 0.1f, 20f);
        }

        /// <summary>
        /// Room size, 0.25 - 2.0. Scales every delay line, so don't automate it while a tail
        /// is ringing - the read taps jump and it clicks.
        /// </summary>
        public float Size
        {
            get => _size;
            set => _size = FastClamp(value, 0.25f, 2f);
        }

        /// <summary>High damping, 0.0 - 1.0. Higher kills the air faster.</summary>
        public float Damping
        {
            get => _damping;
            set => _damping = FastClamp(value, 0f, 1f);
        }

        /// <summary>Low damping, 0.0 - 1.0. Thins the bottom out of the tail.</summary>
        public float LowDamping
        {
            get => _lowDamping;
            set => _lowDamping = FastClamp(value, 0f, 1f);
        }

        /// <summary>Input diffusion, 0.0 - 1.0.</summary>
        public float Diffusion
        {
            get => _diffusion;
            set => _diffusion = FastClamp(value, 0f, 1f);
        }

        /// <summary>Tail modulation rate in Hz, 0.05 - 5.0.</summary>
        public float ModRate
        {
            get => _modRate;
            set => _modRate = FastClamp(value, 0.05f, 5f);
        }

        /// <summary>Modulation depth, 0.0 - 1.0 (up to about 3 ms).</summary>
        public float ModDepth
        {
            get => _modDepth;
            set => _modDepth = FastClamp(value, 0f, 1f);
        }

        /// <summary>Stereo width of the wet signal, 0.0 - 2.0.</summary>
        public float Width
        {
            get => _width;
            set => _width = FastClamp(value, 0f, 2f);
        }

        /// <summary>Early reflection level, 0.0 - 1.0.</summary>
        public float EarlyLevel
        {
            get => _earlyLevel;
            set => _earlyLevel = FastClamp(value, 0f, 1f);
        }

        /// <summary>Late tail level, 0.0 - 1.0.</summary>
        public float LateLevel
        {
            get => _lateLevel;
            set => _lateLevel = FastClamp(value, 0f, 1f);
        }

        /// <summary>
        /// How hard the dry signal ducks the wet, 0.0 is off.
        /// </summary>
        public float DuckDepth
        {
            get => _duckDepth;
            set => _duckDepth = FastClamp(value, 0f, 1f);
        }

        /// <summary>Ducker attack in ms, 1 - 200.</summary>
        public float DuckAttack
        {
            get => _duckAttack;
            set => _duckAttack = FastClamp(value, 1f, 200f);
        }

        /// <summary>Ducker release in ms, 10 - 2000.</summary>
        public float DuckRelease
        {
            get => _duckRelease;
            set => _duckRelease = FastClamp(value, 10f, 2000f);
        }

        /// <summary>
        /// Holds the tail forever and mutes the input into the tank. It is a performance
        /// control, not a setup one - turn it on once the tank already has something in it.
        /// </summary>
        public bool Freeze
        {
            get => _freeze;
            set => _freeze = value;
        }

        /// <summary>
        /// Ticks up on every Reset, that is how the native twin hears about it.
        /// </summary>
        public int ResetGeneration { get; private set; }

        #endregion

        /// <summary>
        /// Loads one of the canned spaces. Ducking and freeze are cleared first, so a preset
        /// never inherits them from whatever was set before - only the presets that actually
        /// want a ducker turn one back on. Mix values sit where an engineer would leave them:
        /// under 0.1 for glue, around a quarter for a normal room, half only for a wash.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(OwnReverbPreset preset)
        {
            DuckDepth = 0f;
            DuckAttack = 12f;
            DuckRelease = 250f;
            Freeze = false;
            LateLevel = 1.0f;

            switch (preset)
            {
                case OwnReverbPreset.SmallRoom:
                    PreDelay = 8f;  Decay = 0.6f;  Size = 0.45f; Damping = 0.65f; LowDamping = 0.30f;
                    Diffusion = 0.60f; ModRate = 1.2f;  ModDepth = 0.20f; Width = 0.80f;
                    EarlyLevel = 0.55f; LateLevel = 0.85f; Mix = 0.18f; break;
                case OwnReverbPreset.Chamber:
                    PreDelay = 18f; Decay = 1.6f;  Size = 0.80f; Damping = 0.45f; LowDamping = 0.20f;
                    Diffusion = 0.75f; ModRate = 0.9f;  ModDepth = 0.25f; Width = 1.00f;
                    EarlyLevel = 0.45f; LateLevel = 0.95f; Mix = 0.24f; break;
                case OwnReverbPreset.LargeHall:
                    PreDelay = 35f; Decay = 3.8f;  Size = 1.50f; Damping = 0.42f; LowDamping = 0.12f;
                    Diffusion = 0.80f; ModRate = 0.5f;  ModDepth = 0.45f; Width = 1.40f;
                    EarlyLevel = 0.30f; Mix = 0.30f; break;
                case OwnReverbPreset.Cathedral:
                    PreDelay = 60f; Decay = 8.5f;  Size = 2.00f; Damping = 0.22f; LowDamping = 0.08f;
                    Diffusion = 0.85f; ModRate = 0.3f;  ModDepth = 0.35f; Width = 1.50f;
                    EarlyLevel = 0.22f; Mix = 0.38f; break;
                case OwnReverbPreset.Plate:
                    PreDelay = 6f;  Decay = 1.9f;  Size = 0.60f; Damping = 0.20f; LowDamping = 0.35f;
                    Diffusion = 0.95f; ModRate = 1.6f;  ModDepth = 0.30f; Width = 1.10f;
                    EarlyLevel = 0.0f; Mix = 0.26f; break;
                case OwnReverbPreset.VocalPlate:
                    PreDelay = 25f; Decay = 1.7f;  Size = 0.65f; Damping = 0.30f; LowDamping = 0.45f;
                    Diffusion = 0.92f; ModRate = 1.3f;  ModDepth = 0.35f; Width = 1.00f;
                    EarlyLevel = 0.05f; Mix = 0.24f;
                    DuckDepth = 0.35f; DuckAttack = 12f; DuckRelease = 220f; break;
                case OwnReverbPreset.DrumRoom:
                    PreDelay = 5f;  Decay = 0.9f;  Size = 0.70f; Damping = 0.55f; LowDamping = 0.18f;
                    Diffusion = 0.65f; ModRate = 1.0f;  ModDepth = 0.15f; Width = 1.15f;
                    EarlyLevel = 0.70f; LateLevel = 0.80f; Mix = 0.22f; break;
                case OwnReverbPreset.Gated:
                    PreDelay = 2f;  Decay = 0.35f; Size = 0.55f; Damping = 0.75f; LowDamping = 0.30f;
                    Diffusion = 0.50f; ModRate = 0.8f;  ModDepth = 0.10f; Width = 1.00f;
                    EarlyLevel = 0.60f; Mix = 0.30f; break;
                case OwnReverbPreset.Bloom:
                    PreDelay = 45f; Decay = 6.0f;  Size = 1.60f; Damping = 0.40f; LowDamping = 0.20f;
                    Diffusion = 0.88f; ModRate = 0.45f; ModDepth = 0.90f; Width = 1.60f;
                    EarlyLevel = 0.15f; Mix = 0.42f;
                    DuckDepth = 0.45f; DuckAttack = 8f; DuckRelease = 400f; break;
                case OwnReverbPreset.AmbientPad:
                    PreDelay = 80f; Decay = 12.0f; Size = 1.80f; Damping = 0.35f; LowDamping = 0.25f;
                    Diffusion = 0.90f; ModRate = 0.35f; ModDepth = 0.80f; Width = 1.50f;
                    EarlyLevel = 0.10f; Mix = 0.50f; break;
                case OwnReverbPreset.DuckedVocal:
                    PreDelay = 30f; Decay = 3.0f;  Size = 1.10f; Damping = 0.45f; LowDamping = 0.40f;
                    Diffusion = 0.85f; ModRate = 0.7f;  ModDepth = 0.40f; Width = 1.30f;
                    EarlyLevel = 0.10f; Mix = 0.35f;
                    DuckDepth = 0.80f; DuckAttack = 6f; DuckRelease = 300f; break;
                case OwnReverbPreset.InfiniteHold:
                    PreDelay = 0f;  Decay = 20.0f; Size = 1.60f; Damping = 0.30f; LowDamping = 0.10f;
                    Diffusion = 0.90f; ModRate = 0.25f; ModDepth = 0.60f; Width = 1.60f;
                    EarlyLevel = 0.0f; Mix = 0.60f; break;
                case OwnReverbPreset.Subtle:
                    PreDelay = 12f; Decay = 1.1f;  Size = 0.60f; Damping = 0.60f; LowDamping = 0.35f;
                    Diffusion = 0.80f; ModRate = 0.9f;  ModDepth = 0.20f; Width = 0.90f;
                    EarlyLevel = 0.30f; LateLevel = 0.90f; Mix = 0.08f; break;
                default:
                    PreDelay = 20f; Decay = 2.5f;  Size = 1.00f; Damping = 0.50f; LowDamping = 0.15f;
                    Diffusion = 0.70f; ModRate = 0.8f;  ModDepth = 0.40f; Width = 1.20f;
                    EarlyLevel = 0.35f; Mix = 0.30f; break;
            }
        }

        /// <summary>
        /// Nothing to set up on this side, the native twin is sized by the mixer.
        /// </summary>
        /// <param name="config"></param>
        public void Initialize(AudioConfig config)
        {
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
        /// Bumps the generation so the mixer flushes the native tail.
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

        private static float FastClamp(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString()
        {
            return $"OwnReverb: Decay={_decay:F2}s, Size={_size:F2}, Damp={_damping:F2}, Duck={_duckDepth:F2}, Mix={_mix:F2}";
        }
    }
}
