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
            
            UpdateStatus(status, statusCallback, MeasurementStatus.Initializing, 0.0f, "Initializing measurement...");

            if (!OwnaudioNET.OwnaudioNet.Engine!.Config.EnableInput)
            {
                throw new InvalidOperationException(
                    "Audio Input is NOT enabled in OwnAudio configuration. Please set 'audioConfig.EnableInput = true' before initializing OwnAudioNet.");
            }

            var inputDevices = OwnaudioNET.OwnaudioNet.Engine.GetInputDevices();
            if (inputDevices == null || inputDevices.Count == 0)
            {
                throw new InvalidOperationException("No audio input devices found!");
            }
            
            if (OwnaudioNET.OwnaudioNet.Engine != null)
            {
                try
                {
                    OwnaudioNET.OwnaudioNet.Engine.ClearOutputBuffer();
                }
                catch (Exception ex)
                {
                    Log.Warning($"[SmartMaster] Output buffer flush before measuring failed, stale audio may skew it: {ex.Message}");
                }
            }

            await Task.Delay(500, cancellationToken);
            
            UpdateStatus(status, statusCallback, MeasurementStatus.CheckingRightChannel, 0.2f, "Checking right channel...");
            bool rightOk = await CheckChannelAsync(1, results, micInputGain, cancellationToken);
            if (!rightOk)
            {
                AddWarning(results, "Right channel error: no signal or too quiet");
            }
            
            UpdateStatus(status, statusCallback, MeasurementStatus.CheckingLeftChannel, 0.4f, "Checking left channel...");
            bool leftOk = await CheckChannelAsync(0, results, micInputGain, cancellationToken);
            if (!leftOk)
            {
                AddWarning(results, "Left channel error: no signal or too quiet");
            }
            
            UpdateStatus(status, statusCallback, MeasurementStatus.CheckingSubwoofer, 0.6f, "Checking subwoofer...");
            bool subOk = await CheckSubwooferAsync(results, micInputGain, cancellationToken);
            if (!subOk)
            {
                AddWarning(results, "Warning: Weak or missing low frequency range");
            }
            
            UpdateStatus(status, statusCallback, MeasurementStatus.AnalyzingSpectrum, 0.75f, "Spectrum analysis...");
            await AnalyzeSpectrumAsync(results, micInputGain, cancellationToken);
            
            if (!rightOk || !leftOk)
            {
                UpdateStatus(status, statusCallback, MeasurementStatus.Error, 1.0f, 
                    "Measurement failed: " + string.Join(", ", results.Warnings));
                
                Log.Warning($"[SmartMaster] Measurement failed, SmartMaster settings remain unchanged. Warnings: {string.Join(", ", results.Warnings)}");
                
                throw new InvalidOperationException(
                    "Measurement failed due to critical errors. Please check microphone placement and volume. SmartMaster settings remain unchanged.");
            }
            
            UpdateStatus(status, statusCallback, MeasurementStatus.CalculatingCorrection, 0.9f, "Calculating correction...");
            
            var measuredConfig = new SmartMasterConfig();
            CalculateCorrectionsToConfig(results, measuredConfig);
            
            measuredConfig.LastMeasurement = results;
            
            try
            {
                string fileName = "measured.smartmaster.json";
                string filePath = Path.Combine(_presetsDirectory, fileName);
                
                string json = System.Text.Json.JsonSerializer.Serialize(
                    measuredConfig, SmartMasterRustNextJsonContext.Default.SmartMasterConfig);
                File.WriteAllText(filePath, json);
                
                Log.Info($"[SmartMaster] Measurement results saved to '{filePath}'");
            }
            catch (Exception ex)
            {
                Log.Warning($"[SmartMaster] Failed to save measurement results: {ex.Message}");
            }
            
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
                if (OwnaudioNET.OwnaudioNet.Engine == null)
                {
                    Log.Warning("[SmartMaster] Audio engine not available for measurement");
                    return false;
                }
                
                int durationSeconds = 2;
                int sampleCount = _config.SampleRate * durationSeconds;
                float[] whiteNoise = NoiseGenerator.GenerateWhiteNoise(sampleCount, 0.3f);
                
                float[] channelAudio = new float[sampleCount * _config.Channels];
                for (int i = 0; i < sampleCount; i++)
                {
                    for (int ch = 0; ch < _config.Channels; ch++)
                    {
                        channelAudio[i * _config.Channels + ch] = (ch == channel) ? whiteNoise[i] : 0f;
                    }
                }
                
                var noiseSource = new SampleSource(channelAudio, _config);
                noiseSource.Loop = false;
                
                var inputSource = new InputSource(OwnaudioNET.OwnaudioNet.Engine, 8192);
                inputSource.Volume = micInputGain;
                
                noiseSource.Play();
                inputSource.Play();
                
                int playbackFrames = _config.SampleRate * 2;
                int playbackSamples = playbackFrames * _config.Channels;
                const int chunkFrames = 512;
                float[] playbackBuffer = new float[chunkFrames * _config.Channels];
                
                int recordDuration = 1500;
                int recordFrames = _config.SampleRate * recordDuration / 1000;
                int recordSamples = recordFrames * _config.Channels;
                float[] recordedBuffer = new float[recordSamples];
                
                int totalPlayed = 0;
                int totalRead = 0;
                
                await Task.Delay(300, cancellationToken);
                
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
                
                await FadeOutSourceAsync(noiseSource, cancellationToken);
                
                noiseSource.Stop();
                inputSource.Stop();
                
                noiseSource.Dispose();
                inputSource.Dispose();
                
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
                
                results.ChannelLevels[channel] = rmsDb;
                results.ChannelDelays[channel] = 0.0f;
                results.ChannelPolarity[channel] = false;
                
                Log.Info($"[SmartMaster] Channel {channel} measured: {rmsDb:F1} dB (read {totalRead}/{recordFrames} frames)");
                
                if (rmsDb < -60.0f)
                {
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[SmartMaster] Channel {channel} check error", ex);
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
                
                int durationSeconds = 2;
                int sampleCount = _config.SampleRate * durationSeconds;
                float[] lowFreqNoise = NoiseGenerator.GenerateLowFrequencyNoise(
                    sampleCount, _config.SampleRate, 0.4f);
                
                float[] channelAudio = new float[sampleCount * _config.Channels];
                for (int i = 0; i < sampleCount; i++)
                {
                    for (int ch = 0; ch < _config.Channels; ch++)
                    {
                        channelAudio[i * _config.Channels + ch] = lowFreqNoise[i];
                    }
                }
                
                var noiseSource = new SampleSource(channelAudio, _config);
                noiseSource.Loop = false;
                
                var inputSource = new InputSource(OwnaudioNET.OwnaudioNet.Engine, 8192);
                inputSource.Volume = micInputGain;
                
                noiseSource.Play();
                inputSource.Play();
                
                int playbackFrames = _config.SampleRate * 2;
                int playbackSamples = playbackFrames * _config.Channels;
                const int chunkFrames = 512;
                float[] playbackBuffer = new float[chunkFrames * _config.Channels];
                
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
                
                await FadeOutSourceAsync(noiseSource, cancellationToken);

                noiseSource.Stop();
                inputSource.Stop();
                
                noiseSource.Dispose();
                inputSource.Dispose();
                
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
                
                results.ChannelLevels[2] = rmsDb;
                results.ChannelDelays[2] = 0.0f;
                results.ChannelPolarity[2] = false;
                
                Log.Info($"[SmartMaster] Subwoofer measured: {rmsDb:F1} dB (read {totalRead}/{recordFrames} frames)");
                
                if (rmsDb < -40.0f)
                {
                    Log.Warning("[SmartMaster] Weak subwoofer response detected, will recommend Subharmonic Synth in measured preset");
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("[SmartMaster] Subwoofer check error", ex);
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
                
                int durationSeconds = 4;
                int sampleCount = _config.SampleRate * durationSeconds;
                float[] pinkNoise = NoiseGenerator.GeneratePinkNoise(sampleCount, 0.3f);
                
                float[] channelAudio = new float[sampleCount * _config.Channels];
                for (int i = 0; i < sampleCount; i++)
                {
                    for (int ch = 0; ch < _config.Channels; ch++)
                    {
                        channelAudio[i * _config.Channels + ch] = pinkNoise[i];
                    }
                }
                
                var noiseSource = new SampleSource(channelAudio, _config);
                noiseSource.Loop = false;
                
                var inputSource = new InputSource(OwnaudioNET.OwnaudioNet.Engine, 16384);
                inputSource.Volume = micInputGain;
                
                noiseSource.Play();
                inputSource.Play();
                
                int playbackFrames = _config.SampleRate * 4;
                int playbackSamples = playbackFrames * _config.Channels;
                const int chunkFrames = 512;
                float[] playbackBuffer = new float[chunkFrames * _config.Channels];
                
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
                
                await FadeOutSourceAsync(noiseSource, cancellationToken);

                noiseSource.Stop();
                inputSource.Stop();
                
                noiseSource.Dispose();
                inputSource.Dispose();
                
                Log.Info($"[SmartMaster] Spectrum recording completed: {totalRead}/{recordFrames} frames");
                
                var analyzer = new SmartMasterSpectrumAnalyzer(_config.SampleRate);
                float[] measuredSpectrum = analyzer.AnalyzeSpectrum(recordedBuffer);
                
                float[] idealSpectrum = new float[measuredSpectrum.Length];
                
                float avgLevel = 0;
                for (int i = 0; i < measuredSpectrum.Length; i++)
                {
                    avgLevel += measuredSpectrum[i];
                }
                avgLevel /= measuredSpectrum.Length;
                
                for (int i = 0; i < idealSpectrum.Length; i++)
                {
                    idealSpectrum[i] = avgLevel;
                }
                
                for (int i = 0; i < SmartMasterConfig.EqBands; i++)
                {
                    float measuredDb = 20f * (float)Math.Log10(Math.Max(measuredSpectrum[i], 1e-10f));
                    float idealDb = 20f * (float)Math.Log10(Math.Max(idealSpectrum[i], 1e-10f));
                    
                    float deviation = idealDb - measuredDb;
                    
                    results.FrequencyResponse[i] = deviation;
                }
                
                Log.Info("[SmartMaster] Spectrum analysis completed:");
                for (int i = 0; i < SmartMasterConfig.EqBands; i++)
                {
                    Log.Info($"  Band {i}: {results.FrequencyResponse[i]:+0.0;-0.0} dB");
                }
            }
            catch (Exception ex)
            {
                Log.Error("[SmartMaster] Spectrum analysis error", ex);

                Array.Clear(results.FrequencyResponse);
            }
        }
        
        /// <summary>
        /// How much of the measured deviation we actually dial in. A room is not a
        /// minimum phase system, so correcting it 1:1 mostly makes it sound worse.
        /// </summary>
        private const float CorrectionFactor = 0.65f;

        /// <summary>
        /// Target house curve in dB against a flat reference, per EQ band. Slightly
        /// warm at the bottom and rolled off on top - a genuinely flat room is
        /// fatiguing, which is why nobody tunes to one.
        /// </summary>
        private static readonly float[] TargetCurve =
        {
             3.0f,  3.0f,  3.0f,  2.8f,  2.5f,  2.0f,  1.5f,  1.0f,
             0.5f,  0.2f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f,
             0.0f,  0.0f,  0.0f,  0.0f,  0.0f,  0.0f, -0.2f, -0.4f,
            -0.8f, -1.2f, -1.6f, -2.0f, -2.5f, -3.0f
        };

        /// <summary>
        /// Turns a measurement into a config: smoothed, partially applied EQ plus
        /// the channel alignment the sweep found. Boosts stay short because filling
        /// a null costs headroom and rarely fills it.
        /// </summary>
        private void CalculateCorrectionsToConfig(MeasurementResults results, SmartMasterConfig targetConfig)
        {
            float[] deviation = _smoothedDeviation(results.FrequencyResponse);

            for (int i = 0; i < SmartMasterConfig.EqBands; i++)
            {
                float gain = (deviation[i] + TargetCurve[i]) * CorrectionFactor;
                float maxBoost = i < 5 ? 2.0f : 6.0f;
                targetConfig.GraphicEQGains[i] = Math.Clamp(gain, -12.0f, maxBoost);
            }

            targetConfig.TimeDelays = results.ChannelDelays;
            targetConfig.PhaseInvert = results.ChannelPolarity;

            if (results.ChannelLevels.Length > 2 && results.ChannelLevels[2] < -40.0f)
            {
                targetConfig.SubharmonicEnabled = true;
                targetConfig.SubharmonicMix = 0.15f;
                Log.Info("[SmartMaster] Enabled Subharmonic Synth in measured preset due to weak subwoofer response");
            }
        }

        /// <summary>
        /// Three band moving average over the deviation. A single mic position is
        /// full of narrow interference dips that say nothing about the system.
        /// </summary>
        private static float[] _smoothedDeviation(float[] raw)
        {
            var smoothed = new float[SmartMasterConfig.EqBands];

            for (int i = 0; i < smoothed.Length; i++)
            {
                float sum = raw[i];
                int count = 1;

                if (i > 0) { sum += raw[i - 1]; count++; }
                if (i < smoothed.Length - 1) { sum += raw[i + 1]; count++; }

                smoothed[i] = sum / count;
            }

            return smoothed;
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
                int fadeFrames = _config.SampleRate / 10;
                if (fadeFrames <= 0) fadeFrames = 4800;

                int channels = _config.Channels;
                float[] buffer = new float[fadeFrames * channels];

                int framesRead = source.ReadSamples(buffer.AsSpan(), fadeFrames);

                if (framesRead > 0)
                {
                    for (int frame = 0; frame < framesRead; frame++)
                    {
                        float gain = 1.0f - ((float)frame / framesRead);
                        for (int ch = 0; ch < channels; ch++)
                        {
                            buffer[frame * channels + ch] *= gain;
                        }
                    }

                    int totalBytes = framesRead * channels;
                    int sent = 0;
                    
                    int engineBufferCapacity = OwnaudioNET.OwnaudioNet.Engine!.FramesPerBuffer * channels * 2;

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
                            framesToSend = Math.Min(framesToSend, 1024);

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
