namespace OwnaudioNET.Features.Vocalremover
{
    public partial class SimpleAudioSeparationService
    {
        #region Private Methods - File I/O

        private (string vocalsPath, string instrumentalPath) SaveResults(
            string filename, float[,] vocals, float[,] instrumental, int sampleRate, string modelName)
        {
            var vocalsPath = Path.Combine(_options.OutputDirectory, $"{filename}_vocals.wav");
            var instrumentalPath = Path.Combine(_options.OutputDirectory, $"{filename}_music.wav");

            if (modelName.CompareTo("DEFAULT") < 0)
            {
                SaveAudio(instrumentalPath, instrumental, sampleRate);
                SaveAudio(vocalsPath, vocals, sampleRate);
            }
            else
            {
                SaveAudio(instrumentalPath, vocals, sampleRate);
                SaveAudio(vocalsPath, instrumental, sampleRate);
            }

            return (vocalsPath, instrumentalPath);
        }

        private void SaveAudio(string filePath, float[,] audio, int sampleRate)
        {
            int channels = audio.GetLength(0);
            int samples = audio.GetLength(1);

            float maxVal = 0f;
            for (int ch = 0; ch < audio.GetLength(0); ch++)
            {
                for (int i = 0; i < audio.GetLength(1); i++)
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
}
