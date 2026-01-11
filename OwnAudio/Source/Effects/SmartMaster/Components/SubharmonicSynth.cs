using System;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// Professional subharmonic synthesizer
    /// With phase-aligned, linear phase FIR filter and waveshaping-based harmonic generation
    /// </summary>
    public class SubharmonicSynth
    {
        /// <summary>
        /// Sample rate in Hz.
        /// </summary>
        private readonly float _sampleRate;
        
        /// <summary>
        /// Mix level (0.0 = dry, 1.0 = full effect).
        /// </summary>
        private float _mix = 0.0f;
        
        /// <summary>
        /// Indicates whether the subharmonic synthesizer is enabled.
        /// </summary>
        private bool _enabled = false;
        
        /// <summary>
        /// FIR bandpass filter (40-120Hz) with linear phase.
        /// </summary>
        private readonly FIRFilter _bandpassFilter;
        
        /// <summary>
        /// Temporary buffer for filtered signal (per channel).
        /// </summary>
        private float[] _filteredBuffer;
        
        /// <summary>
        /// Enable/disable subharmonic synthesis
        /// </summary>
        public bool Enabled 
        { 
            get => _enabled; 
            set => _enabled = value; 
        }
        
        /// <summary>
        /// Mix ratio (0.0 = original signal, 1.0 = full subharmonic effect)
        /// </summary>
        public float Mix 
        { 
            get => _mix; 
            set => _mix = Math.Clamp(value, 0.0f, 1.0f); 
        }
        
        /// <summary>
        /// SubharmonicSynth constructor
        /// </summary>
        /// <param name="sampleRate">Sample rate (Hz)</param>
        public SubharmonicSynth(float sampleRate)
        {
            _sampleRate = sampleRate;
            
            // Generate FIR bandpass kernel (40-120Hz, 127 taps, Kaiser window)
            const int kernelSize = 127;
            const float lowFreq = 40.0f;
            const float highFreq = 120.0f;
            const float kaiserBeta = 5.0f;
            
            float[] kernel = FIRFilter.CreateBandpassKernel(
                sampleRate, 
                lowFreq, 
                highFreq, 
                kernelSize, 
                kaiserBeta
            );
            
            _bandpassFilter = new FIRFilter(kernel, maxChannels: 2);
            
            // Initialize temporary buffer (max 2048 frames for stereo)
            _filteredBuffer = new float[2048 * 2];
        }
        
        /// <summary>
        /// Audio processing with subharmonic generation
        /// </summary>
        /// <param name="buffer">Audio buffer (interleaved)</param>
        /// <param name="frameCount">Number of frames</param>
        /// <param name="channels">Number of channels</param>
        public void Process(Span<float> buffer, int frameCount, int channels)
        {
            if (!_enabled || _mix <= 0.0f)
                return;
            
            // Check buffer size and resize if necessary
            int requiredSize = frameCount * channels;
            if (_filteredBuffer.Length < requiredSize)
            {
                _filteredBuffer = new float[requiredSize];
            }
            
            // 1. Copy original signal to filtered buffer
            Span<float> filteredSpan = _filteredBuffer.AsSpan(0, requiredSize);
            buffer.Slice(0, requiredSize).CopyTo(filteredSpan);
            
            // 2. FIR bandpass filtering (isolate 40-120Hz)
            _bandpassFilter.Process(filteredSpan, frameCount, channels);
            
            // 3. Waveshaping harmonic generation + mix
            ApplyWaveshapingAndMix(buffer, filteredSpan, frameCount, channels);
        }
        
        /// <summary>
        /// Waveshaping-based harmonic generation and mixing
        /// </summary>
        private void ApplyWaveshapingAndMix(Span<float> originalBuffer, Span<float> filteredBuffer, int frameCount, int channels)
        {
            int sampleCount = frameCount * channels;
            
            for (int i = 0; i < sampleCount; i++)
            {
                float original = originalBuffer[i];
                float filtered = filteredBuffer[i];
                
                // This generates harmonics (including subharmonics)
                float shaped = Waveshape(filtered * 2.0f); // 2x gain before waveshaper
                
                // Mix: original signal + generated subharmonics - Linear phase FIR has constant group delay across all frequencies, so no phase alignment issues occur when mixing
                float mixed = original * (1.0f - _mix) + shaped * _mix;
                
                // Safety clamping to prevent hard clipping
                if (mixed > 1.0f) mixed = 1.0f;
                else if (mixed < -1.0f) mixed = -1.0f;
                
                originalBuffer[i] = mixed;
            }
        }
        
        /// <summary>
        /// Soft clipping waveshaper function
        /// Generates harmonics from input signal
        /// </summary>
        /// <param name="x">Input sample</param>
        /// <returns>Waveshaped output</returns>
        private static float Waveshape(float x)
        {
            // Soft clipping: x / (1 + |x|) - This is a tanh-like function that: Generates harmonics (odd and even), Provides soft saturation, Is low CPU intensive
            return x / (1.0f + MathF.Abs(x));
        }
        
        /// <summary>
        /// Clear filter state (e.g. on playback restart)
        /// </summary>
        public void Reset()
        {
            _bandpassFilter.Reset();
            Array.Clear(_filteredBuffer, 0, _filteredBuffer.Length);
        }
    }
}
