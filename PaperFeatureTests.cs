using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HistoryBoard;

public partial class MainWindow
{
    private void RunPaperFeatureTests(string directory, Action<bool, string> check)
    {
        check(new Lesson().Paper == "grid", "New lessons start on warm graph paper");
        SetPaper("dots"); Undo(); check(lesson.Paper == "grid", "Paper changes participate in undo");
        Redo(); check(lesson.Paper == "dots", "Paper changes participate in redo");
        var path = Path.Combine(directory, "paper.hboard"); LessonFile.Save(path, lesson, UsedAssets(lesson));
        check(LessonFile.Load(path).Lesson.Paper == "dots", "Paper choice is stored inside the lesson file");
        var surface = new BoardSurface(); surface.Measure(new Size(320, 240)); surface.Arrange(new Rect(0, 0, 320, 240));
        byte[] Render(string paper, double x = 0, double y = 0, double scale = 1)
        {
            surface.SetLesson(new Lesson { Paper = paper }, new Dictionary<string, byte[]>()); surface.SetCamera(x, y, scale); surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap(320, 240, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
            var pixels = new byte[320 * 240 * 4]; bitmap.CopyPixels(pixels, 320 * 4, 0); return pixels;
        }
        var plain = Render("plain"); var white = Render("white"); var grid = Render("grid"); var dots = Render("dots"); var ruled = Render("ruled");
        check(!plain.SequenceEqual(white) && plain[0] == 239 && plain[1] == 247 && plain[2] == 250, "Warm paper renders the chosen cream color instead of white");
        check(!grid.SequenceEqual(plain) && !dots.SequenceEqual(grid) && !ruled.SequenceEqual(grid), "Grid, dots and ruled paper render distinct visible patterns");
        check(grid.SequenceEqual(Render("grid", 24, 24)), "Graph paper stays anchored to world coordinates when panning by one cell");
        check(!grid.SequenceEqual(Render("grid", 12, 12)), "Half-cell panning moves the paper pattern with the board");
        check(Render("grid", -999000000, -999000000, .02).Length == plain.Length, "Paper renders at the minimum zoom and distant board coordinates");
        var exportPath = Path.Combine(directory, "paper-export.png");
        surface.SetLesson(new Lesson { Paper = "plain" }, new Dictionary<string, byte[]>());
        surface.ExportPng(exportPath, new Rect(0, 0, 120, 100));
        using (var file = File.OpenRead(exportPath))
        {
            var bitmap = BitmapDecoder.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(10, 10, 1, 1), pixel, 4, 0);
            check(pixel[0] == 239 && pixel[1] == 247 && pixel[2] == 250, "PNG export uses the selected paper background");
        }
        SetPaper("grid"); CaptureWindow(Path.Combine(directory, "09-warm-graph-paper.png"));
    }
}
