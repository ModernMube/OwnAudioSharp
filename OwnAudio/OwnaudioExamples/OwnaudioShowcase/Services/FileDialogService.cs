using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace OwnaudioShowcase.Services;

/// <summary>
/// Avalonia-based file dialog service implementation.
/// Uses Avalonia.Platform.Storage API for cross-platform file dialogs.
/// </summary>
public class FileDialogService : IFileDialogService
{
    /// <summary>
    /// Gets the main window's storage provider.
    /// </summary>
    private IStorageProvider? GetStorageProvider()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow?.StorageProvider;
        }
        return null;
    }

    /// <summary>
    /// Converts string filter format to FilePickerFileType.
    /// Expected format: "Description:*.ext1;*.ext2"
    /// Example: "Audio Files:*.mp3;*.wav;*.flac"
    /// </summary>
    private List<FilePickerFileType>? ParseFilters(string[]? filters)
    {
        if (filters == null || filters.Length == 0)
            return null;

        var result = new List<FilePickerFileType>();

        foreach (var filter in filters)
        {
            var parts = filter.Split(':');
            if (parts.Length != 2)
                continue;

            var name = parts[0].Trim();
            var patterns = parts[1].Split(';')
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();

            if (patterns.Length > 0)
            {
                result.Add(new FilePickerFileType(name)
                {
                    Patterns = patterns
                });
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Opens a file selection dialog.
    /// </summary>
    public async Task<string?> OpenFileAsync(string title = "Select File", string[]? filters = null)
    {
        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
            return null;

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ParseFilters(filters)
        };

        var result = await storageProvider.OpenFilePickerAsync(options);

        return result.Count > 0 ? result[0].Path.LocalPath : null;
    }

    /// <summary>
    /// Opens a multiple file selection dialog.
    /// </summary>
    public async Task<string[]?> OpenFilesAsync(string title = "Select Files", string[]? filters = null)
    {
        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
            return null;

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = ParseFilters(filters)
        };

        var result = await storageProvider.OpenFilePickerAsync(options);

        return result.Count > 0
            ? result.Select(f => f.Path.LocalPath).ToArray()
            : null;
    }

    /// <summary>
    /// Opens a save file dialog.
    /// </summary>
    public async Task<string?> SaveFileAsync(string title = "Save File", string? defaultFileName = null, string[]? filters = null)
    {
        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
            return null;

        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultFileName,
            FileTypeChoices = ParseFilters(filters)
        };

        var result = await storageProvider.SaveFilePickerAsync(options);

        return result?.Path.LocalPath;
    }

    /// <summary>
    /// Opens a folder selection dialog.
    /// </summary>
    public async Task<string?> SelectFolderAsync(string title = "Select Folder")
    {
        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
            return null;

        var options = new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        };

        var result = await storageProvider.OpenFolderPickerAsync(options);

        return result.Count > 0 ? result[0].Path.LocalPath : null;
    }
}
