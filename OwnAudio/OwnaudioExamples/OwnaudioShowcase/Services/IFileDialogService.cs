namespace OwnaudioShowcase.Services;

/// <summary>
/// Cross-platform file dialog service interface.
/// Provides abstraction for opening/saving files and selecting folders.
/// </summary>
public interface IFileDialogService
{
    /// <summary>
    /// Opens a file selection dialog.
    /// </summary>
    /// <param name="title">Dialog title</param>
    /// <param name="filters">File type filters (e.g., ["Audio Files:*.mp3;*.wav;*.flac"])</param>
    /// <returns>Selected file path, or null if cancelled</returns>
    Task<string?> OpenFileAsync(string title = "Select File", string[]? filters = null);

    /// <summary>
    /// Opens a multiple file selection dialog.
    /// </summary>
    /// <param name="title">Dialog title</param>
    /// <param name="filters">File type filters</param>
    /// <returns>Array of selected file paths, or null if cancelled</returns>
    Task<string[]?> OpenFilesAsync(string title = "Select Files", string[]? filters = null);

    /// <summary>
    /// Opens a save file dialog.
    /// </summary>
    /// <param name="title">Dialog title</param>
    /// <param name="defaultFileName">Default file name</param>
    /// <param name="filters">File type filters</param>
    /// <returns>Selected file path, or null if cancelled</returns>
    Task<string?> SaveFileAsync(string title = "Save File", string? defaultFileName = null, string[]? filters = null);

    /// <summary>
    /// Opens a folder selection dialog.
    /// </summary>
    /// <param name="title">Dialog title</param>
    /// <returns>Selected folder path, or null if cancelled</returns>
    Task<string?> SelectFolderAsync(string title = "Select Folder");
}
