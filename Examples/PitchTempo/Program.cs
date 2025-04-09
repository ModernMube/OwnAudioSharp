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

                await sourceManager.AddOutputSource("path/audio.mp3");
                int track1Number = sourceManager.Sources.Count - 1;
                await sourceManager.AddOutputSource("path/audio.mp3");
                int track2Number = sourceManager.Sources.Count - 1;

                sourceManager.Play();

                Console.Clear();
                Console.WriteLine("Hi! Ownaudio user");
                Console.WriteLine("Default output device: " + OwnAudio.DefaultOutputDevice.Name);

                Console.WriteLine("Audio pitch 2 semitone, and tempo -4%");
                sourceManager.SetPitch(track1Number, 2);
                sourceManager.SetPitch(track2Number, 2);

                sourceManager.SetTempo(track1Number, -4);
                sourceManager.SetTempo(track2Number, -4);

                Console.WriteLine("Press any key to stop playback...");
                Console.ReadKey();

                sourceManager.Stop();
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
