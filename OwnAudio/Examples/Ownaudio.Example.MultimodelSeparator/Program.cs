using OwnaudioNET.Features.Vocalremover;

namespace OwnSeparator.MultiModel
{
    /// <summary>
    /// Example program demonstrating multi-model audio separator with averaging
    ///
    /// This example shows how to use multiple UVR MDX models in parallel
    /// and average their outputs for better quality results.
    ///
    /// How it works:
    /// - All models process the original audio independently (parallel processing)
    /// - Each model outputs vocals and instrumental (auto-detected or configured)
    /// - Vocals from all models are averaged together
    /// - Instrumentals from all models are averaged together
    /// - Final result: High-quality averaged vocals + averaged instrumental
    ///
    /// Common use cases:
    /// 1. Multiple vocal models → Averaged vocals with better quality
    /// 2. Multiple instrumental models → Averaged instrumental with less artifacts
    /// 3. Mixed models (some vocal-focused, some instrumental-focused) → Best of both worlds
    /// </summary>
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("═══════════════════════════════════════════════════");
            Console.WriteLine("  OwnAudioSharp - Multi-Model Audio Separator");
            Console.WriteLine("═══════════════════════════════════════════════════");
            Console.WriteLine();

            // Parse command line arguments
            if (args.Length > 0 && args[0] == "--help")
            {
                ShowHelp();
                return;
            }

            // Default paths (modify these for your environment)
            string audioFilePath = args.Length > 0 ? args[0] : @"/path/to/audio.mp3";
            string outputDirectory = args.Length > 1 ? args[1] : @"/path/to/output/directory";

            Console.WriteLine("Choose an example:");
            Console.WriteLine("1. Simple 2-Model Averaging (Best + Karaoke)");
            Console.WriteLine("2. Triple Model Averaging (Best + Default + Karaoke)");
            Console.WriteLine("3. Custom Pipeline with Intermediate Saves");
            Console.WriteLine("4. Custom Model Files Pipeline");
            Console.WriteLine("5. Averaging Demo with Auto-Detection");
            Console.WriteLine("6. Mixed OutputType Demo (Vocals + Instrumental models)");
            Console.WriteLine();
            Console.Write("Enter choice (1-6): ");

            string? choice = Console.ReadLine();

