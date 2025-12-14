using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects
{
    public enum DynamicAmpPreset
    {
        Default,
        Speech,
        Music,
        Broadcast,
        Mastering,
        Live,
        Transparent
    }

    /// <summary>
    /// An adaptive volume control class using IIR RMS detection for robust dynamics processing.
    /// </summary>
    public sealed class DynamicAmpEffect : IEffectProcessor
    {
        private readonly Guid _id;
        private readonly string _name;
        private bool _enabled;
        private bool _disposed;
        private AudioConfig? _config;

        private float targetRmsLevelDb;
        private float attackTime;
        private float releaseTime;
        private float noiseGate;
        private float maxGain;
        private float currentGain = 1.0f;
        private float sampleRate;
        private float rmsWindowSeconds;

        // IIR RMS State
        private float _rmsState;
        private float _rmsCoeff;

        // Dynamics State
        private float _lastDetectLevel; 

        public Guid Id => _id;
        public string Name => _name;
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }
        public float Mix { get; set; } = 1.0f;

        public DynamicAmpEffect(float targetLevel = -9.0f, float attackTimeSeconds = 0.2f,
                         float releaseTimeSeconds = 0.8f, float noiseThreshold = 0.005f,
                         float maxGainValue = 10.0f, float sampleRateHz = 44100.0f,
                         float rmsWindowSeconds = 0.3f, float initialGain = 1.0f)
        {
            _id = Guid.NewGuid();
            _name = "DynamicAmp";
            _enabled = true;

            ValidateAndSetTargetLevel(targetLevel);
            ValidateAndSetAttackTime(attackTimeSeconds);
            ValidateAndSetReleaseTime(releaseTimeSeconds);
            // Ignore passed noiseThreshold for now, force low threshold for SmartHeadroom compatibility
            ValidateAndSetNoiseGate(0.00001f); 
            ValidateAndSetMaxGain(maxGainValue);
            ValidateAndSetSampleRate(sampleRateHz);
            
            this.rmsWindowSeconds = rmsWindowSeconds;
            CalculateRmsCoeff();
            
            // Init state
            this.currentGain = initialGain;
            _rmsState = 0.0f; // Start from silence
            _lastDetectLevel = 0.0f;
        }

        public DynamicAmpEffect(DynamicAmpPreset preset, float sampleRateHz = 44100.0f, float rmsWindowSeconds = 0.3f)
        {
            _id = Guid.NewGuid();
            _name = "DynamicAmp";
            _enabled = true;

            ValidateAndSetSampleRate(sampleRateHz);
            this.rmsWindowSeconds = rmsWindowSeconds;
            CalculateRmsCoeff();
            SetPreset(preset);
            
            currentGain = 3.0f;
             _rmsState = DbToLinear(-16.0f); 
             _rmsState *= _rmsState;
        }

        public void Initialize(AudioConfig config)
        {
            _config = config;
            if (config != null && Math.Abs(sampleRate - config.SampleRate) > 1.0f)
            {
                sampleRate = config.SampleRate;
                CalculateRmsCoeff();
            }
        }

        private void CalculateRmsCoeff()
        {
            // IIR coefficient for RMS averaging
            // coeff = exp(-1 / (time * rate))
            // Typically windowSeconds is integration time (tau)
            _rmsCoeff = MathF.Exp(-1.0f / (Math.Max(0.001f, rmsWindowSeconds) * sampleRate));
        }

        // Validation helpers (kept same logic)
        private void ValidateAndSetTargetLevel(float levelDb) { targetRmsLevelDb = Math.Clamp(levelDb, -60.0f, 0.0f); }
        private void ValidateAndSetAttackTime(float t) { attackTime = Math.Max(0.0001f, t); }
        private void ValidateAndSetReleaseTime(float t) { releaseTime = Math.Max(0.0001f, t); }
        private void ValidateAndSetNoiseGate(float t) { noiseGate = Math.Clamp(t, 0.0f, 1.0f); }
        private void ValidateAndSetMaxGain(float g) { maxGain = Math.Max(1.0f, g); }
        private void ValidateAndSetSampleRate(float r) { sampleRate = Math.Max(1.0f, r); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null || !_enabled) return;

            int totalSamples = frameCount * _config.Channels;
            if (totalSamples == 0) return;

            // 1. Calculate Block Energy (Optimization: Average block energy first usually works well for AGC, 
            // but for fast reaction we might want sample-by-sample or smaller chunks. 
            // Given "DynamicAmp" is usually slow levelling, block average update of RMS state is efficient and sufficient.)
            
            float blockSumSq = 0.0f;
            for(int i=0; i<totalSamples; i++)
            {
                float s = buffer[i];
                blockSumSq += s * s;
            }
            float blockMeanSq = blockSumSq / totalSamples;

            // Update IIR RMS
            // Correction for block size: we effectively run the IIR filter for N samples? 
            // Standard approx: Update once per block with adjusted coeff?
            // Or just update once assuming the block is instantaneous "reading"? 
            // For proper time constant, with Block Processing, we should use:
            // state = state * blockDecay + input * (1 - blockDecay)
            // where blockDecay = coeff ^ samples
            
            // Simpler: Just feed the block mean square into the IIR as if it was one sample? 
            // NO, that slows down the window by factor of N.
            // A simple "Block Rate" IIR:
            float blockTime = (float)frameCount / sampleRate;
            float blockAlpha = MathF.Exp(-blockTime / rmsWindowSeconds);
            
            _rmsState = blockAlpha * _rmsState + (1.0f - blockAlpha) * blockMeanSq;
            
            float currentRms = MathF.Sqrt(_rmsState);
            
            // Guard NaN
            if (float.IsNaN(currentRms)) { currentRms = 0f; _rmsState = 0f; }

            // 2. Logic
             if (currentRms < noiseGate)
            {
                // Hold gain or release slowly? 
                // Let's just hold gain logic by NOT updating target strictly, 
                // or letting it behave like silence (gain shouldn't explode).
                // If noise, we usually freeze gain to avoid boosting hiss.
            }
            else
            {
                // Calculate Target
                float targetLinear = DbToLinear(targetRmsLevelDb);
                float desiredGain = targetLinear / Math.Max(currentRms, 1e-6f);
                desiredGain = Math.Min(desiredGain, maxGain);
                desiredGain = Math.Max(desiredGain, 0.1f); // Min gain safety

                // Attack/Release for Gain
                float dt = blockTime; 
                
                // If we need to increase gain (Release phase of compressor logic, or Attack of Expander? - AGC naming is tricky)
                // "Attack" usually means response to input rising (Gain reduction).
                // "Release" means response to input falling (Gain recovery).
                
                // Here: 
                // Input LOUD -> Gain goes DOWN.
                // Input QUIET -> Gain goes UP.
                
                // If Desired < Current (Input is Loud): Use Attack Time (Fast reaction to limit peaks)
                // If Desired > Current (Input is Quiet): Use Release Time (Slow recovery)
                
                float timeConst = (desiredGain < currentGain) ? attackTime : releaseTime;
                float gainAlpha = MathF.Exp(-dt / timeConst);

                // Apply logic
                currentGain = gainAlpha * currentGain + (1.0f - gainAlpha) * desiredGain;
            }

            // 3. Apply Gain
            float gain = currentGain;
            for(int i=0; i<totalSamples; i++)
            {
                float val = buffer[i] * gain;
                
                // Soft Limit safety
                if(val > 0.99f) val = 0.99f + (val - 0.99f) * 0.1f; // crude soft knee
                else if(val < -0.99f) val = -0.99f + (val + 0.99f) * 0.1f;
                // Hard limit
                if (val > 1.0f) val = 1.0f;
                if (val < -1.0f) val = -1.0f;
                
                buffer[i] = val;
            }
            
            _lastDetectLevel = currentRms;
        }

        public void SetPreset(DynamicAmpPreset preset)
        {
            switch (preset)
            {
                case DynamicAmpPreset.Default:
                    // ... (Values from previous file)
                    targetRmsLevelDb = -9.0f; attackTime = 0.2f; releaseTime = 0.8f; noiseGate = 0.005f; maxGain = 10.0f;
                    break;
                case DynamicAmpPreset.Speech:
                    targetRmsLevelDb = -12.0f; attackTime = 0.003f; releaseTime = 0.1f; noiseGate = 0.01f; maxGain = 6.0f;
                    break;
                case DynamicAmpPreset.Music:
                    targetRmsLevelDb = -14.0f; attackTime = 0.03f; releaseTime = 0.6f; noiseGate = 0.002f; maxGain = 4.0f;
                    break;
                case DynamicAmpPreset.Broadcast:
                    targetRmsLevelDb = -16.0f; attackTime = 0.001f; releaseTime = 0.05f; noiseGate = 0.008f; maxGain = 8.0f;
                    break;
                case DynamicAmpPreset.Mastering:
                    targetRmsLevelDb = -8.0f; attackTime = 0.0001f; releaseTime = 0.02f; noiseGate = 0.001f; maxGain = 12.0f;
                    break;
                case DynamicAmpPreset.Live:
                    targetRmsLevelDb = -18.0f; attackTime = 0.005f; releaseTime = 0.2f; noiseGate = 0.015f; maxGain = 3.0f;
                    break;
                case DynamicAmpPreset.Transparent:
                    targetRmsLevelDb = -20.0f; attackTime = 0.02f; releaseTime = 0.5f; noiseGate = 0.001f; maxGain = 2.0f;
                    break;
            }
        }
        
        private static float DbToLinear(float db) => MathF.Pow(10.0f, db / 20.0f);

        public void Reset()
        {
            currentGain = 1.0f;
            _rmsState = 0.0f;
        }

        public void Dispose()
        {
            _disposed = true;
        }
        
        public override string ToString() => $"{_name} (ID: {_id}, Enabled: {_enabled})";
    }
}
