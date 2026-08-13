using Ownaudio.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Effects.SmartMaster
{
    /// <summary>
    /// Smart Master effect - Intelligent master processing chain
    /// Facade pattern: Coordinates audio processing, measurement, and preset management.
    /// </summary>
    public sealed class SmartMasterEffect : IEffectProcessor
    {
        #region Fields
        
        private readonly Guid _id;
        private string _name;
        private bool _enabled;
        private bool _disposed;
        
        private AudioConfig? _config;
        private SmartMasterConfig _configuration;
        private MeasurementStatusInfo _measurementStatus;
        
        private SmartMasterAudioChain? _audioChain;
        private SmartMasterPresetManager? _presetManager;
        private SmartMasterMeasurementService? _measurementService;
        private SmartMasterMicMonitor? _micMonitor;
        
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
        public int LatencySamples => _audioChain?.LimiterLatencySamples ?? 0;

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
            
            if (_audioChain == null)
            {
                _audioChain = new SmartMasterAudioChain(config.SampleRate, config.Channels);
                _audioChain.Configure(config, _configuration);
                Logger.Log.Info($"[SmartMaster] Audio chain created with current configuration");
            }
            else
            {
                Logger.Log.Info($"[SmartMaster] Audio chain already exists - preserving state");
            }
        }
        
        #endregion
        
        #region Audio Processing (Hot Path - Lock-Free Read)
        
        public void Process(Span<float> buffer, int frameCount)
        {
            if (buffer.Length == 0 || frameCount == 0)
                return;

            if (!_enabled || _audioChain == null)
                return;

            for (int i = 0; i < buffer.Length; i++)
            {
                if (!float.IsFinite(buffer[i]))
                {
                    buffer[i] = 0.0f;
                    _sanitizedSamples++;
                }
            }

            _audioChain.Process(buffer, frameCount);
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
            lock (_configLock)
            {
                _audioChain?.Reset();
                _measurementStatus = new MeasurementStatusInfo();
            }
        }
        
        /// <summary>
        /// Call this when playback stops to reset component state
        /// </summary>
        public void OnPlaybackStopped()
        {
            Logger.Log.Info("[SmartMaster] Playback stopped - explicitly resetting components");
            lock (_configLock)
            {
                _audioChain?.Reset();
            }
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            
            _audioChain?.Dispose();
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
        /// Starts the automatic measurement process asynchronously.
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
            
            try
            {
                var measuredConfig = await _measurementService.PerformMeasurementAsync(
                    status => _measurementStatus = status,
                    _configuration.MicInputGain,
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
                _swapChain(new SmartMasterConfig());

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
            _measurementCancellation?.Cancel();
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
        /// Start microphone monitoring (for UI level meter)
        /// </summary>
        public void StartMicMonitoring()
        {
            ThrowIfDisposed();
            
            if (_config == null)
                throw new InvalidOperationException("Effect not initialized");
            
            if (_micMonitor == null)
            {
                _micMonitor = new SmartMasterMicMonitor(_config, _configuration.MicInputGain);
            }
            
            _micMonitor.Start();
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
        /// Rebuilds the chain from the current configuration. Call it after editing what
        /// <see cref="GetConfiguration"/> handed you, otherwise the managed chain keeps
        /// running the values it was built with. Rust-native mode picks the edits up on
        /// its own, but calling this there is harmless.
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
        /// Builds a fresh chain for the given config and swaps it in, so the audio thread
        /// never sees a half configured one.
        /// </summary>
        private void _swapChain(SmartMasterConfig configuration)
        {
            if (_config == null)
                return;

            lock (_configLock)
            {
                _configuration = configuration;

                var newChain = new SmartMasterAudioChain(_config.SampleRate, _config.Channels);
                newChain.Configure(_config, _configuration);

                var oldChain = _audioChain;
                _audioChain = newChain;
                oldChain?.Dispose();
            }
        }

        #endregion
    }
}
