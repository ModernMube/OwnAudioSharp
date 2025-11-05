using OwnaudioNET;
using OwnaudioNET.Sources;

namespace ChordDetect
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            OwnaudioNet.Initialize();

            if (OwnaudioNet.IsInitialized)
            {
                try
                {

                    string audioFilePath = @"path/audio/music.mp3";

                    FileSource audioSource = new FileSource(audioFilePath);

                    if (!File.Exists(audioFilePath))
                    {
                        Console.WriteLine($"Audio file not found: {audioFilePath}");
                        return;
                    }

                    Console.WriteLine($"Audio loaded successfully!");
                    Console.WriteLine($"Duration: {audioSource.Duration}");

                    var (chords, detectedKey, detectedTempo) = OwnaudioNET.Features.OwnChordDetect.ChordDetect.DetectFromFile(audioFilePath);
                    Console.WriteLine($"Detected key: {detectedKey}");
                    Console.WriteLine($"Tempo detection: {detectedTempo} BPM ");

                    foreach (var chord in chords)
                    {
                        if(chord.ChordName.Trim() != "Unknown")
                            Console.WriteLine($"Start time: {chord.StartTime}s     Chord: {chord.ChordName}     Confidence: {chord.Confidence * 100}%     Notes:{chord.Notes.Count()}");
                    }

                    Console.WriteLine("Press any key to exit...");
                    Console.Read();

                    audioSource.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during playback: {ex.Message}");
                }
                finally
                {
                    OwnaudioNet.Shutdown();
                }
            }
            else
            {
                Console.WriteLine("Library initialization failed!");
            }
        }
    }
}
