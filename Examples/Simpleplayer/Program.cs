using Ownaudio;
using Ownaudio.Processors;
using Ownaudio.Sources;
using System.Diagnostics;

namespace Simpleplayer
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            if (OwnAudio.Initialize())
            {
                try
                {
                    SourceManager manager = SourceManager.Instance;

                    string audioFilePath = @"path/audio.mp3";

                    // Check if file exists
                    if (!File.Exists(audioFilePath))
                    {
                        Console.WriteLine($"Audio file not found: {audioFilePath}");
                        return;
                    }

                    bool loaded = await manager.AddOutputSource(audioFilePath);

                    if (!loaded)
                    {
                        Console.WriteLine("Failed to load audio file!");
                        return;
                    }

                    manager.Play();

                    Console.Clear();
                    Console.WriteLine("Hi! Ownaudio user");
                    Console.WriteLine($"Default output device: {OwnAudio.DefaultOutputDevice.Name}");
                    Console.WriteLine($"Audio duration: {manager.Duration}");
                    Console.WriteLine("Press any key to stop playback...");

                    Console.Read();

                    manager.Stop();
                    manager.Reset();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during playback: {ex.Message}");
                }
                finally
                {
                    OwnAudio.Free();
                }
            }
            else
            {
                Console.WriteLine("Library initialization failed!");

                if (!OwnAudio.IsFFmpegInitialized)
                {
                    Console.WriteLine("- FFmpeg initialization failed");
                }

                if (!OwnAudio.IsPortAudioInitialized)
                {
                    Console.WriteLine("- PortAudio initialization failed");
                }

                if (!OwnAudio.IsMiniAudioInitialized)
                {
                    Console.WriteLine("- MiniAudio initialization failed");
                }
            }

            Console.WriteLine("Press any key to exit...");
            Console.Read();
        }
    }
}
