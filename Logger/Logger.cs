namespace Logger;

public static class Log
{
    // We go from Fatal up to Info because we first need to notify user about    //
    // Fatal Errors and Errors, then Warnings and only then we should print info //
    public enum Level
    {
        Disabled = 0,
        FatalError = 1,
        Error = 2,
        Warning = 3,
        Info = 4,
    }
    public static Level LoggerLevel { get; set; } = Level.Info; // Let the user choose the desired LogLevel before any engine stuff;

    public static void Write(string message, Level requiredLogLevel = Level.Info, string end = "\n")
    {
        if (LoggerLevel >= requiredLogLevel)
        {
            Console.Write(message + end);
        }
    }
    public static void InfoDateless(string message)
    {
        Write($"[INFO] {message}", Level.Info);
    }
    public static void Info(string message)
    {
        Write($"[{DateTime.Now:HH:mm:ss}] [INFO] {message}", Level.Info);
    }
    public static void Warning(string message)
    {
        Write($"[{DateTime.Now:HH:mm:ss}] [WARNING] {message}", Level.Warning);
    }


    public static void Error(string message, Exception? ex = null)
    {
        Write($"[{DateTime.Now:HH:mm:ss}] [ERROR] {message}", Level.Error);
        if (ex != null)
        {
            Console.WriteLine($"Exception: {ex.GetType().Name} {ex.Message}");
        }
    }

    public static void FatalError(string message, Exception? ex = null)
    {
        Write($"[{DateTime.Now:HH:mm:ss}] [FATAL_ERROR] {message}", Level.FatalError);
        if (ex != null)
        {
            Console.WriteLine($"Exception: {ex.GetType().Name} {ex.Message}");
        }
    }
}
