using System;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Ready made delay setups. Each one sets every parameter, so a preset never inherits
    /// ducking, freeze or modulation from the previous state.
    /// </summary>
    public enum OwnDelayPreset
    {
        /// <summary>
        /// Clean stereo delay, 375 / 500 ms, moderate feedback.
        /// </summary>
        Default,

        /// <summary>
        /// Single short slap, rockabilly and vocal thickening.
        /// </summary>
        Slapback,

        /// <summary>
        /// Ping-pong: the echoes bounce between left and right.
        /// </summary>
        PingPong,

        /// <summary>
        /// Warm tape echo: driven loop, narrow band, a little wow.
        /// </summary>
        TapeEcho,

        /// <summary>
        /// Dub: long dark repeats that feed back hard and saturate.
        /// </summary>
        DubEcho,

        /// <summary>
        /// Diffused, reverb-like wash of repeats.
        /// </summary>
        Ambient,

        /// <summary>
        /// Vocal delay that ducks under the singer and blooms in the gaps.
        /// </summary>
        DuckedVocal,

        /// <summary>
        /// Worn cassette: heavy wow, narrow band and hard saturation.
        /// </summary>
        LoFi,

        /// <summary>
        /// Short, modulated doubler for width.
        /// </summary>
        Doubler
    }

    /// <summary>
    /// How a delay time change is played.
    /// </summary>
    public enum OwnDelayTimeMode
    {
        /// <summary>
        /// Tape style: the head glides to the new time and the repeats bend in pitch.
        /// </summary>
        Glide = 0,

        /// <summary>
        /// A second head fades in at the new time (40 ms equal power), no pitch artefact.
        /// </summary>
        Crossfade = 1
    }

    /// <summary>
    /// Note values for tempo sync.
    /// </summary>
    public enum OwnDelayNoteValue
    {
        /// <summary>1/1</summary>
        Whole,
        /// <summary>1/2</summary>
        Half,
        /// <summary>1/4</summary>
        Quarter,
        /// <summary>1/8</summary>
        Eighth,
        /// <summary>1/16</summary>
        Sixteenth,
        /// <summary>Dotted 1/2</summary>
        DottedHalf,
        /// <summary>Dotted 1/4</summary>
        DottedQuarter,
        /// <summary>Dotted 1/8</summary>
        DottedEighth,
        /// <summary>Dotted 1/16</summary>
        DottedSixteenth,
        /// <summary>1/4 triplet</summary>
        TripletQuarter,
        /// <summary>1/8 triplet</summary>
        TripletEighth,
        /// <summary>1/16 triplet</summary>
        TripletSixteenth
    }

    /// <summary>
    /// Tape style stereo delay: in-loop saturation, diffusion and filters, ping-pong, ducking,
    /// wow and freeze. The DSP lives in the Rust engine - this is just the parameter model.
    /// </summary>
    public sealed class OwnDelayEffect : NativeBackedEffect, IEffectProcessor
    {
        private const float MinTimeMs = 15f;
        private const float MaxTimeMs = 4000f;

        private float _mix = 0.3f;
        private float _timeLeft = 375f;
        private float _timeRight = 500f;
        private float _feedback = 0.45f;
        private float _crossFeedback;
        private OwnDelayTimeMode _timeMode = OwnDelayTimeMode.Glide;
        private float _glide = 150f;
        private float _drive;
        private float _lowCut = 80f;
        private float _highCut = 8000f;
        private float _diffusion;
        private float _modRate = 0.6f;
        private float _modDepth;
        private float _duckAmount;
        private float _duckThreshold = -30f;
        private float _duckAttack = 10f;
        private float _duckRelease = 250f;
        private float _width = 1f;
        private bool _freeze;

        /// <summary>
        /// Builds the delay on its clean default setup.
        /// </summary>
        public OwnDelayEffect()
            : base("OwnDelay")
        {
        }

        /// <summary>
        /// Builds the delay straight from a preset.
        /// </summary>
        /// <param name="preset">The setup to load.</param>
        public OwnDelayEffect(OwnDelayPreset preset) : this()
        {
            SetPreset(preset);
        }

        #region Properties

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name { get => _name; set => _name = value ?? "OwnDelay"; }

        /// <summary>
        /// Dry/wet, 0.0 - 1.0.
        /// </summary>
        public float Mix
        {
            get => _mix;
            set => _mix = FastClamp(value, 0f, 1f);
        }

        /// <summary>
        /// Left delay time in ms, 15 - 4000.
        /// </summary>
        public float TimeLeft
        {
            get => _timeLeft;
            set => _timeLeft = FastClamp(value, MinTimeMs, MaxTimeMs);
        }

        /// <summary>
        /// Right delay time in ms, 15 - 4000.
        /// </summary>
        public float TimeRight
        {
            get => _timeRight;
            set => _timeRight = FastClamp(value, MinTimeMs, MaxTimeMs);
        }

        /// <summary>
        /// Feedback, 0.0 - 1.0. Feedback plus CrossFeedback is normalised to at most 1,
        /// so the loop can never run away.
        /// </summary>
        public float Feedback
        {
            get => _feedback;
            set => _feedback = FastClamp(value, 0f, 1f);
        }

        /// <summary>
        /// Cross feedback between the channels, 0.0 - 1.0. Feedback 0 with CrossFeedback
        /// high gives ping-pong.
        /// </summary>
        public float CrossFeedback
        {
            get => _crossFeedback;
            set => _crossFeedback = FastClamp(value, 0f, 1f);
        }

        /// <summary>
        /// Glide (tape pitch bend) or crossfade on time changes.
        /// </summary>
        public OwnDelayTimeMode TimeMode
        {
            get => _timeMode;
            set => _timeMode = value;
        }

        /// <summary>
        /// Glide time constant in ms, 5 - 2000. Only used in Glide mode.
        /// </summary>
        public float Glide
        {
            get => _glide;
            set => _glide = FastClamp(value, 5f, 2000f);
        }

        /// <summary>
        /// Saturation drive in dB, 0 - 24. At 0 the loop is clean up to +3.5 dBFS; above
        /// 0 the anti-aliased tape saturation kicks in. The drive is gain compensated, so
        /// quiet repeats keep their level and loud ones get squashed.
        /// </summary>
        public float Drive
        {
            get => _drive;
            set => _drive = FastClamp(value, 0f, 24f);
        }

        /// <summary>
        /// Low cut inside the loop in Hz, 20 - 2000. Every repeat gets thinner.
        /// </summary>
        public float LowCut
        {
            get => _lowCut;
            set => _lowCut = FastClamp(value, 20f, 2000f);
        }

        /// <summary>
        /// High cut inside the loop in Hz, 500 - 20000. Every repeat gets darker.
        /// </summary>
        public float HighCut
        {
            get => _highCut;
            set => _highCut = FastClamp(value, 500f, 20000f);
        }

        /// <summary>
        /// Diffusion, 0.0 - 1.0. Smears the repeats towards a reverb-like wash.
        /// </summary>
        public float Diffusion
        {
            get => _diffusion;
            set => _diffusion = FastClamp(value, 0f, 1f);
        }

        /// <summary>
        /// Wow rate in Hz, 0.05 - 10.
        /// </summary>
        public float ModRate
        {
            get => _modRate;
            set => _modRate = FastClamp(value, 0.05f, 10f);
        }

        /// <summary>
        /// Wow depth in ms, 0 - 5. Left and right run 90 degrees apart.
        /// </summary>
        public float ModDepth
        {
            get => _modDepth;
            set => _modDepth = FastClamp(value, 0f, 5f);
        }

        /// <summary>
        /// Ducking amount, 0.0 (off) - 1.0. The dry input pushes the wet down.
        /// </summary>
        public float DuckAmount
        {
            get => _duckAmount;
            set => _duckAmount = FastClamp(value, 0f, 1f);
        }

        /// <summary>
        /// Ducking threshold in dBFS, -60 - 0. Full ducking is reached 12 dB above it.
        /// </summary>
        public float DuckThreshold
        {
            get => _duckThreshold;
            set => _duckThreshold = FastClamp(value, -60f, 0f);
        }

        /// <summary>
        /// Ducker attack in ms, 0.5 - 200.
        /// </summary>
        public float DuckAttack
        {
            get => _duckAttack;
            set => _duckAttack = FastClamp(value, 0.5f, 200f);
        }

        /// <summary>
        /// Ducker release in ms, 10 - 2000.
        /// </summary>
        public float DuckRelease
        {
            get => _duckRelease;
            set => _duckRelease = FastClamp(value, 10f, 2000f);
        }

        /// <summary>
        /// Stereo width of the wet signal, 0.0 (mono) - 2.0.
        /// </summary>
        public float Width
        {
            get => _width;
            set => _width = FastClamp(value, 0f, 2f);
        }

        /// <summary>
        /// Holds the loop forever: input muted, loop gain 1, filters bypassed.
        /// </summary>
        public bool Freeze
        {
            get => _freeze;
            set => _freeze = value;
        }

        /// <summary>
        /// Ducker gain of the last processed block, linear. Only moves while this instance
        /// is processed directly, not as a mixer twin.
        /// </summary>
        public float DuckGain => _native.GetParam(NativeEffectEngine.MeterCurrentGain) ?? 1f;

        #endregion

        /// <summary>
        /// Converts a note value at a tempo into milliseconds.
        /// </summary>
        /// <param name="bpm">Tempo in beats per minute.</param>
        /// <param name="note">Note value.</param>
        /// <returns>Duration in ms.</returns>
        public static float NoteToMilliseconds(double bpm, OwnDelayNoteValue note)
        {
            double _quarter = 60000.0 / Math.Max(1.0, bpm);
            double _factor = note switch
            {
                OwnDelayNoteValue.Whole => 4.0,
                OwnDelayNoteValue.Half => 2.0,
                OwnDelayNoteValue.Quarter => 1.0,
                OwnDelayNoteValue.Eighth => 0.5,
                OwnDelayNoteValue.Sixteenth => 0.25,
                OwnDelayNoteValue.DottedHalf => 3.0,
                OwnDelayNoteValue.DottedQuarter => 1.5,
                OwnDelayNoteValue.DottedEighth => 0.75,
                OwnDelayNoteValue.DottedSixteenth => 0.375,
                OwnDelayNoteValue.TripletQuarter => 2.0 / 3.0,
                OwnDelayNoteValue.TripletEighth => 1.0 / 3.0,
                OwnDelayNoteValue.TripletSixteenth => 1.0 / 6.0,
                _ => 1.0
            };
            return (float)(_quarter * _factor);
        }

        /// <summary>
        /// Sets both delay times from a tempo and note values.
        /// </summary>
        /// <param name="bpm">Tempo in beats per minute.</param>
        /// <param name="left">Note value of the left channel.</param>
        /// <param name="right">Note value of the right channel.</param>
        public void SyncToTempo(double bpm, OwnDelayNoteValue left, OwnDelayNoteValue right)
        {
            TimeLeft = NoteToMilliseconds(bpm, left);
            TimeRight = NoteToMilliseconds(bpm, right);
        }

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset">The setup to load.</param>
        public void SetPreset(OwnDelayPreset preset)
        {
            // Reset the extras first, nobody wants a stuck freeze or a random ducker
            // hanging around after switching from DuckedVocal to Slapback.
            CrossFeedback = 0f; TimeMode = OwnDelayTimeMode.Glide; Glide = 150f; Drive = 0f;
            Diffusion = 0f; ModRate = 0.6f; ModDepth = 0f; DuckAmount = 0f; DuckThreshold = -30f;
            DuckAttack = 10f; DuckRelease = 250f; Width = 1f; Freeze = false;

            switch (preset)
            {
                case OwnDelayPreset.Slapback:
                    TimeLeft = 110f; TimeRight = 125f; Feedback = 0.1f; LowCut = 120f; HighCut = 6000f;
                    Drive = 3f; Mix = 0.25f; break;
                case OwnDelayPreset.PingPong:
                    TimeLeft = 375f; TimeRight = 375f; Feedback = 0f; CrossFeedback = 0.55f;
                    LowCut = 150f; HighCut = 7000f; Width = 1.5f; Mix = 0.3f; break;
                case OwnDelayPreset.TapeEcho:
                    TimeLeft = 330f; TimeRight = 345f; Feedback = 0.5f; LowCut = 200f; HighCut = 4500f;
                    Drive = 9f; ModRate = 0.8f; ModDepth = 0.6f; Glide = 300f; Mix = 0.3f; break;
                case OwnDelayPreset.DubEcho:
                    TimeLeft = 500f; TimeRight = 750f; Feedback = 0.8f; CrossFeedback = 0.1f;
                    LowCut = 250f; HighCut = 2500f; Drive = 12f; ModRate = 0.4f; ModDepth = 0.8f;
                    Glide = 600f; Mix = 0.35f; break;
                case OwnDelayPreset.Ambient:
                    TimeLeft = 450f; TimeRight = 600f; Feedback = 0.6f; CrossFeedback = 0.2f;
                    LowCut = 200f; HighCut = 6000f; Diffusion = 0.8f; ModRate = 0.3f; ModDepth = 1.2f;
                    Width = 1.6f; Mix = 0.4f; break;
                case OwnDelayPreset.DuckedVocal:
                    TimeLeft = 375f; TimeRight = 500f; Feedback = 0.4f; LowCut = 180f; HighCut = 6500f;
                    DuckAmount = 0.8f; DuckThreshold = -30f; DuckAttack = 5f; DuckRelease = 300f;
                    Mix = 0.35f; break;
                case OwnDelayPreset.LoFi:
                    TimeLeft = 280f; TimeRight = 300f; Feedback = 0.55f; LowCut = 400f; HighCut = 2200f;
                    Drive = 15f; ModRate = 1.4f; ModDepth = 2.5f; Mix = 0.35f; break;
                case OwnDelayPreset.Doubler:
                    TimeLeft = 18f; TimeRight = 27f; Feedback = 0f; LowCut = 100f; HighCut = 12000f;
                    ModRate = 0.9f; ModDepth = 0.4f; Width = 2f; Mix = 0.4f; break;
                default:
                    TimeLeft = 375f; TimeRight = 500f; Feedback = 0.45f; LowCut = 80f; HighCut = 8000f;
                    Mix = 0.3f; break;
            }
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString()
        {
            return $"OwnDelay: L={_timeLeft:F0}ms, R={_timeRight:F0}ms, Fb={_feedback:F2}, " +
                   $"XFb={_crossFeedback:F2}, Drive={_drive:F1}dB, Mix={_mix:F2}";
        }

        private static float FastClamp(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }
}
