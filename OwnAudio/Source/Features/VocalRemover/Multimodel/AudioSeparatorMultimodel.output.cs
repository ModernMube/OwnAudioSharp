namespace OwnaudioNET.Features.Vocalremover
{
    public partial class MultiModelAudioSeparator
    {
        #region Private Methods - File I/O

        /// <summary>
        /// Save audio to WAV file
        /// </summary>
        private void SaveAudio(string filePath, float[,] audio, int sampleRate)
        {
            int channels = audio.GetLength(0);
            int samples = audio.GetLength(1);

            float maxVal = 0f;
            for (int ch = 0; ch < channels; ch++)
            {
                for (int i = 0; i < samples; i++)
                {
                    maxVal = Math.Max(maxVal, Math.Abs(audio[ch, i]));
                }
            }

            float scale = maxVal > 0.95f ? 0.95f / maxVal : 1.0f;

            var interleaved = new float[samples * channels];
            for (int i = 0; i < samples; i++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    interleaved[i * channels + ch] = audio[ch, i] * scale;
                }
            }

            OwnaudioNET.Recording.WaveFile.Create(filePath, interleaved, sampleRate, channels, 16);
        }

        #endregion
    }

    /// <summary>
    /// Helper extension methods for multi-model audio separation with averaging
    /// </summary>
    public static class MultiModelExtensions
    {
        /// <summary>
        /// Creates a simple 2-model averaging pipeline with default settings.
        /// Both models process the original audio independently and results are averaged.
        /// </summary>
        public static MultiModelAudioSeparator CreateSimplePipeline(
            InternalModel model1,
            InternalModel model2,
            string outputDirectory = "separated_multimodel",
            ModelOutputType? model1OutputType = null,
            ModelOutputType? model2OutputType = null)
        {
            var options = new MultiModelSeparationOptions
            {
                Models = new List<MultiModelInfo>
                {
                    new MultiModelInfo { Name = "Model1", Model = model1, OutputType = model1OutputType },
                    new MultiModelInfo { Name = "Model2", Model = model2, OutputType = model2OutputType }
                },
                OutputDirectory = outputDirectory,
                EnableGPU = true
            };

            return new MultiModelAudioSeparator(options);
        }

        /// <summary>
        /// Creates a 3-model averaging pipeline with default settings.
        /// All models process the original audio independently and results are averaged.
        /// </summary>
        public static MultiModelAudioSeparator CreateTriplePipeline(
            InternalModel model1,
            InternalModel model2,
            InternalModel model3,
            string outputDirectory = "separated_multimodel",
            ModelOutputType? model1OutputType = null,
            ModelOutputType? model2OutputType = null,
            ModelOutputType? model3OutputType = null)
        {
            var options = new MultiModelSeparationOptions
            {
                Models = new List<MultiModelInfo>
                {
                    new MultiModelInfo { Name = "Model1", Model = model1, OutputType = model1OutputType },
                    new MultiModelInfo { Name = "Model2", Model = model2, OutputType = model2OutputType },
                    new MultiModelInfo { Name = "Model3", Model = model3, OutputType = model3OutputType }
                },
                OutputDirectory = outputDirectory,
                EnableGPU = true,
                SaveAllIntermediateResults = true
            };

            return new MultiModelAudioSeparator(options);
        }

        /// <summary>
        /// Validates that a file path points to a supported audio format
        /// </summary>
        public static bool IsValidAudioFile(string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var supportedFormats = new[] { ".wav", ".mp3", ".flac" };

            return supportedFormats.Contains(extension);
        }
    }
}
