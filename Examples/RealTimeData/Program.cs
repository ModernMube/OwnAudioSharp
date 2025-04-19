using Ownaudio;
using Ownaudio.Sources;

namespace RealTimeData
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (OwnAudio.Initialize())
            {
                SourceManager manager = SourceManager.Instance;

                SourceSound source = manager.AddRealTimeSource(0.4f);

                manager.Play();

                Console.Clear();
                Console.WriteLine("Hi! Ownaudio user");

                float[] samples = GetAudioSamples(50, 16000, 48000, 20); // Your method to obtain audio samples
                source.SubmitSamples(samples);

                Console.WriteLine("Press any key to stop...");
                Console.ReadKey();

                manager.Stop();

                manager.Reset();
                OwnAudio.Free();
            }
        }

        /// <summary>
        /// Generates an audio sample containing sine waves in the appropriate frequency range
        /// </summary>
        /// <param name="startFrequency">Starting frequency in Hz</param>
        /// <param name="endFrequency">Ending frequency in Hz</param>
        /// <param name="sampleRate">Sampling rate in Hz</param>
        /// <param name="duration">The total sample duration in weeks</param>
        /// <returns>A float array containing the generated samples</returns>
        public static float[] GetAudioSamples(double startFrequency, double endFrequency, double sampleRate, double duration)
        {
            int numSamples = (int)(sampleRate * duration);
            float[] samples = new float[numSamples];

            double freqRatio = Math.Pow(endFrequency / startFrequency, 1.0 / numSamples);

            double phase = 0;
            double currentFrequency = startFrequency;

            for (int i = 0; i < numSamples; i++)
            {
                currentFrequency = startFrequency * Math.Pow(freqRatio, i);

                double phaseIncrement = 2 * Math.PI * currentFrequency / sampleRate;
                phase += phaseIncrement;

                while (phase > 2 * Math.PI)
                {
                    phase -= 2 * Math.PI;
                }

                samples[i] = (float)Math.Sin(phase);
            }

            return samples;
        }
    }
}
