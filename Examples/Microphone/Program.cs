using Ownaudio;
using Ownaudio.Sources;

namespace Microphone
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            if(OwnAudio.Initialize())
            {
                SourceManager sourceManager = SourceManager.Instance;

                await sourceManager.AddInputSource( inputVolume: 1.0f ); //Input volume value 0.0f silence - 1.0f maximum
                int inputNumber = sourceManager.SourcesInput.Count - 1;
                sourceManager.SourcesInput[inputNumber].Volume = 0.8f; //Input volume 80%

                sourceManager.Play();

                Console.Clear();
                Console.WriteLine("Hi! Ownaudio user");
                Console.WriteLine("Default input device: " + OwnAudio.DefaultInputDevice.Name);

                Console.WriteLine("Press any key to stop record...");
                Console.ReadKey();

                sourceManager.Stop();
                OwnAudio.Free();
            }
            else
            {
                if (!OwnAudio.IsFFmpegInitialized)
                {
                    Console.WriteLine("FFMPEG library initialization failed!");
                }

                if(!OwnAudio.IsPortAudioInitialized)
                {
                    Console.WriteLine("PORTAUDIO library initialization failed!");
                }
            }
        }
    }
}
