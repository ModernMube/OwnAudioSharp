using System;
using System.IO;
using System.Threading;

namespace Ownaudio.Android.Common;

/// <summary>
/// Simple file-based logger for Android debugging.
/// Writes log messages to a text file in the app's external files directory.
/// </summary>
public static class FileLogger
{
    private static string? _logFilePath;
    private static readonly object _lock = new object();
    private static bool _initialized = false;

    /// <summary>
    /// Initializes the file logger. Must be called before logging.
    /// </summary>
    /// <param name="context">Android context to get external files directory.</param>
    public static void Initialize(global::Android.Content.Context context)
    {
        if (_initialized)
            return;

        try
        {
            // Use external files directory (doesn't require WRITE_EXTERNAL_STORAGE permission on Android 10+)
            var externalFilesDir = context.GetExternalFilesDir(null);
            if (externalFilesDir != null)
            {
                _logFilePath = Path.Combine(externalFilesDir.AbsolutePath, "ownaudio_debug.log");

                // Clear old log file
                if (File.Exists(_logFilePath))
                {
                    File.Delete(_logFilePath);
                }

                _initialized = true;

                // Write header
                WriteToFile($"=== OwnAudio Debug Log ===");
                WriteToFile($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                WriteToFile($"Log file: {_logFilePath}");
                WriteToFile($"========================================\n");

                global::Android.Util.Log.Info("FileLogger", $"Log file created: {_logFilePath}");
            }
            else
            {
                global::Android.Util.Log.Error("FileLogger", "External files directory is null!");
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("FileLogger", $"Failed to initialize: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the current log file path.
    /// </summary>
    public static string? LogFilePath => _logFilePath;

    /// <summary>
    /// Logs a debug message to file and logcat.
    /// </summary>
    public static void Debug(string tag, string message)
    {
        Log("DEBUG", tag, message);
        global::Android.Util.Log.Debug(tag, message);
    }

    /// <summary>
    /// Logs an info message to file and logcat.
    /// </summary>
    public static void Info(string tag, string message)
    {
        Log("INFO", tag, message);
        global::Android.Util.Log.Info(tag, message);
    }

    /// <summary>
    /// Logs an error message to file and logcat.
    /// </summary>
    public static void Error(string tag, string message)
    {
        Log("ERROR", tag, message);
        global::Android.Util.Log.Error(tag, message);
    }

    /// <summary>
    /// Logs a warning message to file and logcat.
    /// </summary>
    public static void Warn(string tag, string message)
    {
        Log("WARN", tag, message);
        global::Android.Util.Log.Warn(tag, message);
    }

    /// <summary>
    /// Internal logging method.
    /// </summary>
    private static void Log(string level, string tag, string message)
    {
        if (!_initialized || string.IsNullOrEmpty(_logFilePath))
            return;

        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        string logLine = $"[{timestamp}] [{level}] [{tag}] {message}";

        WriteToFile(logLine);
    }

    /// <summary>
    /// Writes a line to the log file (thread-safe).
    /// </summary>
    private static void WriteToFile(string line)
    {
        if (string.IsNullOrEmpty(_logFilePath))
            return;

        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
            catch
            {
                // Ignore write errors to prevent crashes
            }
        }
    }

    /// <summary>
    /// Reads and returns the entire log file contents.
    /// </summary>
    public static string ReadLog()
    {
        if (!_initialized || string.IsNullOrEmpty(_logFilePath))
            return "Log not initialized";

        try
        {
            if (File.Exists(_logFilePath))
            {
                return File.ReadAllText(_logFilePath);
            }
            return "Log file not found";
        }
        catch (Exception ex)
        {
            return $"Error reading log: {ex.Message}";
        }
    }

    /// <summary>
    /// Clears the log file.
    /// </summary>
    public static void Clear()
    {
        if (!_initialized || string.IsNullOrEmpty(_logFilePath))
            return;

        lock (_lock)
        {
            try
            {
                if (File.Exists(_logFilePath))
                {
                    File.Delete(_logFilePath);
                }

                WriteToFile($"=== OwnAudio Debug Log ===");
                WriteToFile($"Cleared: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                WriteToFile($"========================================\n");
            }
            catch
            {
                // Ignore errors
            }
        }
    }
}
