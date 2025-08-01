﻿using Ownaudio;
using Ownaudio.Sources;

namespace Simpleplayer
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            if (OwnAudio.Initialize())
            {
                SourceManager manager = SourceManager.Instance;

                await manager.AddOutputSource("/path/audio1.mp3", "Track1");
                int track1Number = manager.Sources.Count - 1;
                await manager.AddOutputSource("/path/audio2.mp3", "Track2");
                int track2Number = manager.Sources.Count - 1;

                manager.Play();

                Console.Clear();
                Console.WriteLine("Hi! Ownaudio user");
                Console.WriteLine("Default output device: " + OwnAudio.DefaultOutputDevice.Name);

                Console.WriteLine("Audio pitch -2 semitone, and tempo 4%");
                manager["Track1"].Pitch = -2;
                manager["Track1"].Tempo = 4;

                manager["Track2"].Pitch = 1;
                manager["Track2"].Tempo = 4;

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
