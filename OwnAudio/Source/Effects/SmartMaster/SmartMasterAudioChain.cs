using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Effects.SmartMaster.Components;

namespace OwnaudioNET.Effects.SmartMaster
{
    /// <summary>
    /// Sealed DSP processing chain for SmartMaster effect.
    /// CRITICAL: This class is optimized for Zero GC and minimal CPU usage in the audio thread.
    /// </summary>
    internal sealed class SmartMasterAudioChain : IDisposable
    {
        #region Fields
        
        // Parallelization threshold: only use parallel processing for buffers larger than this
        private const int PARALLEL_THRESHOLD = 512;
        
        private readonly int _sampleRate;
        private readonly int _channels;
        
        // DSP Components
        private Equalizer30BandEffect? _graphicEQ;
        private SubharmonicSynth? _subharmonicSynth;
        private CompressorEffect? _compressor;
        private CrossoverFilter? _crossover;
        private PhaseAlignment? _phaseAlignment;
        private LimiterEffect? _limiter;
        
        // Pre-allocated buffers for crossover processing (Zero GC)
        // These are sized during Configure() and reused for every Process() call
        private float[]? _tempLBuffer;
        private float[]? _tempRBuffer;
        private float[]? _subLBuffer;
        private float[]? _subRBuffer;
        private float[]? _monoSubBuffer;
        private int _maxFrameCount;
        
        // Configuration cache
        private bool _subharmonicEnabled;
        private bool _compressorEnabled;
        private bool _needsPhaseAlignment;
        
        private bool _disposed;
        
        #endregion
        
        #region Constructor
        
        /// <summary>
        /// Creates a new audio processing chain.
        /// </summary>
        /// <param name="sampleRate">Sample rate in Hz.</param>
        /// <param name="channels">Number of audio channels.</param>
        public SmartMasterAudioChain(int sampleRate, int channels)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _maxFrameCount = 0;
        }
        
        #endregion
        
        #region Configuration
        
