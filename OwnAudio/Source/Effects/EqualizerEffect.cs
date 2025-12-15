using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects
{
    public enum EqualizerPreset
    {
        Default,
        Bass,
        Treble,
        Rock,
        Classical,
        Pop,
        Jazz,
        Voice
    }

    /// <summary>
    /// Professional 10-band parametric equalizer
    /// Optimized with flattened Biquad structures for maximum performance
    /// </summary>
    public sealed class EqualizerEffect : IEffectProcessor
    {
        private readonly Guid _id;
        private string _name;
        private bool _enabled;
        private AudioConfig? _config;

        // Constants
        private const int Bands = 10;
        private const int FiltersPerBand = 2; // Cascaded for steeper slopes
        
        // Filter Coefficients (Flattened: [Band][Filter][Coeff])
        // Instead of 3D array, we use 1D arrays with striding or separate arrays for each coeff type
        // Struct-of-Arrays for SIMD friendliness
        
        // Coefficients: b0, b1, b2, a1, a2 (a0 normalized to 1)
        private readonly float[] _b0;
        private readonly float[] _b1;
        private readonly float[] _b2;
        private readonly float[] _a1;
        private readonly float[] _a2;
        
        // State: z1, z2 (Per Channel, Per Filter)
        // Assume Max Channels = 2 for now, or dynamic.
        // Dynamic: [Channel][TotalFilters]
        private float[][] _z1;
        private float[][] _z2;
        
        // EQ Parameters
        private readonly float[] _frequencies;
        private readonly float[] _gains;
        private readonly float[] _qFactors;
        private float _sampleRate = 44100;

        // ISO Frequencies
        private static readonly float[] StandardFrequencies = {
            31.25f, 62.5f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f
        };

        public Guid Id => _id;
        public string Name { get => _name; set => _name = value ?? "Equalizer"; }
        public bool Enabled { get => _enabled; set => _enabled = value; }
        public float Mix { get; set; } = 1.0f;

        public EqualizerEffect(float sampleRate = 44100,
                        float band0Gain = 0.0f, float band1Gain = 0.0f, float band2Gain = 0.0f, float band3Gain = 0.0f, float band4Gain = 0.0f,
                        float band5Gain = 0.0f, float band6Gain = 0.0f, float band7Gain = 0.0f, float band8Gain = 0.0f, float band9Gain = 0.0f)
        {
            _id = Guid.NewGuid();
            _name = "Equalizer";
            _enabled = true;
            _sampleRate = sampleRate;

            int totalFilters = Bands * FiltersPerBand;
            
            // Allocate Coeffs
            _b0 = new float[totalFilters];
            _b1 = new float[totalFilters];
            _b2 = new float[totalFilters];
            _a1 = new float[totalFilters];
            _a2 = new float[totalFilters];

            // Allocate State (start with 2 channels capacity)
            _z1 = new float[2][];
            _z2 = new float[2][];
            for(int c=0; c<2; c++) {
                _z1[c] = new float[totalFilters];
                _z2[c] = new float[totalFilters];
            }

            // Params
            _frequencies = new float[Bands];
            _gains = new float[Bands];
            _qFactors = new float[Bands];

            // Initialize defaults
            for(int i=0; i<Bands; i++) {
                _frequencies[i] = StandardFrequencies[i];
                _qFactors[i] = 1.0f;
                _gains[i] = 0.0f;
            }

            // Set Initial Gains from constructor
            _gains[0] = band0Gain; _gains[1] = band1Gain; _gains[2] = band2Gain; _gains[3] = band3Gain; _gains[4] = band4Gain;
            _gains[5] = band5Gain; _gains[6] = band6Gain; _gains[7] = band7Gain; _gains[8] = band8Gain; _gains[9] = band9Gain;

            RecalculateAllFilters();
        }

        public EqualizerEffect(EqualizerPreset preset, float sampleRate = 44100) : this(sampleRate)
        {
            SetPreset(preset);
        }

        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            
            if (Math.Abs(_sampleRate - config.SampleRate) > 1.0f)
            {
                _sampleRate = config.SampleRate;
                RecalculateAllFilters();
            }

            // Resize state arrays if channels differ
            int channels = config.Channels;
            if (_z1.Length != channels)
            {
                _z1 = new float[channels][];
                _z2 = new float[channels][];
                int totalFilters = Bands * FiltersPerBand;
                for(int c=0; c<channels; c++) {
                    _z1[c] = new float[totalFilters];
                    _z2[c] = new float[totalFilters];
                }
            }
        }

        // Propeties for Bands
        public float Band0Gain { get => _gains[0]; set => SetBand(0, value); }
        public float Band1Gain { get => _gains[1]; set => SetBand(1, value); }
        public float Band2Gain { get => _gains[2]; set => SetBand(2, value); }
        public float Band3Gain { get => _gains[3]; set => SetBand(3, value); }
        public float Band4Gain { get => _gains[4]; set => SetBand(4, value); }
        public float Band5Gain { get => _gains[5]; set => SetBand(5, value); }
        public float Band6Gain { get => _gains[6]; set => SetBand(6, value); }
        public float Band7Gain { get => _gains[7]; set => SetBand(7, value); }
        public float Band8Gain { get => _gains[8]; set => SetBand(8, value); }
        public float Band9Gain { get => _gains[9]; set => SetBand(9, value); }

        private void SetBand(int index, float gain)
        {
            if (index < 0 || index >= Bands) return;
            if (Math.Abs(_gains[index] - gain) > 0.01f)
            {
                _gains[index] = Math.Clamp(gain, -12f, 12f);
                UpdateFilter(index);
            }
        }

        public void SetBandGain(int band, float frequency, float q, float gainDB)
        {
             if (band < 0 || band >= Bands) return;
             _frequencies[band] = Math.Clamp(frequency, 20f, 20000f);
             _qFactors[band] = Math.Clamp(q, 0.1f, 10f);
             SetBand(band, gainDB);
        }

        private void RecalculateAllFilters()
        {
            for(int i=0; i<Bands; i++) UpdateFilter(i);
        }

        private void UpdateFilter(int bandIndex)
        {
            float freq = _frequencies[bandIndex];
            float q = _qFactors[bandIndex];
            float gain = _gains[bandIndex];
            
            // Split gain for 2 filters (Cascade)
            float halfGain = gain * 0.5f;

            // Calculate coeffs
            // Peaking EQ
            float omega = 2.0f * MathF.PI * freq / _sampleRate;
            float sinOmega = MathF.Sin(omega);
            float cosOmega = MathF.Cos(omega);
            float alpha = sinOmega / (2.0f * q);
            float A = MathF.Pow(10.0f, halfGain / 40.0f);

            // Biquad coeffs regular (a0 normalized later)
            float b0 = 1.0f + alpha * A;
            float b1 = -2.0f * cosOmega;
            float b2 = 1.0f - alpha * A;
            float a0 = 1.0f + alpha / A;
            float a1 = -2.0f * cosOmega;
            float a2 = 1.0f - alpha / A;

            // Normalize
            float invA0 = 1.0f / a0;
            
            float fb0 = b0 * invA0;
            float fb1 = b1 * invA0;
            float fb2 = b2 * invA0;
            float fa1 = a1 * invA0;
            float fa2 = a2 * invA0;

            // Store for both filters in band
            int baseIdx = bandIndex * FiltersPerBand;
            
            // Filter 1
            _b0[baseIdx] = fb0; _b1[baseIdx] = fb1; _b2[baseIdx] = fb2;
            _a1[baseIdx] = fa1; _a2[baseIdx] = fa2;
            
            // Filter 2 (Identical)
            _b0[baseIdx+1] = fb0; _b1[baseIdx+1] = fb1; _b2[baseIdx+1] = fb2;
            _a1[baseIdx+1] = fa1; _a2[baseIdx+1] = fa2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null || !_enabled) return;
            
            int channels = _config.Channels;
            int totalFilters = Bands * FiltersPerBand;
            int samples = frameCount * channels;

            // Optimization: If mix is 0? 
            // EQ usually doesn't have Mix 0 often, but standard Check.
            if (Mix < 0.01f) return;

            // Process per channel
            // Since samples are interleaved, we jump step = channels
            
            for (int ch = 0; ch < channels; ch++)
            {
                // Cache state array pointer for this channel
                float[] z1 = _z1[ch];
                float[] z2 = _z2[ch];

                // Iterate samples for this channel
                for (int i = ch; i < samples; i += channels)
                {
                    float sample = buffer[i];
                    
                    // Cascade filters
                    // Unrolled loop for Filter Array? 
                    // 20 iterations is small enough for modern CPU branch prediction
                    for (int f = 0; f < totalFilters; f++)
                    {
                        // Direct Form II Transposed
                        // y = b0*x + z1
                        // z1 = b1*x - a1*y + z2
                        // z2 = b2*x - a2*y
                        
                        float output = _b0[f] * sample + z1[f];
                        z1[f] = _b1[f] * sample - _a1[f] * output + z2[f];
                        z2[f] = _b2[f] * sample - _a2[f] * output;
                        
                        sample = output;
                        
                        // Note: If gain is 0, coeffs are b0=1, a1/a2/b1/b2=0? 
                        // Actually standard calculation gives Identity coeffs if Gain=0?
                        // Yes: A=1, alpha=sin/2Q.
                        // b0 = 1+alpha, a0 = 1+alpha -> b0/a0 = 1.
                        // b1 = -2cos, a1 = -2cos -> equal.
                        // b2 = 1-alpha, a2 = 1-alpha -> equal.
                        // y = x + z1...
                        // If fully identity, we could optimize by skipping bands with 0 gain.
                        // But branching might cost more than MUL. 
                        // Vectorization (SIMD) would be best here but hard in pure C#.
                    }
                    
                    // Apply Soft Limit to prevent clipping from EQ Boost
                    if (Math.Abs(sample) > 0.95f)
                    {
                        sample = 0.95f * MathF.Tanh(sample); 
                    }

                    // Mix
                    // buffer[i] is input (we overwrote sample variable, but buffer[i] is still orig until we write)
                    // Wait, I used 'sample = buffer[i]' then modified 'sample'.
                    // So 'sample' is now Wet.
                    
                    buffer[i] = buffer[i] * (1.0f - Mix) + sample * Mix;
                }
            }
        }

        public void SetPreset(EqualizerPreset preset)
        {
            float[] gains = new float[10];
            switch (preset)
            {
                case EqualizerPreset.Bass: gains = new float[] { 6, 4, 2, -1, 0, 0, 0, 1, 2, 0 }; break;
                case EqualizerPreset.Treble: gains = new float[] { 0, 0, 0, 0, 1, 2, 4, 3, 4, 2 }; break;
                case EqualizerPreset.Rock: gains = new float[] { 4, 3, 1, -2, -1, 1, 3, 2, 2, 1 }; break;
                case EqualizerPreset.Classical: gains = new float[] { 1, 0, 0, 0, 0, 0, 1, 1, 2, 2 }; break;
                case EqualizerPreset.Pop: gains = new float[] { 2, 1, 0, 1, 2, 3, 2, 1, 2, 1 }; break;
                case EqualizerPreset.Jazz: gains = new float[] { 2, 1, 1, 0, 0, 1, 0, 1, 0, -1 }; break;
                case EqualizerPreset.Voice: gains = new float[] { -3, -2, 0, 2, 4, 3, 2, 0, -1, -2 }; break;
                default: break;
            }
            
            for(int i=0; i<10; i++) Band0Gain = gains[i]; // Actually need to set each by index.
            // Optimized Set:
             _gains[0] = gains[0]; _gains[1] = gains[1]; _gains[2] = gains[2]; _gains[3] = gains[3]; _gains[4] = gains[4];
             _gains[5] = gains[5]; _gains[6] = gains[6]; _gains[7] = gains[7]; _gains[8] = gains[8]; _gains[9] = gains[9];
             RecalculateAllFilters();
        }

        public void Reset()
        {
            for(int c=0; c<_z1.Length; c++) {
                Array.Clear(_z1[c], 0, _z1[c].Length);
                Array.Clear(_z2[c], 0, _z2[c].Length);
            }
        }

        public void Dispose()
        {
        }

        public override string ToString() => $"Equalizer: Enabled={_enabled}";
    }
}
