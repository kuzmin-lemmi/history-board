using System.IO;
using System.Windows;

namespace HistoryBoard;

public partial class MainWindow
{
    private LessonLibrary Library => new(Path.Combine(App.DataDirectory, "Library"));

    private byte[]? CaptureLessonPreview()
    {
        try { return Surface.RenderPng(VisibleRegion, 480); }
        catch { return null; }
    }

    private void RememberLesson(string path, Lesson snapshot, byte[]? preview)
    {
        try
        {
            var library = Library; library.Remember(path, snapshot);
            if (preview != null)
            {
                var thumbnail = library.ThumbnailPath(path); Directory.CreateDirectory(Path.GetDirectoryName(thumbnail)!);
                File.WriteAllBytes(thumbnail, preview);
            }
        }
        catch { Status.Text += " · Не удалось обновить библиотеку"; }
    }

    private async void OnLibrary(object sender, RoutedEventArgs e)
    {
        if (presenting) return;
        CommitEditor(); FinishGesture();
        var window = new LibraryWindow(Library) { Owner = this };
        if (window.ShowDialog() != true) return;
        if (window.SelectedPath is { } path) await OpenLessonAsync(path);
        else if (window.NewLessonRequested) OnNew(this, e);
    }
}
