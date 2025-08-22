using Ownaudio;
using Ownaudio.Sources;

namespace ChordDetect
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

                    string audioFilePath = @"path\to\audio.mp3";

                    if (!File.Exists(audioFilePath))
                    {
                        Console.WriteLine($"Audio file not found: {audioFilePath}");
                        return;
                    }

                    bool loaded = await manager.AddOutputSource(audioFilePath, "CHORDSOURCE");

                    if (!loaded)
                    {
                        Console.WriteLine("Failed to load audio file!");
                        return;
                    }

                    Console.WriteLine($"Audio loaded successfully!");
                    Console.WriteLine($"Duration: {manager.Duration}");

                    var (chords, detectedKey, detectedTempo) = manager.DetectChords("CHORDSOURCE");
                    Console.WriteLine($"Detected key: {detectedKey}");
                    Console.WriteLine($"Tempo detection: {detectedTempo} BPM ");

                    foreach (var chord in chords)
                    {
                        if(chord.ChordName.Trim() != "Unknown")
                            Console.WriteLine($"Start time: {chord.StartTime}s     Chord: {chord.ChordName}     Confidence: {chord.Confidence * 100}%     Notes:{chord.Notes.Count()}");
                    }

                    manager.Play();
                    Console.WriteLine("Play() called successfully");
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
