using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Logger;

/// <summary>
/// Size-capped log file that rotates into numbered siblings and prunes the old ones,
/// so the log can never grow without bound.
/// </summary>
internal sealed class LogFileWriter : IDisposable
{
    private const int MinFileSizeKb = 64;
    private const int MaxFileSizeKb = 1024 * 1024;
    private const int MaxKeepFiles = 20;

    private readonly string _path;
    private readonly string _directory;
    private readonly string _stem;
    private readonly string _extension;
    private readonly long _maxBytes;
    private readonly int _keepFiles;

    private StreamWriter? _writer;
    private long _bytes;

    private LogFileWriter(string path, long maxBytes, int keepFiles)
    {
        _path = path;
        _directory = Path.GetDirectoryName(path)!;
        _stem = Path.GetFileNameWithoutExtension(path);
        _extension = Path.GetExtension(path);
        _maxBytes = maxBytes;
        _keepFiles = keepFiles;
    }

    /// <summary>
    /// The file being written.
    /// </summary>
    internal string FilePath => _path;

    /// <summary>
    /// Prunes what is stale, opens the live file for append and reports why if it cannot.
    /// </summary>
    internal static LogFileWriter? TryOpen(
        string? path, int maxFileSizeKb, int keepFiles, int maxAgeDays, out string? error)
    {
        error = null;

        try
        {
            string _target = string.IsNullOrWhiteSpace(path) ? _defaultPath() : Path.GetFullPath(path);
            long _maxBytes = Math.Clamp(maxFileSizeKb, MinFileSizeKb, MaxFileSizeKb) * 1024L;
            int _keep = Math.Clamp(keepFiles, 0, MaxKeepFiles);

            Directory.CreateDirectory(Path.GetDirectoryName(_target)!);

            var _file = new LogFileWriter(_target, _maxBytes, _keep);
            _file._prune(maxAgeDays);
            _file._open();

            return _file;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Appends one line, rotating first when it would push the file over the cap.
    /// A failing write stops file logging rather than throwing at the caller.
    /// </summary>
    internal void Write(string message)
    {
        if (_writer is null) { return; }

        try
        {
            long _size = Encoding.UTF8.GetByteCount(message) + Environment.NewLine.Length;
            if (_bytes > 0 && _bytes + _size > _maxBytes) { _rotate(); _stamp(); }

            _writer!.WriteLine(message);
            _bytes += _size;
        }
        catch
        {
            Dispose();
        }
    }

    /// <summary></summary>
    public void Dispose()
    {
        try { _writer?.Dispose(); }
        catch { }

        _writer = null;
    }

    private static string _defaultPath()
    {
        string _root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(_root)) { _root = Path.GetTempPath(); }

        return Path.Combine(_root, "OwnAudio", "logs", "ownaudio.log");
    }

    private void _open()
    {
        var _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _bytes = _stream.Length;
        _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true };

        if (_bytes >= _maxBytes) { _rotate(); }

        _stamp();
    }

    /// <summary>
    /// Dates the run, since the lines themselves only carry the clock.
    /// </summary>
    private void _stamp()
    {
        Write($"===== log opened {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
    }

    private void _rotate()
    {
        _writer?.Dispose();
        _writer = null;

        try
        {
            if (_keepFiles == 0)
            {
                _delete(_path);
            }
            else
            {
                _delete(_rotatedPath(_keepFiles));

                for (int i = _keepFiles - 1; i >= 1; i--)
                    _move(_rotatedPath(i), _rotatedPath(i + 1));

                _move(_path, _rotatedPath(1));
            }
        }
        catch { }

        var _stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        _bytes = 0;
        _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>
    /// Drops rotated files past the keep count and, when maxAgeDays is set, the ones that
    /// outlived it. Runs at open, because a crashed process never cleans up on the way out.
    /// </summary>
    private void _prune(int maxAgeDays)
    {
        DateTime _cutoff = DateTime.UtcNow.AddDays(-Math.Max(0, maxAgeDays));

        foreach ((int index, string file) in _rotatedFiles())
        {
            if (index > _keepFiles)
            {
                _delete(file);
                continue;
            }

            if (maxAgeDays > 0 && File.GetLastWriteTimeUtc(file) < _cutoff)
                _delete(file);
        }
    }

    private List<(int Index, string File)> _rotatedFiles()
    {
        var _found = new List<(int, string)>();

        string[] _candidates;
        try { _candidates = Directory.GetFiles(_directory, _stem + ".*" + _extension); }
        catch { return _found; }

        foreach (string file in _candidates)
        {
            if (string.Equals(file, _path, StringComparison.OrdinalIgnoreCase)) { continue; }

            string _name = Path.GetFileName(file);
            if (_name.Length <= _stem.Length + 1 + _extension.Length) { continue; }

            string _middle = _name.Substring(_stem.Length + 1, _name.Length - _stem.Length - 1 - _extension.Length);
            if (int.TryParse(_middle, NumberStyles.None, CultureInfo.InvariantCulture, out int _index))
                _found.Add((_index, file));
        }

        return _found;
    }

    private string _rotatedPath(int index) =>
        Path.Combine(_directory, $"{_stem}.{index.ToString(CultureInfo.InvariantCulture)}{_extension}");

    private static void _delete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    private static void _move(string from, string to)
    {
        try
        {
            if (File.Exists(from)) { File.Move(from, to, overwrite: true); }
        }
        catch { }
    }
}
