using Logger;

namespace OwnaudioNET.RustNext.Features.Vocalremover
{
    public partial class HTDemucsAudioSeparator
    {
        #region Private Methods - Model Inference

        /// <summary>
        /// Process a single audio chunk through HTDemucs model
        /// </summary>
        private Dictionary<HTDemucsStem, float[,]> ProcessChunk(
            float[,] audioChunk,
            HTDemucsProcessingContext context)
        {
            int chunkLength = audioChunk.GetLength(1);

            var inputTensorWave = new OrtTensor(new[] { 1, 2, chunkLength });

            for (int ch = 0; ch < 2; ch++)
            {
                for (int i = 0; i < chunkLength; i++)
                {
                    inputTensorWave[0, ch, i] = audioChunk[ch, i];
                }
            }

            var spectrogramData = _stftProcessor!.ComputeSpectrogram(audioChunk);
            int n_frames = spectrogramData.GetLength(3);

            var flattenedSpec = STFTProcessor.Flatten5D(spectrogramData);
            var inputTensorSpec = new OrtTensor(flattenedSpec, new[] { 1, 2, 2048, n_frames, 2 });

            Log.Info($"Model expects {_onnxInputNames.Length} inputs: {string.Join(", ", _onnxInputNames)}");
            Log.Info($"Waveform tensor shape: [1, 2, {chunkLength}]");
            Log.Info($"Spectrogram tensor shape: [1, 2, 2048, {n_frames}, 2]");

            var outputList = OrtRunner.Run(_onnxSession!,
                new[] { (_onnxInputNames[0], inputTensorWave), (_onnxInputNames[1], inputTensorSpec) },
                _onnxOutputNames);

            Log.Info($"Model returned {outputList.Length} outputs");

            if (outputList.Length < 2)
            {
                throw new InvalidOperationException(
                    $"Expected 2 outputs from HTDemucs model, got {outputList.Length}. " +
                    "The model should output both frequency branch (add_76) and time branch (add_77).");
            }

            var freqBranchSpectrogram = outputList[0];
            var timeBranchWaveform    = outputList[1];

            Log.Info($"Frequency branch (add_76): shape = [{string.Join(", ", freqBranchSpectrogram.Shape)}]");
            Log.Info($"Time branch (add_77): shape = [{string.Join(", ", timeBranchWaveform.Shape)}]");

            return ExtractStemsFromDualBranch(freqBranchSpectrogram, timeBranchWaveform, chunkLength);
        }

        /// <summary>
        /// Extract individual stems from model output tensor (single-output fallback path)
        /// </summary>
        private Dictionary<HTDemucsStem, float[,]> ExtractStems(OrtTensor outputTensor, int targetLength)
        {
            var stems = new Dictionary<HTDemucsStem, float[,]>();

            int stemCount    = outputTensor.Shape[1];
            int outputChannels = outputTensor.Shape[2];
            int freqBins     = outputTensor.Shape[3];
            int timeFrames   = outputTensor.Shape[4];

            Log.Info($"Extracting stems from output: stems={stemCount}, channels={outputChannels}, freq={freqBins}, time={timeFrames}");

            var stemMapping = new[]
            {
                HTDemucsStem.Drums,
                HTDemucsStem.Bass,
                HTDemucsStem.Other,
                HTDemucsStem.Vocals
            };

            for (int s = 0; s < Math.Min(stemCount, stemMapping.Length); s++)
            {
                var stem = stemMapping[s];

                if (!_options.TargetStems.HasFlag(stem))
                    continue;

                var spectrogram = new float[1, 2, freqBins, timeFrames, 2];

                for (int t = 0; t < timeFrames; t++)
                {
                    for (int f = 0; f < freqBins; f++)
                    {
                        spectrogram[0, 0, f, t, 0] = outputTensor[0, s, 0, f, t]; // L_Real
                        spectrogram[0, 0, f, t, 1] = outputTensor[0, s, 1, f, t]; // L_Imag
                        spectrogram[0, 1, f, t, 0] = outputTensor[0, s, 2, f, t]; // R_Real
                        spectrogram[0, 1, f, t, 1] = outputTensor[0, s, 3, f, t]; // R_Imag
                    }
                }

                Log.Info($"Converting {stem} spectrogram to waveform using ISTFT (target length: {targetLength})");
                var waveform = _stftProcessor!.ComputeISTFT(spectrogram, targetLength);

                stems[stem] = waveform;
            }

            return stems;
        }

