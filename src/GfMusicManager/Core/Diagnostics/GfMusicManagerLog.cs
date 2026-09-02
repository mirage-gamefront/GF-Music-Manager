using System.Text;

namespace GfMusicManager.Core.Diagnostics;

/// <summary>
/// Small, dependency-free file logger used by the desktop app and its core services.
/// Logging must never be allowed to break the music manager itself.
/// </summary>
public static class GfMusicManagerLog
{
    private static readonly object SyncRoot = new();
    private static string? _logPath;
    private static bool _fileLoggingEnabled;

    public static bool FileLoggingEnabled
    {
        get
        {
            lock (SyncRoot)
            {
                return _fileLoggingEnabled;
            }
        }
    }

    public static string LogPath
    {
        get
        {
            lock (SyncRoot)
            {
                EnsureInitializedCore();
                return _logPath ?? string.Empty;
            }
        }
    }

    public static void SetFileLoggingEnabled(bool enabled)
    {
        lock (SyncRoot)
        {
            if (_fileLoggingEnabled == enabled)
            {
                return;
            }

            _fileLoggingEnabled = enabled;
            _logPath = null;
            if (enabled)
            {
                EnsureInitializedCore();
            }
        }
    }

    public static void Initialize()
    {
        lock (SyncRoot)
        {
            EnsureInitializedCore();
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warning(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Exception(string operation, Exception exception) =>
        Write("EXCEPTION", $"{operation}: {exception}");

    private static void Write(string level, string message)
    {
        try
        {
            var line = string.Join(
                " ",
                DateTimeOffset.Now.ToString("O"),
                $"[{level}]",
                $"[PID:{Environment.ProcessId}]",
                $"[TID:{Environment.CurrentManagedThreadId}]",
                message.Replace(Environment.NewLine, " ", StringComparison.Ordinal));

            lock (SyncRoot)
            {
                if (!_fileLoggingEnabled)
                {
                    return;
                }

                EnsureInitializedCore();
                if (_logPath is null)
                {
                    return;
                }

                using var stream = new FileStream(
                    _logPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.WriteLine(line);
            }
        }
        catch
        {
            // Never replace the original failure with a logging failure.
        }
    }

    private static void EnsureInitializedCore()
    {
        if (!_fileLoggingEnabled || _logPath is not null)
        {
            return;
        }

        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GF Music Manager",
                "logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(
                logDirectory,
                $"gf-music-manager-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");

            File.AppendAllText(
                logPath,
                $"{Environment.NewLine}===== GF Music Manager session {DateTimeOffset.Now:O} ====={Environment.NewLine}",
                Encoding.UTF8);
            _logPath = logPath;
        }
        catch
        {
            // Diagnostics must remain best-effort and must never prevent startup.
            _logPath = Path.Combine(
                Path.GetTempPath(),
                $"gf-music-manager-fallback-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
        }
    }
}
