using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HistoryBoard;

public partial class MainWindow
{
    private async Task RunReliabilityFeatureTests(string directory, Action<bool, string> check)
    {
        var folder = Path.Combine(directory, "reliability-tests");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);

        // Size limits: a lesson that Load would reject is never written.
        var hugePath = Path.Combine(folder, "huge.hboard");
        var huge = new Lesson();
        var hugeAssets = new Dictionary<string, byte[]>();
        for (var n = 0; n < 2; n++)
        {
            var id = Guid.NewGuid().ToString("N");
            hugeAssets[id] = new byte[70 * 1024 * 1024];
            huge.Items.Add(new BoardItem { Kind = "image", AssetId = id });
        }
        var refused = false;
        try { LessonFile.Save(hugePath, huge, hugeAssets); } catch (InvalidDataException) { refused = true; }
        check(refused && !File.Exists(hugePath) && !Directory.EnumerateFiles(folder, "*.tmp").Any(), "Saving refuses lessons whose images exceed what a lesson file can reopen, leaving no partial file");
        hugeAssets.Clear();

        var fakeId = Guid.NewGuid().ToString("N");
        var fakeItem = new BoardItem { Kind = "image", AssetId = fakeId };
        var free = LessonFile.MaxAssetBytes - LessonFile.AssetBytes(lesson, assets);
        assets[fakeId] = new byte[(int)Math.Max(1, free - 1024)];
        lesson.Items.Add(fakeItem);
        var blocked = false;
        try { EnsureAssetSpace(4096); } catch (InvalidDataException) { blocked = true; }
        lesson.Items.Remove(fakeItem); assets.Remove(fakeId);
        check(blocked, "Adding an image that would make the lesson too large to reopen is refused with an explanation");
        EnsureAssetSpace(4096);

