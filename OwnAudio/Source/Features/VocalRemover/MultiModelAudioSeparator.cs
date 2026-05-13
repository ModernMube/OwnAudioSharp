using Ownaudio.Decoders;
using Ownaudio.Core;
using OwnaudioNET.Recording;

namespace OwnaudioNET.Features.Vocalremover
{
    /// <summary>Factory helpers for multi-model separation presets.</summary>
    public static class MultiModelExtensions
    {
        public static MultiModelAudioSeparator CreateSimplePipeline(
            InternalModel model1,
            InternalModel model2,
            string outputDirectory = "separated_multimodel")
            => new(new MultiModelSeparationOptions
            {
                Models          = [new() { Name = model1.ToString(), Model = model1 },
                                   new() { Name = model2.ToString(), Model = model2 }],
                OutputDirectory = outputDirectory
            });

        public static MultiModelAudioSeparator CreateTriplePipeline(
            InternalModel model1,
            InternalModel model2,
            InternalModel model3,
            string outputDirectory = "separated_multimodel")
            => new(new MultiModelSeparationOptions
            {
                Models          = [new() { Name = model1.ToString(), Model = model1 },
                                   new() { Name = model2.ToString(), Model = model2 },
                                   new() { Name = model3.ToString(), Model = model3 }],
                OutputDirectory = outputDirectory
            });
    }


    /// <summary>
    /// Backward-compatibility adapter for multi-model MDX separation.
    /// Delegates to <see cref="OwnAudio.ML.MdxSeparator"/> (ownaudio_ml native library).
    /// </summary>
    public sealed class MultiModelAudioSeparator : IDisposable
    {
        public event EventHandler<MultiModelSeparationProgress>? ProgressChanged;
        public event EventHandler<MultiModelSeparationResult>?   ProcessingCompleted;

        private readonly MultiModelSeparationOptions _options;
        private bool _disposed;

        public MultiModelAudioSeparator(MultiModelSeparationOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public void Initialize() { }

        public MultiModelSeparationResult Separate(string inputFilePath)
        {
            if (!File.Exists(inputFilePath))
                throw new FileNotFoundException($"Input file not found: {inputFilePath}");

            var startTime = DateTime.Now;
            var filename  = Path.GetFileNameWithoutExtension(inputFilePath);
            int totalModels = _options.Models.Count;

            Report("Loading audio file...", 0, inputFilePath, 0, totalModels, "");

            const int targetSampleRate = 44100;
            using var decoder = AudioDecoderFactory.Create(inputFilePath, targetSampleRate, 2);
            float[] samples   = decoder.ReadAllSamples();

            Report("Separating with models...", 10, inputFilePath, 0, totalModels, "");

            var modelNames = _options.Models
                .Select(m => ResolveModelName(m))
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();

            OwnAudio.ML.SeparationResult mlResult;
            if (modelNames.Count == 0)
            {
                mlResult = OwnAudio.ML.VocalSeparator.SeparateAsync(samples, targetSampleRate)
                    .GetAwaiter().GetResult();
            }
            else
            {
                mlResult = OwnAudio.ML.MdxSeparator.SeparateEnsembleAsync(samples, targetSampleRate, modelNames)
                    .GetAwaiter().GetResult();
            }

            Report("Saving results...", 90, inputFilePath, totalModels, totalModels, "");

            Directory.CreateDirectory(_options.OutputDirectory);

            var vocalsPath       = Path.Combine(_options.OutputDirectory, $"{filename}_vocals.wav");
            var instrumentalPath = Path.Combine(_options.OutputDirectory, $"{filename}_instrumental.wav");

            WaveFile.Create(vocalsPath,       mlResult.Vocals,        targetSampleRate, 2, 16);
            WaveFile.Create(instrumentalPath, mlResult.Instrumental,  targetSampleRate, 2, 16);

            var result = new MultiModelSeparationResult
            {
                VocalsPath       = vocalsPath,
                InstrumentalPath = instrumentalPath,
                OutputPath       = instrumentalPath,
                ProcessingTime   = DateTime.Now - startTime,
                ModelsProcessed  = Math.Max(1, modelNames.Count)
            };

            Report("Completed", 100, inputFilePath, totalModels, totalModels, "");
            ProcessingCompleted?.Invoke(this, result);
            return result;
        }

        public void Dispose()
        {
            if (!_disposed) _disposed = true;
        }

        private static string ResolveModelName(MultiModelInfo info)
        {
            if (!string.IsNullOrEmpty(info.ModelPath) && File.Exists(info.ModelPath))
                return info.ModelPath;

            return info.Model switch
            {
                InternalModel.Best    => "best",
                InternalModel.Default => "default",
                InternalModel.Karaoke => "karaoke",
                _                     => string.Empty
            };
        }

        private void Report(string status, double progress, string file,
                            int current, int total, string modelName)
        {
            ProgressChanged?.Invoke(this, new MultiModelSeparationProgress
            {
                CurrentFile      = Path.GetFileName(file),
                Status           = status,
                OverallProgress  = progress,
                CurrentModelIndex = current,
                TotalModels      = total,
                CurrentModelName = modelName
            });
        }
    }
}