        /// <summary>
        /// Extract individual stems by merging BOTH frequency and time branches.
        /// This is the CORRECT approach for HTDemucs hybrid model.
        /// Python reference (htdemucs.py line 661): final = time_branch + freq_branch_after_istft
        /// </summary>
        /// <param name="freqSpectrograms">Frequency branch: [batch, stems, 4, freq_bins, time_frames]</param>
        /// <param name="timeWaveforms">Time branch: [batch, stems, channels, samples]</param>
        /// <param name="targetLength">Target length to trim/pad to</param>
        private Dictionary<HTDemucsStem, float[,]> ExtractStemsFromDualBranch(
            OrtTensor freqSpectrograms,
            OrtTensor timeWaveforms,
            int targetLength)
        {
            var stems = new Dictionary<HTDemucsStem, float[,]>();

            int stemCount    = freqSpectrograms.Shape[1];
            int freqChannels = freqSpectrograms.Shape[2];
            int freqBins     = freqSpectrograms.Shape[3];
            int timeFrames   = freqSpectrograms.Shape[4];

            int timeChannels = timeWaveforms.Shape[2];
            int timeSamples  = timeWaveforms.Shape[3];

            Log.Info($"Merging dual branches: freq=[stems={stemCount}, ch={freqChannels}, freq={freqBins}, time={timeFrames}], " +
                     $"time=[stems={stemCount}, ch={timeChannels}, samples={timeSamples}]");

            var stemMapping = new[]
            {
                HTDemucsStem.Drums,
                HTDemucsStem.Bass,
                HTDemucsStem.Other,
                HTDemucsStem.Vocals
            };

            for (int s = 0; s < Math.Min(stemCount, stemMapping.Length); s++)
            {
                var stem = stemMapping[s];

                if (!_options.TargetStems.HasFlag(stem))
                    continue;

                var spectrogram = new float[1, 2, freqBins, timeFrames, 2];

                for (int t = 0; t < timeFrames; t++)
                {
                    for (int f = 0; f < freqBins; f++)
                    {
                        spectrogram[0, 0, f, t, 0] = freqSpectrograms[0, s, 0, f, t]; // L_Real
                        spectrogram[0, 0, f, t, 1] = freqSpectrograms[0, s, 1, f, t]; // L_Imag
                        spectrogram[0, 1, f, t, 0] = freqSpectrograms[0, s, 2, f, t]; // R_Real
                        spectrogram[0, 1, f, t, 1] = freqSpectrograms[0, s, 3, f, t]; // R_Imag
                    }
                }

                Log.Info($"Converting {stem} frequency branch spectrogram to waveform using ISTFT");
                var freqBranchWaveform = _stftProcessor!.ComputeISTFT(spectrogram, targetLength);

                var timeBranchWaveform = new float[timeChannels, Math.Min(timeSamples, targetLength)];
                int copyLength = Math.Min(timeSamples, targetLength);

                for (int ch = 0; ch < timeChannels; ch++)
                {
                    for (int i = 0; i < copyLength; i++)
                    {
                        timeBranchWaveform[ch, i] = timeWaveforms[0, s, ch, i];
                    }
                }

                var mergedWaveform = new float[timeChannels, targetLength];
                for (int ch = 0; ch < timeChannels; ch++)
                {
                    for (int i = 0; i < targetLength; i++)
                    {
                        float freqSample = i < freqBranchWaveform.GetLength(1) ? freqBranchWaveform[ch, i] : 0f;
                        float timeSample = i < timeBranchWaveform.GetLength(1) ? timeBranchWaveform[ch, i] : 0f;
                        mergedWaveform[ch, i] = freqSample + timeSample;
                    }
                }

                Log.Info($"Merged {stem}: freq_branch + time_branch = final waveform [{timeChannels}, {targetLength}]");
                stems[stem] = mergedWaveform;
            }

            return stems;
        }

        #endregion
    }
}
