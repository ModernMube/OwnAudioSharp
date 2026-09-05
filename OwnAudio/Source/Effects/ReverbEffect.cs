using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Reverb setups for the usual spaces.
    /// </summary>
    public enum ReverbPreset
    {
        /// <summary>
        /// Balanced starting point.
        /// </summary>
        Default,

        /// <summary>
        /// Tight room, short tail.
        /// </summary>
        SmallRoom,

        /// <summary>
        /// Concert hall, wide and spacious.
        /// </summary>
        LargeHall,

        /// <summary>
        /// Very long tail with open highs.
        /// </summary>
        Cathedral,

        /// <summary>
        /// EMT 140 flavour: dense and bright.
        /// </summary>
        Plate,

        /// <summary>
        /// Spring tank, metallic and short.
        /// </summary>
        Spring,

        /// <summary>
        /// Endless wash for pads.
        /// </summary>
        AmbientPad,

        /// <summary>
        /// Tiny damped booth, intimate.
        /// </summary>
        VocalBooth,

        /// <summary>
        /// Punchy live room for drums.
        /// </summary>
        DrumRoom,

        /// <summary>
        /// 80s gated sound, heavy damping.
        /// </summary>
        Gated,

        /// <summary>
        /// Glue only, you shouldn't really hear it.
        /// </summary>
        Subtle
    }

    /// <summary>
    /// Freeverb style reverb: 8 damped comb filters into 4 all-passes per side,
    /// with pre-delay and stereo spread. Nothing allocates while it runs.
    /// </summary>
    public sealed class ReverbEffect : IEffectProcessor
    {
        private readonly Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        private readonly NativeEffectEngine _native = new NativeEffectEngine();
        private AudioConfig? _config;

        private float _roomSize = 0.55f;
        private float _damping = 0.5f;
        private float _wet = 0.33f;
        private float _dry = 1.0f;
        private float _width = 1.0f;
        private float _gain = 1.0f;
        private float _mix = 0.25f;

        /// <summary>
        /// Instance id.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name { get => _name; set => _name = value ?? "Reverb"; }

        /// <summary>
        /// On/off switch.
        /// </summary>
        public bool Enabled { get => _enabled; set => _enabled = value; }

        /// <summary>
        /// Room size, bigger means a longer tail.
        /// </summary>
        public float RoomSize
        {
            get => _roomSize;
            set { _roomSize = FastClamp(value, 0f, 1f); }
        }

        /// <summary>
        /// Damping, higher means a darker tail.
        /// </summary>
        public float Damping
        {
            get => _damping;
            set { _damping = FastClamp(value, 0f, 1f); }
        }

        /// <summary>
        /// How much reverb is sent to the output. With the default unity DryLevel the dry
        /// signal stays untouched, so this behaves like a send amount: 0.25 is a normal
        /// insert setting, above 0.5 you are in wash territory.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = FastClamp(value, 0f, 1f);
        }

        /// <summary>
        /// Stereo width, 0 - 2.
        /// </summary>
        public float Width
        {
            get => _width;
            set { _width = FastClamp(value, 0f, 2f); }
        }

        /// <summary>
        /// Old name of Width, kept so existing code keeps compiling.
        /// </summary>
        public float StereoWidth
        {
            get => _width;
            set => Width = value;
        }

        /// <summary>
        /// Tail trim inside the blend. 0.33 is the Freeverb nominal, the presets leave it
        /// there and set the amount with Mix instead.
        /// </summary>
        public float WetLevel
        {
            get => _wet;
            set { _wet = FastClamp(value, 0f, 1f); }
        }

        /// <summary>
        /// Dry trim inside the blend. Kept at 1.0 so the source never dips when you open
        /// up the Mix — drop it only if you want that classic wet/dry crossfade.
        /// </summary>
        public float DryLevel
        {
            get => _dry;
            set => _dry = FastClamp(value, 0f, 1f);
        }

        /// <summary>
        /// Input gain in front of the tank.
        /// </summary>
        [Obsolete("The native reverb has no input-gain param and the whole audio path is native now, " +
            "so this reaches nothing. Drive the send with Mix instead.")]
        public float Gain
        {
            get => _gain;
            set => _gain = Math.Max(0f, value);
        }

        /// <summary>
        /// Builds the reverb with hand picked values: a medium room at roughly 25% wet,
        /// which is where you'd park an insert reverb before touching anything.
        /// </summary>
        public ReverbEffect(float size = 0.55f, float damp = 0.5f, float wet = 0.33f, float dry = 1.0f, float stereoWidth = 1.0f, float mix = 0.25f, float gainLevel = 1.0f)
        {
            _id = Guid.NewGuid();
            _name = "Reverb";
            _enabled = true;

            _roomSize = size;
            _damping = damp;
            _wet = wet;
            _dry = dry;
            _width = stereoWidth;
            _mix = mix;
            _gain = gainLevel;
        }

        /// <summary>
        /// Builds the reverb from a preset.
        /// </summary>
        /// <param name="preset"></param>
        public ReverbEffect(ReverbPreset preset) : this()
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Hands the rate and channel layout to the native tank.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
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
        /// Loads one of the canned spaces. Every preset keeps the dry at unity and the tail
        /// at the Freeverb nominal, so the only thing that moves is Mix — the wet amounts
        /// sit where an engineer would leave them: a few percent for glue, around a quarter
        /// for a normal room, half only for the deliberate wash.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(ReverbPreset preset)
        {
            WetLevel = 0.33f;
            DryLevel = 1.0f;

            switch (preset)
            {
                case ReverbPreset.SmallRoom:  RoomSize=0.30f; Damping=0.62f; Width=0.70f; Mix=0.18f; break;
                case ReverbPreset.LargeHall:  RoomSize=0.85f; Damping=0.42f; Width=1.0f;  Mix=0.30f; break;
                case ReverbPreset.Cathedral:  RoomSize=0.94f; Damping=0.16f; Width=1.0f;  Mix=0.38f; break;
                case ReverbPreset.Plate:      RoomSize=0.62f; Damping=0.18f; Width=0.85f; Mix=0.26f; break;
                case ReverbPreset.Spring:     RoomSize=0.42f; Damping=0.72f; Width=0.55f; Mix=0.22f; break;
                case ReverbPreset.AmbientPad: RoomSize=0.92f; Damping=0.25f; Width=1.0f;  Mix=0.50f; break;
                case ReverbPreset.VocalBooth: RoomSize=0.18f; Damping=0.88f; Width=0.40f; Mix=0.12f; break;
                case ReverbPreset.DrumRoom:   RoomSize=0.58f; Damping=0.58f; Width=0.95f; Mix=0.20f; break;
                case ReverbPreset.Gated:      RoomSize=0.70f; Damping=0.90f; Width=1.0f;  Mix=0.28f; break;
                case ReverbPreset.Subtle:     RoomSize=0.28f; Damping=0.72f; Width=0.75f; Mix=0.08f; break;
                default:                      RoomSize=0.55f; Damping=0.50f; Width=1.0f;  Mix=0.25f; break;
            }
        }

        /// <summary>
        /// Ticks up on every Reset, that is how the native twin hears about it.
        /// </summary>
        public int ResetGeneration { get; private set; }

        /// <summary>
        /// Empties the whole tank.
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
            Reset();
            _native.Dispose();
            _disposed = true;
        }

        /// <summary>
        /// Branch based clamp, cheaper than Math.Clamp in these loops.
        /// </summary>
        private static float FastClamp(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString()
        {
            return $"Reverb: Room={_roomSize:F2}, Damp={_damping:F2}, Width={_width:F2}, Mix={_mix:F2}";
        }
    }
}
