using System;
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
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessCrossoverChain(Span<float> buffer, int frameCount)
        {
            if (_crossover == null || _phaseAlignment == null || _limiter == null)
            {
                // Fallback: only limiter
                _limiter?.Process(buffer, frameCount);
                return;
            }
            
            // If no phase alignment needed, bypass crossover reconstruction
            if (!_needsPhaseAlignment)
            {
                _limiter.Process(buffer, frameCount);
                return;
            }
            
            // ZERO-ALLOCATION: Use pre-allocated buffers (no ArrayPool overhead)
            Span<float> tempL = _tempLBuffer.AsSpan(0, frameCount);
            Span<float> tempR = _tempRBuffer.AsSpan(0, frameCount);
            Span<float> subL = _subLBuffer.AsSpan(0, frameCount);
            Span<float> subR = _subRBuffer.AsSpan(0, frameCount);
            Span<float> monoSub = _monoSubBuffer.AsSpan(0, frameCount);
            
            // Extract Left and Right channels
            for (int i = 0; i < frameCount; i++)
            {
                tempL[i] = buffer[i * _channels + 0]; // Left
                if (_channels > 1)
                    tempR[i] = buffer[i * _channels + 1]; // Right
                else
                    tempR[i] = tempL[i]; // Mono source
            }
            
            // Crossover split for Left channel: High (tempL) and Low (subL)
            _crossover.Process(tempL, tempL, subL, frameCount, 0);
            
            // Crossover split for Right channel: High (tempR) and Low (subR)
            _crossover.Process(tempR, tempR, subR, frameCount, 1);
            
            // Sum to Mono Sub
            for (int i = 0; i < frameCount; i++)
            {
                monoSub[i] = (subL[i] + subR[i]) * 0.5f;
            }
            
            // Phase alignment on Left High
            _phaseAlignment.Process(tempL, 0, frameCount);
            
            // Phase alignment on Right High
            _phaseAlignment.Process(tempR, 1, frameCount);
            
            // Phase alignment on Mono Sub (channel 2)
            _phaseAlignment.Process(monoSub, 2, frameCount);
            
            // Mix back: High + Mono Sub to both channels
            for (int i = 0; i < frameCount; i++)
            {
                buffer[i * _channels + 0] = tempL[i] + monoSub[i]; // Left = L_High + Sub
                if (_channels > 1)
                    buffer[i * _channels + 1] = tempR[i] + monoSub[i]; // Right = R_High + Sub
            }
            
            // Final limiter on the mixed signal
            _limiter.Process(buffer, frameCount);
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
