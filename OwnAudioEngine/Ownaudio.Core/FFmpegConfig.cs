using Ownaudio.Decoders.FFmpeg;

namespace Ownaudio.Core;

/// <summary>
/// Global configuration for the optional FFmpeg dynamic library integration.
/// FFmpeg is only active when the dynamic libraries are found and loaded successfully.
/// </summary>
public static class FFmpegConfig
{
    #region Fields

    private static bool _isAvailable;

    #endregion

    #region Properties

    /// <summary>
    /// Directory path that contains the FFmpeg dynamic library files
    /// (e.g. avcodec, avformat, avutil, swresample).
    /// When empty, the engine searches the standard system locations automatically.
    /// Must be set before the first decoder is created.
    /// </summary>
    public static string CustomLibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Indicates whether the FFmpeg dynamic libraries were found and loaded successfully.
    /// Triggers library detection on first access; subsequent reads are instant.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            FFmpegLoader.Initialize();
            return _isAvailable;
        }
        internal set => _isAvailable = value;
    }

    #endregion
}
