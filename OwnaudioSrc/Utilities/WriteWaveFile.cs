﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ownaudio.Utilities
{
    /// <summary>
    /// The specified path creates the audio file from the data specified in the parameter
    /// </summary>
    public static class WriteWaveFile
    {
        /// <summary>
        /// Creates the audio file
        /// </summary>
        /// <param name="filePath">file access along with the file name</param>
        /// <param name="rawFilePath">array of data</param>
        /// <param name="sampleRate">sample rate</param>
        /// <param name="channels">channels number</param>
        /// <param name="bitPerSamples">bits per sample</param>
        /// public static void WriteFile(string filePath, float[] samples, int sampleRate, int channels, int bitPerSamples)
        public static void WriteFile(string filePath, string rawFilePath, int sampleRate, int channels, int bitPerSamples)
        {
            var rawData = File.ReadAllBytes(rawFilePath);
            
            if (rawData.Length % sizeof(float) != 0) //Check if the number of bytes is divisible by the size of a `float`
                { throw new InvalidDataException("The file size is not divisible by 4 bytes. It probably contains invalid float data."); }

            int floatCount = rawData.Length / sizeof(float);
            float[] samples = new float[floatCount];

            Buffer.BlockCopy(rawData, 0, samples, 0, rawData.Length);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                using (var writer = new BinaryWriter(stream))
                {
                    if (bitPerSamples == 24)
                    {
                        int byteRate = sampleRate * channels * 3;

                        // wave file header
                        writer.Write(new[] { 'R', 'I', 'F', 'F' });
                        writer.Write(36 + samples.Length * 3);
                        writer.Write(new[] { 'W', 'A', 'V', 'E' });

                        // fmt subchunk
                        writer.Write(new[] { 'f', 'm', 't', ' ' });
                        writer.Write(16); 
                        writer.Write((short)1); 
                        writer.Write((short)channels); 
                        writer.Write(sampleRate); 
                        writer.Write(byteRate); 
                        writer.Write((short)(channels * 3)); 
                        writer.Write((short)bitPerSamples);

                        // data subchunk
                        writer.Write(new[] { 'd', 'a', 't', 'a' });
                        writer.Write(samples.Length * 3); 

                        // writing audio data
                        for (int i = 0; i < samples.Length; i++)
                        {
                            // Calculation of PCM values ​​is 24-bit
                            int pcmSample = (int)(samples[i] * 8388607);

                            writer.Write((byte)(pcmSample & 0xFF));         // lower 8 bits
                            writer.Write((byte)((pcmSample >> 8) & 0xFF));  // middle 8 bits
                            writer.Write((byte)((pcmSample >> 16) & 0xFF)); // upper 8 bits
                        }
                    }
                    else if (bitPerSamples == 16)
                    {
                        int byteRate = sampleRate * channels * 2; 

                        // fmt subchunk
                        writer.Write(new[] { 'R', 'I', 'F', 'F' });
                        writer.Write(36 + samples.Length * 2);
                        writer.Write(new[] { 'W', 'A', 'V', 'E' });

                        // fmt subchunk
                        writer.Write(new[] { 'f', 'm', 't', ' ' });
                        writer.Write(16); 
                        writer.Write((short)1); 
                        writer.Write((short)channels); 
                        writer.Write(sampleRate); 
                        writer.Write(byteRate); 
                        writer.Write((short)(channels * 2)); 
                        writer.Write((short)bitPerSamples); 

                        // data subchunk
                        writer.Write(new[] { 'd', 'a', 't', 'a' });
                        writer.Write(samples.Length * 2); 

                        // writing audio data
                        for (int i = 0; i < samples.Length; i += channels)
                        {
                            for (int ch = 0; ch < channels; ch++)
                            {
                                short pcmSample = (short)(samples[i + ch] * short.MaxValue);
                                writer.Write(pcmSample);
                            }
                        }
                    }
                    
                }
            }

            if(File.Exists(filePath))
                { File.Delete(rawFilePath); }
        }
    }
}
