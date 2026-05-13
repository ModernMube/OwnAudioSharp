using Ownaudio.Decoders;
using Ownaudio.Core;
using OwnaudioNET.Recording;

namespace OwnaudioNET.Features.Vocalremover
{
    /// <summary>
    /// Backward-compatibility adapter for HTDemucs stem separation.
    /// Delegates to <see cref="OwnAudio.ML.VocalSeparator"/> (ownaudio_ml native library).
    /// </summary>
    public sealed class HTDemucsAudioSeparator : IDisposable
    {
        public event EventHandler<HTDemucsSeparationProgress>? ProgressChanged;
        public event EventHandler<HTDemucsSeparationResult>?   ProcessingCompleted;

        private readonly HTDemucsSeparationOptions _options;
        private bool _disposed;

        public HTDemucsAudioSeparator(HTDemucsSeparationOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public void Initialize() { }

        public HTDemucsSeparationResult Separate(string inputFilePath)
        {
            if (!File.Exists(inputFilePath))
                throw new FileNotFoundException($"Input file not found: {inputFilePath}");

            var startTime = DateTime.Now;
            var filename  = Path.GetFileNameWithoutExtension(inputFilePath);

            Report("Loading audio file...", 0, inputFilePath);

            using var decoder = AudioDecoderFactory.Create(inputFilePath, _options.TargetSampleRate, 2);
            float[] samples   = decoder.ReadAllSamples();

            var audioDuration = TimeSpan.FromSeconds((double)samples.Length / (_options.TargetSampleRate * 2));

            Report("Separating stems...", 10, inputFilePath);

            string? modelPath = !string.IsNullOrEmpty(_options.ModelPath) && File.Exists(_options.ModelPath)
                ? _options.ModelPath : null;

            var mlResult = OwnAudio.ML.VocalSeparator.SeparateAsync(
                samples, _options.TargetSampleRate, modelPath).GetAwaiter().GetResult();

            Report("Saving stems...", 90, inputFilePath);

            Directory.CreateDirectory(_options.OutputDirectory);
            var stemPaths = new Dictionary<HTDemucsStem, string>();

            if (_options.TargetStems.HasFlag(HTDemucsStem.Vocals))
            {
                var path = Path.Combine(_options.OutputDirectory, $"{filename}_vocals.wav");
                WaveFile.Create(path, mlResult.Vocals, _options.TargetSampleRate, 2, 16);
                stemPaths[HTDemucsStem.Vocals] = path;
            }

            var instrumentalStems = HTDemucsStem.Drums | HTDemucsStem.Bass | HTDemucsStem.Other;
            if ((_options.TargetStems & instrumentalStems) != 0)
            {
                var path = Path.Combine(_options.OutputDirectory, $"{filename}_instrumental.wav");
                WaveFile.Create(path, mlResult.Instrumental, _options.TargetSampleRate, 2, 16);
                foreach (HTDemucsStem s in new[] { HTDemucsStem.Drums, HTDemucsStem.Bass, HTDemucsStem.Other })
                {
                    if (_options.TargetStems.HasFlag(s))
                        stemPaths[s] = path;
                }
            }

            var result = new HTDemucsSeparationResult
            {
                StemPaths      = stemPaths,
                ProcessingTime = DateTime.Now - startTime,
                AudioDuration  = audioDuration
            };

            Report("Completed", 100, inputFilePath);
            ProcessingCompleted?.Invoke(this, result);
            return result;
        }

        public void Dispose()
        {
            if (!_disposed) _disposed = true;
        }

        private void Report(string status, double progress, string file)
        {
            ProgressChanged?.Invoke(this, new HTDemucsSeparationProgress
            {
                CurrentFile     = Path.GetFileName(file),
                Status          = status,
                OverallProgress = progress
            });
        }
    }

    /// <summary>Factory helpers for common separation presets.</summary>
    public static class HTDemucsExtensions
    {
        public static HTDemucsAudioSeparator CreateVocalsOnly(string outputDirectory = "separated_htdemucs")
            => new(new HTDemucsSeparationOptions
            {
                TargetStems     = HTDemucsStem.Vocals,
                OutputDirectory = outputDirectory
            });

        public static HTDemucsAudioSeparator CreateAllStems(string outputDirectory = "separated_htdemucs")
            => new(new HTDemucsSeparationOptions
            {
                TargetStems     = HTDemucsStem.All,
                OutputDirectory = outputDirectory
            });

        public static HTDemucsAudioSeparator CreateWithModel(string modelPath, string outputDirectory = "separated_htdemucs")
            => new(new HTDemucsSeparationOptions
            {
                ModelPath       = modelPath,
                TargetStems     = HTDemucsStem.All,
                OutputDirectory = outputDirectory
            });
    }
}
