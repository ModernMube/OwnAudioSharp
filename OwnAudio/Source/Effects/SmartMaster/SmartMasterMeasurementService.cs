using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Effects.SmartMaster.Components;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using Logger;

namespace OwnaudioNET.Effects.SmartMaster
{
    /// <summary>
    /// Runs the SmartMaster measurement pass: pink noise out through the mixer, mic back in
    /// through the monitor, and the result turned into a config. The spectrum maths and the
    /// low-end verdict live in the sibling partials.
    /// </summary>
    internal sealed partial class SmartMasterMeasurementService
    {
        private const double ChannelSeconds = 1.6;
        private const double SettleSeconds = 1.0;
        private const double CaptureSeconds = 4.0;

        /// <summary>
        /// A channel this far under the loudest one is not playing.
        /// </summary>
        private const float DeadChannelDb = -18f;

        private const float NoSignalDb = -70f;

        private const float NoiseVolume = 0.3f;

        private readonly AudioConfig _config;
        private readonly string _presetsDirectory;
        private readonly PinkNoise _noise = new PinkNoise();

        private volatile int _channelMask = -1;

        /// <summary>
        /// Creates a new measurement service.
        /// </summary>
        /// <param name="config">The mixer's config, the noise runs at its rate and width.</param>
        /// <param name="presetsDirectory">Directory for saving measurement results.</param>
        public SmartMasterMeasurementService(AudioConfig config, string presetsDirectory)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _presetsDirectory = presetsDirectory ?? throw new ArgumentNullException(nameof(presetsDirectory));
        }

        /// <summary>
        /// Checks both channels arrive, measures the room against the reference noise and
        /// judges the low end. mic has to be running already. Nothing gets applied - the
        /// result is saved as the 'measured' preset and handed back.
        /// </summary>
        public async Task<SmartMasterConfig> PerformMeasurementAsync(
            AudioMixer mixer,
            SmartMasterMicMonitor mic,
            Action<MeasurementStatusInfo> statusCallback,
            CancellationToken cancellationToken)
        {
            var results = new MeasurementResults
            {
                MeasurementDate = DateTime.Now
            };

            var status = new MeasurementStatusInfo();

            UpdateStatus(status, statusCallback, MeasurementStatus.Initializing, 0.0f, "Initializing measurement...");

            float[] reference = _referenceSpectrum();
            float[] measured = new float[SmartMasterConfig.EqBands];
            float captureDb;

            _noise.Reset();
            _channelMask = 0;

            var noiseSource = new StreamingSource(_renderNoise, new AudioConfig
            {
                SampleRate = _config.SampleRate,
                Channels = _config.Channels,
                BufferSize = _config.BufferSize
            });
            noiseSource.Volume = NoiseVolume;

            try
            {
                mixer.AddSource(noiseSource);
                if (noiseSource.State != AudioState.Playing) noiseSource.Play();

                UpdateStatus(status, statusCallback, MeasurementStatus.CheckingLeftChannel, 0.05f, "Checking left channel...");
                results.ChannelLevels[0] = await _channelLevelAsync(mic, 0, reference, cancellationToken);

                UpdateStatus(status, statusCallback, MeasurementStatus.CheckingRightChannel, 0.2f, "Checking right channel...");
                results.ChannelLevels[1] = await _channelLevelAsync(mic, 1, reference, cancellationToken);

                foreach (string warning in _checkChannels(results.ChannelLevels[0], results.ChannelLevels[1]))
                    AddWarning(results, warning);

                if (results.Warnings.Length > 0)
                    _fail(status, statusCallback, results);

                _channelMask = -1;
                UpdateStatus(status, statusCallback, MeasurementStatus.AnalyzingSpectrum, 0.3f, "Spectrum analysis...");
                await Task.Delay(TimeSpan.FromSeconds(SettleSeconds), cancellationToken);

                float? _rms = await _averageAsync(mic, measured, CaptureSeconds, cancellationToken,
                    ratio => UpdateStatus(status, statusCallback, MeasurementStatus.AnalyzingSpectrum, 0.3f + 0.55f * ratio, "Measuring room..."));

                if (_rms is null)
                {
                    AddWarning(results, "The mic heard nothing usable - check the input device and its level");
                    _fail(status, statusCallback, results);
                }

                captureDb = _rms ?? -100f;
            }
            finally
            {
                _channelMask = -1;
                mixer.RemoveSource(noiseSource.Id);
                noiseSource.Dispose();
            }

            UpdateStatus(status, statusCallback, MeasurementStatus.CheckingSubwoofer, 0.9f, "Checking low end...");

            var lowEnd = _evaluateLowEnd(measured, reference, captureDb);
            results.ChannelLevels[2] = lowEnd.CaptureDb - lowEnd.LowDeficit;

            if (!lowEnd.Valid)
            {
                AddWarning(results, "Low end not judged: microphone level too low for a verdict");
            }
            else if (lowEnd.WeakLow)
            {
                AddWarning(results, $"Warning: low end sits {lowEnd.LowDeficit:F0} dB under the midrange");
            }

            UpdateStatus(status, statusCallback, MeasurementStatus.CalculatingCorrection, 0.95f, "Calculating correction...");

            _fillFrequencyResponse(results, measured, reference);

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

            Log.Info($"[SmartMaster] Measurement completed. L={results.ChannelLevels[0]:F1} R={results.ChannelLevels[1]:F1} dB, warnings: {results.Warnings.Length}");

            return measuredConfig;
        }

