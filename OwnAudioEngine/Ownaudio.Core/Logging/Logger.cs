using System;
using System.Text;

namespace Logger;

/// <summary>
/// Dead simple logger. Off by default — turn it on through the logLevel argument
/// of OwnaudioNet.Initialize, or set LoggerLevel here before spinning up the engine.
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
    /// Anything above this level gets swallowed. Disabled out of the box so a host app
    /// never gets console spam it did not ask for.
    /// </summary>
    public static Level LoggerLevel { get; set; } = Level.Disabled;

    /// <summary>
    /// Every line that passes LoggerLevel, for the host to route into its own logging.
    /// Subscribing turns the console fallback off; a throwing handler is ignored.
    /// </summary>
    public static event Action<Level, string>? Sink;

    private static readonly object _fileLock = new object();
    private static LogFileWriter? _file;

    [ThreadStatic]
    private static bool _dispatching;

    /// <summary>
    /// Starts writing to a self-rotating file, capped at roughly
    /// maxFileSizeKb * (keepFiles + 1) on disk. Off unless you call this.
    /// </summary>
    /// <param name="path">null for OwnAudio/logs/ownaudio.log under local application data</param>
    /// <param name="maxFileSizeKb">rotation threshold, clamped to 64..1048576</param>
    /// <param name="keepFiles">rotated files kept, clamped to 0..20</param>
    /// <param name="maxAgeDays">rotated files older than this are pruned at open, 0 to keep by count alone</param>
    /// <returns>The file being written, or null when it could not be opened.</returns>
    public static string? ToFile(string? path = null, int maxFileSizeKb = 2048, int keepFiles = 3, int maxAgeDays = 30)
    {
        lock (_fileLock)
        {
            _file?.Dispose();
            _file = LogFileWriter.TryOpen(path, maxFileSizeKb, keepFiles, maxAgeDays, out string? _error);

            if (_file is null)
            {
                _write($"[{DateTime.Now:HH:mm:ss}] [ERROR] [Log] File logging could not start: {_error}", Level.Error);
                return null;
            }

            return _file.FilePath;
        }
    }

    /// <summary>
    /// Stops file logging. Sink and the console are unaffected.
    /// </summary>
    public static void CloseFile()
    {
        lock (_fileLock)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    /// <summary>
    /// The log file being written, null when file logging is off.
    /// </summary>
    public static string? FilePath
    {
        get { lock (_fileLock) return _file?.FilePath; }
    }

    private static void _write(string message, Level requiredLogLevel = Level.Info)
    {
        if (LoggerLevel < requiredLogLevel) { return; }

        _dispatch(requiredLogLevel, message);
    }

    private static void _dispatch(Level level, string message)
    {
        if (_dispatching) { return; }

        _dispatching = true;
        try
        {
            bool _consumed = false;

            lock (_fileLock)
            {
                if (_file is not null)
                {
                    _file.Write(message);
                    _consumed = true;
                }
            }

            Action<Level, string>? _sink = Sink;
            if (_sink is not null)
            {
                _consumed = true;
                try { _sink(level, message); }
                catch { }
            }

            if (!_consumed) { Console.WriteLine(message); }
        }
        finally
        {
            _dispatching = false;
        }
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
    /// Error line, plus the exception chain and its stack when one is passed.
    /// </summary>
    public static void Error(string message, Exception? ex = null)
    {
        _write(_compose("ERROR", message, ex), Level.Error);
    }

    /// <summary>
    /// Same as Error, just louder.
    /// </summary>
    public static void FatalError(string message, Exception? ex = null)
    {
        _write(_compose("FATAL_ERROR", message, ex), Level.FatalError);
    }

    private static string _compose(string tag, string message, Exception? ex)
    {
        string _head = $"[{DateTime.Now:HH:mm:ss}] [{tag}] {message}";
        if (ex is null) { return _head; }

        var _text = new StringBuilder(_head);
        for (Exception? _e = ex; _e is not null; _e = _e.InnerException)
        {
            _text.Append(Environment.NewLine).Append("    ").Append(_e.GetType().Name).Append(": ").Append(_e.Message);
        }

        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            _text.Append(Environment.NewLine).Append(ex.StackTrace);
        }

        return _text.ToString();
    }

    /// <summary>
    /// Only shows up in DEBUG builds.
    /// </summary>
    public static void Debug(string message)
    {
        _debugWrite($"[{DateTime.Now:HH:mm:ss}] {message}");
    }
}
