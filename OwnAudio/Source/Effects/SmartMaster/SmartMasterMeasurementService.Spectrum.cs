using Ownaudio.Core;
using OwnaudioNET.Effects.SmartMaster.Components;
using OwnaudioNET.Sources;
using Logger;

namespace OwnaudioNET.Effects.SmartMaster
{
    /// <summary>
    /// The spectrum half of the measurement: the sweep itself, the mono downmix and turning the
    /// measured curve into correction values.
    /// </summary>
    internal sealed partial class SmartMasterMeasurementService
    {
        /// <summary>
        /// Spectrum analysis, hands back the low end verdict too.
        /// </summary>
        private async Task<LowEndReading> AnalyzeSpectrumAsync(MeasurementResults results, float micInputGain, CancellationToken cancellationToken)
        {
            try
            {
                if (OwnaudioNET.OwnaudioNet.Engine == null)
                {
                    Log.Warning("[SmartMaster] Audio engine not available for measurement");
                    return default;
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
                
                int noiseCursor = 0;

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

                            int samplesPlayed = _takeNoise(channelAudio, ref noiseCursor, playbackBuffer.AsSpan(), framesToPlay);
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
                            totalRead += _captureInto(recordedBuffer, totalRead, framesToRecord, micInputGain);
                    }

                    await Task.Delay(1, cancellationToken);
                }

                await FadeOutSourceAsync(channelAudio, noiseCursor, cancellationToken);

                Log.Info($"[SmartMaster] Spectrum recording completed: {totalRead}/{recordFrames} frames");
                
                var analyzer = new SmartMasterSpectrumAnalyzer(_config.SampleRate);

                float[] captured = _monoDownmix(recordedBuffer, totalRead);
                float[] measuredSpectrum = analyzer.AnalyzeSpectrum(captured);
                float[] referenceSpectrum = analyzer.AnalyzeSpectrum(pinkNoise);

                float offset = _bandGroupDb(measuredSpectrum, RefBandFirst, RefBandLast)
                             - _bandGroupDb(referenceSpectrum, RefBandFirst, RefBandLast);

                for (int i = 0; i < SmartMasterConfig.EqBands; i++)
                {
                    float measuredDb = 20f * (float)Math.Log10(Math.Max(measuredSpectrum[i], 1e-10f));
                    float referenceDb = 20f * (float)Math.Log10(Math.Max(referenceSpectrum[i], 1e-10f));

                    results.FrequencyResponse[i] = referenceDb + offset - measuredDb;
                }

                Log.Info("[SmartMaster] Spectrum analysis completed:");
                for (int i = 0; i < SmartMasterConfig.EqBands; i++)
                {
                    Log.Info($"  Band {i}: {results.FrequencyResponse[i]:+0.0;-0.0} dB");
                }

                return _evaluateLowEnd(measuredSpectrum, referenceSpectrum, analyzer.CalculateRMSdB(captured));
            }
            catch (Exception ex)
            {
                Log.Error("[SmartMaster] Spectrum analysis error", ex);

                Array.Clear(results.FrequencyResponse);
                return default;
            }
        }

        /// <summary>
        /// Interleaved capture folded to one stream - handing the analyzer the
        /// interleaved buffer drags every band down an octave.
        /// </summary>
        private float[] _monoDownmix(float[] interleaved, int frames)
        {
            int channels = _config.Channels;
            var mono = new float[frames];

            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                    sum += interleaved[i * channels + c];

                mono[i] = sum / channels;
            }

            return mono;
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
        private void CalculateCorrectionsToConfig(MeasurementResults results, LowEndReading lowEnd, SmartMasterConfig targetConfig)
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

            if (lowEnd.WantsSubharmonic)
            {
                targetConfig.SubharmonicEnabled = true;
                targetConfig.SubharmonicMix = lowEnd.SubharmonicMix;
                Log.Info($"[SmartMaster] Subharmonic Synth on at mix {lowEnd.SubharmonicMix:F2}, sub band is {lowEnd.SubDeficit:F1} dB under the 40-80Hz range");
            }
            else if (lowEnd.WeakLow)
            {
                Log.Info("[SmartMaster] Low end is weak, leaving it to the EQ");
            }
        }
    }
}
