using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HistoryBoard;

public partial class MainWindow
{
    private async Task RunRelease14FeatureTests(string directory, Action<bool, string> check)
    {
        var folder = Path.Combine(directory, "release-1-4-tests");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        lesson = new Lesson(); assets = []; currentPath = null; undo.Clear(); redo.Clear(); activeFrame = lastFrame = -1;
        Surface.SetLesson(lesson, assets); LessonTitle.Text = lesson.Title; UpdateUi(); SetCamera(0, 0, 1);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        // ---- Timer ----
        StartTimer(TimeSpan.FromMinutes(5));
        check(TimerVisible && TimerRunning && TimerText is "05:00" or "04:59", "The timer starts a five-minute countdown on the board");
        AdvanceTimer(TimeSpan.FromSeconds(295));
        check(TimerText is "00:05" or "00:04" && !TimerFinished, "The countdown shows the remaining time");
        AdvanceTimer(TimeSpan.FromSeconds(10));
        check(TimerFinished && !TimerRunning && TimerText == "00:00", "The countdown stops at zero and signals the end");
        ToggleTimer(); check(TimerRunning && !TimerFinished, "Pressing ▶ after the end starts the same countdown again");
        ResetTimer(); check(!TimerRunning && TimerText == "05:00", "↺ resets the countdown");
        StartStopwatch(); AdvanceTimer(TimeSpan.FromSeconds(75));
        check(TimerText is "01:15" or "01:16", "The stopwatch counts time up");
        ShowTimer(false); check(!TimerVisible && !TimerRunning, "The timer can be hidden");

        // ---- Covers that open on a click ----
        SetTool(BoardTool.Text); BeginEditor(null, new Point(200, 200)); Editor.Text = "1812"; CommitEditor();
        var answer = lesson.Items.Single(i => i.Kind == "text");
        SetTool(BoardTool.Select); Surface.Select(answer);
        var cover = CoverSelection()!;
        check(cover.Kind == "cover" && Surface.Bounds(cover).Contains(Surface.Bounds(answer)) && Surface.Hit(new Point(cover.X + 5, cover.Y + 5)) == cover, "«Закрыть плашкой» covers the selected answer");
        check(Surface.EditorLook, "In the editor the cover is translucent so the answer stays visible to the teacher");
        var export = Surface.RenderBitmap(Surface.Bounds(cover), 200, false);
        var middle = Pixels(export, export.PixelWidth / 2, export.PixelHeight / 2);
        var coverColor = (Color)ColorConverter.ConvertFromString(cover.Color);
        check(Math.Abs(middle.R - coverColor.R) < 6 || middle.R > 240, "Exported images show covers opaque, as on a printed worksheet");
        var path = Path.Combine(folder, "Плашки.hboard"); LessonFile.Save(path, lesson, UsedAssets(lesson));
        check(LessonFile.Load(path).Lesson.Items.Any(i => i.Kind == "cover") && LessonFile.Load(path).Lesson.Version == 3, "Covers are saved in the lesson");
        var center = new Point(cover.X + cover.Width / 2, cover.Y + cover.Height / 2);
        lesson.Frames.Add(new LessonFrame { Name = "Ответ", X = 0, Y = 0, Width = 1200, Height = 700 }); RebuildFrames();
        StartPresentation();
        check(!Surface.EditorLook && !Surface.IsHidden(cover), "In the show the cover is opaque");
        check(ToggleCoverAt(center) && Surface.IsHidden(cover), "A click on the cover in the show opens the answer");
        check(ToggleCoverAt(center) && !Surface.IsHidden(cover), "A second click closes it again");
        ToggleCoverAt(center); check(TimerPresentationButton.IsVisible, "The timer button is available in the show");
        EndPresentation();
        check(!Surface.IsHidden(cover) && Surface.EditorLook, "After the show every cover is back in place");
        check(ShortcutProfile.Default().Resolve(System.Windows.Input.Key.K, System.Windows.Input.ModifierKeys.None, ShortcutContext.Editor, false) == BoardCommand.CoverTool, "K selects the cover tool");

        // ---- PDF import ----
        check(PdfDocument.ParsePages("1-3, 5", 6)?.SequenceEqual([0, 1, 2, 4]) == true && PdfDocument.ParsePages("0", 5) == null && PdfDocument.ParsePages("3-1", 5) == null && PdfDocument.ParsePages("7", 5) == null, "Page lists like «1-3, 5» are read and checked");
        var broken = false; try { using var _ = new PdfDocument([1, 2, 3, 4]); } catch (InvalidDataException) { broken = true; }
        check(broken, "A damaged file is reported instead of crashing");
        var demo = DemoLessons.All[0].Build();
        lesson = demo; assets = []; Surface.SetLesson(lesson, assets); SetCamera(0, 0, 1);
        var pdfPath = Path.Combine(folder, "Пример.pdf"); ExportFramesPdf(pdfPath);
        using (var pdf = new PdfDocument(File.ReadAllBytes(pdfPath)))
        {
            var page = pdf.RenderPage(0, 800);
            check(pdf.PageCount == demo.Frames.Count && page.PixelWidth == 800 && Math.Abs(page.PixelHeight - 450) <= 1, "PDF pages are read with their own proportions");
            var ink = Enumerable.Range(0, 60).SelectMany(k => Enumerable.Range(0, 30).Select(r => Pixels(page, 50 + k * 6, 80 + r * 3))).Min(p => p.R);
            check(ink < 120, "PDF text is rendered onto the page");
        }
        lesson = new Lesson(); assets = []; Surface.SetLesson(lesson, assets); undo.Clear(); SetCamera(0, 0, 1);
        var added = await ImportPdfAsync(pdfPath, "1-2");
        var pages = lesson.Items.Where(i => i.Kind == "image").ToList();
        check(added == 2 && pages.Count == 2 && pages[0].X < pages[1].X && Math.Abs(pages[0].Width / pages[0].Height - 16.0 / 9) < .02 && undo.Count == 1, "Chosen PDF pages are placed side by side on the board in one undoable step");
        check(pages.All(p => assets[p.AssetId!].Length < 2 * 1024 * 1024), "PDF pages are stored compactly");

        // ---- Example lessons ----
        var examples = Path.Combine(folder, "examples"); DemoLessons.WriteAll(examples);
        check(DemoLessons.All.All(d => File.Exists(Path.Combine(examples, d.FileName))), "All three example lessons are written for the release");
        foreach (var d in DemoLessons.All)
        {
            var loaded = LessonFile.Load(Path.Combine(examples, d.FileName)).Lesson;
            var timeline = loaded.Items.First(i => i.Kind == "timeline");
            var layout = Timeline.Arrange(timeline);
            var overlap = layout.Events.GroupBy(e => e.Row).Any(row => row.OrderBy(e => e.X).Zip(row.OrderBy(e => e.X).Skip(1)).Any(pair =>
                pair.First.X + Math.Max(pair.First.Date.Width, pair.First.Title?.Width ?? 0) / 2 > pair.Second.X - Math.Max(pair.Second.Date.Width, pair.Second.Title?.Width ?? 0) / 2 + 1));
            check(loaded.Title == d.Title && loaded.Frames.Count >= 4 && loaded.Items.Any(i => i.Kind == "cover") && loaded.Items.Any(i => i.Step > 0) && !overlap && layout.Events.Count >= 7,
                $"Example «{d.Title}» opens with frames, a timeline without overlapping labels, covers and reveal steps");
            var frameContent = loaded.Frames.All(f => loaded.Items.Any(i => f.Bounds.IntersectsWith(new Rect(i.X, i.Y, 1, 1)) || i.Points.Any(p => f.Bounds.Contains(p.X, p.Y))));
            check(frameContent, $"Every frame of «{d.Title}» shows some material");
        }
        var ensured = DemoLessons.EnsureFile(DemoLessons.All[1], examples);
        var custom = LessonFile.Load(ensured).Lesson; custom.Title = "Мой урок 1812"; LessonFile.Save(ensured, custom, new Dictionary<string, byte[]>());
        check(LessonFile.Load(DemoLessons.EnsureFile(DemoLessons.All[1], examples)).Lesson.Title == "Мой урок 1812", "Opening an example again keeps the teacher's own changes");

        await OpenLessonAsync(Path.Combine(examples, DemoLessons.All[1].FileName), false);
        GoToFrame(2, false); UpdateUi(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        CaptureWindow(Path.Combine(directory, "15-example-1812-scheme.png"));
        await OpenLessonAsync(Path.Combine(examples, DemoLessons.All[2].FileName), false);
        GoToFrame(1, false); UpdateUi(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        CaptureWindow(Path.Combine(directory, "16-example-war-timeline.png"));
        StartPresentation(); GoToFrame(3, false); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        ShowTimer(); StartTimer(TimeSpan.FromMinutes(3)); UpdateLayout();
        CaptureWindow(Path.Combine(directory, "17-show-with-covers-and-timer.png"));
        ShowTimer(false); EndPresentation();

        var library = new LibraryWindow(Library) { Owner = this, Width = 1060, Height = 760 }; library.Show(); library.UpdateLayout();
        check(library.Examples.Children.OfType<System.Windows.Controls.Button>().Count() == 3, "The lesson library offers the three examples");
        var libraryShot = new RenderTargetBitmap((int)library.ActualWidth, (int)library.ActualHeight, 96, 96, PixelFormats.Pbgra32); libraryShot.Render((Visual)library.Content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(libraryShot)); using (var file = File.Create(Path.Combine(directory, "18-library-examples.png"))) png.Save(file);
        library.Close();
    }
}
