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

                await manager.AddInputSource(inputVolume: 1.0f, "InputTrack"); //Input volume value 0.0f silence - 1.0f maximum

                //input FX
                manager["InputTrack"].CustomSampleProcessor = new InputProcessor() { IsEnabled = true };

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
                }
            }
        }
    }

    public class InputProcessor : SampleProcessorBase
    {
        Reverb reverb = new Reverb();
        Delay delay = new Delay();

        public InputProcessor()
        {
            reverb.SampleRate = SourceManager.OutputEngineOptions.SampleRate;
            reverb.SetPreset(ReverbPreset.VocalBooth);

            delay.SampleRate = SourceManager.OutputEngineOptions.SampleRate;
            delay.SetPreset(DelayPreset.TapeEcho);
        }

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
        Compressor compressor = new Compressor();

        public MasterProcessor()
        {
            compressor.SampleRate = SourceManager.OutputEngineOptions.SampleRate;
            compressor.SetPreset(CompressorPreset.VocalGentle);
        }

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
