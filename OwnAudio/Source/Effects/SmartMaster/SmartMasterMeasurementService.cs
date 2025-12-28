using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ownaudio.Core;
using OwnaudioNET.Effects.SmartMaster.Components;
using OwnaudioNET.Sources;
using Logger;

namespace OwnaudioNET.Effects.SmartMaster
{
    /// <summary>
    /// Service for performing SmartMaster automatic measurement and calibration.
    /// </summary>
    internal sealed class SmartMasterMeasurementService
    {
        private readonly AudioConfig _config;
        private readonly string _presetsDirectory;
        
        /// <summary>
        /// Creates a new measurement service.
        /// </summary>
        /// <param name="config">Audio configuration.</param>
        /// <param name="presetsDirectory">Directory for saving measurement results.</param>
        public SmartMasterMeasurementService(AudioConfig config, string presetsDirectory)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _presetsDirectory = presetsDirectory ?? throw new ArgumentNullException(nameof(presetsDirectory));
        }
        
        /// <summary>
        /// Performs the complete measurement process including level detection, delay measurement, and frequency response analysis.
        /// </summary>
        /// <param name="statusCallback">Callback for status updates.</param>
        /// <param name="micInputGain">Microphone input gain.</param>
        /// <param name="cancellationToken">Cancellation token to abort the measurement.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task<SmartMasterConfig> PerformMeasurementAsync(
            Action<MeasurementStatusInfo> statusCallback,
            float micInputGain,
            CancellationToken cancellationToken)
        {
            var results = new MeasurementResults
            {
                MeasurementDate = DateTime.Now
            };
            
            var status = new MeasurementStatusInfo();
            
            // 1. Initialization
            UpdateStatus(status, statusCallback, MeasurementStatus.Initializing, 0.0f, "Initializing measurement...");

            // VERIFICATION: Check if input is enabled in engine configuration
            if (!OwnaudioNET.OwnaudioNet.Engine!.Config.EnableInput)
            {
                throw new InvalidOperationException(
                    "Audio Input is NOT enabled in OwnAudio configuration. Please set 'audioConfig.EnableInput = true' before initializing OwnAudioNet.");
            }

            // VERIFICATION: Check if any input device is available
            var inputDevices = OwnaudioNET.OwnaudioNet.Engine.GetInputDevices();
            if (inputDevices == null || inputDevices.Count == 0)
            {
                throw new InvalidOperationException("No audio input devices found!");
            }
            
            // Clear audio output buffer to prevent residual audio from interfering
            if (OwnaudioNET.OwnaudioNet.Engine != null)
            {
                try
                {
                    OwnaudioNET.OwnaudioNet.Engine.ClearOutputBuffer();
                }
                catch
                {
                    // Ignore if clear fails
                }
            }

            await Task.Delay(500, cancellationToken);
            
            // 2. Right channel check
            UpdateStatus(status, statusCallback, MeasurementStatus.CheckingRightChannel, 0.2f, "Checking right channel...");
            bool rightOk = await CheckChannelAsync(1, results, micInputGain, cancellationToken);
            if (!rightOk)
            {
                AddWarning(results, "Right channel error: no signal or too quiet");
            }
            
            // 3. Left channel check
            UpdateStatus(status, statusCallback, MeasurementStatus.CheckingLeftChannel, 0.4f, "Checking left channel...");
            bool leftOk = await CheckChannelAsync(0, results, micInputGain, cancellationToken);
            if (!leftOk)
            {
                AddWarning(results, "Left channel error: no signal or too quiet");
            }
            
            // 4. Subwoofer check
            UpdateStatus(status, statusCallback, MeasurementStatus.CheckingSubwoofer, 0.6f, "Checking subwoofer...");
            bool subOk = await CheckSubwooferAsync(results, micInputGain, cancellationToken);
            if (!subOk)
            {
                AddWarning(results, "Warning: Weak or missing low frequency range");
            }
            
            // 5. Spectrum analysis
            UpdateStatus(status, statusCallback, MeasurementStatus.AnalyzingSpectrum, 0.75f, "Spectrum analysis...");
            await AnalyzeSpectrumAsync(results, micInputGain, cancellationToken);
            
            // 6. Evaluate results and decide if we can proceed
            if (!rightOk || !leftOk)
            {
                // Critical errors - cannot calculate corrections
                UpdateStatus(status, statusCallback, MeasurementStatus.Error, 1.0f, 
                    "Measurement failed: " + string.Join(", ", results.Warnings));
                
                Log.Warning($"[SmartMaster] Measurement failed, SmartMaster settings remain unchanged. Warnings: {string.Join(", ", results.Warnings)}");
                
                throw new InvalidOperationException(
                    "Measurement failed due to critical errors. Please check microphone placement and volume. SmartMaster settings remain unchanged.");
            }
            
            // 7. Create a NEW configuration for measurement results
            UpdateStatus(status, statusCallback, MeasurementStatus.CalculatingCorrection, 0.9f, "Calculating correction...");
            
            var measuredConfig = new SmartMasterConfig();
            CalculateCorrectionsToConfig(results, measuredConfig);
            
            // 8. Store measurement results in the measured config
            measuredConfig.LastMeasurement = results;
            
            // 9. Save the measured configuration to a separate preset file
            try
            {
                string fileName = "measured.smartmaster.json";
                string filePath = Path.Combine(_presetsDirectory, fileName);
                
                var options = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                };
                
                string json = System.Text.Json.JsonSerializer.Serialize(measuredConfig, options);
                File.WriteAllText(filePath, json);
                
                Log.Info($"[SmartMaster] Measurement results saved to '{filePath}'");
            }
            catch (Exception ex)
            {
                Log.Warning($"[SmartMaster] Failed to save measurement results: {ex.Message}");
            }
            
            // 10. Completion
            if (results.Warnings.Length == 0)
            {
                UpdateStatus(status, statusCallback, MeasurementStatus.Completed, 1.0f, 
                    "Measurement completed. Results saved to 'measured' preset (not applied).");
            }
            else
            {
                UpdateStatus(status, statusCallback, MeasurementStatus.Completed, 1.0f, 
                    $"Measurement completed with {results.Warnings.Length} warning(s). Results saved to 'measured' preset (not applied).");
            }
            
            Log.Info($"[SmartMaster] Measurement completed. Warnings: {results.Warnings.Length}");
            
            return measuredConfig;
        }
        
        /// <summary>
        /// Checks a specific audio channel by playing test noise and recording the response.
        /// </summary>
        private async Task<bool> CheckChannelAsync(int channel, MeasurementResults results, float micInputGain, CancellationToken cancellationToken)
        {
            try
            {
                // Check if audio engine is available
                if (OwnaudioNET.OwnaudioNet.Engine == null)
                {
                    Log.Warning("[SmartMaster] Audio engine not available for measurement");
                    return false;
                }
                
                // 1. Noise generation (white noise, 2 seconds)
                int durationSeconds = 2;
                int sampleCount = _config.SampleRate * durationSeconds;
                float[] whiteNoise = NoiseGenerator.GenerateWhiteNoise(sampleCount, 0.3f);
                
                // 2. Prepare multi-channel audio (noise only on target channel)
                float[] channelAudio = new float[sampleCount * _config.Channels];
                for (int i = 0; i < sampleCount; i++)
                {
                    for (int ch = 0; ch < _config.Channels; ch++)
                    {
                        channelAudio[i * _config.Channels + ch] = (ch == channel) ? whiteNoise[i] : 0f;
                    }
                }
                
                // 3. Create SampleSource for playback
                var noiseSource = new SampleSource(channelAudio, _config);
                noiseSource.Loop = false;
                
                // 4. Create InputSource for recording
                var inputSource = new InputSource(OwnaudioNET.OwnaudioNet.Engine, 8192);
                inputSource.Volume = micInputGain;
                
                // 5. Start playback and recording
                noiseSource.Play();
                inputSource.Play();
                
                // 6. Smart Pumping: Monitor engine buffer and send data only when space is available
                int playbackFrames = _config.SampleRate * 2; // 2 seconds
                int playbackSamples = playbackFrames * _config.Channels;
                const int chunkFrames = 512;
                float[] playbackBuffer = new float[chunkFrames * _config.Channels];
                
                // Recording buffer setup
                int recordDuration = 1500; // ms
                int recordFrames = _config.SampleRate * recordDuration / 1000;
                int recordSamples = recordFrames * _config.Channels;
                float[] recordedBuffer = new float[recordSamples];
                
                int totalPlayed = 0;
                int totalRead = 0;
                
                // Wait for signal to stabilize (300ms)
                await Task.Delay(300, cancellationToken);
                
                // Calculate engine buffer capacity
                int engineBufferCapacity = OwnaudioNET.OwnaudioNet.Engine.FramesPerBuffer * _config.Channels * 2;
                
                // Simultaneous playback and recording loop
                while (totalPlayed < playbackSamples && totalRead < recordFrames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    // Check engine buffer availability
                    int bufferOccupied = OwnaudioNET.OwnaudioNet.Engine.OutputBufferAvailable;
                    int bufferFree = engineBufferCapacity - bufferOccupied;
                    
                    // GREEDY PUMPING: Fill buffer if there is space
                    if (bufferFree >= 64 * _config.Channels)
                    {
                        int framesSpace = bufferFree / _config.Channels;
                        int framesToPlay = Math.Min(framesSpace, (playbackSamples - totalPlayed) / _config.Channels);
                        framesToPlay = Math.Min(framesToPlay, 1024);
                        
                        if (framesToPlay > 0)
                        {
                            if (playbackBuffer.Length < framesToPlay * _config.Channels)
                            {
                                playbackBuffer = new float[framesToPlay * _config.Channels];
                            }

                            int samplesPlayed = noiseSource.ReadSamples(playbackBuffer.AsSpan(), framesToPlay);
                            if (samplesPlayed > 0)
                            {
                                OwnaudioNET.OwnaudioNet.Send(playbackBuffer.AsSpan(0, samplesPlayed * _config.Channels));
                                totalPlayed += samplesPlayed * _config.Channels;
                            }
                        }
                    }
                    
                    // Always try to read from input source (recording)
                    if (totalPlayed > _config.SampleRate * 300 / 1000)
                    {
                        int framesToRecord = Math.Min(512, recordFrames - totalRead);
                        if (framesToRecord > 0)
                        {
                            int framesRecorded = inputSource.ReadSamples(
                                recordedBuffer.AsSpan(totalRead * _config.Channels), 
                                framesToRecord);
                            if (framesRecorded > 0)
                            {
                                totalRead += framesRecorded;
                            }
                        }
                    }
                    
                    await Task.Delay(1, cancellationToken);
                }
                
                // 7. Fade out to prevent clicks
                await FadeOutSourceAsync(noiseSource, cancellationToken);
                
                // 8. Stop
                noiseSource.Stop();
                inputSource.Stop();
                
                // 8. Dispose resources
                noiseSource.Dispose();
                inputSource.Dispose();
                
                // 9. Create spectrum analyzer
                var analyzer = new SmartMasterSpectrumAnalyzer(_config.SampleRate);
                
                // 10. RMS measurement on recorded material
                float rmsLevel;
                if (totalRead > 0)
                {
                    rmsLevel = analyzer.CalculateRMS(recordedBuffer.AsSpan(0, totalRead * _config.Channels).ToArray());
                }
                else
                {
                    rmsLevel = 0f;
                }
                
                float rmsDb = 20f * (float)Math.Log10(Math.Max(rmsLevel, 1e-10f));
                
                // 11. Store results
                results.ChannelLevels[channel] = rmsDb;
                results.ChannelDelays[channel] = 0.0f;
                results.ChannelPolarity[channel] = false;
                
                Log.Info($"[SmartMaster] Channel {channel} measured: {rmsDb:F1} dB (read {totalRead}/{recordFrames} frames)");
                
                // 12. Check: is there a signal?
                if (rmsDb < -60.0f)
                {
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[SmartMaster] Channel {channel} check error: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Check subwoofer
        /// </summary>
        private async Task<bool> CheckSubwooferAsync(MeasurementResults results, float micInputGain, CancellationToken cancellationToken)
        {
            try
            {
                if (OwnaudioNET.OwnaudioNet.Engine == null)
                {
                    Log.Warning("[SmartMaster] Audio engine not available for measurement");
                    return false;
                }
                
                // 1. Generate low frequency noise (20-100Hz, 2 seconds)
                int durationSeconds = 2;
                int sampleCount = _config.SampleRate * durationSeconds;
                float[] lowFreqNoise = NoiseGenerator.GenerateLowFrequencyNoise(
                    sampleCount, _config.SampleRate, 0.4f);
                
                // 2. Channel audio (same signal on all channels)
                float[] channelAudio = new float[sampleCount * _config.Channels];
                for (int i = 0; i < sampleCount; i++)
                {
                    for (int ch = 0; ch < _config.Channels; ch++)
                    {
                        channelAudio[i * _config.Channels + ch] = lowFreqNoise[i];
                    }
                }
                
                // 3. Create SampleSource
                var noiseSource = new SampleSource(channelAudio, _config);
                noiseSource.Loop = false;
                
                // 4. Create InputSource
                var inputSource = new InputSource(OwnaudioNET.OwnaudioNet.Engine, 8192);
                inputSource.Volume = micInputGain;
                
                // 5. Start playback and recording
                noiseSource.Play();
                inputSource.Play();
                
                // 6. Smart Pumping
                int playbackFrames = _config.SampleRate * 2;
                int playbackSamples = playbackFrames * _config.Channels;
                const int chunkFrames = 512;
                float[] playbackBuffer = new float[chunkFrames * _config.Channels];
                
                // Recording buffer
                int recordDuration = 1500;
                int recordFrames = _config.SampleRate * recordDuration / 1000;
                int recordSamples = recordFrames * _config.Channels;
                float[] recordedBuffer = new float[recordSamples];
                
                int totalPlayed = 0;
                int totalRead = 0;
                
                await Task.Delay(100, cancellationToken);
                
                int engineBufferCapacity = OwnaudioNET.OwnaudioNet.Engine.FramesPerBuffer * _config.Channels * 2;
                
                while (totalPlayed < playbackSamples && totalRead < recordFrames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    int bufferOccupied = OwnaudioNET.OwnaudioNet.Engine.OutputBufferAvailable;
                    int bufferFree = engineBufferCapacity - bufferOccupied;
                    
                    if (bufferFree >= 64 * _config.Channels)
                    {
                        int framesSpace = bufferFree / _config.Channels;
                        int framesToPlay = Math.Min(framesSpace, (playbackSamples - totalPlayed) / _config.Channels);
                        framesToPlay = Math.Min(framesToPlay, 1024);
                        
                        if (framesToPlay > 0)
                        {
                            if (playbackBuffer.Length < framesToPlay * _config.Channels)
                            {
                                playbackBuffer = new float[framesToPlay * _config.Channels];
                            }

                            int samplesPlayed = noiseSource.ReadSamples(playbackBuffer.AsSpan(), framesToPlay);
                            if (samplesPlayed > 0)
                            {
                                OwnaudioNET.OwnaudioNet.Send(playbackBuffer.AsSpan(0, samplesPlayed * _config.Channels));
                                totalPlayed += samplesPlayed * _config.Channels;
                            }
                        }
                    }
                    
                    if (totalPlayed > _config.SampleRate * 300 / 1000)
                    {
                        int framesToRecord = Math.Min(512, recordFrames - totalRead);
                        if (framesToRecord > 0)
                        {
                            int framesRecorded = inputSource.ReadSamples(
                                recordedBuffer.AsSpan(totalRead * _config.Channels), 
                                framesToRecord);
                            if (framesRecorded > 0)
                            {
                                totalRead += framesRecorded;
                            }
                        }
                    }
                    
                    await Task.Delay(1, cancellationToken);
                }
                
                // 7. Fade out to prevent clicks
                await FadeOutSourceAsync(noiseSource, cancellationToken);

                // 8. Stop
                noiseSource.Stop();
                inputSource.Stop();
                
                // 8. Dispose
                noiseSource.Dispose();
                inputSource.Dispose();
                
                // 9. RMS measurement
                var analyzer = new SmartMasterSpectrumAnalyzer(_config.SampleRate);
                
                float rmsLevel;
                if (totalRead > 0)
                {
                    rmsLevel = analyzer.CalculateRMS(recordedBuffer.AsSpan(0, totalRead * _config.Channels).ToArray());
                }
                else
                {
                    rmsLevel = 0f;
                }
                
                float rmsDb = 20f * (float)Math.Log10(Math.Max(rmsLevel, 1e-10f));
                
                // 10. Store results (Sub = channel 2)
                results.ChannelLevels[2] = rmsDb;
                results.ChannelDelays[2] = 0.0f;
                results.ChannelPolarity[2] = false;
                
                Log.Info($"[SmartMaster] Subwoofer measured: {rmsDb:F1} dB (read {totalRead}/{recordFrames} frames)");
                
                // 11. Check if subwoofer response is weak
                if (rmsDb < -40.0f)
                {
                    Log.Warning("[SmartMaster] Weak subwoofer response detected, will recommend Subharmonic Synth in measured preset");
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[SmartMaster] Subwoofer check error: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Spectrum analysis
        /// </summary>
        private async Task AnalyzeSpectrumAsync(MeasurementResults results, float micInputGain, CancellationToken cancellationToken)
        {
            try
            {
                if (OwnaudioNET.OwnaudioNet.Engine == null)
                {
                    Log.Warning("[SmartMaster] Audio engine not available for measurement");
                    return;
                }
                
                // 1. Generate pink noise (4 seconds for more accurate measurement)
                int durationSeconds = 4;
                int sampleCount = _config.SampleRate * durationSeconds;
                float[] pinkNoise = NoiseGenerator.GeneratePinkNoise(sampleCount, 0.3f);
                
                // 2. Channel audio (on all channels)
                float[] channelAudio = new float[sampleCount * _config.Channels];
                for (int i = 0; i < sampleCount; i++)
                {
                    for (int ch = 0; ch < _config.Channels; ch++)
                    {
                        channelAudio[i * _config.Channels + ch] = pinkNoise[i];
                    }
                }
                
                // 3. Create SampleSource
                var noiseSource = new SampleSource(channelAudio, _config);
                noiseSource.Loop = false;
                
                // 4. Create InputSource
                var inputSource = new InputSource(OwnaudioNET.OwnaudioNet.Engine, 16384);
                inputSource.Volume = micInputGain;
                
                // 5. Start playback and recording
                noiseSource.Play();
                inputSource.Play();
                
                // 6. Smart Pumping
                int playbackFrames = _config.SampleRate * 4;
                int playbackSamples = playbackFrames * _config.Channels;
                const int chunkFrames = 512;
                float[] playbackBuffer = new float[chunkFrames * _config.Channels];
                
                // Recording (3 seconds)
                int recordDuration = 3000;
                int recordFrames = _config.SampleRate * recordDuration / 1000;
                int recordSamples = recordFrames * _config.Channels;
                float[] recordedBuffer = new float[recordSamples];
                
                int totalPlayed = 0;
                int totalRead = 0;
                
                await Task.Delay(200, cancellationToken);
                
                int engineBufferCapacity = OwnaudioNET.OwnaudioNet.Engine.FramesPerBuffer * _config.Channels * 2;
                
                while (totalPlayed < playbackSamples && totalRead < recordFrames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    int bufferOccupied = OwnaudioNET.OwnaudioNet.Engine.OutputBufferAvailable;
                    int bufferFree = engineBufferCapacity - bufferOccupied;
                    
                    if (bufferFree >= 64 * _config.Channels)
                    {
                        int framesSpace = bufferFree / _config.Channels;
                        int framesToPlay = Math.Min(framesSpace, (playbackSamples - totalPlayed) / _config.Channels);
                        framesToPlay = Math.Min(framesToPlay, 1024);
                        
                        if (framesToPlay > 0)
                        {
                            if (playbackBuffer.Length < framesToPlay * _config.Channels)
                            {
                                playbackBuffer = new float[framesToPlay * _config.Channels];
                            }

                            int samplesPlayed = noiseSource.ReadSamples(playbackBuffer.AsSpan(), framesToPlay);
                            if (samplesPlayed > 0)
                            {
                                OwnaudioNET.OwnaudioNet.Send(playbackBuffer.AsSpan(0, samplesPlayed * _config.Channels));
                                totalPlayed += samplesPlayed * _config.Channels;
                            }
                        }
                    }
                    
                    if (totalPlayed > _config.SampleRate * 500 / 1000)
                    {
                        int framesToRecord = Math.Min(512, recordFrames - totalRead);
                        if (framesToRecord > 0)
                        {
                            int framesRecorded = inputSource.ReadSamples(
                                recordedBuffer.AsSpan(totalRead * _config.Channels), 
                                framesToRecord);
                            if (framesRecorded > 0)
                            {
                                totalRead += framesRecorded;
                            }
                        }
                    }
                    
                    await Task.Delay(1, cancellationToken);
                }
                
                // 7. Fade out to prevent clicks
                await FadeOutSourceAsync(noiseSource, cancellationToken);

                // 8. Stop
                noiseSource.Stop();
                inputSource.Stop();
                
                // 8. Dispose
                noiseSource.Dispose();
                inputSource.Dispose();
                
                Log.Info($"[SmartMaster] Spectrum recording completed: {totalRead}/{recordFrames} frames");
                
                // 9. Spectrum analysis
                var analyzer = new SmartMasterSpectrumAnalyzer(_config.SampleRate);
                float[] measuredSpectrum = analyzer.AnalyzeSpectrum(recordedBuffer);
                
                // 10. Create ideal (flat) spectrum for pink noise
                float[] idealSpectrum = new float[measuredSpectrum.Length];
                
                // Calculate average level from measured spectrum
                float avgLevel = 0;
                for (int i = 0; i < measuredSpectrum.Length; i++)
                {
                    avgLevel += measuredSpectrum[i];
                }
                avgLevel /= measuredSpectrum.Length;
                
                // Ideal spectrum = uniform level
                for (int i = 0; i < idealSpectrum.Length; i++)
                {
                    idealSpectrum[i] = avgLevel;
                }
                
                // 11. Calculate frequency response deviation (in dB)
                for (int i = 0; i < 31; i++)
                {
                    float measuredDb = 20f * (float)Math.Log10(Math.Max(measuredSpectrum[i], 1e-10f));
                    float idealDb = 20f * (float)Math.Log10(Math.Max(idealSpectrum[i], 1e-10f));
                    
                    // Deviation from ideal (inverted to be correction)
                    float deviation = idealDb - measuredDb;
                    
                    // Store result
                    results.FrequencyResponse[i] = deviation;
                }
                
                // 12. Debug log
                Log.Info("[SmartMaster] Spectrum analysis completed:");
                for (int i = 0; i < 31; i++)
                {
                    Log.Info($"  Band {i}: {results.FrequencyResponse[i]:+0.0;-0.0} dB");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[SmartMaster] Spectrum analysis error: {ex.Message}");
                
                // In case of error, flat frequency response (no correction)
                for (int i = 0; i < 31; i++)
                {
                    results.FrequencyResponse[i] = 0.0f;
                }
            }
        }
        
        /// <summary>
        /// Calculate corrections based on measurement results and apply to target configuration
        /// </summary>
        private void CalculateCorrectionsToConfig(MeasurementResults results, SmartMasterConfig targetConfig)
        {
            // 1. Graphic EQ setup (spectrum equalization)
            for (int i = 0; i < 31; i++)
            {
                float gain = results.FrequencyResponse[i];
                
                // Safety Curve: Limit low-frequency boosts to prevent overdriving
                float maxBoost;
                if (i < 5) // Bands 0-4: ~20Hz to ~80Hz
                {
                    maxBoost = 3.0f;
                }
                else // Bands 5+: ~100Hz and above
                {
                    maxBoost = 12.0f;
                }
                
                // Clamp to safe range
                gain = Math.Clamp(gain, -12.0f, maxBoost);
                
                targetConfig.GraphicEQGains[i] = gain;
            }
            
            // 2. Phase Alignment setup
            targetConfig.TimeDelays = results.ChannelDelays;
            targetConfig.PhaseInvert = results.ChannelPolarity;
            
            // 3. Subharmonic Synth - Enable if subwoofer response was weak
            if (results.ChannelLevels.Length > 2 && results.ChannelLevels[2] < -40.0f)
            {
                targetConfig.SubharmonicEnabled = true;
                targetConfig.SubharmonicMix = 0.1f;
                Log.Info("[SmartMaster] Enabled Subharmonic Synth in measured preset due to weak subwoofer response");
            }
        }
        
        /// <summary>
        /// Update measurement status
        /// </summary>
        private void UpdateStatus(MeasurementStatusInfo status, Action<MeasurementStatusInfo> callback, 
            MeasurementStatus newStatus, float progress, string step)
        {
            status.Status = newStatus;
            status.Progress = progress;
            status.CurrentStep = step;
            
            callback?.Invoke(status);
            Log.Info($"[SmartMaster] {step} ({progress * 100:F0}%)");
        }
        
        /// <summary>
        /// Add warning
        /// </summary>
        private void AddWarning(MeasurementResults results, string warning)
        {
            var warnings = new string[results.Warnings.Length + 1];
            Array.Copy(results.Warnings, warnings, results.Warnings.Length);
            warnings[warnings.Length - 1] = warning;
            results.Warnings = warnings;
            
            Log.Warning($"[SmartMaster] {warning}");
        }

        /// <summary>
        /// Continues playing the source for a short time with a fade-out to prevent clicks.
        /// </summary>
        private async Task FadeOutSourceAsync(SampleSource source, CancellationToken cancellationToken)
        {
            try
            {
                // 100ms fade out
                int fadeFrames = _config.SampleRate / 10;
                // Ensure valid duration
                if (fadeFrames <= 0) fadeFrames = 4800; // Default ~100ms at 48k

                int channels = _config.Channels;
                float[] buffer = new float[fadeFrames * channels];

                // Read next chunk from source
                int framesRead = source.ReadSamples(buffer.AsSpan(), fadeFrames);

                if (framesRead > 0)
                {
                    // Apply Fade Out
                    for (int frame = 0; frame < framesRead; frame++)
                    {
                        float gain = 1.0f - ((float)frame / framesRead);
                        // Apply gain to all channels
                        for (int ch = 0; ch < channels; ch++)
                        {
                            buffer[frame * channels + ch] *= gain;
                        }
                    }

                    // Send to Engine
                    int totalBytes = framesRead * channels;
                    int sent = 0;
                    
                    int engineBufferCapacity = OwnaudioNET.OwnaudioNet.Engine.FramesPerBuffer * channels * 2;

                    // We need to pump this data
                    while (sent < totalBytes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int available = OwnaudioNET.OwnaudioNet.Engine.OutputBufferAvailable;
                        int free = engineBufferCapacity - available;

                        if (free >= 64 * channels)
                        {
                            int remaining = totalBytes - sent;
                            int spaceInFrames = free / channels;
                            
                            int framesToSend = Math.Min(remaining / channels, spaceInFrames);
                            framesToSend = Math.Min(framesToSend, 1024); // Chunk limit

                            if (framesToSend > 0)
                            {
                                int samplesToSend = framesToSend * channels;
                                OwnaudioNET.OwnaudioNet.Send(buffer.AsSpan(sent, samplesToSend));
                                sent += samplesToSend;
                            }
                        }

                        if (sent < totalBytes)
                        {
                            await Task.Delay(1, cancellationToken);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[SmartMaster] Fade out error: {ex.Message}");
            }
        }
    }
}
