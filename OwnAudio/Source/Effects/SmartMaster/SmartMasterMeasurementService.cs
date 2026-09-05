using Ownaudio.Core;
using OwnaudioNET.Effects.SmartMaster.Components;
using OwnaudioNET.Sources;
using Logger;

namespace OwnaudioNET.Effects.SmartMaster
{
    /// <summary>
    /// Runs the SmartMaster measurement pass: plays noise, records it back and turns the result
    /// into a config. The spectrum maths and the low-end verdict live in the sibling partials.
    /// </summary>
    internal sealed partial class SmartMasterMeasurementService
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
            
            UpdateStatus(status, statusCallback, MeasurementStatus.AnalyzingSpectrum, 0.6f, "Spectrum analysis...");
            var lowEnd = await AnalyzeSpectrumAsync(results, micInputGain, cancellationToken);

            UpdateStatus(status, statusCallback, MeasurementStatus.CheckingSubwoofer, 0.8f, "Checking low end...");
            results.ChannelLevels[2] = lowEnd.CaptureDb - lowEnd.LowDeficit;

            if (!lowEnd.Valid)
            {
                AddWarning(results, "Low end not judged: microphone level too low for a verdict");
            }
            else if (lowEnd.WeakLow)
            {
                AddWarning(results, $"Warning: low end sits {lowEnd.LowDeficit:F0} dB under the midrange");
            }

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
            CalculateCorrectionsToConfig(results, lowEnd, measuredConfig);
            
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
                
                int noiseCursor = 0;

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

                            int samplesPlayed = _takeNoise(channelAudio, ref noiseCursor, playbackBuffer.AsSpan(), framesToPlay);
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
                            totalRead += _captureInto(recordedBuffer, totalRead, framesToRecord, micInputGain);
                    }

                    await Task.Delay(1, cancellationToken);
                }

                await FadeOutSourceAsync(channelAudio, noiseCursor, cancellationToken);

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
        /// Copies the next chunk out of the pre-rendered noise and moves the cursor on.
        /// We push the noise ourselves through Send instead of letting a source play it —
        /// a played SampleSource would render on its own native track and we'd hear it twice.
        /// </summary>
        /// <param name="noise">interleaved noise buffer</param>
        /// <param name="cursor">sample cursor into it, advanced by what we took</param>
        private int _takeNoise(float[] noise, ref int cursor, Span<float> dest, int frames)
        {
            int _want = Math.Min(frames * _config.Channels, noise.Length - cursor);
            if (_want <= 0) return 0;

            noise.AsSpan(cursor, _want).CopyTo(dest);
            cursor += _want;

            return _want / _config.Channels;
        }

        /// <summary>
        /// Drains whatever the engine captured into dest at frameOffset, scaled by gain.
        /// InputSource can't do this any more — its ReadSamples is silence on the native
        /// chain — so we pull the engine's own capture queue.
        /// </summary>
        /// <param name="maxFrames">how much room is left in dest</param>
        private int _captureInto(float[] dest, int frameOffset, int maxFrames, float gain)
        {
            int _channels = _config.Channels;
            int _written = 0;

            while (_written < maxFrames)
            {
                float[]? _captured = OwnaudioNET.OwnaudioNet.Receive(out int _sampleCount);
                if (_captured == null || _sampleCount <= 0)
                {
                    if (_captured != null) OwnaudioNET.OwnaudioNet.ReturnInputBuffer(_captured);
                    break;
                }

                int _frames = Math.Min(_sampleCount / _channels, maxFrames - _written);
                int _at = (frameOffset + _written) * _channels;

                for (int i = 0; i < _frames * _channels; i++)
                    dest[_at + i] = _captured[i] * gain;

                OwnaudioNET.OwnaudioNet.ReturnInputBuffer(_captured);
                _written += _frames;
            }

            return _written;
        }

        /// <summary>
        /// Pushes a short fade-out tail from where the noise cursor stopped, so the run
        /// doesn't end on a click.
        /// </summary>
        private async Task FadeOutSourceAsync(float[] noise, int cursor, CancellationToken cancellationToken)
        {
            try
            {
                int fadeFrames = _config.SampleRate / 10;
                if (fadeFrames <= 0) fadeFrames = 4800;

                int channels = _config.Channels;
                float[] buffer = new float[fadeFrames * channels];

                int framesRead = _takeNoise(noise, ref cursor, buffer.AsSpan(), fadeFrames);

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
