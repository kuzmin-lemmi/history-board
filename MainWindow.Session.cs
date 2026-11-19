using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

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
    // Used by the self-test runner, which must never wait for a dialog.
    internal void CloseWithoutPrompt() { closing = true; autosaveTimer.Stop(); }

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

    // Written synchronously after an unexpected error. The text being typed is already part of the
    // lesson, so nothing needs to be committed first. A regular autosave may hold the gate; the
    // lesson file is written through a temporary copy, so the two cannot damage each other.
    internal bool EmergencySave()
    {
        var gate = saveGate.Wait(0);
        try
        {
            StoreCamera();
            var snapshot = lesson.Clone();
            LessonFile.Save(AutosavePath, snapshot, UsedAssets(snapshot), true);
            // A caption being typed has not been counted as a change yet, but it is unsaved work.
            WriteSessionInfo(currentPath, HasUnsavedChanges || editing != null);
            if (editing == null) { autosavedRevision = revision; autosavedViewRevision = viewRevision; }
            return true;
        }
        catch (Exception ex) { CrashLog.Write("Аварийное сохранение", ex); return false; }
        finally { if (gate) saveGate.Release(); }
    }

    // ---- Files from a second start (double click on a lesson while the board is open) ----
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    private string? pendingFile;
    private DispatcherTimer? pendingFileTimer;
    private bool openingPendingFile;

    internal void ReceiveFromAnotherStart(string path)
    {
        if (closing) return;
        BringToFront();
        if (path.Length == 0) return;
        pendingFile = path;
        OpenPendingFile();
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = fullscreen ? WindowState.Maximized : WindowState.Normal;
        Activate(); Topmost = true; Topmost = false;
    }

    // An open dialog (a question, a file dialog, settings) disables the board; the file waits for it,
    // so a lesson never replaces another one in the middle of "Save changes?".
    internal bool BoardBusy => !IsWindowEnabled(new WindowInteropHelper(this).Handle) || openingPendingFile || exportingPdf;

    private async void OpenPendingFile()
    {
        if (pendingFile is not { } path || closing) return;
        // The library is only a starting point: a lesson opened from Explorer replaces it.
        foreach (var library in OwnedWindows.OfType<LibraryWindow>().ToList()) library.Close();
        if (BoardBusy)
        {
            Status.Text = $"Закройте открытое окно — затем откроется «{Path.GetFileName(path)}».";
            if (pendingFileTimer == null)
            {
                pendingFileTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                pendingFileTimer.Tick += (_, _) => OpenPendingFile();
            }
            pendingFileTimer.Start(); return;
        }
        pendingFileTimer?.Stop(); pendingFile = null;
        if (presenting) EndPresentation();
        if (currentPath != null && string.Equals(Path.GetFullPath(currentPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            Status.Text = HasUnsavedChanges ? $"●  «{Path.GetFileName(path)}» уже открыт, несохранённые изменения на месте" : $"●  «{Path.GetFileName(path)}» уже открыт";
            return;
        }
        openingPendingFile = true;
        try { await OpenLessonAsync(path); }
        finally { openingPendingFile = false; }
    }

    // ---- Earlier versions: the previous autosave and the copies kept every ten minutes ----
    internal sealed record AutosaveVersion(string Path, DateTime Saved, string Title, int Objects, int Frames, bool Previous)
    {
        public string Label => $"{Saved:dd.MM.yyyy HH:mm} · «{Title}» · объектов: {Objects}, кадров: {Frames}{(Previous ? " · предыдущее автосохранение" : "")}";
    }

    internal List<AutosaveVersion> ListEarlierVersions()
    {
        var versions = new List<AutosaveVersion>();
        // The current autosave is the lesson on the board; only older copies are offered.
        foreach (var file in AutosaveCandidates().Skip(1))
        {
            try
            {
                if (!File.Exists(file)) continue;
                var lessonInfo = LessonFile.Peek(file);
                versions.Add(new AutosaveVersion(file, File.GetLastWriteTime(file), lessonInfo.Title, lessonInfo.Items.Count, lessonInfo.Frames.Count, file == LessonFile.BackupPath(AutosavePath)));
            }
            catch { /* A damaged copy is simply not offered. */ }
        }
        return versions.OrderByDescending(v => v.Saved).ToList();
    }

    private async Task RestoreEarlierVersionAsync()
    {
        if (presenting) return;
        CommitEditor(); FinishGesture();
        Status.Text = "Ищем более ранние версии…";
        var versions = await Task.Run(ListEarlierVersions);
        if (versions.Count == 0)
        {
            Status.Text = "●  Более ранних версий пока нет";
            MessageBox.Show(this, "Более ранних версий пока нет. Доска сохраняет копию урока каждые 10 минут работы, последние 10 копий.", "Более ранние версии", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var window = new Window { Owner = this, Title = "Более ранние версии", Width = 620, Height = 440, MinWidth = 460, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = BoardSurface.Brush("#FAF7EF") };
        var root = new DockPanel { Margin = new Thickness(20) };
        var hint = new TextBlock { Text = "Доска сохраняет копию урока каждые 10 минут. Выбранная версия откроется как несохранённый урок: текущий урок можно сначала сохранить.", TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = BoardSurface.Brush("#56665B"), Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        var open = new Button { Content = "Открыть версию", IsDefault = true, IsEnabled = false, Padding = new Thickness(14, 7, 14, 7), Background = BoardSurface.Brush("#58725B"), Foreground = Brushes.White };
        buttons.Children.Add(cancel); buttons.Children.Add(open);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var list = new ListBox { ItemsSource = versions, DisplayMemberPath = nameof(AutosaveVersion.Label), FontSize = 13, Background = Brushes.White };
        list.SelectionChanged += (_, _) => open.IsEnabled = list.SelectedItem != null;
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem != null) window.DialogResult = true; };
        open.Click += (_, _) => window.DialogResult = true;
        root.Children.Add(list); window.Content = root;
        window.Loaded += (_, _) => { list.SelectedIndex = 0; list.Focus(); };
        Status.Text = "";
        if (window.ShowDialog() == true && list.SelectedItem is AutosaveVersion chosen) await OpenEarlierVersionAsync(chosen);
    }

    internal async Task<bool> OpenEarlierVersionAsync(AutosaveVersion version)
    {
        if (!await ConfirmReplace()) return false;
        try
        {
            var result = await Task.Run(() => LessonFile.Load(version.Path));
            StopCameraAnimation(); lesson = result.Lesson; assets = result.Assets; undo.Clear(); redo.Clear(); activeFrame = -1;
            // The copy may belong to any lesson file, so it is never written back to one silently.
            currentPath = null; revision++; savedRevision = revision - 1;
            Surface.SetLesson(lesson, assets); LessonTitle.Text = lesson.Title;
            UpdateUi(); RestoreCamera(); ScheduleAutosave();
            Status.Text = $"●  Открыта версия от {version.Saved:dd.MM HH:mm}. Сохраните её в файл, если она нужна.";
            return true;
        }
        catch (Exception ex) { Error("Не удалось открыть версию", ex); return false; }
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
