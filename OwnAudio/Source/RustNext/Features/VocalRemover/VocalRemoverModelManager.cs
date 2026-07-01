namespace OwnaudioNET.RustNext.Features.Vocalremover
{
    /// <summary>
    /// Progress information for a model download operation.
    /// </summary>
    public class ModelDownloadProgress
    {
        /// <summary>
        /// The model being downloaded.
        /// </summary>
        public InternalModel Model { get; set; }

        /// <summary>
        /// The file name of the model being downloaded.
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// The number of bytes downloaded so far.
        /// </summary>
        public long BytesDownloaded { get; set; }

        /// <summary>
        /// The total number of bytes to download, or <see langword="null"/> if unknown.
        /// </summary>
        public long? TotalBytes { get; set; }

        /// <summary>
        /// Download progress as a percentage (0–100), or -1 if the total size is unknown.
        /// </summary>
        public double Percentage { get; set; }
    }

    /// <summary>
    /// Manages the local storage and on-demand downloading of VocalRemover ONNX model files.
    /// Models are stored in a per-user directory and downloaded from HuggingFace when missing.
    /// </summary>
    /// <remarks>
    /// Use <see cref="IsModelAvailable"/> to check whether a model is ready before calling
    /// <c>Initialize()</c> on any separator, and <see cref="DownloadModelAsync"/> to fetch
    /// missing models. The <see cref="DefaultModelsDirectory"/> property can be overridden at
    /// application startup to redirect model storage to a custom path.
    /// </remarks>
    public static class VocalRemoverModelManager
    {
        private static readonly Dictionary<InternalModel, string> ModelUrls = new()
        {
            [InternalModel.Best]     = "https://huggingface.co/ModernMube/HTDemucs_onnx/resolve/main/best.onnx",
            [InternalModel.Default]  = "https://huggingface.co/ModernMube/HTDemucs_onnx/resolve/main/default.onnx",
            [InternalModel.HTDemucs] = "https://huggingface.co/ModernMube/HTDemucs_onnx/resolve/main/htdemucs.onnx",
            [InternalModel.Karaoke]  = "https://huggingface.co/ModernMube/HTDemucs_onnx/resolve/main/karaoke.onnx",
        };

        private static readonly Dictionary<InternalModel, string> ModelFileNames = new()
        {
            [InternalModel.Best]     = "best.onnx",
            [InternalModel.Default]  = "default.onnx",
            [InternalModel.HTDemucs] = "htdemucs.onnx",
            [InternalModel.Karaoke]  = "karaoke.onnx",
        };

        /// <summary>
        /// Gets or sets the directory where model files are stored.
        /// Defaults to <c>OwnAudio/models</c> inside the per-user local application data folder.
        /// Override at application startup before any separator is initialized.
        /// </summary>
        public static string DefaultModelsDirectory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OwnAudio", "models"
        );

        /// <summary>
        /// Returns <see langword="true"/> if the model file for <paramref name="model"/> exists
        /// in <see cref="DefaultModelsDirectory"/>.
        /// </summary>
        /// <param name="model">The model to check.</param>
        public static bool IsModelAvailable(InternalModel model)
        {
            if (!ModelFileNames.TryGetValue(model, out var fileName))
                return false;

            return File.Exists(Path.Combine(DefaultModelsDirectory, fileName));
        }

        /// <summary>
        /// Returns the full path to the model file for <paramref name="model"/>.
        /// </summary>
        /// <param name="model">The model whose path is requested.</param>
        /// <returns>Absolute path to the model file.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="model"/> is not a downloadable VocalRemover model.
        /// </exception>
        /// <exception cref="FileNotFoundException">
        /// Thrown when the model file is not present in <see cref="DefaultModelsDirectory"/>.
        /// Call <see cref="DownloadModelAsync"/> to fetch it first.
        /// </exception>
        public static string GetModelPath(InternalModel model)
        {
            if (!ModelFileNames.TryGetValue(model, out var fileName))
                throw new ArgumentException(
                    $"'{model}' is not a downloadable VocalRemover model.", nameof(model));

            var path = Path.Combine(DefaultModelsDirectory, fileName);

            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"VocalRemover model file '{fileName}' was not found in '{DefaultModelsDirectory}'. " +
                    $"Download it first by calling VocalRemoverModelManager.DownloadModelAsync(InternalModel.{model}).",
                    path);

            return path;
        }

        /// <summary>
        /// Downloads the specified model from HuggingFace to <see cref="DefaultModelsDirectory"/>.
        /// If the file already exists it is skipped.
        /// </summary>
        /// <param name="model">The model to download.</param>
        /// <param name="progress">
        /// Optional progress sink that receives <see cref="ModelDownloadProgress"/> updates.
        /// </param>
        /// <param name="cancellationToken">Token to cancel the download.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="model"/> is not a downloadable VocalRemover model.
        /// </exception>
        public static async Task DownloadModelAsync(
            InternalModel model,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!ModelUrls.TryGetValue(model, out var url))
                throw new ArgumentException(
                    $"'{model}' is not a downloadable VocalRemover model.", nameof(model));

            var fileName = ModelFileNames[model];
            Directory.CreateDirectory(DefaultModelsDirectory);

            var destPath = Path.Combine(DefaultModelsDirectory, fileName);

            if (File.Exists(destPath))
                return;

            var tempPath = destPath + ".download";

            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromHours(2);

                using var response = await client.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength;

                await using var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(
                    tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long downloadedBytes = 0;
                int bytesRead;

                while ((bytesRead = await networkStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    downloadedBytes += bytesRead;

                    progress?.Report(new ModelDownloadProgress
                    {
                        Model          = model,
                        FileName       = fileName,
                        BytesDownloaded = downloadedBytes,
                        TotalBytes     = totalBytes,
                        Percentage     = totalBytes.HasValue
                            ? (double)downloadedBytes / totalBytes.Value * 100.0
                            : -1
                    });
                }
            }
            catch
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
                throw;
            }

            File.Move(tempPath, destPath, overwrite: true);
        }

        /// <summary>
        /// Downloads every model in <paramref name="models"/> that is not yet present in
        /// <see cref="DefaultModelsDirectory"/>. Models are downloaded sequentially.
        /// </summary>
        /// <param name="models">Collection of models to ensure are available.</param>
        /// <param name="progress">
        /// Optional progress sink that receives <see cref="ModelDownloadProgress"/> updates.
        /// </param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        public static async Task EnsureModelsAvailableAsync(
            IEnumerable<InternalModel> models,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            foreach (var model in models)
            {
                if (!IsModelAvailable(model))
                    await DownloadModelAsync(model, progress, cancellationToken);
            }
        }
    }
}
