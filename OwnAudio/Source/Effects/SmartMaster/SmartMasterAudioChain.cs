using System;
using System.Runtime.CompilerServices;
using Ownaudio.Core;
using OwnaudioNET.Effects.SmartMaster.Components;

namespace OwnaudioNET.Effects.SmartMaster
{
    /// <summary>
    /// Managed mirror of the native SmartMaster chain, in the same order:
    /// subsonic HPF, graphic EQ, parametric EQ, subharmonic, compressor, then the
    /// crossover section with per-band trim, alignment and limiters, then the
    /// output limiter.
    /// </summary>
    internal sealed class SmartMasterAudioChain : IDisposable
    {
        #region Fields

        private readonly int _sampleRate;
        private readonly int _channels;

        private SubsonicFilter? _subsonic;
        private Equalizer30BandEffect? _graphicEQ;
        private ParametricEqStage? _parametricEQ;
        private SubharmonicSynth? _subharmonicSynth;
        private CompressorEffect? _compressor;
        private CrossoverFilter? _crossover;
        private PhaseAlignment? _phaseAlignment;
        private LimiterEffect? _mainLimiter;
        private LimiterEffect? _subLimiter;
        private LimiterEffect? _limiter;

        private float[]? _tempLBuffer;
        private float[]? _tempRBuffer;
        private float[]? _subLBuffer;
        private float[]? _subRBuffer;
        private float[]? _monoSubBuffer;
        private float[]? _bandScratch;
        private int _maxFrameCount;

        private bool _subharmonicEnabled;
        private bool _compressorEnabled;
        private bool _crossoverActive;
        private float _gainMainL = 1.0f;
        private float _gainMainR = 1.0f;
        private float _gainSub = 1.0f;

        private bool _disposed;

        #endregion

        #region Constructor

        /// <summary>
        /// Sample rate in Hz and the channel count the chain will see.
        /// </summary>
        public SmartMasterAudioChain(int sampleRate, int channels)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _maxFrameCount = 0;
        }

        #endregion

        #region Configuration

