using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HistoryBoard;

// "--promo-screenshots <folder>" writes clean pictures of the example lessons for catalogues and
// posts. It uses its own data folder and never touches the teacher's lessons or autosave.
public partial class MainWindow
{
    internal async Task WritePromoScreenshots(string directory)
    {
        Directory.CreateDirectory(directory);
        var examples = Path.Combine(directory, "examples");
        DemoLessons.WriteAll(examples);
        Width = 1600; Height = 1000; WindowState = WindowState.Normal;
        await Settle();

        // 1. The editor with the 1812 lesson: the whole interface, frames tray with thumbnails.
        await OpenLessonAsync(Path.Combine(examples, DemoLessons.All[1].FileName), false);
        GoToFrame(2, false); RefreshFrameThumbnails(); UpdateUi(); await Settle();
        CaptureWindow(Path.Combine(directory, "01-editor-1812-scheme.png"));

        // 2. The same scheme in the show, all steps revealed, with the pointer on Moscow.
        StartPresentation(); GoToFrame(2, false);
        while (FrameSteps(activeFrame).Count(s => s > revealStep) > 0) PresentStep(1);
        SetPresentationTool(PresentationTool.Pointer); await Settle();
        // Set after settling: a real mouse movement over the window would move the pointer.
        var moscow = lesson.Items.FirstOrDefault(i => i.Kind == "text" && i.Text == "Москва");
        if (moscow != null) { Overlay.Pointer = Surface.WorldToScreen(new Point(moscow.X - 14, moscow.Y + 34)); Overlay.HasPointer = true; }
        Overlay.InvalidateVisual(); UpdateLayout();
        CaptureWindow(Path.Combine(directory, "02-show-1812-pointer.png"));
        SetPresentationTool(PresentationTool.None); EndPresentation();

        // 3. A timeline of the Great Patriotic War in the show.
        await OpenLessonAsync(Path.Combine(examples, DemoLessons.All[2].FileName), false);
        StartPresentation(); GoToFrame(1, false); await Settle();
        CaptureWindow(Path.Combine(directory, "03-show-timeline.png"));

        // 4. Covers: one already opened by a click, the other still closed, and the timer.
        GoToFrame(3, false); await Settle();
        var covers = lesson.Items.Where(i => i.Kind == "cover" && lesson.Frames[3].Bounds.IntersectsWith(Surface.Bounds(i))).OrderBy(i => i.X).ToList();
        if (covers.Count > 0) { var first = Surface.Bounds(covers[0]); ToggleCoverAt(new Point(first.X + first.Width / 2, first.Y + first.Height / 2)); }
        ShowTimer(); StartTimer(TimeSpan.FromMinutes(5)); await Settle();
        CaptureWindow(Path.Combine(directory, "04-show-covers-timer.png"));
        ShowTimer(false); EndPresentation();

        // 5. The lesson library with the three examples.
        var library = new LibraryWindow(Library) { Owner = this, Width = 1060, Height = 760 }; library.Show(); library.UpdateLayout(); await Settle();
        var shot = new RenderTargetBitmap((int)library.ActualWidth, (int)library.ActualHeight, 96, 96, PixelFormats.Pbgra32); shot.Render((Visual)library.Content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(shot));
        using (var file = File.Create(Path.Combine(directory, "05-library.png"))) png.Save(file);
        library.Close();
        CloseWithoutPrompt();
    }

    private async Task Settle()
    {
        UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(400);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        UpdateLayout();
    }
}
