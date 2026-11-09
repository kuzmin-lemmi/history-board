using System.IO;
using System.Text.Json;
using System.Windows;

namespace HistoryBoard;

// Stored next to the autosave: which lesson file the autosave belongs to and whether it holds
// changes that were never written to that file.
public sealed record AutosaveSession(string? SourcePath, bool Dirty, DateTime? SourceWriteTimeUtc);

public partial class MainWindow
{
    private const int AutosaveHistoryLimit = 10;
    private static readonly TimeSpan AutosaveHistoryInterval = TimeSpan.FromMinutes(10);
    private string SessionPath => Path.Combine(App.DataDirectory, "last-lesson.json");
    private string AutosaveHistoryDirectory => Path.Combine(App.DataDirectory, "autosave-history");
    private bool HasUnsavedChanges => revision != savedRevision;

    private static DateTime? WriteTime(string? path)
    {
        try { return path != null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null; }
        catch { return null; }
    }

    private void WriteSessionInfo(string? source, bool dirty)
    {
        try
        {
            Directory.CreateDirectory(App.DataDirectory);
            var temporary = SessionPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new AutosaveSession(source, dirty, WriteTime(source))));
            File.Move(temporary, SessionPath, true);
        }
        catch { /* The autosave itself is still valid; only the link to the source file is missing. */ }
    }

    internal AutosaveSession? ReadSessionInfo()
    {
        try
        {
            if (!File.Exists(SessionPath) || new FileInfo(SessionPath).Length > 64 * 1024) return null;
            return JsonSerializer.Deserialize<AutosaveSession>(File.ReadAllText(SessionPath));
        }
        catch { return null; }
    }

    private void KeepAutosaveHistory()
    {
        try
        {
            if (!File.Exists(AutosavePath)) return;
            Directory.CreateDirectory(AutosaveHistoryDirectory);
            var copies = new DirectoryInfo(AutosaveHistoryDirectory).GetFiles("*.hboard").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
            if (copies.Count > 0 && DateTime.UtcNow - copies[0].LastWriteTimeUtc < AutosaveHistoryInterval) return;
            var name = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".hboard";
            File.Copy(AutosavePath, Path.Combine(AutosaveHistoryDirectory, name), true);
            foreach (var old in new DirectoryInfo(AutosaveHistoryDirectory).GetFiles("*.hboard").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(AutosaveHistoryLimit))
                old.Delete();
        }
        catch { /* History is a convenience; the main autosave has already been written. */ }
    }

    // Newest first: the autosave, its backup from the previous autosave, then the periodic copies.
    private IEnumerable<string> AutosaveCandidates()
    {
        yield return AutosavePath;
        yield return LessonFile.BackupPath(AutosavePath);
        if (!Directory.Exists(AutosaveHistoryDirectory)) yield break;
        foreach (var file in new DirectoryInfo(AutosaveHistoryDirectory).GetFiles("*.hboard").OrderByDescending(f => f.Name, StringComparer.Ordinal))
            yield return file.FullName;
    }

    internal void RestoreAutosave()
    {
        var session = ReadSessionInfo();
        var source = session?.SourcePath is { } path && File.Exists(path) ? path : null;
        // The lesson file was saved elsewhere after the autosave (for example on another computer):
        // with no unsaved changes here, the file itself is the newest version.
        if (source != null && session is { Dirty: false } && WriteTime(source) != session.SourceWriteTimeUtc && TryRestore(source, source, false, false)) return;
        var first = true;
        foreach (var candidate in AutosaveCandidates())
        {
            if (File.Exists(candidate) && TryRestore(candidate, source, session?.Dirty == true || !first, !first)) return;
            first = false;
        }
        if (File.Exists(AutosavePath)) Status.Text = "Последний урок не восстановлен. Можно открыть сохранённый файл.";
    }

    private bool TryRestore(string file, string? source, bool dirty, bool fromBackup)
    {
        try
        {
            var result = LessonFile.Load(file);
            lesson = result.Lesson; assets = result.Assets; currentPath = source;
            undo.Clear(); redo.Clear(); activeFrame = -1;
            revision++; savedRevision = dirty ? revision - 1 : revision;
            Surface.SetLesson(lesson, assets); LessonTitle.Text = lesson.Title;
            RestoreCamera(); autosavedRevision = fromBackup ? -1 : revision; autosavedViewRevision = viewRevision; UpdateUi();
            Status.Text = fromBackup ? "●  Последний урок восстановлен из резервной копии автосохранения"
                : dirty ? "●  Восстановлен последний урок с несохранёнными изменениями" : "●  Восстановлен последний урок";
            if (fromBackup) ScheduleAutosave();
            return true;
        }
        catch { return false; }
    }

    private async Task<bool> ConfirmCloseAsync()
    {
        if (!HasUnsavedChanges) return true;
        var message = currentPath != null
            ? $"Сохранить изменения в файле «{Path.GetFileName(currentPath)}»?\n\n«Нет» — файл останется прежним, а изменения сохранятся в автосохранении и откроются при следующем запуске как несохранённые."
            : "Сохранить урок в файл?\n\n«Нет» — урок останется в автосохранении и откроется при следующем запуске.";
        var result = MessageBox.Show(this, message, "Доска истории", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.No || result == MessageBoxResult.Yes && await SaveLessonAsync();
    }

    private async Task<bool> OpenBackupAfterFailureAsync(string path, Exception error)
    {
        var backup = LessonFile.BackupPath(path);
        if (!File.Exists(backup)) return false;
        var answer = MessageBox.Show(this, $"{error.Message}\n\nЕсть резервная копия предыдущего сохранения этого урока. Открыть её?", "Не удалось открыть урок", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return true;
        try
        {
            var result = await Task.Run(() => LessonFile.Load(backup));
            StopCameraAnimation(); lesson = result.Lesson; assets = result.Assets; currentPath = path; undo.Clear(); redo.Clear(); activeFrame = -1;
            revision++; savedRevision = revision - 1; Surface.SetLesson(lesson, assets); LessonTitle.Text = lesson.Title;
            UpdateUi(); RestoreCamera(); ScheduleAutosave();
            Status.Text = "●  Открыта резервная копия. Нажмите «Сохранить», чтобы заменить повреждённый файл.";
        }
        catch (Exception ex) { Error("Не удалось открыть резервную копию", ex); }
        return true;
    }
}
