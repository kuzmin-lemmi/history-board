using System.IO;
using System.Text;

namespace HistoryBoard;

// Unexpected errors are written to Data\logs, so a problem reported from a classroom can be traced.
// The log never contains lesson content: only the time, the version, the place and the exception.
public static class CrashLog
{
    public const long MaxLogBytes = 1024 * 1024;
    private static readonly object Gate = new();
    public static string LogDirectory => Path.Combine(App.DataDirectory, "logs");
    public static string LogPath => Path.Combine(LogDirectory, "errors.log");

    public static void Write(string context, Exception error)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                // Keep one previous log, so the folder never grows beyond two megabytes.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes) File.Move(LogPath, LogPath + ".old", true);
                var entry = new StringBuilder()
                    .Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ")
                    .Append("версия ").Append(UpdateService.DisplayVersion).Append(" · ").AppendLine(context)
                    .AppendLine(error.ToString()).AppendLine();
                File.AppendAllText(LogPath, entry.ToString());
            }
        }
        catch { /* Logging must never cause a second failure. */ }
    }
}
