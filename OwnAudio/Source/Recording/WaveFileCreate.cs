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

                for (int i = 0; i < samples.Length; i++)
                {
                    float s = samples[i] < -1f ? -1f : samples[i] > 1f ? 1f : samples[i];

                    if (bitPerSamples == 16)
                    {
                        writer.Write((short)(s * short.MaxValue));
                    }
                    else if (bitPerSamples == 24)
                    {
                        int pcm = (int)(s * 8388607);
                        writer.Write((byte)(pcm & 0xFF));
                        writer.Write((byte)((pcm >> 8) & 0xFF));
                        writer.Write((byte)((pcm >> 16) & 0xFF));
                    }
                    else
                    {
                        writer.Write((int)(s * int.MaxValue));
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
    }
}
