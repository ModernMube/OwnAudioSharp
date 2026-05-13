using OwnAudio.ML.Interop;
using System.Security.Cryptography;

namespace OwnAudio.ML;

/// <summary>Progress information for a model download operation.</summary>
public sealed class ModelDownloadProgress
{
    public string ModelName { get; init; } = string.Empty;
    public long BytesReceived { get; init; }
    public long TotalBytes { get; init; }

    /// <summary>Download progress in [0, 1], or -1 if total size is unknown.</summary>
    public double Fraction => TotalBytes > 0 ? (double)BytesReceived / TotalBytes : -1;
}

/// <summary>
/// Ensures that the ownaudio_ml model files are present and valid on disk.
/// Downloads missing or corrupted models from the configured base URL.
/// </summary>
public static class ModelManager
{
    private static readonly ModelInfo[] RequiredModels =
    [
        new("htdemucs.onnx",
            "https://models.ownaudio.io/htdemucs.onnx",
            SizeBytes: 94_371_840,
            Sha256: ""),   // filled in when native library ships

        new("nmp.onnx",
            "https://models.ownaudio.io/nmp.onnx",
            SizeBytes: 17_825_792,
            Sha256: ""),

        new("best.onnx",
            "https://models.ownaudio.io/best.onnx",
            SizeBytes: 62_914_560,
            Sha256: ""),

        new("default.onnx",
            "https://models.ownaudio.io/default.onnx",
            SizeBytes: 62_914_560,
            Sha256: ""),

        new("karaoke.onnx",
            "https://models.ownaudio.io/karaoke.onnx",
            SizeBytes: 62_914_560,
            Sha256: ""),
    ];

    /// <summary>
    /// Default model directory. Returns the application base directory when
    /// <c>nmp.onnx</c> is already present there (bundled NuGet content file).
    /// Falls back to <c>%LocalAppData%/OwnAudio/Models</c> on Windows,
    /// <c>~/.local/share/OwnAudio/Models</c> on Linux/macOS.
    /// </summary>
    public static string DefaultModelDirectory { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "nmp.onnx"))
            ? AppContext.BaseDirectory
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OwnAudio", "Models");

    /// <summary>
    /// Ensures all required models are present in <paramref name="modelDirectory"/>.
    /// Downloads any missing or corrupted models.
    /// </summary>
    /// <param name="modelDirectory">Directory where model files are stored.</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task EnsureModelsAsync(
        string modelDirectory,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(modelDirectory);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OwnAudioSharp/3.0");

        foreach (var model in RequiredModels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string localPath = Path.Combine(modelDirectory, model.FileName);

            if (IsModelValid(localPath, model.Sha256))
                continue;

            await DownloadModelAsync(http, model, localPath, progress, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Initialises the native ML runtime and loads models from <paramref name="modelDirectory"/>.
    /// Call after <see cref="EnsureModelsAsync"/>.
    /// </summary>
    /// <returns>0 on success, negative on error.</returns>
    public static int Initialize(string modelDirectory)
    {
        return NativeMl.ownaudio_ml_init(modelDirectory);
    }

    /// <summary>
    /// Loads a specific model file into the native runtime.
    /// Can be called after <see cref="Initialize"/> to switch models at runtime.
    /// </summary>
    /// <param name="modelName">
    /// Logical name recognised by the native library:
    /// <c>"htdemucs"</c> for vocal separation, <c>"nmp"</c> for chord detection.
    /// </param>
    /// <param name="path">Absolute path to the <c>.onnx</c> file.</param>
    /// <returns>0 on success, negative on error.</returns>
    public static int LoadModel(string modelName, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelName);
        ArgumentException.ThrowIfNullOrEmpty(path);
        return NativeMl.ownaudio_ml_load_model(modelName, path);
    }

    /// <summary>
    /// Returns <see langword="true"/> if the named model is loaded and ready for inference.
    /// </summary>
    /// <param name="modelName">Logical name, e.g. <c>"htdemucs"</c>, <c>"nmp"</c>.</param>
    public static bool IsModelLoaded(string modelName)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelName);
        return NativeMl.ownaudio_ml_is_model_loaded(modelName) == 1;
    }

    /// <summary>Shuts down the native ML runtime.</summary>
    public static void Shutdown()
    {
        NativeMl.ownaudio_ml_shutdown();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static bool IsModelValid(string path, string expectedSha256)
    {
        if (!File.Exists(path)) return false;

        // Skip hash check when sha256 is not yet populated (pre-release)
        if (string.IsNullOrEmpty(expectedSha256)) return true;

        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        byte[] hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DownloadModelAsync(
        HttpClient http,
        ModelInfo model,
        string localPath,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        string tempPath = localPath + ".tmp";

        try
        {
            using var response = await http
                .GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? model.SizeBytes;

            await using var contentStream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var fileStream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 81920, useAsync: true);

            byte[] buffer = new byte[81920];
            long bytesReceived = 0;
            int bytesRead;

            while ((bytesRead = await contentStream
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                    .ConfigureAwait(false);

                bytesReceived += bytesRead;
                progress?.Report(new ModelDownloadProgress
                {
                    ModelName = model.FileName,
                    BytesReceived = bytesReceived,
                    TotalBytes = totalBytes
                });
            }

            File.Move(tempPath, localPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    private sealed record ModelInfo(
        string FileName,
        string Url,
        long SizeBytes,
        string Sha256);
}
