using System;

namespace OwnaudioNET.Effects.SmartMaster.Components
{
    /// <summary>
    /// Noise generator class for white and pink noise generation.
    /// </summary>
    internal static class NoiseGenerator
    {
        private static readonly Random _random = new Random();
        
        /// <summary>
        /// Generate white noise (flat spectrum).
        /// </summary>
        /// <param name="sampleCount">Number of samples</param>
        /// <param name="amplitude">Amplitude (0.0 - 1.0)</param>
        /// <returns>White noise samples</returns>
        public static float[] GenerateWhiteNoise(int sampleCount, float amplitude = 0.5f)
        {
            float[] noise = new float[sampleCount];
            
            for (int i = 0; i < sampleCount; i++)
            {
                // Uniformly distributed random number between -1.0 and 1.0
                noise[i] = ((float)_random.NextDouble() * 2.0f - 1.0f) * amplitude;
            }
            
            return noise;
        }
        
        /// <summary>
        /// Generate pink noise (1/f spectrum, -3dB/octave).
        /// Using Voss-McCartney algorithm.
        /// </summary>
        /// <param name="sampleCount">Number of samples</param>
        /// <param name="amplitude">Amplitude (0.0 - 1.0)</param>
        /// <returns>Pink noise samples</returns>
        public static float[] GeneratePinkNoise(int sampleCount, float amplitude = 0.5f)
        {
            float[] noise = new float[sampleCount];
            
            // Voss-McCartney algorithm - 7 octave generator
            const int numGenerators = 7;
            float[] generators = new float[numGenerators];
            int[] counters = new int[numGenerators];
            
            // Initialization
            for (int i = 0; i < numGenerators; i++)
            {
                generators[i] = (float)_random.NextDouble() * 2.0f - 1.0f;
                counters[i] = (int)Math.Pow(2, i);
            }
            
            int sampleIndex = 0;
            
            for (int i = 0; i < sampleCount; i++)
            {
                // Update generators at appropriate frequency
                for (int g = 0; g < numGenerators; g++)
                {
                    if (sampleIndex % counters[g] == 0)
                    {
                        generators[g] = (float)_random.NextDouble() * 2.0f - 1.0f;
                    }
                }
                
                // Sum all generators
                float sum = 0;
                for (int g = 0; g < numGenerators; g++)
                {
                    sum += generators[g];
                }
                
                // Normalization and amplitude application
                noise[i] = (sum / numGenerators) * amplitude;
                sampleIndex++;
            }
            
            return noise;
        }
        
        /// <summary>
        /// Generate low frequency noise (filtered white noise, 20-100Hz).
        /// </summary>
        /// <param name="sampleCount">Number of samples</param>
        /// <param name="sampleRate">Sample rate</param>
        /// <param name="amplitude">Amplitude (0.0 - 1.0)</param>
        /// <returns>Low frequency noise samples</returns>
        public static float[] GenerateLowFrequencyNoise(int sampleCount, int sampleRate, float amplitude = 0.5f)
        {
            // Generate white noise
            float[] whiteNoise = GenerateWhiteNoise(sampleCount, amplitude);
            
            // Simple low-pass filter (around 100Hz)
            // Cutoff frequency: 100Hz
            float cutoffFreq = 100.0f;
            float rc = 1.0f / (2.0f * (float)Math.PI * cutoffFreq);
            float dt = 1.0f / sampleRate;
            float alpha = dt / (rc + dt);
            
            float[] filtered = new float[sampleCount];
            filtered[0] = whiteNoise[0];
            
            for (int i = 1; i < sampleCount; i++)
            {
                filtered[i] = filtered[i - 1] + alpha * (whiteNoise[i] - filtered[i - 1]);
            }
            
            // High-pass filter (around 20Hz) to remove DC
            float hpCutoffFreq = 20.0f;
            float hpRc = 1.0f / (2.0f * (float)Math.PI * hpCutoffFreq);
            float hpAlpha = hpRc / (hpRc + dt);
            
            float[] output = new float[sampleCount];
            output[0] = filtered[0];
            float prevInput = filtered[0];
            float prevOutput = 0;
            
            for (int i = 1; i < sampleCount; i++)
            {
                output[i] = hpAlpha * (prevOutput + filtered[i] - prevInput);
                prevInput = filtered[i];
                prevOutput = output[i];
            }
            
            // Normalization
            float maxVal = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                if (Math.Abs(output[i]) > maxVal)
                    maxVal = Math.Abs(output[i]);
            }
            
            if (maxVal > 0)
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    output[i] = (output[i] / maxVal) * amplitude;
                }
            }
            
            return output;
        }
    }
}
