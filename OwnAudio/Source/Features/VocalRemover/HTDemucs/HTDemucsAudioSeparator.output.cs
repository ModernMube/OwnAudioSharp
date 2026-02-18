using Logger;

namespace OwnaudioNET.Features.Vocalremover
{
    public partial class HTDemucsAudioSeparator
    {
        #region Private Methods - File I/O

        /// <summary>
        /// Save separated stems to WAV files
        /// </summary>
        private Dictionary<HTDemucsStem, string> SaveResults(
            string filename,
            Dictionary<HTDemucsStem, float[,]> stems,
            int sampleRate)
        {
            var outputPaths = new Dictionary<HTDemucsStem, string>();

            foreach (var kvp in stems)
            {
                var stem = kvp.Key;
                var audio = kvp.Value;

                string stemName = stem.ToString().ToLower();
                string outputPath = Path.Combine(
                    _options.OutputDirectory,
                    $"{filename}_{stemName}.wav"
                );

                SaveAudio(outputPath, audio, sampleRate);
                outputPaths[stem] = outputPath;

                Log.Info($"Saved {stemName} stem: {outputPath}");
            }

            return outputPaths;
        }

        /// <summary>
        /// Save audio data to WAV file with normalization
        /// </summary>
        private void SaveAudio(string filePath, float[,] audio, int sampleRate)
        {
            int channels = audio.GetLength(0);
            int samples = audio.GetLength(1);

            // Find peak for normalization
            float maxVal = 0f;
            for (int ch = 0; ch < channels; ch++)
            {
                for (int i = 0; i < samples; i++)
                {
                    maxVal = Math.Max(maxVal, Math.Abs(audio[ch, i]));
                }
            }

            // Normalize to prevent clipping (0.95 = -0.44 dBFS headroom)
            float scale = (maxVal > 0.95f) ? (0.95f / maxVal) : 1.0f;

            // Interleave channels for WAV format
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
    /// Helper extension methods for HTDemucs audio separation
    /// </summary>
    public static class HTDemucsExtensions
    {
        /// <summary>
        /// Creates a default HTDemucs separator instance using embedded model
        /// </summary>
        public static HTDemucsAudioSeparator CreateDefaultSeparator(string outputDirectory = "separated_htdemucs")
        {
            var options = new HTDemucsSeparationOptions
            {
                Model = InternalModel.HTDemucs,
                OutputDirectory = outputDirectory,
                ChunkSizeSeconds = 10,
                OverlapFactor = 0.25f,
                EnableGPU = true,
                TargetStems = HTDemucsStem.All
            };

            return new HTDemucsAudioSeparator(options);
        }

        /// <summary>
        /// Creates a HTDemucs separator using external model file
        /// </summary>
        public static HTDemucsAudioSeparator CreateFromFile(string modelPath, string outputDirectory = "separated_htdemucs")
        {
            var options = new HTDemucsSeparationOptions
            {
                ModelPath = modelPath,
                OutputDirectory = outputDirectory,
                ChunkSizeSeconds = 10,
                OverlapFactor = 0.25f,
                EnableGPU = true,
                TargetStems = HTDemucsStem.All
            };

            return new HTDemucsAudioSeparator(options);
        }

        /// <summary>
        /// Creates a separator for specific stems only (using embedded model)
        /// </summary>
        public static HTDemucsAudioSeparator CreateStemSelector(HTDemucsStem targetStems, string outputDirectory = "separated_htdemucs")
        {
            var options = new HTDemucsSeparationOptions
            {
                Model = InternalModel.HTDemucs,
                OutputDirectory = outputDirectory,
                ChunkSizeSeconds = 10,
                OverlapFactor = 0.25f,
                EnableGPU = true,
                TargetStems = targetStems
            };

            return new HTDemucsAudioSeparator(options);
        }

        /// <summary>
        /// Validate audio file format
        /// </summary>
        public static bool IsValidAudioFile(string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var supportedFormats = new[] { ".wav", ".mp3", ".flac" };

            return supportedFormats.Contains(extension);
        }

        /// <summary>
        /// Get estimated processing time based on file size
        /// </summary>
        public static TimeSpan EstimateProcessingTime(string filePath, bool useGPU = false)
        {
            if (!File.Exists(filePath))
                return TimeSpan.Zero;

            var fileInfo = new FileInfo(filePath);
            var fileSizeMB = fileInfo.Length / (1024.0 * 1024.0);

            // Rough estimates: GPU ~50-100x realtime, CPU ~10-15x realtime
            double minutesPerMB = useGPU ? 0.1 : 1.0;
            var estimatedMinutes = fileSizeMB * minutesPerMB;

            return TimeSpan.FromMinutes(Math.Max(0.1, estimatedMinutes));
        }

        /// <summary>
        /// Get stem name as human-readable string
        /// </summary>
        public static string GetStemName(this HTDemucsStem stem)
        {
            return stem switch
            {
                HTDemucsStem.Vocals => "Vocals",
                HTDemucsStem.Drums => "Drums",
                HTDemucsStem.Bass => "Bass",
                HTDemucsStem.Other => "Other",
                _ => "Unknown"
            };
        }
    }
}
