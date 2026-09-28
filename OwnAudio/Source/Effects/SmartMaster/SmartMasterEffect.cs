using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Mixing;
using NativeEffectEngine = OwnaudioNET.Effects.NativeEffectEngine;

namespace OwnaudioNET.Effects.SmartMaster
{
    /// <summary>
    /// Smart Master effect - Intelligent master processing chain
    /// Facade pattern: Coordinates audio processing, measurement, and preset management.
    /// </summary>
    public sealed class SmartMasterEffect : IEffectProcessor, IMasterBusAware
    {
        #region Fields
        
        private readonly Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        
        private AudioConfig? _config;
        private SmartMasterConfig _configuration;
        private MeasurementStatusInfo _measurementStatus;
        
        private readonly NativeEffectEngine _native = new NativeEffectEngine();
        private SmartMasterPresetManager? _presetManager;
        private SmartMasterMeasurementService? _measurementService;
        private SmartMasterMicMonitor? _micMonitor;

        /// <summary>
        /// The mixer whose master bus we sit on, the measurement plays and records through it.
        /// </summary>
        private AudioMixer? _mixer;

        private readonly object _configLock = new object();
        
        private CancellationTokenSource? _measurementCancellation;
        
        private long _sanitizedSamples;

        private const float SILENCE_THRESHOLD = 0.0001f;
        
        #endregion
        
        #region IEffectProcessor Implementation
        
        public Guid Id => _id;
        
        public string Name
        {
            get => _name;
            set => _name = value ?? "SmartMaster";
        }
        
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }
        
        public float Mix { get; set; } = 1.0f;

        /// <summary>
        /// Gets the processing latency introduced by the internal effect chain in samples.
        /// </summary>
        /// <remarks>
        /// The SmartMaster chain consists of a graphic equalizer, optional subharmonic
        /// synth, compressor, crossover, phase alignment, and a brick-wall limiter.
        /// Only the internal <see cref="LimiterEffect"/> introduces latency (its lookahead
        /// window). All other components are zero-latency IIR processors.
        /// Returns 0 before <see cref="Initialize"/> has been called.
        /// </remarks>
        public int LatencySamples => _native.LatencySamples;

        #endregion
        
        #region Constructor
        
        public SmartMasterEffect()
        {
            _id = Guid.NewGuid();
            _name = "SmartMaster";
            _enabled = true;
            _configuration = new SmartMasterConfig();
            _measurementStatus = new MeasurementStatusInfo();
        }

