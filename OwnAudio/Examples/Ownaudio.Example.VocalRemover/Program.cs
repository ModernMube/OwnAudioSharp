using Logger;
using OwnaudioNET.Features.Vocalremover;
using static OwnaudioNET.Features.Vocalremover.SimpleSeparator;

namespace OwnSeparator.BasicConsole
{
    /// <summary>
    /// Example program demonstrating simplified audio separator usage
    /// </summary>
    class Program
    {
        static void Main(string[] args)
        {
            Log.Info("OwnSeparator Audio Separation - Simplified");
            Log.Info("==========================================");

            //string audioFilePath = @"path/audio/music.flac";
            string audioFilePath = @"/path/audio/music.mp3";
            string outputDirectory = @"/path/output";

            try
            {
                // Create and initialize the separator
                (SimpleAudioSeparationService? service, string vocalPath, string instrumentPath) = Separator(InternalModel.Default, outputDirectory);

                if (service != null)
                {
                    // Subscribe to events
                    service.ProgressChanged += (s, progress) =>
                        Log.Info($"{progress?.Status}: {progress?.OverallProgress:F1}%");

                    service.ProcessingCompleted += (s, result) =>
                        Log.Info($"Completed: {result?.ProcessingTime}");               

                    // Process the audio file
                    Log.Info("Starting processing...");
                    var result = service.Separate(audioFilePath);

                    Log.Info($"Vocals file: {result.VocalsPath}");
                    Log.Info($"Instrumental file: {result.InstrumentalPath}");

                    service.Dispose();
                }
            }
            catch (FileNotFoundException ex)
            {
                Log.Info($"File not found: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log.Info($"An error occurred: {ex.Message}");
            }

            Log.Info("Press any key to exit...");
            Console.Read();
        }
    }
}
