using System;
using System.Threading;
using Logger;
using OwnaudioNET.Effects.SmartMaster.Components;
using OwnaudioNET.Mixing;
using OwnaudioNET.Monitoring;
using OwnaudioNET.Sources;

namespace OwnaudioNET.Effects.SmartMaster
{
    /// <summary>
    /// Measurement mic. The capture hangs on the mixer as a muted InputSource and we read it
    /// through an effect tap, which sits ahead of the track gain - full level with nothing
    /// fed back into the speakers. The only reader of that tap, the measurement goes through here too.
    /// </summary>
    internal sealed class SmartMasterMicMonitor : IDisposable
    {
        private const int PollMilliseconds = 40;

        private readonly AudioMixer _mixer;
        private readonly object _lock = new object();
        private readonly SmartMasterSpectrumAnalyzer _spectrum;

        private InputSource? _input;
        private EffectTap? _tap;
        private Timer? _timer;
        private float[] _pre = Array.Empty<float>();
        private float[] _post = Array.Empty<float>();
        private float _lastMicLevel = -100.0f;
        private double _rmsPower;
        private long _rmsSamples;
        private bool _disposed;

        public SmartMasterMicMonitor(AudioMixer mixer)
        {
            _mixer = mixer;
            _spectrum = new SmartMasterSpectrumAnalyzer(mixer.Config.SampleRate);
        }

        /// <summary>
        /// Scales the capture before anything is measured, 1 is unity.
        /// </summary>
        public float Gain { get; set; } = 1.0f;

        /// <summary>
        /// Mic peak in dBFS from the last poll.
        /// </summary>
        public float LastMicLevel => _lastMicLevel;

        public bool IsRunning => _input != null;

        /// <summary>
        /// Hangs the capture on the mixer and starts polling. False when the engine came up
        /// without its input side.
        /// </summary>
        public bool Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SmartMasterMicMonitor));

            lock (_lock)
            {
                if (_input != null) return true;

                var _engine = OwnaudioNet.Engine;
                if (_engine == null || !_engine.Config.EnableInput)
                {
                    Log.Warning("[SmartMaster] Audio input is not enabled (AudioConfig.EnableInput), nothing to measure with");
                    return false;
                }

                var _source = new InputSource(_engine, 2048);
                _source.Volume = 0f;

                _mixer.AddSource(_source);
                _source.Play();

                //200ms of ring, a GC pause must not cost the measurement a window
                int _capacity = _mixer.Config.SampleRate / 5 * _mixer.Config.Channels;

                try
                {
                    _tap = _mixer.CreateEffectTap(_source.Id, _capacity);
                }
                catch (Exception ex)
                {
                    Log.Error("[SmartMaster] Could not tap the mic track", ex);
                    _mixer.RemoveSource(_source.Id);
                    _source.Dispose();
                    return false;
                }

                if (_pre.Length < _capacity)
                {
                    _pre = new float[_capacity];
                    _post = new float[_capacity];
                }

                _spectrum.ResetAverage();
                _input = _source;
                _timer = new Timer(_ => _poll(), null, PollMilliseconds, PollMilliseconds);
            }

            Log.Info("[SmartMaster] Mic capture running");
            return true;
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;

            lock (_lock)
            {
                var _source = _input;
                if (_source == null) return;

                _input = null;
                _tap?.Dispose();
                _tap = null;

                _mixer.RemoveSource(_source.Id);
                _source.Dispose();

                _lastMicLevel = -100.0f;
                _rmsPower = 0;
                _rmsSamples = 0;
            }

            Log.Info("[SmartMaster] Mic capture stopped");
        }

        /// <summary>
        /// Starts a fresh averaging window, spectrum and RMS both.
        /// </summary>
        public void BeginWindow()
        {
            lock (_lock)
            {
                _spectrum.ResetAverage();
                _rmsPower = 0;
                _rmsSamples = 0;
            }
        }

        /// <summary>
        /// Mean band levels in dBFS since BeginWindow, plus the true RMS of the same stretch -
        /// a band sum carries the window's own gain and reads several dB off.
        /// </summary>
        /// <returns>Windows averaged, 0 means the mic never delivered a full one.</returns>
        public int ReadWindow(float[] bandsDb, out float rmsDb)
        {
            lock (_lock)
            {
                rmsDb = _rmsSamples > 0
                    ? (float)Math.Max(10.0 * Math.Log10(Math.Max(_rmsPower / _rmsSamples, 1e-12)), -100.0)
                    : -100f;

                return _spectrum.Average(bandsDb);
            }
        }

        /// <summary>
        /// Drains the tap once. Timer thread, allocation free.
        /// </summary>
        private void _poll()
        {
            lock (_lock)
            {
                if (_tap == null) return;

                int _read;
                try { _read = _tap.Read(_pre, _post); }
                catch (Exception ex)
                {
                    //A ClearSources took the mic track away under us; a throw here would take the process down
                    Log.Warning($"[SmartMaster] Mic tap went away, capture stops: {ex.Message}");
                    _tap = null;
                    return;
                }

                if (_read <= 0) return;

                float _gain = Gain;
                float _peak = 0f;

                for (int i = 0; i < _read; i++)
                {
                    float _v = _pre[i] * _gain;
                    _pre[i] = _v;

                    float _abs = Math.Abs(_v);
                    if (_abs > _peak) _peak = _abs;
                    _rmsPower += (double)_v * _v;
                }

                _rmsSamples += _read;
                _lastMicLevel = _peak > 0.00001f ? MathF.Max(20f * MathF.Log10(_peak), -100f) : -100f;
                _spectrum.Push(_pre.AsSpan(0, _read), _tap.Channels);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            Stop();
            _disposed = true;
        }
    }
}
