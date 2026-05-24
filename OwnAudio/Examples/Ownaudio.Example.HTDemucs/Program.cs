using Logger;
using OwnaudioNET.Features.Vocalremover;

namespace HTDemucsExample
{
    /// <summary>
    /// Example program demonstrating HTDemucs audio stem separation
    /// Separates audio into 4 stems: vocals, drums, bass, and other instruments
    /// </summary>
    class Program
    {
        static async Task Main(string[] args)
        {
            Log.Info("HTDemucs Audio Stem Separation Example");
            Log.Info("======================================");

            // Configuration
            string audioFilePath = @"path/to/your";
            string outputDirectory = @"path/to/output";

            // Check if example paths need to be updated
            if (audioFilePath.Contains("path/to/your"))
            {
                Log.Info("Please update the audioFilePath in Program.cs");
                Log.Info("Press any key to exit...");
                Console.ReadKey();
                return;
            }

            try
            {
                // Download the model on first run if it is not yet present
                const InternalModel model = InternalModel.HTDemucs;

                if (!VocalRemoverModelManager.IsModelAvailable(model))
                {
                    Log.Info("HTDemucs model not found – downloading now (first-time setup, ~166 MB)...");
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

                // Create separation options
                var options = new HTDemucsSeparationOptions
                {
                    Model = model,
                    OutputDirectory = outputDirectory,
                    ChunkSizeSeconds = 10,           // Process in 10-second chunks
                    OverlapFactor = 0.25f,           // 25% overlap between chunks
                    EnableGPU = true,                // Use GPU if available
                    TargetStems = HTDemucsStem.All   // Extract all stems
                };

                Log.Info($"Input file: {audioFilePath}");
                Log.Info($"Model: Embedded HTDemucs");
                Log.Info($"Output directory: {outputDirectory}");
                Log.Info($"Chunk size: {options.ChunkSizeSeconds}s");
                Log.Info($"GPU acceleration: {(options.EnableGPU ? "Enabled" : "Disabled")}");

                // Alternative: Use external model file
                // options.ModelPath = @"path/to/htdemucs.onnx";
                // options.Model = InternalModel.None;

                // Create and initialize the separator
                using var separator = new HTDemucsAudioSeparator(options);

                // Subscribe to progress events
                separator.ProgressChanged += (s, progress) =>
                {
                    // K�sz�ts EGY teljes status stringet
                    string statusLine = $"{progress.Status}: {progress.OverallProgress:F1}%";

                    if (progress.TotalChunks > 0)
                    {
                        statusLine += $" [{progress.ProcessedChunks}/{progress.TotalChunks} chunks]";
                    }

                    // PadRight(80) t�rli a r�gi hosszabb sz�veget
                    Console.Write($"\r{statusLine.PadRight(80)}");
                };

                separator.ProcessingCompleted += (s, result) =>
                {
                    Log.Info($"\nProcessing completed in {result.ProcessingTime.TotalSeconds:F1}s");
                    Log.Info($"Audio duration: {result.AudioDuration.TotalSeconds:F1}s");
                    Log.Info($"Realtime factor: {result.AudioDuration.TotalSeconds / result.ProcessingTime.TotalSeconds:F1}x");
                };

                // Initialize model (model file must already be downloaded at this point)
                Log.Info("Initializing HTDemucs model...");
                separator.Initialize();
                Log.Info("Model initialized successfully!");

                // Process the audio file
                Log.Info("Starting stem separation...");
                var result = separator.Separate(audioFilePath);

                // Display results
                Log.Info("Separation Results:");
                Log.Info("==================");

                foreach (var stem in result.StemPaths)
                {
                    var fileInfo = new FileInfo(stem.Value);
                    Log.Info($"  {stem.Key,-8}: {Path.GetFileName(stem.Value)} ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");
                }

                Log.Info($"All stems saved to: {outputDirectory}");

                // Example: Using helper extension methods
                Log.Info("You can also use helper methods:");
                Log.Info("  var separator = HTDemucsExtensions.CreateDefaultSeparator();");
                Log.Info("  var separator = HTDemucsExtensions.CreateStemSelector(HTDemucsStem.Vocals | HTDemucsStem.Other);");
            }
            catch (FileNotFoundException ex)
            {
                Log.Info($"\nError - File not found: {ex.Message}");
                Log.Info("Please check that the audio file path is correct.");
            }
            catch (InvalidOperationException ex)
            {
                Log.Info($"\nError - Invalid operation: {ex.Message}");
                Log.Info("Make sure the model was downloaded before calling Initialize().");
            }
            catch (Exception ex)
            {
                Log.Info($"\nError occurred: {ex.Message}");
                Log.Info($"Stack trace: {ex.StackTrace}");
            }

            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }
}
