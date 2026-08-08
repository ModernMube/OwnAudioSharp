using System;
using System.IO;

namespace OwnaudioNET.Recording
{
    /// <summary>
    /// Bakes a PCM WAV file out of float samples (16/24/32 bit).
    /// </summary>
    public static class WaveFile
    {
        /// <summary>
        /// Read a raw float dump from disk and turn it into a WAV, then toss the raw file.
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="rawFilePath"></param>
        /// <param name="sampleRate"></param>
        /// <param name="channels"></param>
        /// <param name="bitPerSamples"></param>
        public static void Create(string filePath, string rawFilePath, int sampleRate, int channels, int bitPerSamples)
        {
            var raw = File.ReadAllBytes(rawFilePath);
            if (raw.Length % sizeof(float) != 0)
                throw new InvalidDataException("The file size is not divisible by 4 bytes. It probably contains invalid float data.");

            float[] samples = new float[raw.Length / sizeof(float)];
            Buffer.BlockCopy(raw, 0, samples, 0, raw.Length);

            Create(filePath, samples, sampleRate, channels, bitPerSamples);

            if (File.Exists(filePath))
                File.Delete(rawFilePath);
        }

        /// <summary>
        /// Write the float samples straight out as a WAV.
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="samples"></param>
        /// <param name="sampleRate"></param>
        /// <param name="channels"></param>
        /// <param name="bitPerSamples"></param>
        public static void Create(string filePath, float[] samples, int sampleRate, int channels, int bitPerSamples)
        {
            int bytesPerSample = bitPerSamples / 8;
            int dataSize = samples.Length * bytesPerSample;

            using (var stream = new FileStream(filePath, FileMode.Create))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataSize);
                writer.Write(new[] { 'W', 'A', 'V', 'E' });

                writer.Write(new[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * bytesPerSample);
                writer.Write((short)(channels * bytesPerSample));
                writer.Write((short)bitPerSamples);

                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(dataSize);

                var rng = new Random(0x5EED);
                float lastDither = _rpdf(rng);

                for (int i = 0; i < samples.Length; i++)
                {
                    float s = samples[i];

                    if (bitPerSamples == 32)
                    {
                        s = s < -1f ? -1f : s > 1f ? 1f : s;
                        writer.Write((int)Math.Round(s * (double)int.MaxValue));
                        continue;
                    }

                    float scale = bitPerSamples == 16 ? short.MaxValue : 8388607f;

                    float d = _rpdf(rng);
                    s += (d - lastDither) / scale;
                    lastDither = d;

                    s = s < -1f ? -1f : s > 1f ? 1f : s;
                    int pcm = (int)Math.Round(s * scale);

                    if (bitPerSamples == 16)
                    {
                        writer.Write((short)Math.Clamp(pcm, short.MinValue, short.MaxValue));
                    }
                    else
                    {
                        pcm = Math.Clamp(pcm, -8388608, 8388607);
                        writer.Write((byte)(pcm & 0xFF));
                        writer.Write((byte)((pcm >> 8) & 0xFF));
                        writer.Write((byte)((pcm >> 16) & 0xFF));
                    }
                }
            }
        }

        /// <summary>
        /// Same as above but starting from a raw byte blob of floats.
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="rawData"></param>
        /// <param name="sampleRate"></param>
        /// <param name="channels"></param>
        /// <param name="bitPerSamples"></param>
        public static void Create(string filePath, byte[] rawData, int sampleRate, int channels, int bitPerSamples)
        {
            if (rawData.Length % sizeof(float) != 0)
                throw new InvalidDataException("The data size is not divisible by 4 bytes. It probably contains invalid float data.");

            float[] samples = new float[rawData.Length / sizeof(float)];
            Buffer.BlockCopy(rawData, 0, samples, 0, rawData.Length);

            Create(filePath, samples, sampleRate, channels, bitPerSamples);
        }

        /// <summary>
        /// One rectangular dither sample, +/-0.5 LSB. Two of these subtracted give the
        /// triangular distribution the quantizer wants, with the noise pushed up high
        /// where nobody hears it - beats plain truncation, which correlates its error
        /// with the signal.
        /// </summary>
        private static float _rpdf(Random rng) => (float)(rng.NextDouble() - 0.5);
    }
}
