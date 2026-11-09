using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace HistoryBoard;

public partial class MainWindow
{
    private async Task RunLibraryFeatureTests(string directory, Action<bool, string> check)
    {
        var root = Path.Combine(directory, "library-tests", Guid.NewGuid().ToString("N"));
        var library = new LessonLibrary(root);
        check(library.Load().Count == 0, "A fresh lesson library has an empty state");
        var path = Path.Combine(directory, "clipboard-and-groups.hboard");
        var saved = LessonFile.Load(path);
        saved.Lesson.Title = "Эпоха великих географических открытий";
        library.Remember(path, saved.Lesson); library.Remember(path.ToUpperInvariant(), saved.Lesson);
        var entries = new LessonLibrary(root).Load();
        check(entries.Count == 1 && entries[0].ObjectCount == saved.Lesson.Items.Count && entries[0].Exists, "Recent lessons persist and paths are deduplicated without regard to case");
        check(LessonLibrary.Search(entries, "ГЕОГРАФИЧЕСКИХ").Count() == 1 && !LessonLibrary.Search(entries, "несуществующий запрос").Any(), "Library search handles Cyrillic titles and no-result queries");
        var missingPath = Path.Combine(directory, "missing-lesson.hboard");
        library.Remember(missingPath, new Lesson { Title = "Урок, перенесённый на флешку" });
        check(library.Load().Any(e => !e.Exists), "Missing lesson files remain visible with unavailable status");
        foreach (var entry in library.Load())
        {
            var thumbnail = library.ThumbnailPath(entry.Path); Directory.CreateDirectory(Path.GetDirectoryName(thumbnail)!);
            Surface.ExportPng(thumbnail, VisibleRegion, 480);
        }
        var preview = library.Load().First(e => e.Exists).Preview;
        check(preview != null && preview.PixelWidth <= 480 && preview.PixelHeight <= 480, "Library thumbnails use a bounded rendering resolution");
        var window = new LibraryWindow(library) { Owner = this }; window.Show(); window.UpdateLayout();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var list = (ListBox)window.FindName("LessonsList");
        check(list.Items.Count == 2, "The production library window displays saved and missing lessons");
        list.SelectedItem = list.Items.Cast<LibraryLesson>().First(e => !e.Exists);
        check(!((Button)window.FindName("OpenButton")).IsEnabled, "Unavailable lessons cannot be opened from the library");
        var target = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        target.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(target));
        using (var file = File.Create(Path.Combine(directory, "07-lesson-library.png"))) png.Save(file);
        window.ApplySearch("нет такого урока");
        check(list.Items.Count == 0 && ((FrameworkElement)window.FindName("EmptyState")).Visibility == Visibility.Visible, "The library exposes a searchable no-results state");
        window.Width = 720; window.Height = 560; window.ApplySearch(""); window.UpdateLayout();
        check(((Button)window.FindName("OpenButton")).ActualWidth > 0 && list.ActualWidth > 500, "The lesson library remains usable at its minimum desktop size");
        var compact = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        compact.Render(window); png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(compact));
        using (var file = File.Create(Path.Combine(directory, "08-library-compact.png"))) png.Save(file);
        window.Close();
        library.Forget(path);
        check(File.Exists(path) && library.Load().All(e => !string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)), "Removing a library entry preserves the actual lesson file");
        File.WriteAllText(Path.Combine(root, "library.json"), "invalid json");
        check(library.Load().Count == 0 && library.Warning != null, "A damaged library index reports an error without affecting lessons");

        var originalLesson = lesson; var originalAssets = assets; var originalPath = currentPath;
        var oldRevision = revision; var oldSavedRevision = savedRevision;
        try
        {
            await OpenLessonAsync(path, false);
            check(Library.Load().Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)), "Opening a lesson through the editor registers it in the real library");
            LessonTitle.Text = "Сохранённая версия урока"; CommitTitle();
            var expectedPreview = CaptureLessonPreview(); var expectedPaper = lesson.Paper;
            currentPath = Path.Combine(directory, "library-save-integration.hboard");
            var saving = SaveLessonAsync(); SetPaper("white");
            check(await saving, "Saving an editor lesson also updates the recent-lesson library");
            var recorded = Library.Load().First(e => e.Path == currentPath);
            check(recorded.Title == "Сохранённая версия урока" && LessonFile.Load(currentPath).Lesson.Paper == expectedPaper, "Library metadata describes the saved snapshot while newer edits remain unsaved");
            check(expectedPreview != null && File.ReadAllBytes(Library.ThumbnailPath(currentPath)).SequenceEqual(expectedPreview), "Library thumbnail matches the captured save snapshot rather than later edits");
        }
        finally
        {
            lesson = originalLesson; assets = originalAssets; currentPath = originalPath; revision = oldRevision; savedRevision = oldSavedRevision;
            Surface.SetLesson(lesson, UsedAssets(lesson)); LessonTitle.Text = lesson.Title; RestoreCamera(); undo.Clear(); redo.Clear();
        }
    }
}
