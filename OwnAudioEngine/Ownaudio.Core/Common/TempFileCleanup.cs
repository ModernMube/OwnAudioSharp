using System;
using System.IO;
using System.Threading;
using Logger;

namespace Ownaudio.Core.Common;

/// <summary>
/// Clears the temp files a killed process left behind. Nothing deletes them otherwise:
/// the owning dispose or finally never ran.
/// </summary>
public static class TempFileCleanup
{
    /// <summary>
    /// Orphans younger than this may still belong to a live decode, so they stay.
    /// </summary>
    private static readonly TimeSpan MinAge = TimeSpan.FromHours(24);

    private static int _swept;

    /// <summary>
    /// Sweeps the stream-decode spills and the matchering work directories, once per process.
    /// </summary>
    public static void SweepOnce()
    {
        if (Interlocked.Exchange(ref _swept, 1) != 0) { return; }

        int _removed = _sweep("ownaudio_stream_*", directories: false)
            + _sweep("enhanced_preset_*", directories: true);

        if (_removed > 0)
            Log.Info($"[TempCleanup] Removed {_removed} orphaned temp entries older than {MinAge.TotalHours:F0}h");
    }

    private static int _sweep(string pattern, bool directories)
    {
        string _temp = Path.GetTempPath();
        DateTime _cutoff = DateTime.UtcNow - MinAge;
        int _removed = 0;

        string[] _entries;
        try
        {
            _entries = directories
                ? Directory.GetDirectories(_temp, pattern)
                : Directory.GetFiles(_temp, pattern);
        }
        catch (Exception ex)
        {
            Log.Warning($"[TempCleanup] Could not enumerate '{pattern}' in the temp directory: {ex.Message}");
            return 0;
        }

        foreach (string entry in _entries)
        {
            try
            {
                DateTime _touched = directories
                    ? Directory.GetLastWriteTimeUtc(entry)
                    : File.GetLastWriteTimeUtc(entry);

                if (_touched >= _cutoff) { continue; }

                if (directories) { Directory.Delete(entry, recursive: true); }
                else { File.Delete(entry); }

                _removed++;
            }
            catch { }
        }

        return _removed;
    }
}
