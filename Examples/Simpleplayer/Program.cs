using Ownaudio;
using Ownaudio.Sources;

namespace Simpleplayer
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            if(OwnAudio.Initialize())
            {
                SourceManager manager = SourceManager.Instance;

                await manager.AddOutputSource("D:\\Sogorock\\Ocam\\2025\\Szepjulia\\Szép Júlia - Beszkid József (cover)_audio.flac");

                manager.Play();

                Console.Clear();
                Console.WriteLine("Hi! Ownaudio user");
                Console.WriteLine("Default output device: " + OwnAudio.DefaultOutputDevice.Name);

                Console.WriteLine("Press any key to stop playback...");
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
