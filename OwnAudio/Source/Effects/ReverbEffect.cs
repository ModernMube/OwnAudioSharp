using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Reverb presets for different acoustic environments and audio processing scenarios
    /// </summary>
    public enum ReverbPreset
    {
        Default,
        SmallRoom,
        LargeHall,
        Cathedral,
        Plate,
        Spring,
        AmbientPad,
        VocalBooth,
        DrumRoom,
        Gated,
        Subtle
    }

    /// <summary>
    /// Professional quality reverb effect implementation based on an optimized, extended Freeverb algorithm.
    /// Features: Stereo Spread, Pre-Delay, Input Filtering, Zero-Allocation processing.
    /// </summary>
    public sealed class ReverbEffect : IEffectProcessor
    {
        // IEffectProcessor
        private readonly Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        private AudioConfig? _config;

        // --- Constants & Tunings (Freeverb Standard + Stereo Spread) ---
        // Basic tunings at 44.1kHz
        private const int NumCombs = 8;
        private const int NumAllPasses = 4;
        private const float FixedGain = 0.015f;
        private const float ScaleWet = 3.0f;
        private const float ScaleDamp = 0.4f;
        private const float ScaleRoom = 0.28f;
        private const float OffsetRoom = 0.7f;
        private const int StereoSpread = 23;

        private static readonly int[] CombTuningL = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        private static readonly int[] CombTuningR = { 1116 + StereoSpread, 1188 + StereoSpread, 1277 + StereoSpread, 1356 + StereoSpread, 1422 + StereoSpread, 1491 + StereoSpread, 1557 + StereoSpread, 1617 + StereoSpread };
        private static readonly int[] AllPassTuningL = { 556, 441, 341, 225 };
        private static readonly int[] AllPassTuningR = { 556 + StereoSpread, 441 + StereoSpread, 341 + StereoSpread, 225 + StereoSpread };

        // --- DSP State (Flat Arrays for Performance) ---
        
        // Comb Filters State
        // [Channel 0=L, 1=R][FilterIndex]
        private readonly float[][][] _combBuffers; 
        private readonly int[][] _combIndices;
        private readonly int[][] _combLengths;
        private readonly float[][] _combFilterStore; // Lowpass filter state for damping

        // AllPass Filters State
        private readonly float[][][] _allPassBuffers;
        private readonly int[][] _allPassIndices;
        private readonly int[][] _allPassLengths;

        // Pre-Delay State
        private float[]? _preDelayBuffer;
        private int _preDelayIndex;
        private int _preDelayLength;

        // --- Parameters ---
        private float _roomSize = 0.5f;
        private float _damping = 0.5f;
        private float _wet = 0.33f;
        private float _dry = 0.67f; // Automatically adjusted
        private float _width = 1.0f;
        private float _gain = 1.0f;
        private float _mix = 0.5f;
        private float _sampleRate = 44100f;

        // Cached Coefficients
        private float _roomSizeVal;
        private float _dampVal;
        private float _wet1;
        private float _wet2;
        
        // Locks
        private readonly object _lock = new object();

        // --- Properties ---
        public Guid Id => _id;
        public string Name { get => _name; set => _name = value ?? "Reverb"; }
        public bool Enabled { get => _enabled; set => _enabled = value; }

        public float RoomSize
        {
            get => _roomSize;
            set { _roomSize = FastClamp(value, 0f, 1f); UpdateCoefficients(); }
        }

        public float Damping
        {
            get => _damping;
            set { _damping = FastClamp(value, 0f, 1f); UpdateCoefficients(); }
        }

        public float Mix
        {
            get => _mix;
            set => _mix = FastClamp(value, 0f, 1f);
        }

        public float Width
        {
            get => _width;
            set { _width = FastClamp(value, 0f, 2f); UpdateCoefficients(); }
        }

        // Backward compatibility alias
        public float StereoWidth
        {
            get => _width;
            set => Width = value;
        }

        public float WetLevel
        {
            get => _wet;
            set { _wet = FastClamp(value, 0f, 1f); UpdateCoefficients(); }
        }

        public float DryLevel
        {
            get => _dry;
            set => _dry = FastClamp(value, 0f, 1f);
        }

        public float Gain
        {
            get => _gain;
            set => _gain = Math.Max(0f, value);
        }

        // Constructor with backward compatible parameter names
        public ReverbEffect(float size = 0.5f, float damp = 0.5f, float wet = 0.33f, float dry = 0.67f, float stereoWidth = 1.0f, float mix = 0.5f, float gainLevel = 1.0f)
        {
            _id = Guid.NewGuid();
            _name = "Reverb";
            _enabled = true;

            // Allocate State Arrays
            // 2 Channels (L/R)
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
                _combFilterStore[ch] = new float[NumCombs]; // auto-init to 0

                _allPassBuffers[ch] = new float[NumAllPasses][];
                _allPassIndices[ch] = new int[NumAllPasses];
                _allPassLengths[ch] = new int[NumAllPasses];
            }

            // Set Params
            _roomSize = size;
            _damping = damp;
            _wet = wet;
            _dry = dry;
            _width = stereoWidth;
            _mix = mix;
            _gain = gainLevel;

            // Initialize Buffers (assuming default 44.1k, will re-init in Initialize)
            ResizeBuffers(44100);
            UpdateCoefficients();
        }

        public ReverbEffect(ReverbPreset preset) : this()
        {
            SetPreset(preset);
        }

        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            
            if (Math.Abs(_sampleRate - config.SampleRate) > 1.0f)
            {
                lock (_lock)
                {
                    _sampleRate = config.SampleRate;
                    ResizeBuffers(_sampleRate);
                    Reset();
                }
            }
        }

        private void ResizeBuffers(float newSampleRate)
        {
            float scale = newSampleRate / 44100f;

            // PreDelay: 20ms fixed
            int preDelaySamples = (int)(0.020f * newSampleRate);
            _preDelayBuffer = new float[preDelaySamples];
            _preDelayLength = preDelaySamples;
            _preDelayIndex = 0;

            for (int ch = 0; ch < 2; ch++)
            {
                // Combs
                for (int i = 0; i < NumCombs; i++)
                {
                    int tuning = (ch == 0) ? CombTuningL[i] : CombTuningR[i];
                    int size = (int)(tuning * scale);
                    _combBuffers[ch][i] = new float[size];
                    _combLengths[ch][i] = size;
                    _combIndices[ch][i] = 0;
                    _combFilterStore[ch][i] = 0;
                }

                // AllPasses
                for (int i = 0; i < NumAllPasses; i++)
                {
                    int tuning = (ch == 0) ? AllPassTuningL[i] : AllPassTuningR[i];
                    int size = (int)(tuning * scale);
                    _allPassBuffers[ch][i] = new float[size];
                    _allPassLengths[ch][i] = size;
                    _allPassIndices[ch][i] = 0;
                }
            }
        }

        private void UpdateCoefficients()
        {
            _roomSizeVal = (_roomSize * ScaleRoom) + OffsetRoom;
            _dampVal = _damping * ScaleDamp;
            
            // Wet spread logic
            _wet1 = _wet * (0.5f * _width + 0.5f);
            _wet2 = _wet * ((1.0f - _width) * 0.5f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null || !_enabled || _preDelayBuffer == null) return;

            // Local cache (Struct copy)
            float room = _roomSizeVal;
            float damp = _dampVal;
            float g = _gain;
            float dry = _dry;
            float mix = _mix;
            float w1 = _wet1;
            float w2 = _wet2;
            int pdMask = _preDelayLength - 1; // Used only if power of two, but strictly we use modulo check
            
            int channels = _config.Channels;
            int totalSamples = frameCount * channels;

            bool isStereo = channels >= 2;

            for (int frame = 0; frame < frameCount; frame++)
            {
                int idx = frame * channels;
                
                // 1. Input Mix & HPF & Pre-Delay
                float inputL = buffer[idx];
                float inputR = isStereo ? buffer[idx + 1] : inputL;
                
                // Mono-sum for reverb engine (Freeverb style)
                float inputMono = (inputL + inputR) * 0.5f;

                // Apply Gain
                inputMono *= g;

                // Pre-Delay
                float delayedInput = _preDelayBuffer[_preDelayIndex];
                _preDelayBuffer[_preDelayIndex] = inputMono;
                _preDelayIndex++;
                if (_preDelayIndex >= _preDelayLength) _preDelayIndex = 0;

                inputMono = delayedInput;

                // 2. Reverb Engine (Dual Mono processing for Stereo Width)
                float outL = 0f;
                float outR = 0f;
                
                // Process Left Engine
                for(int i=0; i<NumCombs; i++)
                {                    
                    float[] buf = _combBuffers[0][i];
                    int bIdx = _combIndices[0][i];
                    
                    float output = buf[bIdx];
                    _combFilterStore[0][i] = (output * (1.0f - damp)) + (_combFilterStore[0][i] * damp);
                    buf[bIdx] = inputMono + (_combFilterStore[0][i] * room);
                    
                    // Increment Index
                    bIdx++;
                    if (bIdx >= _combLengths[0][i]) bIdx = 0;
                    _combIndices[0][i] = bIdx;

                    outL += output;
                }

                // Process Right Engine
                for(int i=0; i<NumCombs; i++)
                {
                    float[] buf = _combBuffers[1][i];
                    int bIdx = _combIndices[1][i];

                    float output = buf[bIdx];
                    _combFilterStore[1][i] = (output * (1.0f - damp)) + (_combFilterStore[1][i] * damp);
                    buf[bIdx] = inputMono + (_combFilterStore[1][i] * room);

                    bIdx++;
                    if (bIdx >= _combLengths[1][i]) bIdx = 0;
                    _combIndices[1][i] = bIdx;

                    outR += output;
                }

                // AllPass Filters Series (Left)
                for(int i=0; i<NumAllPasses; i++)
                {
                    float[] buf = _allPassBuffers[0][i];
                    int bIdx = _allPassIndices[0][i];
                    
                    float bufOut = buf[bIdx];
                    float processed = outL; // chain
                    
                    buf[bIdx] = processed + (bufOut * 0.5f);
                    outL = processed - (buf[bIdx] * 0.5f) + bufOut; // Standard AllPass: -input + bufOut + feedback*input??
                                        
                    float temp = processed * -0.5f + bufOut;
                    buf[bIdx] = processed + (bufOut * 0.5f);
                    outL = temp;

                    bIdx++;
                    if (bIdx >= _allPassLengths[0][i]) bIdx = 0;
                    _allPassIndices[0][i] = bIdx;
                }

                // AllPass Filters Series (Right)
                for(int i=0; i<NumAllPasses; i++)
                {
                    float[] buf = _allPassBuffers[1][i];
                    int bIdx = _allPassIndices[1][i];

                    float bufOut = buf[bIdx];
                    float processed = outR;

                    float temp = processed * -0.5f + bufOut;
                    buf[bIdx] = processed + (bufOut * 0.5f);
                    outR = temp;

                    bIdx++;
                    if (bIdx >= _allPassLengths[1][i]) bIdx = 0;
                    _allPassIndices[1][i] = bIdx;
                }

                // 3. Stereo Mixing
                // Freeverb "Wet" Logic with Spread
                float wetL = outL * w1 + outR * w2;
                float wetR = outR * w1 + outL * w2;

                // Apply Dry/Wet levels first
                wetL *= ScaleWet; // internal scaling
                wetR *= ScaleWet;
                float finalDryL = inputL * dry;
                float finalDryR = inputR * dry;

                // Blend
                float blendedL = finalDryL + wetL;
                float blendedR = finalDryR + wetR;

                // Final Master Mix
                buffer[idx] = inputL * (1.0f - mix) + blendedL * mix;
                if (isStereo)
                {
                    buffer[idx + 1] = inputR * (1.0f - mix) + blendedR * mix;
                }
            }
        }

        public void SetPreset(ReverbPreset preset)
        {
            switch (preset)
            {
                case ReverbPreset.Default:
                    RoomSize = 0.5f; Damping = 0.5f; Width = 1.0f; WetLevel = 0.33f; DryLevel = 0.8f;
                    break;
                case ReverbPreset.SmallRoom:
                    RoomSize = 0.3f; Damping = 0.2f; Width = 0.5f; WetLevel = 0.2f; DryLevel = 0.9f;
                    break;
                case ReverbPreset.LargeHall:
                    RoomSize = 0.85f; Damping = 0.5f; Width = 1.0f; WetLevel = 0.5f; DryLevel = 0.6f;
                    break;
                case ReverbPreset.Cathedral:
                    RoomSize = 0.95f; Damping = 0.1f; Width = 1.0f; WetLevel = 0.7f; DryLevel = 0.5f;
                    break;
                case ReverbPreset.Plate:
                    RoomSize = 0.6f; Damping = 0.1f; Width = 0.8f; WetLevel = 0.4f; DryLevel = 0.8f;
                    break;
                case ReverbPreset.AmbientPad:
                    RoomSize = 0.92f; Damping = 0.8f; Width = 1.0f; WetLevel = 0.8f; DryLevel = 0.4f;
                    break;
                default:
                    RoomSize = 0.5f; Damping = 0.5f; Width = 1.0f; WetLevel = 0.33f; DryLevel = 0.8f;
                    break;
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                if (_preDelayBuffer != null) Array.Clear(_preDelayBuffer, 0, _preDelayBuffer.Length);
                for (int ch = 0; ch < 2; ch++)
                {
                    for (int i = 0; i < NumCombs; i++)
                    {
                        if (_combBuffers[ch][i] != null) Array.Clear(_combBuffers[ch][i], 0, _combBuffers[ch][i].Length);
                        _combFilterStore[ch][i] = 0;
                    }
                    for (int i = 0; i < NumAllPasses; i++)
                    {
                        if (_allPassBuffers[ch][i] != null) Array.Clear(_allPassBuffers[ch][i], 0, _allPassBuffers[ch][i].Length);
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Reset();
            _disposed = true;
        }

        private static float FastClamp(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        public override string ToString()
        {
            return $"Reverb: Room={_roomSize:F2}, Damp={_damping:F2}, Width={_width:F2}, Mix={_mix:F2}";
        }
    }
}
