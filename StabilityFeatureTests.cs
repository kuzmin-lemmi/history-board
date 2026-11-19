using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace HistoryBoard;

public partial class MainWindow
{
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window, bool enable);

    private async Task RunStabilityFeatureTests(string directory, Action<bool, string> check)
    {
        var folder = Path.Combine(directory, "stability-tests");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);

        // Saving checks the same rules as opening.
        var longText = new Lesson { Items = [new BoardItem { Text = new string('я', LessonFile.MaxTextLength + 1) }] };
        var longPath = Path.Combine(folder, "long-text.hboard");
        string? textError = null;
        try { LessonFile.Save(longPath, longText, new Dictionary<string, byte[]>()); } catch (InvalidDataException ex) { textError = ex.Message; }
        check(textError?.Contains("Надпись длиннее") == true && !File.Exists(longPath) && !Directory.EnumerateFiles(folder, "*.tmp").Any(),
            "Saving refuses a caption that the lesson file could not reopen, explains why and leaves no file");
        var longStroke = new Lesson { Items = [new BoardItem { Kind = "pen", Points = Enumerable.Range(0, LessonFile.MaxPoints + 1).Select(n => new BoardPoint(n, 0)).ToList() }] };
        string? strokeError = null;
        try { LessonFile.Save(longPath, longStroke, new Dictionary<string, byte[]>()); } catch (InvalidDataException ex) { strokeError = ex.Message; }
        check(strokeError?.Contains("точек") == true && !File.Exists(longPath), "Saving refuses a stroke with more points than a lesson file can reopen");
        var broken = new Lesson { Items = [new BoardItem { Width = double.NaN }] };
        var brokenRefused = false;
        try { LessonFile.Save(longPath, broken, new Dictionary<string, byte[]>()); } catch (InvalidDataException) { brokenRefused = true; }
        check(brokenRefused && !File.Exists(longPath), "Saving refuses malformed geometry instead of writing an unreadable lesson");
        check(Editor.MaxLength == LessonFile.MaxTextLength, "The caption editor cannot grow beyond the length a lesson file accepts");

        // Emergency save after an unexpected error keeps the text being typed.
        savedRevision = revision;
        SetTool(BoardTool.Text); BeginEditor(null, new Point(40, -900));
        Editor.Text = "Набирается в момент сбоя";
        var emergency = EmergencySave();
        var rescued = LessonFile.Load(AutosavePath).Lesson;
        check(emergency && rescued.Items.Any(i => i.Text == "Набирается в момент сбоя"), "Emergency save writes the autosave including the caption still being typed");
        CommitEditor();
        check(ReadSessionInfo()?.Dirty == true, "Emergency save marks the autosave as unsaved work for the next start");

        // The error log stays small and readable.
        var logError = new InvalidOperationException("Проверочная ошибка журнала");
        CrashLog.Write("Проверка", logError);
        check(File.Exists(CrashLog.LogPath) && File.ReadAllText(CrashLog.LogPath).Contains("Проверочная ошибка журнала"), "Unexpected errors are written to the log with their place and message");
        File.WriteAllBytes(CrashLog.LogPath, new byte[CrashLog.MaxLogBytes + 1]);
        CrashLog.Write("Проверка", logError);
        check(File.Exists(CrashLog.LogPath + ".old") && new FileInfo(CrashLog.LogPath).Length < 64 * 1024, "The error log keeps one previous file and never grows without limit");

        // A second start hands its file to the running board.
        var instanceData = Path.Combine(folder, "instance-data");
        var guard = SingleInstance.Acquire(instanceData, null);
        check(guard != null, "The first start becomes the board");
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        guard!.Listen(message => received.TrySetResult(message));
        var handedPath = Path.Combine(folder, "Урок из Проводника.hboard");
        var second = await Task.Run(() => SingleInstance.Acquire(instanceData, handedPath));
        var message = await Task.WhenAny(received.Task, Task.Delay(5000)) == received.Task ? received.Task.Result : null;
        check(second == null && message == handedPath, "A second start passes its lesson to the running board and exits instead of opening another window");
        var otherData = await Task.Run(() => { var other = SingleInstance.Acquire(Path.Combine(folder, "other-data"), null); var ok = other != null; other?.Dispose(); return ok; });
        check(otherData, "Boards with separate data folders do not block each other");
        guard.Dispose();
        var afterExit = await Task.Run(() => { var next = SingleInstance.Acquire(instanceData, null); var ok = next != null; next?.Dispose(); return ok; });
        check(afterExit, "After the board closes, the next start becomes the board");

        // The running board opens a handed file, but never in the middle of a dialog.
        var first = Path.Combine(folder, "Первый урок.hboard");
        var secondLesson = Path.Combine(folder, "Второй урок.hboard");
        LessonFile.Save(first, new Lesson { Title = "Первый урок", Items = [new BoardItem { Text = "Первый" }] }, new Dictionary<string, byte[]>());
        LessonFile.Save(secondLesson, new Lesson { Title = "Второй урок", Items = [new BoardItem { Text = "Второй" }] }, new Dictionary<string, byte[]>());
        savedRevision = revision;
        ReceiveFromAnotherStart(first);
        await WaitUntil(() => currentPath == first);
        check(currentPath == first && lesson.Title == "Первый урок", "A lesson handed from Explorer opens in the running board");
        ReceiveFromAnotherStart(first);
        check(currentPath == first && Status.Text.Contains("уже открыт"), "Handing the lesson that is already open keeps it, including unsaved changes");

        var handle = new WindowInteropHelper(this).Handle;
        EnableWindow(handle, false);
        ReceiveFromAnotherStart(secondLesson);
        await Task.Delay(500);
        var waited = currentPath == first && Status.Text.Contains("Закройте открытое окно");
        EnableWindow(handle, true);
        await WaitUntil(() => currentPath == secondLesson);
        check(waited && currentPath == secondLesson && lesson.Title == "Второй урок", "A handed lesson waits while a dialog is open and opens once it is closed");
        savedRevision = revision;

        // Closing a board with nothing left to save finishes on the first attempt and without an error.
        var board = new MainWindow(false) { ShowActivated = false };
        var closed = false; board.Closed += (_, _) => closed = true;
        board.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        board.autosavedRevision = board.revision; board.autosavedViewRevision = board.viewRevision;
        board.Close();
        await WaitUntil(() => closed);
        check(closed, "A board without unsaved changes closes on the first attempt");
        Activate();
    }

    private async Task WaitUntil(Func<bool> condition, int milliseconds = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(25);
    }
}
