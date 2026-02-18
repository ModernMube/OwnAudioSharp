using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Logger;

namespace OwnaudioNET.Features.Vocalremover
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

            // Prepare waveform input tensor: [batch=1, channels=2, samples]
            var inputTensorWave = new DenseTensor<float>(new[] { 1, 2, chunkLength });

            for (int ch = 0; ch < 2; ch++)
            {
                for (int i = 0; i < chunkLength; i++)
                {
                    inputTensorWave[0, ch, i] = audioChunk[ch, i];
                }
            }

            // Compute spectrogram input tensor using shared STFT processor
            var spectrogramData = _stftProcessor!.ComputeSpectrogram(audioChunk);
            int n_frames = spectrogramData.GetLength(3);

            var flattenedSpec = STFTProcessor.Flatten5D(spectrogramData);
            var inputTensorSpec = new DenseTensor<float>(flattenedSpec, new[] { 1, 2, 2048, n_frames, 2 });

            var inputNames = _onnxSession!.InputMetadata.Keys.ToArray();

            Log.Info($"Model expects {inputNames.Length} inputs: {string.Join(", ", inputNames)}");
            Log.Info($"Waveform tensor shape: [1, 2, {chunkLength}]");
            Log.Info($"Spectrogram tensor shape: [1, 2, 2048, {n_frames}, 2]");

            // Run ONNX inference with both inputs
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputNames[0], inputTensorWave),
                NamedOnnxValue.CreateFromTensor(inputNames[1], inputTensorSpec)
            };

            using var outputs = _onnxSession.Run(inputs);

            // HTDemucs HYBRID model outputs TWO tensors that must be MERGED:
            // outputs[0] = "add_76": [1, 4, 4, 2048, frames] - FREQUENCY BRANCH
            // outputs[1] = "add_77": [1, 4, 2, samples]      - TIME BRANCH
            // Final output = time_branch + ISTFT(frequency_branch)
            var outputList = outputs.ToList();
            Log.Info($"Model returned {outputList.Count} outputs");

            if (outputList.Count < 2)
            {
                throw new InvalidOperationException(
                    $"Expected 2 outputs from HTDemucs model, got {outputList.Count}. " +
                    "The model should output both frequency branch (add_76) and time branch (add_77).");
            }

            var freqBranchSpectrogram = outputList[0].AsTensor<float>();
            var timeBranchWaveform = outputList[1].AsTensor<float>();

            Log.Info($"Frequency branch (add_76): shape = [{string.Join(", ", freqBranchSpectrogram.Dimensions.ToArray())}]");
            Log.Info($"Time branch (add_77): shape = [{string.Join(", ", timeBranchWaveform.Dimensions.ToArray())}]");

            return ExtractStemsFromDualBranch(freqBranchSpectrogram, timeBranchWaveform, chunkLength);
        }

        /// <summary>
        /// Extract individual stems from model output tensor (single-output fallback path)
        /// </summary>
        private Dictionary<HTDemucsStem, float[,]> ExtractStems(Tensor<float> outputTensor, int targetLength)
        {
            var stems = new Dictionary<HTDemucsStem, float[,]>();

            int stemCount = outputTensor.Dimensions[1];
            int outputChannels = outputTensor.Dimensions[2];
            int freqBins = outputTensor.Dimensions[3];
            int timeFrames = outputTensor.Dimensions[4];

            Log.Info($"Extracting stems from output: stems={stemCount}, channels={outputChannels}, freq={freqBins}, time={timeFrames}");

            // HTDemucs default order: Drums, Bass, Other, Vocals
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

                // Reorganize from [L_Real, L_Imag, R_Real, R_Imag] to [L, R] with [Real, Imag]
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
            Tensor<float> freqSpectrograms,
            Tensor<float> timeWaveforms,
            int targetLength)
        {
            var stems = new Dictionary<HTDemucsStem, float[,]>();

            int stemCount = freqSpectrograms.Dimensions[1];
            int freqChannels = freqSpectrograms.Dimensions[2];
            int freqBins = freqSpectrograms.Dimensions[3];
            int timeFrames = freqSpectrograms.Dimensions[4];

            int timeChannels = timeWaveforms.Dimensions[2];
            int timeSamples = timeWaveforms.Dimensions[3];

            Log.Info($"Merging dual branches: freq=[stems={stemCount}, ch={freqChannels}, freq={freqBins}, time={timeFrames}], " +
                     $"time=[stems={stemCount}, ch={timeChannels}, samples={timeSamples}]");

            // HTDemucs default order: Drums, Bass, Other, Vocals
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

                // STEP 1: Convert frequency branch spectrogram to waveform
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

                // STEP 2: Extract time branch waveform
                var timeBranchWaveform = new float[timeChannels, Math.Min(timeSamples, targetLength)];
                int copyLength = Math.Min(timeSamples, targetLength);

                for (int ch = 0; ch < timeChannels; ch++)
                {
                    for (int i = 0; i < copyLength; i++)
                    {
                        timeBranchWaveform[ch, i] = timeWaveforms[0, s, ch, i];
                    }
                }

                // STEP 3: MERGE both branches (Python line 661: x = xt + x)
                // final = time_branch + freq_branch_istft
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
