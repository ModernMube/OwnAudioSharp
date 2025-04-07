using Ownaudio;
using Ownaudio.Sources;

namespace Simpleplayer
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            if (OwnAudio.Initialize())
            {
                SourceManager sourceManager = SourceManager.Instance;

                await sourceManager.AddOutputSource("path/track1audio.mp3");
                await sourceManager.AddOutputSource("path/track2audio.mp3");

                sourceManager.Play();

                Console.Clear();
                Console.WriteLine("Hi! Ownaudio user");
                Console.WriteLine("Default output device: " + OwnAudio.DefaultOutputDevice.Name);

                Console.WriteLine(sourceManager.Sources.Count.ToString() + " track player...");

                Console.WriteLine("Press any key to stop playback...");
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

                if (!OwnAudio.IsPortAudioInitialized)
                {
                    Console.WriteLine("PORTAUDIO library initialization failed!");
                }
            }
        }
    }
}
