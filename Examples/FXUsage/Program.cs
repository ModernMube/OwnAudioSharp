using Ownaudio;
using Ownaudio.Fx;
using Ownaudio.Processors;
using Ownaudio.Sources;

namespace Microphone
{
    internal class Program
    {
        static async Task Main(string[] args)
        {

            if (OwnAudio.Initialize())
            {
                SourceManager manager = SourceManager.Instance;

                await manager.AddInputSource(inputVolume: 1.0f); //Input volume value 0.0f silence - 1.0f maximum
                int inputNumber = manager.SourcesInput.Count - 1;
                manager.SourcesInput[inputNumber].CustomSampleProcessor = new InputProcessor() { IsEnabled = true }; //Fx input

                //master FX
                manager.CustomSampleProcessor = new MasterProcessor() { IsEnabled = true };

                manager.Play();

                Console.Clear();
                Console.WriteLine("Hi! Ownaudio user");
                Console.WriteLine("Default input device: " + OwnAudio.DefaultInputDevice.Name);

                Console.WriteLine("Press any key to stop record...");
                Console.Read();

                manager.Stop();

                manager.Reset();
                OwnAudio.Free();
            }
            else
            {
                if (!OwnAudio.IsFFmpegInitialized || !OwnAudio.IsPortAudioInitialized)
                {
                    Console.WriteLine("library initialization failed!");
                    Console.WriteLine("Unpack the files in the LIB directory!");
                }
            }
        }
    }

    public class InputProcessor : SampleProcessorBase
    {
        Reverb reverb = new Reverb
        (
            size: 0.5f,        // Medium space, long reverb tail
            damp: 0.45f,        // Moderate high frequency damping
            wet: 0.35f,         // 35% effect - not too much reverb
            dry: 0.65f,         // 65% dry signal - vocal intelligibility is maintained
            stereoWidth: 0.8f,  // Good stereo space, but not too wide
            sampleRate: SourceManager.OutputEngineOptions.SampleRate
        );

        Delay delay = new Delay
        (
            time: 410,      // Delay time 410 ms
            repeat: 0.4f,   // Rate of delayed signal feedback to the input 40%
            mix: 0.35f,     // Delayed signal ratio in the mix 35%
            sampleRate: SourceManager.OutputEngineOptions.SampleRate
        );

        public override void Process(Span<float> sample)
        {
            reverb.Process(sample);
            delay.Process(sample);
        }

        public override void Reset()
        {
            reverb.Reset();
            delay.Reset();
        }
    }

    public class MasterProcessor : SampleProcessorBase
    {
        Compressor compressor = new Compressor
        (
            threshold: 0.5f,    // -6 dB
            ratio: 4.0f,        // 4:1 compression ratio
            attackTime: 100f,   // 100 ms
            releaseTime: 200f,  // 200 ms
            makeupGain: 1.0f,    // 0 dB
            sampleRate: SourceManager.OutputEngineOptions.SampleRate
        );

        public override void Process(Span<float> sample)
        {
            compressor.Process(sample);
        }

        public override void Reset()
        {
            compressor.Reset();
        }
    }
}
