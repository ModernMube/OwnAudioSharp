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
                SourceManager manager = SourceManager.Instance;

                await manager.AddInputSource( inputVolume: 1.0f ); //Input volume value 0.0f silence - 1.0f maximum
                int inputNumber = manager.SourcesInput.Count - 1;
                manager.SourcesInput[inputNumber].Volume = 0.8f; //Input volume 80%

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
}
