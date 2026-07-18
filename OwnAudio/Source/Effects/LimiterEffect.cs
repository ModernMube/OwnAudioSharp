using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects
{
    /// <summary>
    /// Limiter setups per job.
    /// </summary>
    public enum LimiterPreset
    {
        /// <summary>
        /// Balanced starting point.
        /// </summary>
        Default,

        /// <summary>
        /// Transparent, only catches the true peaks.
        /// </summary>
        Mastering,

        /// <summary>
        /// Tighter and faster, consistent on-air level.
        /// </summary>
        Broadcast,

        /// <summary>
        /// Peak protection for a live rig.
        /// </summary>
        Live,

        /// <summary>
        /// Short lookahead so the drums keep their punch.
        /// </summary>
        DrumBus,

        /// <summary>
        /// Gentle with a long release, natural on vocals.
        /// </summary>
        VocalSafety,

        /// <summary>
        /// Controlled release so the low end doesn't pump.
        /// </summary>
        Bass,

        /// <summary>
        /// Slow and smooth, speech stays intelligible.
        /// </summary>
        Podcast,

        /// <summary>
        /// Heavy limiting for loud electronic material.
        /// </summary>
        Aggressive
    }

    /// <summary>
    /// Lookahead peak limiter. Every buffer is allocated up front at max lookahead size,
    /// changing the lookahead only moves the active length, so Process never allocates.
    /// </summary>
    public sealed class LimiterEffect : IEffectProcessor
    {
        private Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        private AudioConfig _config = null!;

        private float[] _delayBuffer;
        private float[] _envelopeBuffer;
        private int _delayIndex;
        private int _envelopeIndex;
        private float _currentGain;
        private float _targetGain;
        private readonly float _sampleRate;
        private readonly int _maxBufferSize;

        /// <summary>
        /// Monotonic deque for the sliding window maximum, array based so it stays GC free.
        /// </summary>
        private readonly long[] _dequeIndices;
        private readonly float[] _dequeValues;
        private int _dequeHead;
        private int _dequeTail;
        private int _dequeSize;

        /// <summary>
        /// Keeps counting past the ring wrap, that's how the deque knows what expired.
        /// </summary>
        private long _absoluteSampleIndex;

        private float _threshold;
        private float _ceiling;
        private float _release;
        private float _lookAheadMs;
        private int _lookAheadSamples;
        private int _activeBufferSize;

        private const float DEFAULT_THRESHOLD = -3.0f;
        private const float DEFAULT_CEILING = -0.1f;
        private const float DEFAULT_RELEASE = 50.0f;
        private const float DEFAULT_LOOKAHEAD = 5.0f;

        private const float MIN_THRESHOLD = -20.0f;
        private const float MAX_THRESHOLD = 0.0f;
        private const float MIN_CEILING = -2.0f;
        private const float MAX_CEILING = 0.0f;
        private const float MIN_RELEASE = 1.0f;
        private const float MAX_RELEASE = 1000.0f;
        private const float MIN_LOOKAHEAD = 1.0f;
        private const float MAX_LOOKAHEAD = 20.0f;

        /// <summary>
        /// Instance id.
        /// </summary>
        public Guid Id => _id;

        /// <summary>
        /// Effect name.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// On/off switch.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// A limiter is always fully wet, so this stays at 1.0.
        /// </summary>
        public float Mix
        {
            get => 1.0f;
            set { }
        }

        /// <summary>
        /// Lookahead latency in samples, the mixer uses this for PDC.
        /// 5ms is 240 samples at 48k, 20ms is 960.
        /// </summary>
        public int LatencySamples => _lookAheadSamples;

        /// <summary>
        /// Builds the limiter with hand picked values. Threshold and ceiling are in dB,
        /// release and lookahead in ms.
        /// </summary>
        public LimiterEffect(float sampleRate, float threshold = DEFAULT_THRESHOLD,
            float ceiling = DEFAULT_CEILING, float release = DEFAULT_RELEASE,
            float lookAheadMs = DEFAULT_LOOKAHEAD)
        {
            _id = Guid.NewGuid();
            _name = "Limiter";
            _enabled = true;

            _sampleRate = sampleRate;
            _maxBufferSize = (int)(MAX_LOOKAHEAD * sampleRate / 1000.0f);

            Threshold = threshold;
            Ceiling = ceiling;
            Release = release;

            _lookAheadMs = Math.Clamp(lookAheadMs, MIN_LOOKAHEAD, MAX_LOOKAHEAD);
            _lookAheadSamples = (int)(_lookAheadMs * sampleRate / 1000.0f);
            _activeBufferSize = _lookAheadSamples;

            _delayBuffer = new float[_maxBufferSize];
            _envelopeBuffer = new float[_maxBufferSize];
            Array.Fill(_envelopeBuffer, 1.0f);

            _currentGain = 1.0f;
            _targetGain = 1.0f;

            _dequeIndices = new long[_maxBufferSize];
            _dequeValues = new float[_maxBufferSize];
        }

        /// <summary>
        /// Builds the limiter from a preset.
        /// </summary>
        /// <param name="sampleRate"></param>
        /// <param name="preset"></param>
        public LimiterEffect(float sampleRate, LimiterPreset preset)
            : this(sampleRate)
        {
            SetPreset(preset);
        }

        /// <summary>
        /// Stores the engine config.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Threshold in dB, -20 to 0.
        /// </summary>
        public float Threshold
        {
            get => _linearToDb(_threshold);
            set => _threshold = _dbToLinear(Math.Clamp(value, MIN_THRESHOLD, MAX_THRESHOLD));
        }

        /// <summary>
        /// Output ceiling in dB, -2 to 0.
        /// </summary>
        public float Ceiling
        {
            get => _linearToDb(_ceiling);
            set => _ceiling = _dbToLinear(Math.Clamp(value, MIN_CEILING, MAX_CEILING));
        }

        /// <summary>
        /// Release in ms, 1 to 1000.
        /// </summary>
        public float Release
        {
            get => -1000.0f / MathF.Log(1.0f - _release) / _sampleRate;
            set => _release = _releaseCoeff(Math.Clamp(value, MIN_RELEASE, MAX_RELEASE), _sampleRate);
        }

        /// <summary>
        /// Sample rate this instance was built for.
        /// </summary>
        public float SampleRate => _sampleRate;

        /// <summary>
        /// Lookahead in ms, 1 to 20. Only the active length moves, no reallocation,
        /// but the state gets reset because the window changed.
        /// </summary>
        public float LookAheadMs
        {
            get => _lookAheadMs;
            set
            {
                _lookAheadMs = Math.Clamp(value, MIN_LOOKAHEAD, MAX_LOOKAHEAD);
                int newSamples = (int)(_lookAheadMs * _sampleRate / 1000.0f);

                if (newSamples != _lookAheadSamples)
                {
                    _lookAheadSamples = newSamples;
                    _activeBufferSize = newSamples;
                    Reset();
                }
            }
        }

        /// <summary>
        /// Delays the signal by the lookahead, then applies the gain the upcoming peaks call for.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null)
                throw new InvalidOperationException("Effect not initialized. Call Initialize() first.");

            if (!_enabled) return;

            int sampleCount = frameCount * _config.Channels;

            for (int i = 0; i < sampleCount; i++)
            {
                _delayBuffer[_delayIndex] = buffer[i];
                _envelopeBuffer[_envelopeIndex] = _gainReduction(_peakLevel());

                float smoothGain = _smoothedGain();
                if (!float.IsFinite(smoothGain))
                {
                    smoothGain = 1.0f;
                    _currentGain = 1.0f;
                    _targetGain = 1.0f;
                }

                float delayed = _delayBuffer[(_delayIndex - _lookAheadSamples + _activeBufferSize) % _activeBufferSize];
                buffer[i] = _applyCeiling(delayed * smoothGain);

                _delayIndex = (_delayIndex + 1) % _activeBufferSize;
                _envelopeIndex = (_envelopeIndex + 1) % _activeBufferSize;
                _absoluteSampleIndex++;
            }
        }

        /// <summary>
        /// Empties the active part of the ring and opens the gain back up.
        /// </summary>
        public void Reset()
        {
            Array.Clear(_delayBuffer, 0, _activeBufferSize);
            Array.Fill(_envelopeBuffer, 1.0f, 0, _activeBufferSize);

            _currentGain = 1.0f;
            _targetGain = 1.0f;
            _delayIndex = 0;
            _envelopeIndex = 0;
            _absoluteSampleIndex = 0;

            _dequeHead = 0;
            _dequeTail = 0;
            _dequeSize = 0;
        }

        /// <summary>
        /// Nothing unmanaged here.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
        }

        /// <summary>
        /// Short state dump for logs.
        /// </summary>
        public override string ToString()
        {
            return $"{_name} (Enabled: {_enabled}, Threshold: {Threshold:F1}dB, Ceiling: {Ceiling:F1}dB)";
        }

        /// <summary>
        /// Loads one of the canned setups.
        /// </summary>
        /// <param name="preset"></param>
        public void SetPreset(LimiterPreset preset)
        {
            switch (preset)
            {
                case LimiterPreset.Mastering:
                    Threshold = -1.0f; Ceiling = -0.1f; Release = 100f; LookAheadMs = 8.0f;
                    break;

                case LimiterPreset.Broadcast:
                    Threshold = -6.0f; Ceiling = -0.3f; Release = 25f; LookAheadMs = 5.0f;
                    break;

                case LimiterPreset.Live:
                    Threshold = -3.0f; Ceiling = -0.5f; Release = 50f; LookAheadMs = 3.0f;
                    break;

                case LimiterPreset.DrumBus:
                    Threshold = -2.0f; Ceiling = -0.1f; Release = 15f; LookAheadMs = 2.0f;
                    break;

                case LimiterPreset.VocalSafety:
                    Threshold = -4.0f; Ceiling = -0.2f; Release = 200f; LookAheadMs = 10.0f;
                    break;

                case LimiterPreset.Bass:
                    Threshold = -5.0f; Ceiling = -0.1f; Release = 150f; LookAheadMs = 6.0f;
                    break;

                case LimiterPreset.Podcast:
                    Threshold = -8.0f; Ceiling = -0.5f; Release = 300f; LookAheadMs = 12.0f;
                    break;

                case LimiterPreset.Aggressive:
                    Threshold = -10.0f; Ceiling = -0.1f; Release = 10f; LookAheadMs = 3.0f;
                    break;

                default:
                    Threshold = DEFAULT_THRESHOLD; Ceiling = DEFAULT_CEILING;
                    Release = DEFAULT_RELEASE; LookAheadMs = DEFAULT_LOOKAHEAD;
                    break;
            }
        }

        /// <summary>
        /// Amplitude to dB.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float _linearToDb(float linear)
        {
            return 20.0f * MathF.Log10(Math.Max(linear, 1e-6f));
        }

        /// <summary>
        /// Biggest absolute value inside the lookahead window, amortized O(1) from the deque.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float _peakLevel()
        {
            long expire = _absoluteSampleIndex - _activeBufferSize;

            while (_dequeSize > 0 && _dequeIndices[_dequeHead] <= expire)
            {
                _dequeHead = (_dequeHead + 1) % _maxBufferSize;
                _dequeSize--;
            }

            float currentAbs = Math.Abs(_delayBuffer[_delayIndex]);

            while (_dequeSize > 0)
            {
                int backIdx = (_dequeTail - 1 + _maxBufferSize) % _maxBufferSize;
                if (_dequeValues[backIdx] >= currentAbs) break;

                _dequeTail = backIdx;
                _dequeSize--;
            }

            _dequeIndices[_dequeTail] = _absoluteSampleIndex;
            _dequeValues[_dequeTail] = currentAbs;
            _dequeTail = (_dequeTail + 1) % _maxBufferSize;
            _dequeSize++;

            return _dequeValues[_dequeHead];
        }

        /// <summary>
        /// Gain the given peak needs, never pulls below 10%.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float _gainReduction(float peakLevel)
        {
            if (peakLevel <= _threshold) return 1.0f;

            float excess = peakLevel / _threshold;
            return Math.Max((_threshold / excess) / peakLevel, 0.1f);
        }

        /// <summary>
        /// Smallest gain in the window, grabbed instantly on the way down and eased back
        /// on the way up. The release speeds up or slows down with how deep we are.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float _smoothedGain()
        {
            float minGain = 1.0f;
            for (int i = 0; i < _activeBufferSize; i++)
            {
                if (_envelopeBuffer[i] < minGain) minGain = _envelopeBuffer[i];
            }

            _targetGain = minGain;

            if (_targetGain < _currentGain)
            {
                _currentGain = _targetGain;
                return _currentGain;
            }

            float gainDiff = 1.0f - _currentGain;
            float rel = _release;

            if (gainDiff > 0.3f) rel *= 1.5f;
            else if (gainDiff < 0.1f) rel *= 0.5f;

            rel = Math.Clamp(rel, 0.0001f, 0.9999f);
            _currentGain += (_targetGain - _currentGain) * rel;

            if (Math.Abs(_targetGain - _currentGain) < 0.0001f) _currentGain = _targetGain;

            return _currentGain;
        }

        /// <summary>
        /// Hard stop at the ceiling.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float _applyCeiling(float sample)
        {
            if (Math.Abs(sample) > _ceiling) return sample > 0 ? _ceiling : -_ceiling;
            return sample;
        }

        /// <summary>
        /// dB to amplitude.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float _dbToLinear(float db)
        {
            return MathF.Pow(10.0f, db / 20.0f);
        }

        /// <summary>
        /// One-pole release coefficient from a time in ms.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float _releaseCoeff(float timeMs, float sampleRate)
        {
            return 1.0f - MathF.Exp(-1.0f / (timeMs * sampleRate / 1000.0f));
        }

        /// <summary>
        /// Current gain reduction in dB, for meters.
        /// </summary>
        public float GetGainReductionDb()
        {
            return 20.0f * MathF.Log10(_currentGain);
        }

        /// <summary>
        /// True while the limiter is pulling the level down.
        /// </summary>
        public bool IsLimiting => _currentGain < 0.99f;
    }
}
