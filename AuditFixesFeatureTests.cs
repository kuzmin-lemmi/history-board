using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HistoryBoard;

public partial class MainWindow
{
    private async Task RunAuditFixesFeatureTests(string directory, Action<bool, string> check)
    {
        var folder = Path.Combine(directory, "audit-fixes-tests");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);

        // ---- Timeline: centuries typed with Cyrillic letters ----
        check(Timeline.Year("ХIХ век") == 1850 && Timeline.Year("Х век") == 950 && Timeline.Year("ХХ в.") == 1950 && Timeline.Year("ІV в. до н. э.") == -350,
            "Centuries typed with Cyrillic Х and І are placed like Latin roman numerals");
        check(Timeline.Year("XIX век") == 1850 && Timeline.Year("с 1812") == 1812 && Timeline.Year("1 сентября 1939") > 1939.6 && Timeline.Year("Смута") == null,
            "Ordinary words, prepositions and month names keep their meaning after the conversion");
        var cyrillicLine = new BoardItem { Kind = "timeline", Text = "ХVII век — Смутное время\nХIХ век — Отечественная война\nХХ век — Революция", Width = 1200, Height = 1 };
        check(Timeline.Arrange(cyrillicLine).Proportional, "A timeline of Cyrillic-typed centuries keeps its scale by years");

        // ---- Earlier versions from the autosave history ----
        savedRevision = revision;
        Directory.CreateDirectory(AutosaveHistoryDirectory);
        var oldCopy = Path.Combine(AutosaveHistoryDirectory, "2001-01-01_09-00-00.hboard");
        LessonFile.Save(oldCopy, new Lesson { Title = "Урок утром", Items = [new BoardItem { Text = "Утренняя версия" }] }, new Dictionary<string, byte[]>());
        File.SetLastWriteTime(oldCopy, new DateTime(2001, 1, 1, 9, 0, 0));
        File.WriteAllBytes(Path.Combine(AutosaveHistoryDirectory, "2001-01-01_08-00-00.hboard"), [1, 2, 3]);
        await AutosaveAsync();
        var versions = ListEarlierVersions();
        var morning = versions.FirstOrDefault(v => v.Path == oldCopy);
        check(morning != null && morning.Title == "Урок утром" && morning.Objects == 1 && versions.All(v => v.Path != AutosavePath) && versions.All(v => !v.Path.EndsWith("08-00-00.hboard")),
            "Earlier versions list the periodic copies with title and contents, without the current autosave or damaged copies");
        check(versions.Select(v => v.Saved).SequenceEqual(versions.Select(v => v.Saved).OrderByDescending(t => t)), "Earlier versions are listed newest first");
        var opened = await OpenEarlierVersionAsync(morning!);
        check(opened && lesson.Title == "Урок утром" && lesson.Items.Any(i => i.Text == "Утренняя версия") && currentPath == null && HasUnsavedChanges,
            "An earlier version opens as an unsaved lesson that is never written back to a file silently");
        File.Delete(oldCopy); File.Delete(Path.Combine(AutosaveHistoryDirectory, "2001-01-01_08-00-00.hboard"));
        savedRevision = revision;

        // ---- Screen capture ignores pictures from other programs and can be cancelled ----
        check(IsSnippingProcess("ScreenClippingHost") && IsSnippingProcess("SnippingTool") && !IsSnippingProcess("chrome") && !IsSnippingProcess(null),
            "Only the Windows snipping tools count as the source of a screen capture");
        var picture = new WriteableBitmap(40, 30, 96, 96, PixelFormats.Bgr32, null);
        var before = lesson.Items.Count;
        var foreign = CaptureScreenAsync(() => "chrome", false);
        await Task.Delay(300);
        var foreignCopied = TrySetClipboardImage(picture);
        await Task.Delay(1200);
        var ignored = lesson.Items.Count == before && capturing;
        CancelScreenCapture(); await foreign;
        check(!foreignCopied || ignored, "A picture copied in another program while waiting for a snip is not placed on the board");
        check(!capturing && Status.Text.Contains("отменён"), "Esc cancels waiting for a screen capture");
        var snip = CaptureScreenAsync(() => "ScreenClippingHost", false);
        await Task.Delay(300);
        var snipCopied = TrySetClipboardImage(picture);
        await Task.WhenAny(snip, Task.Delay(5000));
        check(!snipCopied || lesson.Items.Count == before + 1 && lesson.Items.Any(i => i.Kind == "image"), "A picture from the snipping tool is placed on the board");
        if (capturing) CancelScreenCapture();

        // ---- PDF export in steps, with progress and cancellation ----
        var demo = DemoLessons.All[0].Build();
        lesson = demo; assets = []; Surface.SetLesson(lesson, assets); undo.Clear(); SetCamera(0, 0, 1); savedRevision = revision;
        var pdfPath = Path.Combine(folder, "Кадры.pdf");
        var reported = new List<int>();
        await ExportFramesPdfAsync(pdfPath, new SyncProgress(reported), CancellationToken.None);
        using (var pdf = new PdfDocument(File.ReadAllBytes(pdfPath)))
            check(pdf.PageCount == demo.Frames.Count && reported.SequenceEqual(Enumerable.Range(1, demo.Frames.Count)), "PDF export reports progress for every frame and writes all pages");
        var cancelledPath = Path.Combine(folder, "Отменённый.pdf");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var stopped = false;
            try { await ExportFramesPdfAsync(cancelledPath, null, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
            check(stopped && !File.Exists(cancelledPath) && !File.Exists(cancelledPath + ".tmp"), "A cancelled PDF export leaves no file");
        }

        // ---- PDF pages are imported at a readable resolution ----
        lesson = new Lesson(); assets = []; Surface.SetLesson(lesson, assets); undo.Clear(); savedRevision = revision;
        await ImportPdfAsync(pdfPath, "1");
        var page = lesson.Items.Single(i => i.Kind == "image");
        var decoded = LessonFile.DecodeImage(assets[page.AssetId!]);
        check(Math.Max(decoded.PixelWidth, decoded.PixelHeight) == 3000, "PDF pages are imported with a 3000-pixel long side");
        savedRevision = revision;

        // ---- The library at start-up can be switched off ----
        File.WriteAllText(Preferences.FilePath, "{ повреждено");
        check(Preferences.Load().ShowLibraryOnStart, "A damaged preferences file falls back to showing the library");
        var library = new LibraryWindow(Library) { Owner = this };
        library.ShowOnStart.IsChecked = false; library.OnShowOnStartChanged(library, new RoutedEventArgs());
        check(!Preferences.Load().ShowLibraryOnStart, "The library checkbox turns off the library at start-up");
        library.ShowOnStart.IsChecked = true; library.OnShowOnStartChanged(library, new RoutedEventArgs());
        check(Preferences.Load().ShowLibraryOnStart && new LibraryWindow(Library).ShowOnStart.IsChecked == true, "The library at start-up can be turned on again");
        library.Close();
    }

    private static bool TrySetClipboardImage(BitmapSource image)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetImage(image); return true; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(100); } // the clipboard is briefly held by another program
        }
        return false;
    }

    private sealed class SyncProgress(List<int> list) : IProgress<int> { public void Report(int value) => list.Add(value); }
}
