using Ownaudio.Decoders;
using Ownaudio.Core;
using OwnaudioNET.Recording;

namespace OwnaudioNET.Features.Vocalremover
{
    /// <summary>Factory helper for simple vocal separation.</summary>
    public static class SimpleSeparator
    {
        /// <summary>Creates a service with the given model and output directory.</summary>
        public static (SimpleAudioSeparationService service, string vocalPath, string instrumentPath)
            Separator(InternalModel model, string outputDirectory = "separated")
        {
            var svc = new SimpleAudioSeparationService(new SimpleSeparationOptions
            {
                Model           = model,
                OutputDirectory = outputDirectory
            });
            string vocalPath      = Path.Combine(outputDirectory, "vocals.wav");
            string instrumentPath = Path.Combine(outputDirectory, "instrumental.wav");
            return (svc, vocalPath, instrumentPath);
        }
    }


    /// <summary>
    /// Backward-compatibility adapter for simple vocal/instrumental separation.
    /// Delegates to <see cref="OwnAudio.ML.MdxSeparator"/> or
    /// <see cref="OwnAudio.ML.VocalSeparator"/> based on the selected model.
    /// </summary>
    public sealed class SimpleAudioSeparationService : IDisposable
    {
        public event EventHandler<SimpleSeparationProgress>? ProgressChanged;
        public event EventHandler<SimpleSeparationResult>?   ProcessingCompleted;

        private readonly SimpleSeparationOptions _options;
        private bool _disposed;

        private const int TargetSampleRate = 44100;

        public SimpleAudioSeparationService(SimpleSeparationOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public void Initialize() { }

        public SimpleSeparationResult Separate(string inputFilePath)
        {
            if (!File.Exists(inputFilePath))
                throw new FileNotFoundException($"Input file not found: {inputFilePath}");

            var startTime = DateTime.Now;
            var filename  = Path.GetFileNameWithoutExtension(inputFilePath);

            Report("Loading audio file...", 0, inputFilePath);

            using var decoder = AudioDecoderFactory.Create(inputFilePath, TargetSampleRate, 2);
            float[] samples   = decoder.ReadAllSamples();

            Report("Separating...", 10, inputFilePath);

            OwnAudio.ML.SeparationResult mlResult = RunSeparation(samples);

            Report("Saving results...", 90, inputFilePath);

            Directory.CreateDirectory(_options.OutputDirectory);

            var vocalsPath       = Path.Combine(_options.OutputDirectory, $"{filename}_vocals.wav");
            var instrumentalPath = Path.Combine(_options.OutputDirectory, $"{filename}_instrumental.wav");

            WaveFile.Create(vocalsPath,       mlResult.Vocals,       TargetSampleRate, 2, 16);
            WaveFile.Create(instrumentalPath, mlResult.Instrumental, TargetSampleRate, 2, 16);

            var result = new SimpleSeparationResult
            {
                VocalsPath       = vocalsPath,
                InstrumentalPath = instrumentalPath,
                ProcessingTime   = DateTime.Now - startTime
            };

            Report("Completed", 100, inputFilePath);
            ProcessingCompleted?.Invoke(this, result);
            return result;
        }

        public void Dispose()
        {
            if (!_disposed) _disposed = true;
        }

        private OwnAudio.ML.SeparationResult RunSeparation(float[] samples)
        {
            if (!string.IsNullOrEmpty(_options.ModelPath) && File.Exists(_options.ModelPath))
            {
                return OwnAudio.ML.MdxSeparator.SeparateAsync(
                    samples, TargetSampleRate, _options.ModelPath).GetAwaiter().GetResult();
            }

            return _options.Model switch
            {
                InternalModel.Best    => OwnAudio.ML.MdxSeparator.SeparateAsync(samples, TargetSampleRate, "best")
                                            .GetAwaiter().GetResult(),
                InternalModel.Default => OwnAudio.ML.MdxSeparator.SeparateAsync(samples, TargetSampleRate, "default")
                                            .GetAwaiter().GetResult(),
                InternalModel.Karaoke => OwnAudio.ML.MdxSeparator.SeparateAsync(samples, TargetSampleRate, "karaoke")
                                            .GetAwaiter().GetResult(),
                _ => OwnAudio.ML.VocalSeparator.SeparateAsync(samples, TargetSampleRate)
                         .GetAwaiter().GetResult()
            };
        }

        private void Report(string status, double progress, string file)
        {
            ProgressChanged?.Invoke(this, new SimpleSeparationProgress
            {
                CurrentFile     = Path.GetFileName(file),
                Status          = status,
                OverallProgress = progress
            });
        }
    }
}
