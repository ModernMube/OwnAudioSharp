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

        /// <summary>
        /// Look-ahead line holding whole interleaved frames, slot * channels + ch.
        /// </summary>
        private float[] _delayBuffer;
        private int _delayIndex;
        private int _channels;
        private float _currentGain;
        private float _targetGain;
        private readonly float _sampleRate;

        /// <summary>
        /// Window length in frames, so the look-ahead stays the same in ms whatever
        /// the channel count.
        /// </summary>
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
        /// Same trick for the window minimum of the required gain - the old code
        /// rescanned the whole envelope buffer per sample.
        /// </summary>
        private readonly long[] _minIndices;
        private readonly float[] _minValues;
        private int _minHead;
        private int _minTail;
        private int _minSize;

        /// <summary>
        /// Keeps counting past the ring wrap, that's how the deques know what expired.
        /// </summary>
        private long _absoluteFrameIndex;

        private float _threshold;
        private float _ceiling;
        private float _release;
        private float _attack;
        private float _lookAheadMs;
        private int _lookAheadFrames;
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
        /// Lookahead latency in frames, the mixer uses this for PDC.
        /// 5ms is 240 frames at 48k, 20ms is 960.
        /// </summary>
        public int LatencySamples => _lookAheadFrames;

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

            _maxBufferSize = (int)(MAX_LOOKAHEAD * sampleRate / 1000.0f) + 1;

            Threshold = threshold;
            Ceiling = ceiling;
            Release = release;

            _lookAheadMs = Math.Clamp(lookAheadMs, MIN_LOOKAHEAD, MAX_LOOKAHEAD);
            _lookAheadFrames = (int)(_lookAheadMs * sampleRate / 1000.0f);
            _activeBufferSize = _lookAheadFrames + 1;
            _attack = _attackCoeff(_lookAheadMs, sampleRate);

            _channels = 2;
            _delayBuffer = new float[_maxBufferSize * _channels];

            _currentGain = 1.0f;
            _targetGain = 1.0f;

            _dequeIndices = new long[_maxBufferSize];
            _dequeValues = new float[_maxBufferSize];
            _minIndices = new long[_maxBufferSize];
            _minValues = new float[_maxBufferSize];
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
        /// Stores the engine config, and relays out the delay line if the stream is
        /// wider than the stereo we pre-sized for.
        /// </summary>
        public void Initialize(AudioConfig config)
        {
            _config = config;

            int ch = Math.Max(config.Channels, 1);
            if (ch != _channels)
            {
                _channels = ch;
                _delayBuffer = new float[_maxBufferSize * ch];
                Reset();
            }
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
                _attack = _attackCoeff(_lookAheadMs, _sampleRate);

                int newFrames = (int)(_lookAheadMs * _sampleRate / 1000.0f);
                if (newFrames != _lookAheadFrames)
                {
                    _lookAheadFrames = newFrames;
                    _activeBufferSize = newFrames + 1;
                    Reset();
                }
            }
        }

        /// <summary>
        /// Delays the signal by the lookahead, then applies the gain the upcoming peaks
        /// call for. Detection is per frame off the loudest channel, so every channel
        /// gets the same gain and the stereo image stays put.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_config == null)
                throw new InvalidOperationException("Effect not initialized. Call Initialize() first.");

            if (!_enabled) return;

            int ch = _channels;

            for (int f = 0; f < frameCount; f++)
            {
                int write = _delayIndex * ch;
                float linked = 0.0f;

                for (int c = 0; c < ch; c++)
                {
                    float s = buffer[f * ch + c];
                    _delayBuffer[write + c] = s;

                    float a = Math.Abs(s);
                    if (a > linked) linked = a;
                }

                float gain = _smoothedGain(_windowMinGain(_gainReduction(_windowPeak(linked))));
                if (!float.IsFinite(gain))
                {
                    gain = 1.0f;
                    _currentGain = 1.0f;
                    _targetGain = 1.0f;
                }

                int read = ((_delayIndex - _lookAheadFrames + _activeBufferSize) % _activeBufferSize) * ch;

                for (int c = 0; c < ch; c++)
                    buffer[f * ch + c] = _applyCeiling(_delayBuffer[read + c] * gain);

                _delayIndex = (_delayIndex + 1) % _activeBufferSize;
                _absoluteFrameIndex++;
            }
        }

        /// <summary>
        /// Ticks up on every Reset, that is how the native twin hears about it.
        /// </summary>
        public int ResetGeneration { get; private set; }

        /// <summary>
        /// Empties the ring and opens the gain back up.
        /// </summary>
        public void Reset()
        {
            ResetGeneration++;
            Array.Clear(_delayBuffer);

            _currentGain = 1.0f;
            _targetGain = 1.0f;
            _delayIndex = 0;
            _absoluteFrameIndex = 0;

            _dequeHead = _dequeTail = _dequeSize = 0;
            _minHead = _minTail = _minSize = 0;
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
        /// Biggest linked peak inside the lookahead window, amortized O(1) from the deque.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float _windowPeak(float linked)
        {
            long expire = _absoluteFrameIndex - _activeBufferSize;

            while (_dequeSize > 0 && _dequeIndices[_dequeHead] <= expire)
            {
                _dequeHead = (_dequeHead + 1) % _maxBufferSize;
                _dequeSize--;
            }

            while (_dequeSize > 0)
            {
                int backIdx = (_dequeTail - 1 + _maxBufferSize) % _maxBufferSize;
                if (_dequeValues[backIdx] >= linked) break;

                _dequeTail = backIdx;
                _dequeSize--;
            }

            _dequeIndices[_dequeTail] = _absoluteFrameIndex;
            _dequeValues[_dequeTail] = linked;
            _dequeTail = (_dequeTail + 1) % _maxBufferSize;
            _dequeSize++;

            return _dequeValues[_dequeHead];
        }

        /// <summary>
        /// Smallest required gain inside the window, same deque trick the other way round.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float _windowMinGain(float required)
        {
            long expire = _absoluteFrameIndex - _activeBufferSize;

            while (_minSize > 0 && _minIndices[_minHead] <= expire)
            {
                _minHead = (_minHead + 1) % _maxBufferSize;
                _minSize--;
            }

            while (_minSize > 0)
            {
                int backIdx = (_minTail - 1 + _maxBufferSize) % _maxBufferSize;
                if (_minValues[backIdx] <= required) break;

                _minTail = backIdx;
                _minSize--;
            }

            _minIndices[_minTail] = _absoluteFrameIndex;
            _minValues[_minTail] = required;
            _minTail = (_minTail + 1) % _maxBufferSize;
            _minSize++;

            return _minValues[_minHead];
        }

        /// <summary>
        /// Gain that lands the peak on the threshold, never below 10%. The old form
        /// divided by the excess twice, which ducked well under the threshold.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float _gainReduction(float peakLevel)
        {
            if (peakLevel <= _threshold) return 1.0f;

            return Math.Max(_threshold / peakLevel, 0.1f);
        }

        /// <summary>
        /// Rides the window minimum: ramped down over the lookahead instead of stepped
        /// (a gain step modulates the low end), eased back up on release. The release
        /// speeds up or slows down with how deep we are.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float _smoothedGain(float minGain)
        {
            _targetGain = minGain;

            if (_targetGain < _currentGain)
            {
                _currentGain += (_targetGain - _currentGain) * _attack;
                if (Math.Abs(_targetGain - _currentGain) < 0.0001f) _currentGain = _targetGain;

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
        /// Attack fast enough to finish inside the lookahead window.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float _attackCoeff(float lookAheadMs, float sampleRate)
        {
            return _releaseCoeff(Math.Max(lookAheadMs / 3.0f, 0.05f), sampleRate);
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
