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
        static async Task Main(string[] args)
        {
            Log.Info("OwnSeparator Audio Separation - Simplified");
            Log.Info("==========================================");

            //string audioFilePath = @"path/audio/music.flac";
            string audioFilePath = @"/path/audio/music.mp3";
            string outputDirectory = @"/path/output";

            try
            {
                // Download the model on first run if it is not yet present
                const InternalModel model = InternalModel.Default;

                if (!VocalRemoverModelManager.IsModelAvailable(model))
                {
                    Log.Info($"Model '{model}' not found – downloading now (first-time setup)...");
                    Log.Info($"Storage: {VocalRemoverModelManager.DefaultModelsDirectory}");

                    await VocalRemoverModelManager.DownloadModelAsync(
                        model,
                        new Progress<ModelDownloadProgress>(p =>
                        {
                            string pct = p.Percentage >= 0 ? $"{p.Percentage:F1}%" : "?%";
                            Console.Write($"\r  Downloading: {pct}  ({p.BytesDownloaded / 1024 / 1024} MB)");
                        }));

                    Console.WriteLine();
                    Log.Info("Download complete.");
                }

                // Create and initialize the separator
                (SimpleAudioSeparationService? service, string vocalPath, string instrumentPath) = Separator(model, outputDirectory);

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
