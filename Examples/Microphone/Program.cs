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

                await manager.AddInputSource( inputVolume: 1.0f, "Input"); //Input volume value 0.0f silence - 1.0f maximum

                manager["Input"].Volume = 0.8f; //Input volume 80%

                manager.Play(@"D:\output.wav", 16);

                Console.Clear();
                Console.WriteLine("Hi! Ownaudio user");
                Console.WriteLine("Default input device: " + OwnAudio.DefaultInputDevice.Name);

                Console.WriteLine("Press any key to stop record...");
                Console.Read();

                manager.Stop();

                manager.Reset();
                OwnAudio.Free();
            }
        }
    }
}