        // Large maps are stored at the resolution the board can display.
        byte[] Png(int width, int height)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(BoardSurface.Brush("#E9EDDD"), null, new Rect(0, 0, width, height));
                for (var x = 0; x < width; x += 97) dc.DrawRectangle(BoardSurface.Brush(x % 2 == 0 ? "#58725B" : "#C94F49"), null, new Rect(x, 0, 40, height));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var memory = new MemoryStream(); encoder.Save(memory); return memory.ToArray();
        }
        var small = Png(400, 200);
        check(ReferenceEquals(LessonFile.CompactImage(small), small), "Images within the working resolution are stored unchanged");
        var wide = Png(6000, 300); var compact = LessonFile.CompactImage(wide);
        check(compact.Length <= wide.Length && LessonFile.DecodeImage(compact).PixelWidth == LessonFile.WorkingImageWidth, "Very wide maps are stored at the 3600-pixel working width");

        // Durable saving keeps the previous version next to the lesson.
        var lessonPath = Path.Combine(folder, "Урок с копией.hboard");
        var first = new Lesson { Title = "Первая версия" };
        LessonFile.Save(lessonPath, first, new Dictionary<string, byte[]>(), true);
        check(!File.Exists(LessonFile.BackupPath(lessonPath)), "The first save of a lesson does not create an empty backup");
        LessonFile.Save(lessonPath, new Lesson { Title = "Вторая версия" }, new Dictionary<string, byte[]>(), true);
        check(LessonFile.Load(lessonPath).Lesson.Title == "Вторая версия" && LessonFile.Load(LessonFile.BackupPath(lessonPath)).Lesson.Title == "Первая версия", "Saving over a lesson keeps the previous version as a .bak backup");

        // Closing and reopening: the autosave remembers its lesson file and unsaved changes.
        var beforeTitle = lesson.Clone();
        var sessionFile = Path.Combine(folder, "Сеанс.hboard");
        currentPath = sessionFile;
        check(await SaveLessonAsync(), "A lesson with a known file saves without a dialog");
        var savedCopy = LessonFile.Load(sessionFile).Lesson.Title;
        lesson.Title = "Несохранённое название"; LessonTitle.Text = lesson.Title; Changed(beforeTitle);
        check(await AutosaveAsync(), "Autosave succeeds for a lesson linked to a file");
        var session = ReadSessionInfo();
        check(session?.SourcePath == sessionFile && session.Dirty, "Autosave records the lesson file and that it has unsaved changes");
        check(Directory.Exists(AutosaveHistoryDirectory) && Directory.EnumerateFiles(AutosaveHistoryDirectory, "*.hboard").Any(), "Autosave keeps periodic history copies");
        currentPath = null; revision++; savedRevision = revision;
        RestoreAutosave();
        check(currentPath == sessionFile && HasUnsavedChanges && lesson.Title == "Несохранённое название" && LessonFile.Load(sessionFile).Lesson.Title == savedCopy && Title.Contains('•'),
            "After a restart the restored lesson stays linked to its file and is marked as unsaved");
        check(await SaveLessonAsync() && !HasUnsavedChanges && ReadSessionInfo() is { Dirty: false } && LessonFile.Load(sessionFile).Lesson.Title == "Несохранённое название",
            "Saving the restored lesson writes its file and clears the unsaved mark for the next start");

        LessonFile.Save(sessionFile, new Lesson { Title = "Изменён на другом компьютере" }, new Dictionary<string, byte[]>());
        File.SetLastWriteTimeUtc(sessionFile, DateTime.UtcNow.AddMinutes(5));
        RestoreAutosave();
        check(lesson.Title == "Изменён на другом компьютере" && currentPath == sessionFile && !HasUnsavedChanges, "A lesson file changed elsewhere after a clean session opens from the file, not from the older autosave");

        Changed(lesson.Clone()); await AutosaveAsync(); await AutosaveAsync();
        lesson.Title = "Последняя версия"; Changed(lesson.Clone()); await AutosaveAsync();
        File.WriteAllText(AutosavePath, "повреждённый файл");
        RestoreAutosave();
        check(Status.Text.Contains("резервной копии") && lesson.Title == "Изменён на другом компьютере" && HasUnsavedChanges, "A damaged autosave is recovered from its backup copy and marked as unsaved");
        await AutosaveAsync();
        check(LessonFile.Load(AutosavePath).Lesson.Title == lesson.Title, "The recovered lesson immediately replaces the damaged autosave");

        // Touch: two-finger navigation and cancelling the stroke the first finger started.
        var startWorld = new Point(1200, -300);
        var pinch = Pinch(startWorld, 1, 200, new Point(500, 320), 400);
        check(Math.Abs(pinch.Zoom - 2) < 1e-9 && Math.Abs(pinch.X + 500 / 2.0 - startWorld.X) < 1e-9 && Math.Abs(pinch.Y + 320 / 2.0 - startWorld.Y) < 1e-9, "Pinch zoom keeps the board point between the fingers in place");
        check(Pinch(startWorld, 1, 200, new Point(0, 0), 1_000_000).Zoom == 8 && Pinch(startWorld, 1, 0, new Point(0, 0), 100).Zoom == 1, "Pinch zoom stays within limits and ignores degenerate finger distances");

        SetTool(BoardTool.Pen);
        var items = lesson.Items.Count; var undoDepth = undo.Count;
        draft = new BoardItem { Kind = "pen", Points = [new(0, 0), new(40, 40)] }; gestureBefore = lesson.Clone(); gesture = true; Surface.Preview(draft);
        CancelGesture();
        check(draft == null && !gesture && lesson.Items.Count == items && undo.Count == undoDepth, "A second finger cancels the stroke started by the first one without an undo step");

        SetTool(BoardTool.Select);
        var target = new BoardItem { Kind = "text", Text = "Перенос пальцем", X = 10, Y = 20 };
        Surface.Add(target); Surface.Select(target);
        moving = target; moveOriginal = target.Clone(); moveOriginals = Surface.SelectedItems.ToDictionary(i => i.Id, i => i.Clone()); gestureBefore = lesson.Clone(); gesture = true;
        MoveSelection(new Vector(300, 150));
        CancelGesture();
        check(target.X == 10 && target.Y == 20 && undo.Count == undoDepth, "Switching to a pinch returns objects being dragged to their original place");
        Surface.Remove(target);
        check(!Stylus.GetIsPressAndHoldEnabled(Viewport) && !Stylus.GetIsFlicksEnabled(Viewport), "Pen and touch drawing are not delayed by Windows press-and-hold or flick gestures");
        currentPath = null; revision++; savedRevision = revision; UpdateUi();
    }
}
