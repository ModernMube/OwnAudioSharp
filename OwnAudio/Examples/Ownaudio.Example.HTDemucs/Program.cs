using OwnaudioNET.Features.Vocalremover;

namespace HTDemucsExample
{
    /// <summary>
    /// Example program demonstrating HTDemucs audio stem separation
    /// Separates audio into 4 stems: vocals, drums, bass, and other instruments
    /// </summary>
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("HTDemucs Audio Stem Separation Example");
            Console.WriteLine("======================================");
            Console.WriteLine();

            // Configuration
            string audioFilePath = @"path/to/your";
            string outputDirectory = @"path/to/output";

            // Check if example paths need to be updated
            if (audioFilePath.Contains("path/to/your"))
            {
                Console.WriteLine("Please update the audioFilePath in Program.cs");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                return;
            }

            try
            {
                // Create separation options using EMBEDDED htdemucs.onnx model
                var options = new HTDemucsSeparationOptions
                {
                    Model = InternalModel.HTDemucs,  // Use embedded model
                    OutputDirectory = outputDirectory,
                    ChunkSizeSeconds = 10,           // Process in 10-second chunks
                    OverlapFactor = 0.25f,           // 25% overlap between chunks
                    EnableGPU = true,                // Use GPU if available
                    TargetStems = HTDemucsStem.All   // Extract all stems
                };

                Console.WriteLine($"Input file: {audioFilePath}");
                Console.WriteLine($"Model: Embedded HTDemucs");
                Console.WriteLine($"Output directory: {outputDirectory}");
                Console.WriteLine($"Chunk size: {options.ChunkSizeSeconds}s");
                Console.WriteLine($"GPU acceleration: {(options.EnableGPU ? "Enabled" : "Disabled")}");
                Console.WriteLine();

                // Alternative: Use external model file
                // options.ModelPath = @"path/to/htdemucs.onnx";
                // options.Model = InternalModel.None;

                // Create and initialize the separator
                using var separator = new HTDemucsAudioSeparator(options);

                // Subscribe to progress events
                separator.ProgressChanged += (s, progress) =>
                {
                    // Készíts EGY teljes status stringet
                    string statusLine = $"{progress.Status}: {progress.OverallProgress:F1}%";

                    if (progress.TotalChunks > 0)
                    {
                        statusLine += $" [{progress.ProcessedChunks}/{progress.TotalChunks} chunks]";
                    }

                    // PadRight(80) törli a régi hosszabb szöveget
                    Console.Write($"\r{statusLine.PadRight(80)}");
                };

                separator.ProcessingCompleted += (s, result) =>
                {
                    Console.WriteLine();
                    Console.WriteLine($"\nProcessing completed in {result.ProcessingTime.TotalSeconds:F1}s");
                    Console.WriteLine($"Audio duration: {result.AudioDuration.TotalSeconds:F1}s");
                    Console.WriteLine($"Realtime factor: {result.AudioDuration.TotalSeconds / result.ProcessingTime.TotalSeconds:F1}x");
                };

                // Initialize model
                Console.WriteLine("Initializing HTDemucs model...");
                separator.Initialize();
                Console.WriteLine("Model initialized successfully!");
                Console.WriteLine();

                // Process the audio file
                Console.WriteLine("Starting stem separation...");
                var result = separator.Separate(audioFilePath);

                // Display results
                Console.WriteLine();
                Console.WriteLine("Separation Results:");
                Console.WriteLine("==================");

                foreach (var stem in result.StemPaths)
                {
                    var fileInfo = new FileInfo(stem.Value);
                    Console.WriteLine($"  {stem.Key,-8}: {Path.GetFileName(stem.Value)} ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");
                }

                Console.WriteLine();
                Console.WriteLine($"All stems saved to: {outputDirectory}");

                // Example: Using helper extension methods
                Console.WriteLine();
                Console.WriteLine("You can also use helper methods:");
                Console.WriteLine("  var separator = HTDemucsExtensions.CreateDefaultSeparator();");
                Console.WriteLine("  var separator = HTDemucsExtensions.CreateStemSelector(HTDemucsStem.Vocals | HTDemucsStem.Other);");
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine($"\nError - File not found: {ex.Message}");
                Console.WriteLine("Please check that the audio file path is correct.");
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine($"\nError - Invalid operation: {ex.Message}");
                Console.WriteLine("The embedded htdemucs.onnx model may be missing from the assembly.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nError occurred: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }

            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }
}
