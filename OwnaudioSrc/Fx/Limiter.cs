using Ownaudio.Processors;
using System;
using System.Runtime.CompilerServices;

namespace Ownaudio.Fx
{
    /// <summary>
    /// Professional audio limiter with look-ahead and smooth gain reduction
    /// </summary>
    public class Limiter : SampleProcessorBase
    {
        private readonly float[] _delayBuffer;
        private readonly float[] _envelopeBuffer;
        private int _delayIndex;
        private int _envelopeIndex;
        private float _currentGain;
        private float _targetGain;
        private readonly float _sampleRate;

        // Limiter parameters
        private float _threshold;
        private float _ceiling;
        private float _release;
        private float _lookAheadMs;
        private int _lookAheadSamples;

        // Constants
        private const float DEFAULT_THRESHOLD = -3.0f;  // dB
        private const float DEFAULT_CEILING = -0.1f;    // dB
        private const float DEFAULT_RELEASE = 50.0f;    // ms
        private const float DEFAULT_LOOKAHEAD = 5.0f;   // ms

        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Professional limiter constructor
        /// </summary>
        /// <param name="sampleRate">Sample rate</param>
        /// <param name="threshold">Threshold in dB (default: -3dB)</param>
        /// <param name="ceiling">Output ceiling in dB (default: -0.1dB)</param>
        /// <param name="release">Release time in ms (default: 50ms)</param>
        /// <param name="lookAheadMs">Look-ahead time in ms (default: 5ms)</param>
        public Limiter(float sampleRate, float threshold = DEFAULT_THRESHOLD,
            float ceiling = DEFAULT_CEILING, float release = DEFAULT_RELEASE,
            float lookAheadMs = DEFAULT_LOOKAHEAD)
        {
            _sampleRate = sampleRate;
            _threshold = DbToLinear(threshold);
            _ceiling = DbToLinear(ceiling);
            _release = CalculateReleaseCoeff(release, sampleRate);
            _lookAheadMs = lookAheadMs;
            _lookAheadSamples = (int)(lookAheadMs * sampleRate / 1000.0f);

            // Initialize buffers
            _delayBuffer = new float[_lookAheadSamples];
            _envelopeBuffer = new float[_lookAheadSamples];

            _currentGain = 1.0f;
            _targetGain = 1.0f;
            _delayIndex = 0;
            _envelopeIndex = 0;
        }

        /// <summary>
        /// Set limiter parameters
        /// </summary>
        /// <param name="threshold">Threshold in dB</param>
        /// <param name="ceiling">Output ceiling in dB</param>
        /// <param name="release">Release time in ms</param>
        public void SetParameters(float threshold, float ceiling, float release)
        {
            _threshold = DbToLinear(threshold);
            _ceiling = DbToLinear(ceiling);
            _release = CalculateReleaseCoeff(release, _sampleRate);
        }

        /// <summary>
        /// Process audio samples with professional limiting
        /// </summary>
        /// <param name="samples">Audio samples to process</param>
        public override void Process(Span<float> samples)
        {
            if (!IsEnabled)
                return;

            for (int i = 0; i < samples.Length; i++)
            {
                float inputSample = samples[i];

                // Store input in delay buffer
                _delayBuffer[_delayIndex] = inputSample;

                // Calculate peak level for look-ahead detection
                float peakLevel = GetPeakLevel();

                // Calculate required gain reduction
                float requiredGain = CalculateGainReduction(peakLevel);

                // Store gain in envelope buffer for smooth transitions
                _envelopeBuffer[_envelopeIndex] = requiredGain;

                // Get smoothed gain from envelope buffer
                float smoothGain = GetSmoothedGain();

                // Apply gain reduction to delayed sample
                float delayedSample = _delayBuffer[(_delayIndex - _lookAheadSamples + _delayBuffer.Length) % _delayBuffer.Length];
                float processedSample = delayedSample * smoothGain;

                // Apply final ceiling limit
                processedSample = ApplyCeiling(processedSample);

                samples[i] = processedSample;

                // Update buffer indices
                _delayIndex = (_delayIndex + 1) % _delayBuffer.Length;
                _envelopeIndex = (_envelopeIndex + 1) % _envelopeBuffer.Length;
            }
        }

        /// <summary>
        /// Reset limiter state
        /// </summary>
        public override void Reset()
        {
            Array.Clear(_delayBuffer);
            Array.Clear(_envelopeBuffer);
            _currentGain = 1.0f;
            _targetGain = 1.0f;
            _delayIndex = 0;
            _envelopeIndex = 0;
        }

        /// <summary>
        /// Get peak level from look-ahead buffer
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float GetPeakLevel()
        {
            float peak = 0.0f;
            for (int i = 0; i < _delayBuffer.Length; i++)
            {
                float abs = Math.Abs(_delayBuffer[i]);
                if (abs > peak)
                    peak = abs;
            }
            return peak;
        }

        /// <summary>
        /// Calculate required gain reduction based on peak level
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float CalculateGainReduction(float peakLevel)
        {
            if (peakLevel <= _threshold)
                return 1.0f;

            // Calculate ratio of excess over threshold
            float excess = peakLevel / _threshold;
            float targetLevel = _threshold / excess;

            return Math.Max(targetLevel / peakLevel, 0.1f); // Minimum 10% gain
        }

        /// <summary>
        /// Get smoothed gain from envelope buffer
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float GetSmoothedGain()
        {
            // Find minimum gain in envelope buffer (most restrictive)
            float minGain = 1.0f;
            for (int i = 0; i < _envelopeBuffer.Length; i++)
            {
                if (_envelopeBuffer[i] < minGain)
                    minGain = _envelopeBuffer[i];
            }

            // Smooth gain changes
            _targetGain = minGain;

            if (_targetGain < _currentGain)
            {
                // Fast attack for gain reduction
                _currentGain = _targetGain;
            }
            else
            {
                // Slow release for gain recovery
                _currentGain += (_targetGain - _currentGain) * _release;
            }

            return _currentGain;
        }

        /// <summary>
        /// Apply final ceiling limit
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float ApplyCeiling(float sample)
        {
            float abs = Math.Abs(sample);
            if (abs > _ceiling)
            {
                return sample > 0 ? _ceiling : -_ceiling;
            }
            return sample;
        }

        /// <summary>
        /// Convert dB to linear scale
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float DbToLinear(float db)
        {
            return MathF.Pow(10.0f, db / 20.0f);
        }

        /// <summary>
        /// Calculate release coefficient from time in ms
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float CalculateReleaseCoeff(float timeMs, float sampleRate)
        {
            return 1.0f - MathF.Exp(-1.0f / (timeMs * sampleRate / 1000.0f));
        }

        /// <summary>
        /// Get current gain reduction in dB for metering
        /// </summary>
        public float GetGainReductionDb()
        {
            return 20.0f * MathF.Log10(_currentGain);
        }

        /// <summary>
        /// Check if limiter is currently reducing gain
        /// </summary>
        public bool IsLimiting => _currentGain < 0.99f;
    }
}