            try
            {
                switch (choice)
                {
                    case "1":
                        RunSimplePipeline(audioFilePath, outputDirectory);
                        break;
                    case "2":
                        RunTriplePipeline(audioFilePath, outputDirectory);
                        break;
                    case "3":
                        RunCustomPipelineWithDebug(audioFilePath, outputDirectory);
                        break;
                    case "4":
                        RunCustomFilesPipeline(audioFilePath, outputDirectory);
                        break;
                    case "5":
                        RunAveragingDemo(audioFilePath, outputDirectory);
                        break;
                    case "6":
                        RunMixedOutputTypeDemo(audioFilePath, outputDirectory);
                        break;
                    default:
                        Console.WriteLine("Invalid choice. Running simple pipeline as default.");
                        RunSimplePipeline(audioFilePath, outputDirectory);
                        break;
                }
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine();
                Console.WriteLine($"❌ Error: File not found");
                Console.WriteLine($"   {ex.Message}");
                Console.WriteLine();
                Console.WriteLine("Please update the file paths in the code or pass them as arguments:");
                Console.WriteLine("   dotnet run <audio-file> <output-directory>");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"❌ Error occurred: {ex.Message}");
                Console.WriteLine();
                Console.WriteLine("Stack trace:");
                Console.WriteLine(ex.StackTrace);
            }

            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        /// <summary>
        /// Example 1: Simple 2-model averaging using helper method
        /// This is the easiest way to average results from multiple models
        /// </summary>
        static void RunSimplePipeline(string audioFilePath, string outputDirectory)
        {
            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════");
            Console.WriteLine("  Example 1: Simple 2-Model Averaging");
            Console.WriteLine("═══════════════════════════════════════════");
            Console.WriteLine();
            Console.WriteLine($"Input:  {audioFilePath}");
            Console.WriteLine($"Output: {outputDirectory}");
            Console.WriteLine();
            Console.WriteLine("ℹ️  Both models will process the original audio independently.");
            Console.WriteLine("   Results will be averaged for better quality.");
            Console.WriteLine();

            // Create separator using helper method
            var separator = MultiModelExtensions.CreateSimplePipeline(
                model1: InternalModel.Best,
                model2: InternalModel.Karaoke,
                outputDirectory: outputDirectory
            );

            // Subscribe to progress updates
            separator.ProgressChanged += OnProgressChanged;
            separator.ProcessingCompleted += OnProcessingCompleted;

            // Initialize and process
            Console.WriteLine("⚙️  Initializing models...");
            separator.Initialize();

            Console.WriteLine("🚀 Starting processing...");
            Console.WriteLine();

            var result = separator.Separate(audioFilePath);

            Console.WriteLine();
            Console.WriteLine("✅ Processing completed!");
            Console.WriteLine($"   Time: {result.ProcessingTime}");
            Console.WriteLine($"   Models: {result.ModelsProcessed}");
            Console.WriteLine();
            Console.WriteLine("📁 Output files:");
            Console.WriteLine($"   🎤 Vocals:       {result.VocalsPath}");
            Console.WriteLine($"   🎸 Instrumental: {result.InstrumentalPath}");

            separator.Dispose();
        }

        /// <summary>
        /// Example 2: Triple model averaging with all intermediate results saved
        /// </summary>
        static void RunTriplePipeline(string audioFilePath, string outputDirectory)
        {
            Console.WriteLine();
            Console.WriteLine("══════════════════════════════════════════");
            Console.WriteLine("  Example 2: Triple Model Averaging");
            Console.WriteLine("══════════════════════════════════════════");
            Console.WriteLine();
            Console.WriteLine($"Input:  {audioFilePath}");
            Console.WriteLine($"Output: {outputDirectory}");
            Console.WriteLine();
            Console.WriteLine("Models: Best + Default + Karaoke (all parallel)");
            Console.WriteLine("All intermediate results will be saved.");
            Console.WriteLine();
            Console.WriteLine("ℹ️  Each model processes the original audio independently.");
            Console.WriteLine("   Results are averaged: (Best + Default + Karaoke) / 3");
            Console.WriteLine();

            // Create separator with 3 models
            var separator = MultiModelExtensions.CreateTriplePipeline(
                model1: InternalModel.Best,
                model2: InternalModel.Default,
                model3: InternalModel.Karaoke,
                outputDirectory: outputDirectory
            );

            // Subscribe to events
            separator.ProgressChanged += OnProgressChanged;
            separator.ProcessingCompleted += OnProcessingCompleted;

            // Initialize and process
            Console.WriteLine("⚙️  Initializing models...");
            separator.Initialize();

            Console.WriteLine("🚀 Starting processing...");
            Console.WriteLine();

            var result = separator.Separate(audioFilePath);

            Console.WriteLine();
            Console.WriteLine("✅ Processing completed!");
            Console.WriteLine($"   Time: {result.ProcessingTime}");
            Console.WriteLine($"   Models: {result.ModelsProcessed}");
            Console.WriteLine();
            Console.WriteLine("📁 Final outputs:");
            Console.WriteLine($"   🎤 Vocals:       {result.VocalsPath}");
            Console.WriteLine($"   🎸 Instrumental: {result.InstrumentalPath}");
            Console.WriteLine();
            Console.WriteLine("📁 Intermediate results:");
            foreach (var intermediate in result.IntermediatePaths)
            {
                Console.WriteLine($"   {intermediate.Key}:");
                Console.WriteLine($"      {intermediate.Value}");
            }

            separator.Dispose();
        }

        /// <summary>
        /// Example 3: Custom averaging pipeline with full control and debugging
        /// </summary>
        static void RunCustomPipelineWithDebug(string audioFilePath, string outputDirectory)
        {
            Console.WriteLine();
            Console.WriteLine("════════════════════════════════════════════════");
            Console.WriteLine("  Example 3: Custom Averaging with Debug Mode");
            Console.WriteLine("════════════════════════════════════════════════");
            Console.WriteLine();
            Console.WriteLine($"Input:  {audioFilePath}");
            Console.WriteLine($"Output: {outputDirectory}");
            Console.WriteLine();
            Console.WriteLine("ℹ️  This example shows fine-grained control over averaging.");
            Console.WriteLine();

            // Create options with full control
            var options = new MultiModelSeparationOptions
            {
                Models = new List<MultiModelInfo>
                {
                    new MultiModelInfo
                    {
                        Name = "Step1_VocalExtraction",
                        Model = InternalModel.Best,
                        NFft = 6144,
                        DimT = 8,
                        DimF = 2048,
                        DisableNoiseReduction = false,
                        SaveIntermediateOutput = true  // Save this step
                    },
                    new MultiModelInfo
                    {
                        Name = "Step2_Enhancement",
                        Model = InternalModel.Default,
                        NFft = 6144,
                        DimT = 8,
                        DimF = 2048,
                        DisableNoiseReduction = false,
                        SaveIntermediateOutput = true  // Save this step
                    },
                    new MultiModelInfo
                    {
                        Name = "Step3_FinalPolish",
                        Model = InternalModel.Karaoke,
                        NFft = 6144,
                        DimT = 8,
                        DimF = 2048,
                        DisableNoiseReduction = true,  // Different setting
                        SaveIntermediateOutput = false  // Don't save (we have final)
                    }
                },
                OutputDirectory = outputDirectory,
                EnableGPU = true,
                ChunkSizeSeconds = 15,
                Margin = 44100,
                SaveAllIntermediateResults = true  // Force save all
            };

            Console.WriteLine($"Pipeline configured with {options.Models.Count} models:");
            for (int i = 0; i < options.Models.Count; i++)
            {
                var model = options.Models[i];
                Console.WriteLine($"  {i + 1}. {model.Name}");
                Console.WriteLine($"     Model: {model.Model}");
                Console.WriteLine($"     FFT: {model.NFft}, DimF: {model.DimF}, DimT: {model.DimT}");
                Console.WriteLine($"     Noise Reduction: {!model.DisableNoiseReduction}");
            }
            Console.WriteLine();

            var separator = new MultiModelAudioSeparator(options);

            // Detailed progress reporting
            separator.ProgressChanged += (sender, progress) =>
            {
                Console.Write($"\r[Model {progress.CurrentModelIndex}/{progress.TotalModels}: {progress.CurrentModelName}] ");
                Console.Write($"Chunk {progress.ProcessedChunks}/{progress.TotalChunks} ");
                Console.Write($"({progress.OverallProgress:F1}%) - {progress.Status}");
            };

            separator.ProcessingCompleted += (sender, result) =>
            {
                Console.WriteLine();
                Console.WriteLine();
                Console.WriteLine($"⏱️  Total processing time: {result.ProcessingTime}");
            };

            // Initialize and process
            Console.WriteLine("⚙️  Initializing models...");
            separator.Initialize();

            Console.WriteLine("🚀 Starting processing...");
            Console.WriteLine();

            var result = separator.Separate(audioFilePath);

            Console.WriteLine();
            Console.WriteLine("✅ Processing completed!");
            Console.WriteLine();
            Console.WriteLine("📁 Final outputs:");
            Console.WriteLine($"   🎤 Vocals:       {result.VocalsPath}");
            Console.WriteLine($"   🎸 Instrumental: {result.InstrumentalPath}");
            Console.WriteLine();
            Console.WriteLine("📁 All intermediate files:");
            foreach (var intermediate in result.IntermediatePaths.OrderBy(x => x.Key))
            {
                Console.WriteLine($"   {intermediate.Key}:");
                Console.WriteLine($"      {intermediate.Value}");
            }

            separator.Dispose();
        }

        /// <summary>
        /// Example 4: Using custom model files from disk with auto-detection
        /// </summary>
        static void RunCustomFilesPipeline(string audioFilePath, string outputDirectory)
        {
            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════");
            Console.WriteLine("  Example 4: Custom Model Files");
            Console.WriteLine("═══════════════════════════════════════");
            Console.WriteLine();
            Console.WriteLine("This example shows how to use custom ONNX models from disk.");
            Console.WriteLine("OutputType will be auto-detected from filename:");
            Console.WriteLine("  - 'Voc_FT' contains 'Voc' → Auto-detected as Vocals");
            Console.WriteLine("  - 'Inst_HQ_3' contains 'Inst' → Auto-detected as Instrumental");
            Console.WriteLine();

            // Example paths (update these to your actual model files)
            //string model1Path = @"path/models/custom_model_1.onnx";
            //string model2Path = @"path/models/custom_model_2.onnx";
            string model1Path =
                @"/path/model_1.onnx";
            string model2Path = @"/path/model_2.onnx"; 
            
            var options = new MultiModelSeparationOptions
            {
                Models = new List<MultiModelInfo>
                {
                    new MultiModelInfo
                    {
                        Name = "CustomModel1",
                        ModelPath = model1Path,
                        NFft = 6144,
                        DimT = 8,
                        DimF = 2048
                    },
                    new MultiModelInfo
                    {
                        Name = "CustomModel2",
                        ModelPath = model2Path,
                        NFft = 6144,
                        DimT = 8,
                        DimF = 2048
                    }
                },
                OutputDirectory = outputDirectory,
                EnableGPU = true
            };

            Console.WriteLine($"Input:  {audioFilePath}");
            Console.WriteLine($"Output: {outputDirectory}");
            Console.WriteLine();
            Console.WriteLine("Models:");
            Console.WriteLine($"  1. {model1Path}");
            Console.WriteLine($"  2. {model2Path}");
            Console.WriteLine();

            var separator = new MultiModelAudioSeparator(options);

            separator.ProgressChanged += OnProgressChanged;
            separator.ProcessingCompleted += OnProcessingCompleted;

            Console.WriteLine("⚙️  Initializing custom models...");
            separator.Initialize();

            Console.WriteLine("🚀 Starting processing...");
            Console.WriteLine();

            var result = separator.Separate(audioFilePath);

            Console.WriteLine();
            Console.WriteLine("✅ Processing completed!");
            Console.WriteLine();
            Console.WriteLine("📁 Output files:");
            Console.WriteLine($"   🎤 Vocals:       {result.VocalsPath}");
            Console.WriteLine($"   🎸 Instrumental: {result.InstrumentalPath}");

            separator.Dispose();
        }

        /// <summary>
        /// Example 5: Demonstrates how multi-model averaging works with auto-detection
        /// Shows the parallel processing pipeline with detailed explanation
        /// </summary>
        static void RunAveragingDemo(string audioFilePath, string outputDirectory)
        {
            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════════");
            Console.WriteLine("  Example 5: Multi-Model Averaging Demo");
            Console.WriteLine("═══════════════════════════════════════════════════");
            Console.WriteLine();
            Console.WriteLine("This demo shows how multi-model averaging works:");
            Console.WriteLine();
            Console.WriteLine("                Original Mix");
            Console.WriteLine("                     │");
            Console.WriteLine("        ┌────────────┼────────────┐");
            Console.WriteLine("        ↓            ↓            ↓");
            Console.WriteLine("   ┌────────┐  ┌────────┐  ┌────────┐");
            Console.WriteLine("   │Model 1 │  │Model 2 │  │Model 3 │  ← All process original");
            Console.WriteLine("   │ Best   │  │Default │  │Karaoke │     independently");
            Console.WriteLine("   └────────┘  └────────┘  └────────┘");
            Console.WriteLine("        │            │            │");
            Console.WriteLine("        ↓            ↓            ↓");
            Console.WriteLine("    V₁ + I₁      V₂ + I₂      V₃ + I₃");
            Console.WriteLine("        │            │            │");
            Console.WriteLine("        └────────────┼────────────┘");
            Console.WriteLine("                     ↓");
            Console.WriteLine("              ┌─────────────┐");
            Console.WriteLine("              │  AVERAGING  │");
            Console.WriteLine("              └─────────────┘");
            Console.WriteLine("                     │");
            Console.WriteLine("        ┌────────────┴────────────┐");
            Console.WriteLine("        ↓                         ↓");
            Console.WriteLine("  Vocals_avg           Instrumental_avg");
            Console.WriteLine("  (V₁+V₂+V₃)/3         (I₁+I₂+I₃)/3");
            Console.WriteLine("        │                         │");
            Console.WriteLine("        ↓                         ↓");
            Console.WriteLine("    💾 SAVE!                  💾 SAVE!");
            Console.WriteLine();
            Console.WriteLine($"Input:  {audioFilePath}");
            Console.WriteLine($"Output: {outputDirectory}");
            Console.WriteLine();

            var options = new MultiModelSeparationOptions
            {
                Models = new List<MultiModelInfo>
                {
                    new MultiModelInfo
                    {
                        Name = "Model_Best",
                        Model = InternalModel.Best,
                        NFft = 6144,
                        DimT = 8,
                        DimF = 2048,
                        DisableNoiseReduction = false
                        // OutputType = null (auto-detect)
                    },
                    new MultiModelInfo
                    {
                        Name = "Model_Default",
                        Model = InternalModel.Default,
                        NFft = 6144,
                        DimT = 8,
                        DimF = 2048,
                        DisableNoiseReduction = false
                        // OutputType = null (auto-detect)
                    },
                    new MultiModelInfo
                    {
                        Name = "Model_Karaoke",
                        Model = InternalModel.Karaoke,
                        NFft = 6144,
                        DimT = 8,
                        DimF = 2048,
                        DisableNoiseReduction = false
                        // OutputType = null (auto-detect)
                    }
                },
                OutputDirectory = outputDirectory,
                EnableGPU = true,
                ChunkSizeSeconds = 15,
                Margin = 44100,
                SaveAllIntermediateResults = true  // Save individual model outputs
            };

            var separator = new MultiModelAudioSeparator(options);

            separator.ProgressChanged += (sender, progress) =>
            {
                Console.Write($"\r🔄 Processing model {progress.CurrentModelIndex}/{progress.TotalModels} ");
                Console.Write($"({progress.CurrentModelName})... ");
                Console.Write($"Chunk {progress.ProcessedChunks}/{progress.TotalChunks} ({progress.OverallProgress:F1}%)");
            };

            Console.WriteLine("⚙️  Initializing models...");
            separator.Initialize();

            Console.WriteLine("🚀 Starting parallel processing + averaging pipeline...");
            Console.WriteLine();

            var startTime = DateTime.Now;
            var result = separator.Separate(audioFilePath);
            var elapsed = DateTime.Now - startTime;

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("✅ Processing completed!");
            Console.WriteLine();
            Console.WriteLine("📊 Statistics:");
            Console.WriteLine($"   ⏱️  Total time: {elapsed}");
            Console.WriteLine($"   🔧 Models used: {result.ModelsProcessed}");
            Console.WriteLine($"   💾 Memory: Streaming (minimal footprint)");
            Console.WriteLine();
            Console.WriteLine("📁 Final averaged outputs:");
            Console.WriteLine($"   🎤 Vocals (averaged):       {result.VocalsPath}");
            Console.WriteLine($"      ↳ Average of {result.ModelsProcessed} model outputs");
            Console.WriteLine($"   🎸 Instrumental (averaged): {result.InstrumentalPath}");
            Console.WriteLine($"      ↳ Average of {result.ModelsProcessed} model outputs");
            Console.WriteLine();
            Console.WriteLine("📁 Individual model outputs:");
            foreach (var intermediate in result.IntermediatePaths.OrderBy(x => x.Key))
            {
                Console.WriteLine($"   {intermediate.Key}");
            }
            Console.WriteLine();
            Console.WriteLine("💡 How averaging works:");
            Console.WriteLine("   1. All models process the ORIGINAL audio independently");
            Console.WriteLine("   2. Each model outputs vocals and instrumental");
            Console.WriteLine("   3. Vocals from all models are averaged: (V₁+V₂+V₃)/3");
            Console.WriteLine("   4. Instrumentals from all models are averaged: (I₁+I₂+I₃)/3");
            Console.WriteLine("   5. Result: Better quality with reduced artifacts!");

            separator.Dispose();
        }

        /// <summary>
        /// Example 6: Mixed OutputType demo - combining vocal and instrumental models
        /// Shows explicit OutputType configuration for models that output different stems
        /// </summary>
        static void RunMixedOutputTypeDemo(string audioFilePath, string outputDirectory)
        {
            Console.WriteLine();
            Console.WriteLine("═════════════════════════════════════════════════════");
            Console.WriteLine("  Example 6: Mixed OutputType Demo");
            Console.WriteLine("═════════════════════════════════════════════════════");
            Console.WriteLine();
            Console.WriteLine("This demo shows how to combine models with different outputs:");
            Console.WriteLine();
            Console.WriteLine("  🎤 Model 1 (Voc_FT) → Outputs VOCALS");
            Console.WriteLine("  🎸 Model 2 (Inst_HQ_3) → Outputs INSTRUMENTAL");
            Console.WriteLine();
            Console.WriteLine("  The system will:");
            Console.WriteLine("  1. Extract vocals from Model 1 (direct output)");
            Console.WriteLine("  2. Calculate instrumental from Model 1 (original - vocals)");
            Console.WriteLine("  3. Extract instrumental from Model 2 (direct output)");
            Console.WriteLine("  4. Calculate vocals from Model 2 (original - instrumental)");
            Console.WriteLine("  5. Average both vocals: (V₁ + V₂) / 2");
            Console.WriteLine("  6. Average both instrumentals: (I₁ + I₂) / 2");
            Console.WriteLine();
            Console.WriteLine($"Input:  {audioFilePath}");
            Console.WriteLine($"Output: {outputDirectory}");
            Console.WriteLine();

            var options = new MultiModelSeparationOptions
            {
                Models = new List<MultiModelInfo>
                {
                    new MultiModelInfo
                    {
                        Name = "VocalModel_VocFT",
                        ModelPath = @"/path/to/Voc_FT.onnx",
                        NFft = 6144,
                        DimT = 8,
                        DimF = 2048,
                        OutputType = ModelOutputType.Vocals  // Explicit: this outputs VOCALS
                    },
                    new MultiModelInfo
                    {
                        Name = "InstrumentalModel_InstHQ3",
                        ModelPath = @"/path/to/Inst_HQ_3.onnx"",
                        NFft = 6144,
                        DimT = 8,
                        DimF = 2048,
                        OutputType = ModelOutputType.Instrumental  // Explicit: this outputs INSTRUMENTAL
                    }
                },
                OutputDirectory = outputDirectory,
                EnableGPU = true,
                ChunkSizeSeconds = 15,
                Margin = 44100,
                SaveAllIntermediateResults = true
            };

            Console.WriteLine("⚙️  Configuration:");
            for (int i = 0; i < options.Models.Count; i++)
            {
                var model = options.Models[i];
                Console.WriteLine($"   Model {i + 1}: {model.Name}");
                Console.WriteLine($"      OutputType: {model.OutputType}");
                Console.WriteLine($"      Path: {Path.GetFileName(model.ModelPath ?? "embedded")}");
            }
            Console.WriteLine();

            var separator = new MultiModelAudioSeparator(options);

            separator.ProgressChanged += (sender, progress) =>
            {
                Console.Write($"\r[{progress.CurrentModelName}] ");
                Console.Write($"Chunk {progress.ProcessedChunks}/{progress.TotalChunks} ({progress.OverallProgress:F1}%)");
            };

            Console.WriteLine("⚙️  Initializing models...");
            separator.Initialize();

            Console.WriteLine("🚀 Starting processing...");
            Console.WriteLine();

            var result = separator.Separate(audioFilePath);

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("✅ Processing completed!");
            Console.WriteLine($"   Time: {result.ProcessingTime}");
            Console.WriteLine();
            Console.WriteLine("📁 Final averaged outputs:");
            Console.WriteLine($"   🎤 Vocals (averaged):       {result.VocalsPath}");
            Console.WriteLine($"      ↳ (Vocal model output + Instrumental model complement) / 2");
            Console.WriteLine($"   🎸 Instrumental (averaged): {result.InstrumentalPath}");
            Console.WriteLine($"      ↳ (Instrumental model output + Vocal model complement) / 2");
            Console.WriteLine();
            Console.WriteLine("📁 Individual model outputs:");
            foreach (var intermediate in result.IntermediatePaths.OrderBy(x => x.Key))
            {
                Console.WriteLine($"   {intermediate.Key}");
            }
            Console.WriteLine();
            Console.WriteLine("💡 Benefits of mixing OutputTypes:");
            Console.WriteLine("   ✅ Combines strengths of specialized models");
            Console.WriteLine("   ✅ Vocal-focused model improves vocal quality");
            Console.WriteLine("   ✅ Instrumental-focused model improves instrumental quality");
            Console.WriteLine("   ✅ Averaging reduces artifacts from both");

            separator.Dispose();
        }

        /// <summary>
        /// Standard progress event handler
        /// </summary>
        static void OnProgressChanged(object? sender, MultiModelSeparationProgress progress)
        {
            Console.Write($"\r[{progress.CurrentModelIndex}/{progress.TotalModels}: {progress.CurrentModelName}] ");
            Console.Write($"{progress.Status} ({progress.OverallProgress:F1}%)");
        }

        /// <summary>
        /// Processing completed event handler
        /// </summary>
        static void OnProcessingCompleted(object? sender, MultiModelSeparationResult result)
        {
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine($"⏱️  Processing time: {result.ProcessingTime}");
            Console.WriteLine($"🎤 Vocals:       {result.VocalsPath}");
            Console.WriteLine($"🎸 Instrumental: {result.InstrumentalPath}");
        }

        /// <summary>
        /// Show help information
        /// </summary>
        static void ShowHelp()
        {
            Console.WriteLine("Usage: dotnet run [audio-file] [output-directory]");
            Console.WriteLine();
            Console.WriteLine("Arguments:");
            Console.WriteLine("  audio-file         Path to input audio file (WAV, MP3, or FLAC)");
            Console.WriteLine("  output-directory   Path to output directory for results");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  dotnet run song.mp3 output/");
            Console.WriteLine("  dotnet run \"C:\\Music\\song.wav\" \"C:\\Output\\\"");
            Console.WriteLine();
            Console.WriteLine("The program will prompt you to choose an example pipeline:");
            Console.WriteLine("  1. Simple 2-Model Averaging");
            Console.WriteLine("  2. Triple Model Averaging");
            Console.WriteLine("  3. Custom Pipeline with Debug");
            Console.WriteLine("  4. Custom Model Files");
            Console.WriteLine("  5. Averaging Demo with Auto-Detection");
            Console.WriteLine("  6. Mixed OutputType Demo (Vocals + Instrumental models)");
        }
    }
}