        /// <summary>
        /// Builds every stage from the config. Allocates, so keep it off the audio thread.
        /// </summary>
        public void Configure(AudioConfig config, SmartMasterConfig masterConfig)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SmartMasterAudioChain));

            var subsonic = new SubsonicFilter(_sampleRate, masterConfig.SubsonicFrequency, _channels)
            {
                Enabled = masterConfig.SubsonicEnabled
            };

            var graphicEQ = new Equalizer30BandEffect(_sampleRate);
            graphicEQ.Initialize(config);
            graphicEQ.SetAllGains(masterConfig.GraphicEQGains);

            var parametricEQ = new ParametricEqStage(_sampleRate, masterConfig.ParametricEQ, _channels);

            var subharmonicSynth = new SubharmonicSynth(_sampleRate)
            {
                Enabled = masterConfig.SubharmonicEnabled,
                Mix = masterConfig.SubharmonicMix,
                LowLevel = masterConfig.SubharmonicLowLevel,
                HighLevel = masterConfig.SubharmonicHighLevel
            };

            var compressor = new CompressorEffect(sampleRate: _sampleRate);
            compressor.Initialize(config);
            compressor.Enabled = masterConfig.CompressorEnabled;

            compressor.Threshold = CompressorEffect.LinearToDb(masterConfig.CompressorThreshold);
            compressor.Ratio = masterConfig.CompressorRatio;
            compressor.AttackTime = masterConfig.CompressorAttack;
            compressor.ReleaseTime = masterConfig.CompressorRelease;
            compressor.KneeWidth = masterConfig.CompressorKnee;

            var crossover = new CrossoverFilter(_sampleRate, masterConfig.CrossoverFrequency);

            var phaseAlignment = new PhaseAlignment(_sampleRate);
            phaseAlignment.SetDelays(masterConfig.TimeDelays);
            phaseAlignment.SetPhaseInversions(masterConfig.PhaseInvert);

            var mainLimiter = _bandLimiter(config, 2, masterConfig.MainLimiterThreshold);
            var subLimiter = _bandLimiter(config, 1, masterConfig.SubLimiterThreshold);

            var limiter = new LimiterEffect(sampleRate: _sampleRate);
            limiter.Initialize(config);
            limiter.Threshold = masterConfig.LimiterThreshold;
            limiter.Ceiling = masterConfig.LimiterCeiling;
            limiter.Release = masterConfig.LimiterRelease;

            subsonic.Reset();
            graphicEQ.Reset();
            parametricEQ.Reset();
            subharmonicSynth.Reset();
            compressor.Reset();
            crossover.Reset();
            phaseAlignment.Reset();
            mainLimiter.Reset();
            subLimiter.Reset();
            limiter.Reset();

            _maxFrameCount = Math.Max(_maxFrameCount, 2048);
            _tempLBuffer = new float[_maxFrameCount];
            _tempRBuffer = new float[_maxFrameCount];
            _subLBuffer = new float[_maxFrameCount];
            _subRBuffer = new float[_maxFrameCount];
            _monoSubBuffer = new float[_maxFrameCount];
            _bandScratch = new float[_maxFrameCount * 2];

            _subharmonicEnabled = masterConfig.SubharmonicEnabled;
            _compressorEnabled = masterConfig.CompressorEnabled;

            float[] delays = masterConfig.TimeDelays;
            bool[] invert = masterConfig.PhaseInvert;
            _crossoverActive = masterConfig.CrossoverEnabled;
            for (int i = 0; i < SmartMasterConfig.AlignChannels && !_crossoverActive; i++)
            {
                if (Math.Abs(delays[i]) > 0.001f || invert[i]) _crossoverActive = true;
            }

            float[] gains = masterConfig.OutputGains;
            _gainMainL = _dbToLinear(gains[0]);
            _gainMainR = _dbToLinear(gains[1]);
            _gainSub = _dbToLinear(gains[2]);

            _subsonic = subsonic;
            _graphicEQ = graphicEQ;
            _parametricEQ = parametricEQ;
            _subharmonicSynth = subharmonicSynth;
            _compressor = compressor;
            _crossover = crossover;
            _phaseAlignment = phaseAlignment;
            _mainLimiter = mainLimiter;
            _subLimiter = subLimiter;
            _limiter = limiter;
        }

        /// <summary>
        /// Driver protection limiter for one band, laid out for its own channel
        /// count. At 0 dBFS it just sits open.
        /// </summary>
        private LimiterEffect _bandLimiter(AudioConfig config, int channels, float thresholdDb)
        {
            var bandConfig = new AudioConfig
            {
                SampleRate = config.SampleRate,
                Channels = channels,
                BufferSize = config.BufferSize
            };

            var l = new LimiterEffect(sampleRate: _sampleRate);
            l.Initialize(bandConfig);
            l.Threshold = thresholdDb;
            l.Ceiling = 0.0f;
            l.Release = 80.0f;

            return l;
        }

        private static float _dbToLinear(float db) => MathF.Pow(10.0f, db / 20.0f);

        #endregion

        #region Latency

        /// <summary>
        /// Lookahead the chain adds. Only the limiters delay anything, and with the
        /// crossover running the band limiter sits in series with the output one.
        /// </summary>
        public int LimiterLatencySamples
        {
            get
            {
                int band = _crossoverActive ? (_mainLimiter?.LatencySamples ?? 0) : 0;
                return band + (_limiter?.LatencySamples ?? 0);
            }
        }

        #endregion

        #region Audio Processing

        /// <summary>
        /// Runs an interleaved block through the chain, in place.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(Span<float> buffer, int frameCount)
        {
            if (_graphicEQ == null) return;

            if (frameCount > _maxFrameCount)
            {
                _maxFrameCount = frameCount;
                _tempLBuffer = new float[_maxFrameCount];
                _tempRBuffer = new float[_maxFrameCount];
                _subLBuffer = new float[_maxFrameCount];
                _subRBuffer = new float[_maxFrameCount];
                _monoSubBuffer = new float[_maxFrameCount];
                _bandScratch = new float[_maxFrameCount * 2];
            }

            _subsonic?.Process(buffer, frameCount, _channels);
            _graphicEQ.Process(buffer, frameCount);
            _parametricEQ?.Process(buffer, frameCount, _channels);

            if (_subharmonicEnabled && _subharmonicSynth != null)
                _subharmonicSynth.Process(buffer, frameCount, _channels);

            if (_compressorEnabled && _compressor != null)
                _compressor.Process(buffer, frameCount);

            if (_crossoverActive) ProcessCrossoverChain(buffer, frameCount);

            _limiter?.Process(buffer, frameCount);
        }

        /// <summary>
        /// Splits into a main and a mono sub band, trims, aligns and limits each,
        /// then sums them back into the buffer.
        /// </summary>
        private void ProcessCrossoverChain(Span<float> buffer, int frameCount)
        {
            if (_crossover == null || _phaseAlignment == null) return;

            float[] tempL = _tempLBuffer!;
            float[] tempR = _tempRBuffer!;
            float[] subL = _subLBuffer!;
            float[] subR = _subRBuffer!;
            float[] monoSub = _monoSubBuffer!;
            float[] band = _bandScratch!;

            int channels = _channels;

            for (int i = 0; i < frameCount; i++)
            {
                tempL[i] = buffer[i * channels];
                tempR[i] = channels > 1 ? buffer[i * channels + 1] : tempL[i];
            }

            _crossover.Process(tempL.AsSpan(0, frameCount), tempL.AsSpan(0, frameCount), subL.AsSpan(0, frameCount), frameCount, 0);
            _crossover.Process(tempR.AsSpan(0, frameCount), tempR.AsSpan(0, frameCount), subR.AsSpan(0, frameCount), frameCount, 1);

            for (int i = 0; i < frameCount; i++)
            {
                monoSub[i] = (subL[i] + subR[i]) * 0.5f * _gainSub;
                tempL[i] *= _gainMainL;
                tempR[i] *= _gainMainR;
            }

            _phaseAlignment.Process(tempL.AsSpan(0, frameCount), 0, frameCount);
            _phaseAlignment.Process(tempR.AsSpan(0, frameCount), 1, frameCount);
            _phaseAlignment.Process(monoSub.AsSpan(0, frameCount), 2, frameCount);

            for (int i = 0; i < frameCount; i++)
            {
                band[i * 2] = tempL[i];
                band[i * 2 + 1] = tempR[i];
            }

            _mainLimiter?.Process(band.AsSpan(0, frameCount * 2), frameCount);
            _subLimiter?.Process(monoSub.AsSpan(0, frameCount), frameCount);

            for (int i = 0; i < frameCount; i++)
            {
                float sub = monoSub[i];
                buffer[i * channels] = band[i * 2] + sub;
                if (channels > 1) buffer[i * channels + 1] = band[i * 2 + 1] + sub;
            }
        }

        #endregion

        #region Reset

        /// <summary>
        /// Clears every stage's state, keeps the configuration.
        /// </summary>
        public void Reset()
        {
            _subsonic?.Reset();
            _graphicEQ?.Reset();
            _parametricEQ?.Reset();
            _subharmonicSynth?.Reset();
            _compressor?.Reset();
            _crossover?.Reset();
            _phaseAlignment?.Reset();
            _mainLimiter?.Reset();
            _subLimiter?.Reset();
            _limiter?.Reset();
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            if (_disposed) return;

            _graphicEQ?.Dispose();
            _compressor?.Dispose();
            _mainLimiter?.Dispose();
            _subLimiter?.Dispose();
            _limiter?.Dispose();

            _disposed = true;
        }

        #endregion
    }
}
