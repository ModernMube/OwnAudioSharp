using Ownaudio;
using Ownaudio.Engines;
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

                SourceSound source = manager.AddRealTimeSource(initialVolume: 0.8f, dataChannels: 1, "RealtimeSource");

                float[] samples = GetAudioSamples(100, 10000, SourceManager.OutputEngineOptions.SampleRate, 20); // Your method to obtain audio samples

                Console.Clear();
                Console.WriteLine("Hi! Ownaudio user");
                
                manager.Play();
                source.SubmitSamples(samples);

                Console.WriteLine("Press any key to stop...");
                Console.Read();

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

            double totalTime = duration;

            double phase = 0.0;

            for (int i = 0; i < numSamples; i++)
            {
                double timePosition = i / sampleRate;

                double normalizedPosition = timePosition / totalTime;
                double currentFrequency = startFrequency * Math.Pow(endFrequency / startFrequency, normalizedPosition);

                double angularFrequency = 2.0 * Math.PI * currentFrequency;

                double instantPhaseIncrement = angularFrequency / sampleRate;

                phase += instantPhaseIncrement;

                samples[i] = Math.Clamp((float)Math.Sin(phase), -1.0f, 1.0f)    ;
            }
            ApplyFadeInOut(samples, sampleRate, 0.01); // 10 ms fade

            return samples;
        }

        /// <summary>
        /// Apply fade-in and fade-out at the beginning and end of the signal to avoid clicks
        /// </summary>
        /// <param name="samples"></param>
        /// <param name="sampleRate"></param>
        /// <param name="fadeTime"></param>
        private static void ApplyFadeInOut(float[] samples, double sampleRate, double fadeTime)
        {
            int fadeLength = (int)(fadeTime * sampleRate);

            fadeLength = Math.Min(fadeLength, samples.Length / 2);

            for (int i = 0; i < fadeLength; i++)
            {
                double fadeMultiplier = (double)i / fadeLength;
                samples[i] *= (float)fadeMultiplier;
            }

            for (int i = 0; i < fadeLength; i++)
            {
                double fadeMultiplier = (double)i / fadeLength;
                samples[samples.Length - i - 1] *= (float)fadeMultiplier;
            }
        }
    }
}
