using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Preset configurations for the DynamicAmpEffect.
    /// </summary>
    public enum DynamicAmpPreset
    {
        /// <summary>
        /// Balanced settings suitable for general use.
        /// </summary>
        Default,
        
        /// <summary>
        /// Quick response optimized for speech clarity with higher noise gate.
        /// </summary>
        Speech,
        
        /// <summary>
        /// Gentle, musical settings that preserve natural dynamics.
        /// </summary>
        Music,
        
        /// <summary>
        /// Tight control for consistent broadcast levels.
        /// </summary>
        Broadcast,
        
        /// <summary>
        /// Subtle, transparent mastering-grade dynamics processing.
        /// </summary>
        Mastering,
        
        /// <summary>
        /// Fast response for live performance with feedback prevention.
        /// </summary>
        Live,
        
        /// <summary>
        /// Minimal processing maintaining natural dynamics.
        /// </summary>
        Transparent
    }

    /// <summary>
    /// Professional adaptive volume control with intelligent dynamics processing.
    /// Uses dual-window RMS detection, smart noise gating, and gain change limiting.
    /// </summary>
    public sealed class DynamicAmpEffect : IEffectProcessor
    {
        private readonly Guid _id;
        private readonly string _name;
        private bool _enabled;
        private AudioConfig? _config;

        private float targetRmsLevelDb;
        private float attackTime;
        private float releaseTime;
        private float noiseGateThresholdDb;
        private float maxGain;
        private float maxGainReductionDb;
        private float currentGain = 1.0f;
        private float sampleRate;
        private float rmsWindowSeconds;
        private float maxGainChangePerSecondDb;

        // Dual IIR RMS State (fast for detection, slow for musical tracking)
        private float _rmsFastState;
        private float _rmsSlowState;
        private float _rmsFastCoeff;
        private float _rmsSlowCoeff;

        // Dynamics State
        private float _lastDetectLevel;
        private float _lastGainDb;
        private bool _isAboveNoiseGate; 

        /// <summary>
        /// Gets the unique identifier for this effect instance.
        /// </summary>
        public Guid Id => _id;
        
        /// <summary>
        /// Gets the name of this effect instance.
        /// </summary>
        public string Name => _name;
        
        /// <summary>
        /// Gets or sets whether this effect is enabled.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }
        
        /// <summary>
        /// Gets or sets the wet/dry mix. Always 1.0 for DynamicAmp (no dry signal mixing).
        /// </summary>
        public float Mix { get; set; } = 1.0f;

        /// <summary>
        /// Initializes a new instance of the DynamicAmpEffect with custom parameters.
        /// </summary>
        /// <param name="targetLevel">Target RMS level in dB (default: -12.0).</param>
        /// <param name="attackTimeSeconds">Attack time in seconds (default: 0.5).</param>
        /// <param name="releaseTimeSeconds">Release time in seconds (default: 2.0).</param>
        /// <param name="noiseThresholdDbOrLinear">Noise gate threshold in dB or linear (0-1 for legacy compatibility) (default: -50.0).</param>
        /// <param name="maxGainValue">Maximum gain multiplier (default: 6.0).</param>
        /// <param name="sampleRateHz">Sample rate in Hz (default: 44100.0).</param>
        /// <param name="rmsWindowSeconds">RMS averaging window in seconds (default: 0.5).</param>
        /// <param name="initialGain">Initial gain value (default: 1.0).</param>
        /// <param name="maxGainChangePerSecondDb">Maximum gain change rate in dB/second (default: 12.0).</param>
        /// <param name="maxGainReductionDb">Maximum gain reduction in dB (default: 12.0).</param>
        public DynamicAmpEffect(float targetLevel = -12.0f, float attackTimeSeconds = 0.5f,
                         float releaseTimeSeconds = 2.0f, float noiseThresholdDbOrLinear = -50.0f,
                         float maxGainValue = 6.0f, float sampleRateHz = 44100.0f,
                         float rmsWindowSeconds = 0.5f, float initialGain = 1.0f,
                         float maxGainChangePerSecondDb = 12.0f, float maxGainReductionDb = 12.0f)
        {
            _id = Guid.NewGuid();
            _name = "DynamicAmp";
            _enabled = true;

            ValidateAndSetTargetLevel(targetLevel);
            ValidateAndSetAttackTime(attackTimeSeconds);
            ValidateAndSetReleaseTime(releaseTimeSeconds);

            // Backward compatibility: Auto-detect linear vs dB values (0-1 = linear, otherwise dB)
            float noiseThresholdDb;
            if (noiseThresholdDbOrLinear >= 0.0f && noiseThresholdDbOrLinear <= 1.0f)
            {
                // Legacy linear value (0-1 range) - convert to dB
                noiseThresholdDb = noiseThresholdDbOrLinear < 0.000001f
                    ? -80.0f
                    : LinearToDb(noiseThresholdDbOrLinear);
            }
            else
            {
                // Modern dB value (negative)
                noiseThresholdDb = noiseThresholdDbOrLinear;
            }

            ValidateAndSetNoiseGateDb(noiseThresholdDb);
            ValidateAndSetMaxGain(maxGainValue);
            ValidateAndSetSampleRate(sampleRateHz);
            ValidateAndSetMaxGainReduction(maxGainReductionDb);

            this.rmsWindowSeconds = rmsWindowSeconds;
            this.maxGainChangePerSecondDb = Math.Max(1.0f, maxGainChangePerSecondDb);
            CalculateRmsCoeffs();

            // Init state
            this.currentGain = initialGain;
            _rmsFastState = 0.0f;
            _rmsSlowState = 0.0f;
            _lastDetectLevel = 0.0f;
            _lastGainDb = LinearToDb(initialGain);
            _isAboveNoiseGate = false;
        }

        /// <summary>
        /// Initializes a new instance of the DynamicAmpEffect using a preset configuration.
        /// </summary>
        /// <param name="preset">The preset configuration to use.</param>
        /// <param name="sampleRateHz">Sample rate in Hz (default: 44100.0).</param>
        /// <param name="rmsWindowSeconds">RMS averaging window in seconds (default: 0.5).</param>
        public DynamicAmpEffect(DynamicAmpPreset preset, float sampleRateHz = 44100.0f, float rmsWindowSeconds = 0.5f)
        {
            _id = Guid.NewGuid();
            _name = "DynamicAmp";
            _enabled = true;

            ValidateAndSetSampleRate(sampleRateHz);
            this.rmsWindowSeconds = rmsWindowSeconds;
            this.maxGainChangePerSecondDb = 12.0f;
            CalculateRmsCoeffs();
            SetPreset(preset);

            // Initialize with reasonable starting values
            currentGain = 1.0f;
            _lastGainDb = 0.0f;
            float initRmsLinear = DbToLinear(-20.0f);
            _rmsFastState = initRmsLinear * initRmsLinear;
            _rmsSlowState = _rmsFastState;
            _isAboveNoiseGate = false;
        }

        /// <summary>
        /// Initializes the effect with the specified audio configuration.
        /// </summary>
        /// <param name="config">The audio configuration.</param>
        public void Initialize(AudioConfig config)
        {
            _config = config;
            if (config != null && Math.Abs(sampleRate - config.SampleRate) > 1.0f)
            {
                sampleRate = config.SampleRate;
                CalculateRmsCoeffs();
            }
        }

        /// <summary>
        /// Calculates dual-window IIR coefficients for RMS averaging.
        /// Fast window is 1/10 of main window for quick peak reaction.
        /// </summary>
        private void CalculateRmsCoeffs()
        {
            // Dual-window IIR coefficients: fast (1/10 window) for peaks, slow for musical tracking
            float fastWindow = Math.Max(0.01f, rmsWindowSeconds * 0.1f);
            float slowWindow = Math.Max(0.1f, rmsWindowSeconds);

            _rmsFastCoeff = MathF.Exp(-1.0f / (fastWindow * sampleRate));
            _rmsSlowCoeff = MathF.Exp(-1.0f / (slowWindow * sampleRate));
        }

        /// <summary>
        /// Validates and sets the target RMS level in dB (-60.0 to -3.0).
        /// </summary>
        private void ValidateAndSetTargetLevel(float levelDb) { targetRmsLevelDb = Math.Clamp(levelDb, -60.0f, -3.0f); }
        
        /// <summary>
        /// Validates and sets the attack time in seconds (minimum 0.05).
        /// </summary>
        private void ValidateAndSetAttackTime(float t) { attackTime = Math.Max(0.05f, t); }
        
        /// <summary>
        /// Validates and sets the release time in seconds (minimum 0.2).
        /// </summary>
        private void ValidateAndSetReleaseTime(float t) { releaseTime = Math.Max(0.2f, t); }
        
        /// <summary>
        /// Validates and sets the noise gate threshold in dB (-80.0 to -30.0).
        /// </summary>
        private void ValidateAndSetNoiseGateDb(float db) { noiseGateThresholdDb = Math.Clamp(db, -80.0f, -30.0f); }
        
        /// <summary>
        /// Validates and sets the maximum gain (1.0 to 20.0).
        /// </summary>
        private void ValidateAndSetMaxGain(float g) { maxGain = Math.Clamp(g, 1.0f, 20.0f); }
        
        /// <summary>
        /// Validates and sets the maximum gain reduction in dB (3.0 to 40.0).
        /// </summary>
        private void ValidateAndSetMaxGainReduction(float db) { maxGainReductionDb = Math.Clamp(db, 3.0f, 40.0f); }
        
        /// <summary>
        /// Validates and sets the sample rate in Hz (minimum 8000.0).
        /// </summary>
        private void ValidateAndSetSampleRate(float r) { sampleRate = Math.Max(8000.0f, r); }

        /// <summary>
        /// Processes the audio buffer with adaptive dynamics control.
        /// </summary>
        /// <param name="buffer">The audio buffer to process.</param>
        /// <param name="frameCount">The number of frames in the buffer.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null || !_enabled) return;

            int totalSamples = frameCount * _config.Channels;
            if (totalSamples == 0) return;

            // 1. Calculate block RMS energy
            float blockSumSq = 0.0f;
            for (int i = 0; i < totalSamples; i++)
            {
                float s = buffer[i];
                blockSumSq += s * s;
            }
            float blockMeanSq = blockSumSq / totalSamples;

            // 2. Update dual RMS detectors with proper time constant adjustment
            float blockTime = (float)frameCount / sampleRate;
            float fastAlpha = MathF.Exp(-blockTime / (rmsWindowSeconds * 0.1f));
            float slowAlpha = MathF.Exp(-blockTime / rmsWindowSeconds);

            _rmsFastState = fastAlpha * _rmsFastState + (1.0f - fastAlpha) * blockMeanSq;
            _rmsSlowState = slowAlpha * _rmsSlowState + (1.0f - slowAlpha) * blockMeanSq;

            float rmsFast = MathF.Sqrt(Math.Max(0, _rmsFastState));
            float rmsSlow = MathF.Sqrt(Math.Max(0, _rmsSlowState));

            // Guard NaN
            if (float.IsNaN(rmsFast)) { rmsFast = 0f; _rmsFastState = 0f; }
            if (float.IsNaN(rmsSlow)) { rmsSlow = 0f; _rmsSlowState = 0f; }

            // 3. Intelligent noise gate with hysteresis
            float noiseGateLinear = DbToLinear(noiseGateThresholdDb);
            float hysteresisRatio = 1.5f; // 3dB hysteresis

            if (!_isAboveNoiseGate)
            {
                // Need to exceed threshold * hysteresis to open gate
                if (rmsSlow > noiseGateLinear * hysteresisRatio)
                {
                    _isAboveNoiseGate = true;
                }
            }
            else
            {
                // Need to fall below threshold to close gate
                if (rmsSlow < noiseGateLinear)
                {
                    _isAboveNoiseGate = false;
                }
            }

            // 4. Calculate desired gain
            float desiredGainDb = _lastGainDb; // Hold by default

            if (_isAboveNoiseGate)
            {
                // Use slow RMS for musical gain calculation
                float currentLevelDb = LinearToDb(Math.Max(rmsSlow, 1e-6f));
                float gainErrorDb = targetRmsLevelDb - currentLevelDb;

                // Limit gain reduction to prevent over-compression
                gainErrorDb = Math.Clamp(gainErrorDb, -maxGainReductionDb, LinearToDb(maxGain));

                desiredGainDb = gainErrorDb;
            }

            // 5. Apply attack/release with gain change limiting
            float maxChangeThisBlock = maxGainChangePerSecondDb * blockTime;
            float gainChangeDb = desiredGainDb - _lastGainDb;

            // Apply attack/release timing
            float timeConst = (gainChangeDb < 0) ? attackTime : releaseTime;
            float alpha = MathF.Exp(-blockTime / timeConst);
            float smoothedChangeDb = (1.0f - alpha) * gainChangeDb;

            // Limit the rate of change
            smoothedChangeDb = Math.Clamp(smoothedChangeDb, -maxChangeThisBlock, maxChangeThisBlock);

            float newGainDb = _lastGainDb + smoothedChangeDb;
            currentGain = DbToLinear(newGainDb);

            // Additional safety limits
            currentGain = Math.Clamp(currentGain, 0.1f, maxGain);

            // 6. Apply gain with soft limiting
            float finalGain = currentGain;
            for (int i = 0; i < totalSamples; i++)
            {
                float val = buffer[i] * finalGain;

                // Professional soft-knee limiting at 0.95 threshold
                float absVal = MathF.Abs(val);
                if (absVal > 0.95f)
                {
                    float excess = absVal - 0.95f;
                    float limited = 0.95f + excess / (1.0f + excess * 20.0f); // Soft knee
                    val = val > 0 ? limited : -limited;
                }

                // Hard limit safety
                buffer[i] = Math.Clamp(val, -1.0f, 1.0f);
            }

            // 7. Update state
            _lastDetectLevel = rmsSlow;
            _lastGainDb = LinearToDb(currentGain);
        }

        /// <summary>
        /// Applies a preset configuration to the effect.
        /// </summary>
        /// <param name="preset">The preset to apply.</param>
        public void SetPreset(DynamicAmpPreset preset)
        {
            switch (preset)
            {
                case DynamicAmpPreset.Default:
                    // Balanced settings for general use
                    targetRmsLevelDb = -12.0f;
                    attackTime = 0.5f;
                    releaseTime = 2.0f;
                    noiseGateThresholdDb = -50.0f;
                    maxGain = 6.0f;
                    maxGainReductionDb = 12.0f;
                    maxGainChangePerSecondDb = 12.0f;
                    break;

                case DynamicAmpPreset.Speech:
                    // Quick response for speech clarity, higher noise gate
                    targetRmsLevelDb = -15.0f;
                    attackTime = 0.2f;
                    releaseTime = 0.8f;
                    noiseGateThresholdDb = -45.0f;
                    maxGain = 8.0f;
                    maxGainReductionDb = 15.0f;
                    maxGainChangePerSecondDb = 20.0f;
                    break;

                case DynamicAmpPreset.Music:
                    // Gentle, musical settings preserving dynamics
                    targetRmsLevelDb = -14.0f;
                    attackTime = 1.0f;
                    releaseTime = 3.0f;
                    noiseGateThresholdDb = -55.0f;
                    maxGain = 4.0f;
                    maxGainReductionDb = 8.0f;
                    maxGainChangePerSecondDb = 6.0f;
                    break;

                case DynamicAmpPreset.Broadcast:
                    // Tight control for consistent broadcast levels
                    targetRmsLevelDb = -16.0f;
                    attackTime = 0.3f;
                    releaseTime = 1.5f;
                    noiseGateThresholdDb = -48.0f;
                    maxGain = 10.0f;
                    maxGainReductionDb = 18.0f;
                    maxGainChangePerSecondDb = 15.0f;
                    break;

                case DynamicAmpPreset.Mastering:
                    // Subtle, transparent mastering-grade dynamics
                    targetRmsLevelDb = -10.0f;
                    attackTime = 2.0f;
                    releaseTime = 5.0f;
                    noiseGateThresholdDb = -60.0f;
                    maxGain = 3.0f;
                    maxGainReductionDb = 6.0f;
                    maxGainChangePerSecondDb = 3.0f;
                    break;

                case DynamicAmpPreset.Live:
                    // Fast response for live performance, prevents feedback
                    targetRmsLevelDb = -12.0f;
                    attackTime = 0.15f;
                    releaseTime = 0.6f;
                    noiseGateThresholdDb = -42.0f;
                    maxGain = 5.0f;
                    maxGainReductionDb = 12.0f;
                    maxGainChangePerSecondDb = 18.0f;
                    break;

                case DynamicAmpPreset.Transparent:
                    // Minimal processing, natural dynamics
                    targetRmsLevelDb = -16.0f;
                    attackTime = 3.0f;
                    releaseTime = 8.0f;
                    noiseGateThresholdDb = -65.0f;
                    maxGain = 2.5f;
                    maxGainReductionDb = 5.0f;
                    maxGainChangePerSecondDb = 2.0f;
                    break;
            }
        }
        
        /// <summary>
        /// Converts decibels to linear gain.
        /// </summary>
        /// <param name="db">Value in decibels.</param>
        /// <returns>Linear gain value.</returns>
        private static float DbToLinear(float db) => MathF.Pow(10.0f, db / 20.0f);

        /// <summary>
        /// Converts linear gain to decibels.
        /// </summary>
        /// <param name="linear">Linear gain value.</param>
        /// <returns>Value in decibels.</returns>
        private static float LinearToDb(float linear)
        {
            if (linear <= 1e-6f) return -120.0f;
            return 20.0f * MathF.Log10(linear);
        }

        /// <summary>
        /// Resets the effect state to initial values.
        /// </summary>
        public void Reset()
        {
            currentGain = 1.0f;
            _rmsFastState = 0.0f;
            _rmsSlowState = 0.0f;
            _lastDetectLevel = 0.0f;
            _lastGainDb = 0.0f;
            _isAboveNoiseGate = false;
        }

        /// <summary>
        /// Disposes the effect and releases resources.
        /// </summary>
        public void Dispose()
        {
        }
        
        /// <summary>
        /// Returns a string representation of the effect's current state.
        /// </summary>
        /// <returns>A string describing the effect state.</returns>
        public override string ToString() => $"{_name} (ID: {_id}, Enabled: {_enabled})";
    }
}
