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
            Console.WriteLine("OwnSeparator Audio Separation - Simplified");
            Console.WriteLine("==========================================");

            string audioFilePath = @"path/audio/music.flac";
            string outputDirectory = @"path/output";

            try
            {
                // Create and initialize the separator
                (SimpleAudioSeparationService? service, string vocalPath, string instrumentPath) = Separator(InternalModel.Default, outputDirectory);

                if (service != null)
                {
                    // Subscribe to events
                    service.ProgressChanged += (s, progress) =>
                        Console.WriteLine($"{progress?.Status}: {progress?.OverallProgress:F1}%");

                    service.ProcessingCompleted += (s, result) =>
                        Console.WriteLine($"Completed: {result?.ProcessingTime}");               

                    // Process the audio file
                    Console.WriteLine("Starting processing...");
                    var result = service.Separate(audioFilePath);

                    Console.WriteLine($"Vocals file: {result.VocalsPath}");
                    Console.WriteLine($"Instrumental file: {result.InstrumentalPath}");

                    service.Dispose();
                }
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine($"File not found: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
            }

            Console.WriteLine("Press any key to exit...");
            Console.Read();
        }
    }
}
