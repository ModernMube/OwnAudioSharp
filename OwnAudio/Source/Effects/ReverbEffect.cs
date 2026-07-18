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
        private AudioConfig? _config;

        private const int NumCombs = 8;
        private const int NumAllPasses = 4;
        private const float ScaleWet = 3.0f;
        private const float ScaleDamp = 0.4f;
        private const float ScaleRoom = 0.28f;
        private const float OffsetRoom = 0.7f;

        /// <summary>
        /// The right side runs on slightly longer lines, that's what makes it stereo.
        /// </summary>
        private const int StereoSpread = 23;

        private static readonly int[] CombTuningL = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        private static readonly int[] CombTuningR = { 1116 + StereoSpread, 1188 + StereoSpread, 1277 + StereoSpread, 1356 + StereoSpread, 1422 + StereoSpread, 1491 + StereoSpread, 1557 + StereoSpread, 1617 + StereoSpread };
        private static readonly int[] AllPassTuningL = { 556, 441, 341, 225 };
        private static readonly int[] AllPassTuningR = { 556 + StereoSpread, 441 + StereoSpread, 341 + StereoSpread, 225 + StereoSpread };

        /// <summary>
        /// Comb state, indexed as [channel][filter]. FilterStore is the damping lowpass memory.
        /// </summary>
        private readonly float[][][] _combBuffers;
        private readonly int[][] _combIndices;
        private readonly int[][] _combLengths;
        private readonly float[][] _combFilterStore;

        /// <summary>
        /// All-pass state, same layout as the combs.
        /// </summary>
        private readonly float[][][] _allPassBuffers;
        private readonly int[][] _allPassIndices;
        private readonly int[][] _allPassLengths;

        /// <summary>
        /// 20ms pre-delay ring in front of the tank.
        /// </summary>
        private float[]? _preDelayBuffer;
        private int _preDelayIndex;
        private int _preDelayLength;

        private float _roomSize = 0.5f;
        private float _damping = 0.5f;
        private float _wet = 0.33f;
        private float _dry = 0.67f;
        private float _width = 1.0f;
        private float _gain = 1.0f;
        private float _mix = 0.5f;
        private float _sampleRate = 44100f;

        private float _roomSizeVal;
        private float _dampVal;
        private float _wet1;
        private float _wet2;

        private readonly object _lock = new object();

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
            set { _roomSize = FastClamp(value, 0f, 1f); _updateCoeffs(); }
        }

        /// <summary>
        /// Damping, higher means a darker tail.
        /// </summary>
        public float Damping
        {
            get => _damping;
            set { _damping = FastClamp(value, 0f, 1f); _updateCoeffs(); }
        }

        /// <summary>
        /// Master dry to wet balance.
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
            set { _width = FastClamp(value, 0f, 2f); _updateCoeffs(); }
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
        /// Wet level inside the blend.
        /// </summary>
        public float WetLevel
        {
            get => _wet;
            set { _wet = FastClamp(value, 0f, 1f); _updateCoeffs(); }
        }

        /// <summary>
        /// Dry level inside the blend.
        /// </summary>
        public float DryLevel
        {
            get => _dry;
            set => _dry = FastClamp(value, 0f, 1f);
        }

        /// <summary>
        /// Input gain in front of the tank.
        /// </summary>
        public float Gain
        {
            get => _gain;
            set => _gain = Math.Max(0f, value);
        }

        /// <summary>
        /// Builds the reverb with hand picked values. Buffers are sized for 44.1k here,
        /// Initialize rebuilds them for the real rate.
        /// </summary>
        public ReverbEffect(float size = 0.5f, float damp = 0.5f, float wet = 0.33f, float dry = 0.67f, float stereoWidth = 1.0f, float mix = 0.5f, float gainLevel = 1.0f)
        {
            _id = Guid.NewGuid();
            _name = "Reverb";
            _enabled = true;

            _combBuffers = new float[2][][];
            _combIndices = new int[2][];
            _combLengths = new int[2][];
            _combFilterStore = new float[2][];

            _allPassBuffers = new float[2][][];
            _allPassIndices = new int[2][];
            _allPassLengths = new int[2][];

            for (int ch = 0; ch < 2; ch++)
            {
                _combBuffers[ch] = new float[NumCombs][];
                _combIndices[ch] = new int[NumCombs];
                _combLengths[ch] = new int[NumCombs];
                _combFilterStore[ch] = new float[NumCombs];

                _allPassBuffers[ch] = new float[NumAllPasses][];
                _allPassIndices[ch] = new int[NumAllPasses];
                _allPassLengths[ch] = new int[NumAllPasses];
            }

            _roomSize = size;
            _damping = damp;
            _wet = wet;
            _dry = dry;
            _width = stereoWidth;
            _mix = mix;
            _gain = gainLevel;

            _resizeBuffers(44100);
            _updateCoeffs();
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
        /// Takes the engine config, rebuilds the tank if the rate changed.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            if (Math.Abs(_sampleRate - config.SampleRate) > 1.0f)
            {
                lock (_lock)
                {
                    _sampleRate = config.SampleRate;
                    _resizeBuffers(_sampleRate);
                    Reset();
                }
            }
        }

        /// <summary>
        /// Reallocates every delay line, the standard tunings scaled to the new rate.
        /// </summary>
        private void _resizeBuffers(float newSampleRate)
        {
            float scale = newSampleRate / 44100f;

            _preDelayLength = (int)(0.020f * newSampleRate);
            _preDelayBuffer = new float[_preDelayLength];
            _preDelayIndex = 0;

            for (int ch = 0; ch < 2; ch++)
            {
                for (int i = 0; i < NumCombs; i++)
                {
                    int size = (int)((ch == 0 ? CombTuningL[i] : CombTuningR[i]) * scale);
                    _combBuffers[ch][i] = new float[size];
                    _combLengths[ch][i] = size;
                    _combIndices[ch][i] = 0;
                    _combFilterStore[ch][i] = 0;
                }

                for (int i = 0; i < NumAllPasses; i++)
                {
                    int size = (int)((ch == 0 ? AllPassTuningL[i] : AllPassTuningR[i]) * scale);
                    _allPassBuffers[ch][i] = new float[size];
                    _allPassLengths[ch][i] = size;
                    _allPassIndices[ch][i] = 0;
                }
            }
        }

        /// <summary>
        /// Rebuilds the scaled room/damp values and the two stereo wet gains.
        /// </summary>
        private void _updateCoeffs()
        {
            _roomSizeVal = (_roomSize * ScaleRoom) + OffsetRoom;
            _dampVal = _damping * ScaleDamp;

            _wet1 = _wet * (0.5f * _width + 0.5f);
            _wet2 = _wet * ((1.0f - _width) * 0.5f);
        }

        /// <summary>
        /// Sums the input to mono, runs it through the pre-delay and the tank,
        /// then spreads the result back to stereo.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null || !_enabled || _preDelayBuffer == null) return;

            float room = _roomSizeVal;
            float damp = _dampVal;
            float g = _gain;
            float dry = _dry;
            float mix = _mix;
            float w1 = _wet1;
            float w2 = _wet2;

            int channels = _config.Channels;
            bool isStereo = channels >= 2;

            for (int frame = 0; frame < frameCount; frame++)
            {
                int idx = frame * channels;

                float inputL = buffer[idx];
                float inputR = isStereo ? buffer[idx + 1] : inputL;

                float mono = (inputL + inputR) * 0.5f * g;

                float delayedInput = _preDelayBuffer[_preDelayIndex];
                _preDelayBuffer[_preDelayIndex] = mono;
                _preDelayIndex++;
                if (_preDelayIndex >= _preDelayLength) _preDelayIndex = 0;

                mono = delayedInput;

                float outL = 0f;
                float outR = 0f;

                for(int i = 0; i < NumCombs; i++)
                {
                    float[] bufL = _combBuffers[0][i];
                    int iL = _combIndices[0][i];
                    float oL = bufL[iL];
                    _combFilterStore[0][i] = oL * (1.0f - damp) + _combFilterStore[0][i] * damp;
                    bufL[iL] = mono + _combFilterStore[0][i] * room;
                    if (++iL >= _combLengths[0][i]) iL = 0;
                    _combIndices[0][i] = iL;
                    outL += oL;

                    float[] bufR = _combBuffers[1][i];
                    int iR = _combIndices[1][i];
                    float oR = bufR[iR];
                    _combFilterStore[1][i] = oR * (1.0f - damp) + _combFilterStore[1][i] * damp;
                    bufR[iR] = mono + _combFilterStore[1][i] * room;
                    if (++iR >= _combLengths[1][i]) iR = 0;
                    _combIndices[1][i] = iR;
                    outR += oR;
                }

                for(int i = 0; i < NumAllPasses; i++)
                {
                    float[] bufL = _allPassBuffers[0][i];
                    int iL = _allPassIndices[0][i];
                    float storedL = bufL[iL];
                    bufL[iL] = outL + storedL * 0.5f;
                    outL = storedL - outL * 0.5f;
                    if (++iL >= _allPassLengths[0][i]) iL = 0;
                    _allPassIndices[0][i] = iL;

                    float[] bufR = _allPassBuffers[1][i];
                    int iR = _allPassIndices[1][i];
                    float storedR = bufR[iR];
                    bufR[iR] = outR + storedR * 0.5f;
                    outR = storedR - outR * 0.5f;
                    if (++iR >= _allPassLengths[1][i]) iR = 0;
                    _allPassIndices[1][i] = iR;
                }

                float wetL = (outL * w1 + outR * w2) * ScaleWet;
                float wetR = (outR * w1 + outL * w2) * ScaleWet;

                buffer[idx] = inputL * (1.0f - mix) + (inputL * dry + wetL) * mix;
                if (isStereo) buffer[idx + 1] = inputR * (1.0f - mix) + (inputR * dry + wetR) * mix;
            }
        }

        /// <summary>
        /// Loads one of the canned spaces.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(ReverbPreset preset)
        {
            switch (preset)
            {
                case ReverbPreset.SmallRoom:
                    RoomSize = 0.30f; Damping = 0.65f; Width = 0.6f; WetLevel = 0.18f; DryLevel = 0.90f;
                    break;
                case ReverbPreset.LargeHall:
                    RoomSize = 0.85f; Damping = 0.45f; Width = 1.0f; WetLevel = 0.45f; DryLevel = 0.70f;
                    break;
                case ReverbPreset.Cathedral:
                    RoomSize = 0.95f; Damping = 0.12f; Width = 1.0f; WetLevel = 0.60f; DryLevel = 0.55f;
                    break;
                case ReverbPreset.Plate:
                    RoomSize = 0.62f; Damping = 0.08f; Width = 0.85f; WetLevel = 0.38f; DryLevel = 0.82f;
                    break;
                case ReverbPreset.Spring:
                    RoomSize = 0.42f; Damping = 0.75f; Width = 0.55f; WetLevel = 0.28f; DryLevel = 0.85f;
                    break;
                case ReverbPreset.AmbientPad:
                    RoomSize = 0.92f; Damping = 0.20f; Width = 1.0f; WetLevel = 0.70f; DryLevel = 0.35f;
                    break;
                case ReverbPreset.VocalBooth:
                    RoomSize = 0.18f; Damping = 0.90f; Width = 0.35f; WetLevel = 0.12f; DryLevel = 0.95f;
                    break;
                case ReverbPreset.DrumRoom:
                    RoomSize = 0.65f; Damping = 0.55f; Width = 0.95f; WetLevel = 0.32f; DryLevel = 0.75f;
                    break;
                case ReverbPreset.Gated:
                    RoomSize = 0.72f; Damping = 0.92f; Width = 1.0f; WetLevel = 0.42f; DryLevel = 0.72f;
                    break;
                case ReverbPreset.Subtle:
                    RoomSize = 0.28f; Damping = 0.72f; Width = 0.75f; WetLevel = 0.10f; DryLevel = 0.96f;
                    break;
                default:
                    RoomSize = 0.50f; Damping = 0.50f; Width = 1.0f; WetLevel = 0.30f; DryLevel = 0.80f;
                    break;
            }
        }

        /// <summary>
        /// Empties the whole tank.
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                if (_preDelayBuffer != null) Array.Clear(_preDelayBuffer, 0, _preDelayBuffer.Length);
                for (int ch = 0; ch < 2; ch++)
                {
                    for (int i = 0; i < NumCombs; i++)
                    {
                        Array.Clear(_combBuffers[ch][i], 0, _combBuffers[ch][i].Length);
                        _combFilterStore[ch][i] = 0;
                    }
                    for (int i = 0; i < NumAllPasses; i++)
                        Array.Clear(_allPassBuffers[ch][i], 0, _allPassBuffers[ch][i].Length);
                }
            }
        }

        /// <summary>
        /// Nothing unmanaged here.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            Reset();
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
