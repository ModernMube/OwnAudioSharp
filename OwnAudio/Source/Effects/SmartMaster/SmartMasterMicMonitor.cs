using System;
using System.Threading;
using System.Threading.Tasks;
using Ownaudio.Core;
using OwnaudioNET.Effects.SmartMaster.Components;
using OwnaudioNET.Sources;

namespace OwnaudioNET.Effects.SmartMaster
{
    /// <summary>
    /// Monitors microphone input level for UI feedback.
    /// Runs on a background thread with optimized memory usage.
    /// </summary>
    internal sealed class SmartMasterMicMonitor : IDisposable
    {
        private readonly AudioConfig _config;
        private readonly float _micInputGain;
        
        private CancellationTokenSource? _cancellation;
        private Task? _monitoringTask;
        private float _lastMicLevel = -100.0f;
        private bool _disposed;
        
        /// <summary>
        /// Creates a new microphone monitor.
        /// </summary>
        /// <param name="config">Audio configuration.</param>
        /// <param name="micInputGain">Microphone input gain (0.0 - 2.0).</param>
        public SmartMasterMicMonitor(AudioConfig config, float micInputGain)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _micInputGain = micInputGain;
        }
        
        /// <summary>
        /// Gets the last measured microphone level in dB.
        /// </summary>
        public float LastMicLevel => _lastMicLevel;
        
        /// <summary>
        /// Starts microphone monitoring.
        /// </summary>
        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SmartMasterMicMonitor));
            
            if (_monitoringTask != null && !_monitoringTask.IsCompleted)
            {
                return; // Already monitoring
            }
            
            _cancellation = new CancellationTokenSource();
            _monitoringTask = Task.Run(() => MonitoringLoop(_cancellation.Token));
        }
        
        /// <summary>
        /// Stops microphone monitoring.
        /// </summary>
        public void Stop()
        {
            _cancellation?.Cancel();
            _monitoringTask?.Wait(1000); // Wait up to 1 second
            _cancellation?.Dispose();
            _cancellation = null;
            _monitoringTask = null;
            _lastMicLevel = -100.0f;
        }
        
        /// <summary>
        /// Microphone monitoring loop - continuously reads input and updates level.
        /// OPTIMIZED: Pre-allocates all buffers and analyzer to prevent GC.
        /// </summary>
        private void MonitoringLoop(CancellationToken cancellationToken)
        {
            try
            {
                if (OwnaudioNET.OwnaudioNet.Engine == null || !OwnaudioNET.OwnaudioNet.Engine.Config.EnableInput)
                {
                    Logger.Log.Warning("[SmartMaster] Audio input not available for microphone monitoring");
                    return;
                }
                
                var inputSource = new InputSource(OwnaudioNET.OwnaudioNet.Engine, 2048);
                inputSource.Volume = _micInputGain;
                inputSource.Play();
                
                int frameCount = 512;
                int sampleCount = frameCount * _config.Channels;
                float[] buffer = new float[sampleCount];
                
                var analyzer = new SmartMasterSpectrumAnalyzer(_config.SampleRate);
                
                while (!cancellationToken.IsCancellationRequested)
                {
                    int framesRead = inputSource.ReadSamples(buffer.AsSpan(), frameCount);
                    
                    if (framesRead > 0)
                    {
                        int actualSampleCount = framesRead * _config.Channels;
                        float rmsLevel = analyzer.CalculateRMS(buffer.AsSpan(0, actualSampleCount));
                        float rmsDb = 20f * (float)Math.Log10(Math.Max(rmsLevel, 1e-10f));
                        
                        _lastMicLevel = rmsDb;
                    }
                    
                    Thread.Sleep(50); // Update ~20 times per second
                }
                
                inputSource.Stop();
                inputSource.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"[SmartMaster] Microphone monitoring error: {ex.Message}");
                _lastMicLevel = -100.0f;
            }
        }
        
        public void Dispose()
        {
            if (_disposed)
                return;
            
            Stop();
            _disposed = true;
        }
    }
}
