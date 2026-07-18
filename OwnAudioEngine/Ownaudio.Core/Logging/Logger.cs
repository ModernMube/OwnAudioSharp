using System;

namespace Logger;

/// <summary>
/// Dead simple console logger. Set LoggerLevel before spinning up the engine.
/// </summary>
public static class Log
{
    /// <summary>
    /// How chatty we are. Disabled shuts it up completely.
    /// </summary>
    public enum Level
    {
        /// <summary></summary>
        Disabled = 0,
        /// <summary></summary>
        FatalError = 1,
        /// <summary></summary>
        Error = 2,
        /// <summary></summary>
        Warning = 3,
        /// <summary></summary>
        Info = 4,
    }

    /// <summary>
    /// Anything above this level gets swallowed.
    /// </summary>
    public static Level LoggerLevel { get; set; } = Level.Info;

    private static void _write(string message, Level requiredLogLevel = Level.Info, string end = "\n")
    {
        if (LoggerLevel >= requiredLogLevel) { Console.Write(message + end); }
    }

    private static void _debugWrite(string message)
    {
        #if DEBUG
        System.Diagnostics.Debug.WriteLine(message);
        #endif
    }

    /// <summary>
    /// Info line without the timestamp, for banners and such.
    /// </summary>
    public static void InfoDateless(string message)
    {
        _write($"[INFO] {message}", Level.Info);
    }

    /// <summary></summary>
    public static void Info(string message)
    {
        _write($"[{DateTime.Now:HH:mm:ss}] [INFO] {message}", Level.Info);
    }

    /// <summary></summary>
    public static void Warning(string message)
    {
        _write($"[{DateTime.Now:HH:mm:ss}] [WARNING] {message}", Level.Warning);
    }

    /// <summary>
    /// Error line, plus the exception type and message when one is passed.
    /// </summary>
    public static void Error(string message, Exception? ex = null)
    {
        _write($"[{DateTime.Now:HH:mm:ss}] [ERROR] {message}", Level.Error);
        if (ex != null) { Console.WriteLine($"Exception: {ex.GetType().Name} {ex.Message}"); }
    }

    /// <summary>
    /// Same as Error, just louder.
    /// </summary>
    public static void FatalError(string message, Exception? ex = null)
    {
        _write($"[{DateTime.Now:HH:mm:ss}] [FATAL_ERROR] {message}", Level.FatalError);
        if (ex != null) { Console.WriteLine($"Exception: {ex.GetType().Name} {ex.Message}"); }
    }

    /// <summary>
    /// Only shows up in DEBUG builds.
    /// </summary>
    public static void Debug(string message)
    {
        _debugWrite($"[{DateTime.Now:HH:mm:ss}] {message}");
    }
}