        private static string _defaultPresetsDirectory()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            return System.IO.Path.Combine(userProfile, ".ownaudio", "smartmasterpresets");
        }
        
        #endregion
        
        #region Initialization
        
        /// <summary>
        /// Somewhere other than the user profile to keep presets. Tests use a temp folder.
        /// </summary>
        internal static string? PresetsDirectoryOverride { get; set; }

        public void Initialize(AudioConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            string presetsDirectory = PresetsDirectoryOverride ?? _defaultPresetsDirectory();

            _presetManager = new SmartMasterPresetManager(presetsDirectory);
            _presetManager.CreateFactoryPresetsIfNeeded();
            
            _measurementService = new SmartMasterMeasurementService(config, presetsDirectory);
            _native.Initialize(this, config);
        }

        /// <summary>
        /// The mic hangs on the old mixer as a source, so it goes when the bus changes - and a
        /// measurement running on the old bus is cancelled rather than left with a dead mic.
        /// </summary>
        void IMasterBusAware.AttachMixer(AudioMixer? mixer)
        {
            if (ReferenceEquals(_mixer, mixer)) return;

            CancelMeasurement();
            _micMonitor?.Dispose();
            _micMonitor = null;
            _mixer = mixer;
        }

        #endregion
        
        #region Audio Processing

        /// <summary>
        /// Sanitises the block, then hands it to the standalone native chain. Bypassed means
        /// bypassed — we don't even scan the buffer.
        /// </summary>
        public void Process(Span<float> buffer, int frameCount)
        {
            if (!_enabled || buffer.Length == 0 || frameCount == 0)
                return;

            for (int i = 0; i < buffer.Length; i++)
            {
                if (!float.IsFinite(buffer[i]))
                {
                    buffer[i] = 0.0f;
                    _sanitizedSamples++;
                }
            }

            _native.Process(this, buffer, frameCount);
        }

        /// <summary>
        /// How many NaN/Inf samples the chain has had to zero out since it started.
        /// Non-zero means something upstream is broken.
        /// </summary>
        public long SanitizedSampleCount => _sanitizedSamples;
        
        #endregion
        
        #region Reset and Dispose
        
        /// <summary>
        /// Ticks up on every Reset, that is how the native twin hears about it.
        /// </summary>
        public int ResetGeneration { get; private set; }

        /// <summary>
        /// Full reset including measurement status
        /// </summary>
        public void Reset()
        {
            ResetGeneration++;
            _native.Reset();
            _sanitizedSamples = 0;
            lock (_configLock)
            {
                _measurementStatus = new MeasurementStatusInfo();
            }
        }
        
        /// <summary>
        /// Call this when playback stops to reset component state
        /// </summary>
        public void OnPlaybackStopped()
        {
            Logger.Log.Info("[SmartMaster] Playback stopped - explicitly resetting components");
            _native.Reset();
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            
            _native.Dispose();
            _micMonitor?.Dispose();
            
            _disposed = true;
        }
        
        #endregion
        
        #region Public API - Preset Management
        
        /// <summary>
        /// Saves the current configuration to a preset file.
        /// </summary>
        public void Save(string presetName)
        {
            ThrowIfDisposed();
            
            if (_presetManager == null)
                throw new InvalidOperationException("Effect not initialized");
            
            _presetManager.Save(_configuration, presetName);
        }
        
        /// <summary>
        /// Loads a preset from disk and applies it.
        /// </summary>
        public void Load(string presetName)
        {
            ThrowIfDisposed();
            
            if (_presetManager == null || _config == null)
                throw new InvalidOperationException("Effect not initialized");
            
            _swapChain(_presetManager.Load(presetName));
            Logger.Log.Info($"[SmartMaster] Preset loaded and applied: {presetName}");
        }
        
        /// <summary>
        /// Loads a speaker-specific factory preset.
        /// </summary>
        public void LoadSpeakerPreset(SpeakerType speakerType)
        {
            ThrowIfDisposed();
            
            if (_presetManager == null || _config == null)
                throw new InvalidOperationException("Effect not initialized");
            
            _swapChain(_presetManager.LoadSpeakerPreset(speakerType));
            Logger.Log.Info($"[SmartMaster] Loaded speaker preset: {speakerType}");
        }
        
        /// <summary>
        /// Reset to default settings and save
        /// </summary>
        public void ResetToDefaults()
        {
            ThrowIfDisposed();
            
            if (_presetManager == null || _config == null)
                throw new InvalidOperationException("Effect not initialized");
            
            _swapChain(new SmartMasterConfig());
            _presetManager.Save(_configuration, "default");

            Logger.Log.Info("[SmartMaster] Default settings restored and saved");
        }
        
        #endregion
        
        #region Public API - Measurement
        
        /// <summary>
        /// Get measurement status
        /// </summary>
        public MeasurementStatusInfo GetMeasurementStatus()
        {
            return _measurementStatus;
        }
        
        /// <summary>
        /// Plays pink noise through the mixer this effect sits on and measures the room with
        /// the mic. The mixer has to be running; the effect is bypassed while it measures, so
        /// it doesn't hear its own EQ back. The result goes to the 'measured' preset, not applied.
        /// </summary>
        public async Task StartMeasurementAsync()
        {
            ThrowIfDisposed();

            if (_measurementService == null || _config == null)
                throw new InvalidOperationException("Effect not initialized");

            if (_measurementStatus.Status != MeasurementStatus.Idle &&
                _measurementStatus.Status != MeasurementStatus.Completed &&
                _measurementStatus.Status != MeasurementStatus.Error)
            {
                throw new InvalidOperationException("A measurement is already in progress!");
            }

            bool wasEnabled = _enabled;
            _enabled = false;

            Reset();

            _measurementCancellation = new CancellationTokenSource();
            bool _ownMic = _micMonitor?.IsRunning != true;

            try
            {
                if (_mixer == null)
                    throw new InvalidOperationException(
                        "SmartMaster is not on a mixer's master bus - AddMasterEffect it first, the measurement plays and records through that mixer.");

                if (!_mixer.IsRunning)
                    throw new InvalidOperationException("The mixer is not running, start it before measuring.");

                var mic = _startMic();

                await _measurementService.PerformMeasurementAsync(
                    _mixer,
                    mic,
                    status => _measurementStatus = status,
                    _measurementCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                Logger.Log.Info("[SmartMaster] Measurement cancelled");
                _measurementStatus.Status = MeasurementStatus.Idle;
                _measurementStatus.ErrorMessage = "Measurement cancelled";
            }
            catch (Exception ex)
            {
                Logger.Log.Error("[SmartMaster] Measurement error", ex);
                _measurementStatus.Status = MeasurementStatus.Error;
                _measurementStatus.ErrorMessage = ex.Message;
            }
            finally
            {
                if (_ownMic) _micMonitor?.Stop();

                _enabled = wasEnabled;
                
                _measurementCancellation?.Dispose();
                _measurementCancellation = null;
                
                await Task.Delay(100);
                if (_measurementStatus.Status == MeasurementStatus.Completed || 
                    _measurementStatus.Status == MeasurementStatus.Error)
                {
                    _measurementStatus.Status = MeasurementStatus.Idle;
                }
            }
        }
        
        /// <summary>
        /// Cancels the currently running measurement process.
        /// </summary>
        public void CancelMeasurement()
        {
            try { _measurementCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        
        #endregion
        
        #region Public API - Microphone Monitoring
        
        /// <summary>
        /// Get the last measured microphone level in dB
        /// </summary>
        public float GetLastMicLevel()
        {
            return _micMonitor?.LastMicLevel ?? -100.0f;
        }
        
        /// <summary>
        /// Start microphone monitoring (for UI level meter). Needs the effect on a mixer's
        /// master bus - the mic hangs on that mixer, muted.
        /// </summary>
        public void StartMicMonitoring()
        {
            ThrowIfDisposed();

            if (_config == null)
                throw new InvalidOperationException("Effect not initialized");

            if (_mixer == null)
                throw new InvalidOperationException("SmartMaster is not on a mixer's master bus - AddMasterEffect it first.");

            _startMic();
        }

        /// <summary>
        /// Builds the monitor on first use and starts it with the current mic gain.
        /// </summary>
        private SmartMasterMicMonitor _startMic()
        {
            _micMonitor ??= new SmartMasterMicMonitor(_mixer!);
            _micMonitor.Gain = _configuration.MicInputGain;

            if (!_micMonitor.Start())
                throw new InvalidOperationException(
                    "Microphone capture could not start. Set 'audioConfig.EnableInput = true' before initializing OwnAudioNet and check the input device.");

            return _micMonitor;
        }
        
        /// <summary>
        /// Stop microphone monitoring
        /// </summary>
        public void StopMicMonitoring()
        {
            _micMonitor?.Stop();
        }
        
        #endregion
        
        #region Public API - Configuration
        
        /// <summary>
        /// Get configuration
        /// </summary>
        public SmartMasterConfig GetConfiguration()
        {
            return _configuration;
        }

        /// <summary>
        /// Pushes the current configuration onto the native engine. Call it after editing
        /// what <see cref="GetConfiguration"/> handed you so the next Process() sees it.
        /// </summary>
        public void ApplyConfiguration()
        {
            ApplyConfiguration(_configuration);
        }

        /// <summary>
        /// Takes over a configuration built elsewhere (own JSON, UI model, measured preset)
        /// and rebuilds the chain from it.
        /// </summary>
        public void ApplyConfiguration(SmartMasterConfig configuration)
        {
            ThrowIfDisposed();

            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            if (_config == null)
                throw new InvalidOperationException("Effect not initialized");

            _swapChain(configuration);
        }

        #endregion

        #region Private Methods

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SmartMasterEffect));
        }

        /// <summary>
        /// Stores the config and pushes it onto the native engine. The managed chain
        /// is not on the audio path.
        /// </summary>
        private void _swapChain(SmartMasterConfig configuration)
        {
            if (_config == null)
                return;

            lock (_configLock)
            {
                _configuration = configuration;
                _native.Push(this);
            }
        }

        #endregion
    }
}