        /// <summary>
        /// Noise into one channel only, then how loud the mic heard the midrange - relative
        /// to the reference, so the mic's own gain drops out.
        /// </summary>
        private async Task<float> _channelLevelAsync(SmartMasterMicMonitor mic, int channel, float[] reference, CancellationToken token)
        {
            _channelMask = channel;
            await Task.Delay(TimeSpan.FromSeconds(SettleSeconds * 0.6), token);

            var _bands = new float[SmartMasterConfig.EqBands];
            if (await _averageAsync(mic, _bands, ChannelSeconds, token, null) is null) return -100f;

            float _level = _bandGroupDb(_bands, RefBandFirst, RefBandLast) - _bandGroupDb(reference, RefBandFirst, RefBandLast);
            Log.Info($"[SmartMaster] Channel {channel} measured: {_level:F1} dB against the reference");

            return _level;
        }

        /// <summary>
        /// Lets the monitor average for a while, then reads it back into bands.
        /// </summary>
        /// <returns>RMS of the stretch in dBFS, null when fewer than three windows came in - too few to trust.</returns>
        private static async Task<float?> _averageAsync(SmartMasterMicMonitor mic, float[] bands, double seconds,
            CancellationToken token, Action<float>? onProgress)
        {
            mic.BeginWindow();

            var _clock = System.Diagnostics.Stopwatch.StartNew();
            while (_clock.Elapsed.TotalSeconds < seconds)
            {
                await Task.Delay(100, token);
                onProgress?.Invoke((float)Math.Min(1.0, _clock.Elapsed.TotalSeconds / seconds));
            }

            int _windows = mic.ReadWindow(bands, out float _rmsDb);
            if (_windows >= 3) return _rmsDb;

            Log.Warning($"[SmartMaster] Only {_windows} analysis windows came in from the mic");
            return null;
        }

        /// <summary>
        /// A silent or far off channel makes the room curve meaningless, we'd rather say so.
        /// </summary>
        private static List<string> _checkChannels(float left, float right)
        {
            var warnings = new List<string>();
            float loudest = Math.Max(left, right);

            if (loudest < NoSignalDb)
            {
                warnings.Add("Neither channel reached the mic - check the output, the mic and its level");
                return warnings;
            }

            if (left - loudest < DeadChannelDb)
                warnings.Add($"Left channel error: {loudest - left:F0} dB down - dead speaker, or the mic is far off centre");
            if (right - loudest < DeadChannelDb)
                warnings.Add($"Right channel error: {loudest - right:F0} dB down - dead speaker, or the mic is far off centre");

            return warnings;
        }

        private void _fail(MeasurementStatusInfo status, Action<MeasurementStatusInfo> callback, MeasurementResults results)
        {
            status.Warnings = results.Warnings;
            UpdateStatus(status, callback, MeasurementStatus.Error, 1.0f, "Measurement failed: " + string.Join(", ", results.Warnings));

            Log.Warning($"[SmartMaster] Measurement failed, SmartMaster settings remain unchanged. Warnings: {string.Join(", ", results.Warnings)}");

            throw new InvalidOperationException(
                "Measurement failed: " + string.Join(" ", results.Warnings) + " SmartMaster settings remain unchanged.");
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
        /// StreamingSource callback, pump thread.
        /// </summary>
        private void _renderNoise(Span<float> buffer, int frameCount, long framePosition)
        {
            _noise.Render(buffer, _config.Channels, _channelMask);
        }
    }
}