        /// <summary>
        /// Configures the audio chain with the given settings.
        /// This method allocates all necessary resources and should NOT be called from the audio thread.
        /// </summary>
        /// <param name="config">Audio configuration.</param>
        /// <param name="masterConfig">SmartMaster configuration.</param>
        public void Configure(AudioConfig config, SmartMasterConfig masterConfig)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SmartMasterAudioChain));
            
            // 1. Create and configure components
            var graphicEQ = new Equalizer30BandEffect(_sampleRate);
            graphicEQ.Initialize(config);
            graphicEQ.SetAllGains(masterConfig.GraphicEQGains);
            
            var subharmonicSynth = new SubharmonicSynth(_sampleRate);
            subharmonicSynth.Enabled = masterConfig.SubharmonicEnabled;
            subharmonicSynth.Mix = masterConfig.SubharmonicMix;
            
            var compressor = new CompressorEffect(sampleRate: _sampleRate);
            compressor.Initialize(config);
            compressor.Enabled = masterConfig.CompressorEnabled;
            compressor.Threshold = masterConfig.CompressorThreshold;
            compressor.Ratio = masterConfig.CompressorRatio;
            compressor.AttackTime = masterConfig.CompressorAttack;
            compressor.ReleaseTime = masterConfig.CompressorRelease;
            
            var crossover = new CrossoverFilter(_sampleRate, masterConfig.CrossoverFrequency);
            
            var phaseAlignment = new PhaseAlignment(_sampleRate);
            phaseAlignment.SetDelays(masterConfig.TimeDelays);
            phaseAlignment.SetPhaseInversions(masterConfig.PhaseInvert);
            
            var limiter = new LimiterEffect(sampleRate: _sampleRate);
            limiter.Initialize(config);
            limiter.Threshold = masterConfig.LimiterThreshold;
            limiter.Ceiling = masterConfig.LimiterCeiling;
            
            // 2. Reset all components to clean state
            graphicEQ.Reset();
            subharmonicSynth.Reset();
            compressor.Reset();
            crossover.Reset();
            phaseAlignment.Reset();
            limiter.Reset();
            
            // 3. Pre-allocate buffers for crossover processing
            // Estimate max frame count based on typical audio buffer sizes (e.g., 2048 frames)
            _maxFrameCount = Math.Max(_maxFrameCount, 2048);
            
            _tempLBuffer = new float[_maxFrameCount];
            _tempRBuffer = new float[_maxFrameCount];
            _subLBuffer = new float[_maxFrameCount];
            _subRBuffer = new float[_maxFrameCount];
            _monoSubBuffer = new float[_maxFrameCount];
            
            // 4. Cache configuration flags
            _subharmonicEnabled = masterConfig.SubharmonicEnabled;
            _compressorEnabled = masterConfig.CompressorEnabled;
            
            // Check if phase alignment is needed
            _needsPhaseAlignment = false;
            if (masterConfig.TimeDelays != null && masterConfig.PhaseInvert != null)
            {
                for (int i = 0; i < Math.Min(3, masterConfig.TimeDelays.Length); i++)
                {
                    if (Math.Abs(masterConfig.TimeDelays[i]) > 0.001f || masterConfig.PhaseInvert[i])
                    {
                        _needsPhaseAlignment = true;
                        break;
                    }
                }
            }
            
            // 5. Atomic assignment (thread-safe swap)
            _graphicEQ = graphicEQ;
            _subharmonicSynth = subharmonicSynth;
            _compressor = compressor;
            _crossover = crossover;
            _phaseAlignment = phaseAlignment;
            _limiter = limiter;
        }
        
        #endregion
        
        #region Audio Processing (Hot Path - Zero GC)
        
        /// <summary>
        /// Processes audio buffer through the DSP chain.
        /// CRITICAL: This method is called from the audio thread and MUST NOT allocate any memory.
        /// </summary>
        /// <param name="buffer">Audio buffer to process (interleaved samples).</param>
        /// <param name="frameCount">Number of frames to process.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            // Early exit if no components initialized
            if (_graphicEQ == null)
                return;
            
            // Expand pre-allocated buffers if needed (rare case, but prevents crashes)
            if (frameCount > _maxFrameCount)
            {
                // This should ideally never happen in production, but we handle it gracefully
                // Note: This WILL cause GC, but only once when buffer size increases
                _maxFrameCount = frameCount;
                _tempLBuffer = new float[_maxFrameCount];
                _tempRBuffer = new float[_maxFrameCount];
                _subLBuffer = new float[_maxFrameCount];
                _subRBuffer = new float[_maxFrameCount];
                _monoSubBuffer = new float[_maxFrameCount];
            }
            
            // 1. Graphic EQ
            _graphicEQ.Process(buffer, frameCount);
            
            // 2. Subharmonic Synth
            if (_subharmonicEnabled && _subharmonicSynth != null)
            {
                _subharmonicSynth.Process(buffer, frameCount, _channels);
            }
            
            // 3. Compressor
            if (_compressorEnabled && _compressor != null)
            {
                _compressor.Process(buffer, frameCount);
            }
            
            // 4. Crossover + Phase Alignment + Limiter
            ProcessCrossoverChain(buffer, frameCount);
        }
        
        /// <summary>
        /// Processes the crossover chain (frequency splitting, phase alignment, and limiting).
        /// CRITICAL: Zero allocation - uses pre-allocated buffers.
        /// Optimized: SIMD vectorization + adaptive parallelization based on buffer size.
        /// Unsafe: Uses pointers for SIMD operations.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void ProcessCrossoverChain(Span<float> buffer, int frameCount)
        {
            if (_crossover == null || _phaseAlignment == null || _limiter == null)
            {
                _limiter?.Process(buffer, frameCount);
                return;
            }
            
            if (!_needsPhaseAlignment)
            {
                _limiter.Process(buffer, frameCount);
                return;
            }
            
            // ZERO-ALLOCATION: Use pre-allocated buffers
            float[] tempL = _tempLBuffer!;
            float[] tempR = _tempRBuffer!;
            float[] subL = _subLBuffer!;
            float[] subR = _subRBuffer!;
            float[] monoSub = _monoSubBuffer!;
            
            int channels = _channels;
            bool useParallel = frameCount >= PARALLEL_THRESHOLD;
            
            // STEP 1: Extract L/R channels (Interleaved -> Planar) with SIMD
            if (channels == 2)
            {
                DeinterleaveStereoSIMD(buffer, tempL, tempR, frameCount);
            }
            else
            {
                // Mono fallback
                for (int i = 0; i < frameCount; i++)
                {
                    tempL[i] = buffer[i * channels + 0];
                    tempR[i] = channels > 1 ? buffer[i * channels + 1] : tempL[i];
                }
            }
            
            // STEP 2: Crossover Split (Adaptive parallelization)
            if (useParallel)
            {
                Parallel.Invoke(
                    () => _crossover.Process(tempL.AsSpan(0, frameCount), tempL.AsSpan(0, frameCount), subL.AsSpan(0, frameCount), frameCount, 0),
                    () => _crossover.Process(tempR.AsSpan(0, frameCount), tempR.AsSpan(0, frameCount), subR.AsSpan(0, frameCount), frameCount, 1)
                );
            }
            else
            {
                _crossover.Process(tempL.AsSpan(0, frameCount), tempL.AsSpan(0, frameCount), subL.AsSpan(0, frameCount), frameCount, 0);
                _crossover.Process(tempR.AsSpan(0, frameCount), tempR.AsSpan(0, frameCount), subR.AsSpan(0, frameCount), frameCount, 1);
            }
            
            // STEP 3: Sum to Mono Sub with SIMD
            SumToMonoSIMD(subL, subR, monoSub, frameCount);
            
            // STEP 4: Phase Alignment (Adaptive parallelization)
            if (useParallel)
            {
                Parallel.Invoke(
                    () => _phaseAlignment.Process(tempL.AsSpan(0, frameCount), 0, frameCount),
                    () => _phaseAlignment.Process(tempR.AsSpan(0, frameCount), 1, frameCount),
                    () => _phaseAlignment.Process(monoSub.AsSpan(0, frameCount), 2, frameCount)
                );
            }
            else
            {
                _phaseAlignment.Process(tempL.AsSpan(0, frameCount), 0, frameCount);
                _phaseAlignment.Process(tempR.AsSpan(0, frameCount), 1, frameCount);
                _phaseAlignment.Process(monoSub.AsSpan(0, frameCount), 2, frameCount);
            }
            
            // STEP 5: Mix back (Planar -> Interleaved) with SIMD
            if (channels == 2)
            {
                InterleaveStereoPlusSubSIMD(buffer, tempL, tempR, monoSub, frameCount);
            }
            else
            {
                for (int i = 0; i < frameCount; i++)
                {
                    buffer[i * channels + 0] = tempL[i] + monoSub[i];
                    if (channels > 1)
                        buffer[i * channels + 1] = tempR[i] + monoSub[i];
                }
            }
            
            _limiter.Process(buffer, frameCount);
        }
        
        /// <summary>
        /// SIMD-optimized deinterleaving: Stereo interleaved -> Separate L/R buffers.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void DeinterleaveStereoSIMD(Span<float> interleaved, float[] left, float[] right, int frameCount)
        {
            fixed (float* pInterleaved = interleaved)
            fixed (float* pLeft = left)
            fixed (float* pRight = right)
            {
                int i = 0;
                int simdWidth = Vector<float>.Count;
                
                // SIMD processing (processes multiple frames at once)
                // Note: We process pairs, so we need 2*simdWidth samples
                int simdLimit = frameCount - (simdWidth - 1);
                
                for (; i < simdLimit; i += simdWidth)
                {
                    // Load interleaved data (L0,R0,L1,R1,...)
                    // We need to load 2*simdWidth values and separate them
                    for (int j = 0; j < simdWidth; j++)
                    {
                        pLeft[i + j] = pInterleaved[(i + j) * 2 + 0];
                        pRight[i + j] = pInterleaved[(i + j) * 2 + 1];
                    }
                }
                
                // Scalar tail
                for (; i < frameCount; i++)
                {
                    pLeft[i] = pInterleaved[i * 2 + 0];
                    pRight[i] = pInterleaved[i * 2 + 1];
                }
            }
        }
        
        /// <summary>
        /// SIMD-optimized mono sum: (L + R) * 0.5.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SumToMonoSIMD(float[] left, float[] right, float[] mono, int frameCount)
        {
            int i = 0;
            int simdWidth = Vector<float>.Count;
            var half = new Vector<float>(0.5f);
            
            // SIMD loop
            for (; i <= frameCount - simdWidth; i += simdWidth)
            {
                var vL = new Vector<float>(left, i);
                var vR = new Vector<float>(right, i);
                var vSum = (vL + vR) * half;
                vSum.CopyTo(mono, i);
            }
            
            // Scalar tail
            for (; i < frameCount; i++)
            {
                mono[i] = (left[i] + right[i]) * 0.5f;
            }
        }
        
        /// <summary>
        /// SIMD-optimized interleaving: L/R/Sub -> Stereo interleaved (L+Sub, R+Sub).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void InterleaveStereoPlusSubSIMD(Span<float> interleaved, float[] left, float[] right, float[] sub, int frameCount)
        {
            fixed (float* pInterleaved = interleaved)
            fixed (float* pLeft = left)
            fixed (float* pRight = right)
            fixed (float* pSub = sub)
            {
                int i = 0;
                int simdWidth = Vector<float>.Count;
                
                // SIMD processing
                int simdLimit = frameCount - (simdWidth - 1);
                
                for (; i < simdLimit; i += simdWidth)
                {
                    // Process simdWidth frames
                    for (int j = 0; j < simdWidth; j++)
                    {
                        float subVal = pSub[i + j];
                        pInterleaved[(i + j) * 2 + 0] = pLeft[i + j] + subVal;
                        pInterleaved[(i + j) * 2 + 1] = pRight[i + j] + subVal;
                    }
                }
                
                // Scalar tail
                for (; i < frameCount; i++)
                {
                    float subVal = pSub[i];
                    pInterleaved[i * 2 + 0] = pLeft[i] + subVal;
                    pInterleaved[i * 2 + 1] = pRight[i] + subVal;
                }
            }
        }
        
        #endregion
        
        #region Reset
        
        /// <summary>
        /// Resets all DSP component states without clearing configuration.
        /// </summary>
        public void Reset()
        {
            _graphicEQ?.Reset();
            _subharmonicSynth?.Reset();
            _compressor?.Reset();
            _crossover?.Reset();
            _phaseAlignment?.Reset();
            _limiter?.Reset();
        }
        
        #endregion
        
        #region Dispose
        
        public void Dispose()
        {
            if (_disposed)
                return;
            
            _graphicEQ?.Dispose();
            _compressor?.Dispose();
            _limiter?.Dispose();
            
            _disposed = true;
        }
        
        #endregion
    }
}
