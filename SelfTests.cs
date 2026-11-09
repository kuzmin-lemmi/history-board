using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HistoryBoard;

public partial class MainWindow
{
    internal async Task RunSelfTests(string directory)
    {
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAILED: " + name); checks.Add(name); }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var workspaceRatio = Viewport.ActualWidth * Viewport.ActualHeight / (((FrameworkElement)Content).ActualWidth * ((FrameworkElement)Content).ActualHeight);
        CaptureWindow(Path.Combine(directory, "01-empty-board.png"));
        Check(workspaceRatio > .75, $"Compact interface dedicates more than 75 percent of its area to the working canvas even in a small window ({Viewport.ActualWidth:0}x{Viewport.ActualHeight:0} of {((FrameworkElement)Content).ActualWidth:0}x{((FrameworkElement)Content).ActualHeight:0})");
        var installer = Installer.CreateInstallerWindow(); installer.Show(); installer.UpdateLayout();
        var installPanel = (System.Windows.Controls.StackPanel)installer.Content;
        var installButton = (System.Windows.Controls.Button)installPanel.Children[^1];
        var buttonBottom = installButton.TranslatePoint(new Point(0, installButton.ActualHeight), installPanel).Y;
        Check(installButton.ActualHeight > 25 && buttonBottom <= installPanel.ActualHeight, "Installer action button fits within the visible dialog without installing anything");
        var installerBitmap = new RenderTargetBitmap((int)installPanel.ActualWidth + 70, (int)installPanel.ActualHeight + 70, 96, 96, PixelFormats.Pbgra32);
        installerBitmap.Render(installer);
        var installerPng = new PngBitmapEncoder(); installerPng.Frames.Add(BitmapFrame.Create(installerBitmap));
        using (var installerFile = File.Create(Path.Combine(directory, "03-installer.png"))) installerPng.Save(installerFile);
        installer.Close();

        BeginEditor(null, new Point(90, 70));
        Editor.Text = "Великая Отечественная война\n1941–1945";
        AdjustSize(2); CommitEditor();
        Check(lesson.Items.Count == 1 && lesson.Items[0].Text.Contains("1941–1945") && lesson.Items[0].Size == 36, "Cyrillic multiline editing and font-size change");
        var originalTextId = lesson.Items[0].Id;
        SetTool(BoardTool.List); BeginEditor(null, new Point(90, 240));
        Editor.Text = "Причины и предпосылки\nОсновные этапы\nИтоги и значение"; CommitEditor();
        Check(lesson.Items[1].Numbered && BoardSurface.Format(lesson.Items[1]).Height > 90, "Numbered list preserves multiline layout");

        var mapPath = Path.Combine(directory, "test-map.png");
        var mapVisual = new DrawingVisual();
        using (var dc = mapVisual.RenderOpen())
        {
            dc.DrawRectangle(BoardSurface.Brush("#E9EDDD"), null, new Rect(0, 0, 800, 480));
            dc.DrawEllipse(BoardSurface.Brush("#D2DDC2"), null, new Point(260, 220), 200, 170);
            dc.DrawEllipse(BoardSurface.Brush("#DCE4CB"), null, new Point(650, 180), 220, 230);
            var river = new StreamGeometry(); using (var c = river.Open()) { c.BeginFigure(new Point(440, 0), false, false); c.BezierTo(new Point(300, 180), new Point(570, 220), new Point(400, 480), true, false); }
            dc.DrawGeometry(null, new Pen(BoardSurface.Brush("#ADC9D1"), 19), river);
            dc.DrawText(BoardSurface.Format(new BoardItem { Text = "УЧЕБНОЕ ИЗОБРАЖЕНИЕ ДЛЯ ПРОВЕРКИ", Size = 20, Color = "#6F8060", Width = 760 }), new Point(28, 26));
            dc.DrawEllipse(BoardSurface.Brush("#6F8060"), null, new Point(210, 290), 6, 6);
            dc.DrawEllipse(BoardSurface.Brush("#6F8060"), null, new Point(620, 190), 6, 6);
        }
        var bitmap = new RenderTargetBitmap(800, 480, 96, 96, PixelFormats.Pbgra32); bitmap.Render(mapVisual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(mapPath)) encoder.Save(file);
        await AddMapAsync(mapPath);
        var map = lesson.Items[0];
        Check(map.Kind == "image" && assets.Count == 1 && map.Width / map.Height > 1.6, "Map import retains aspect ratio and places map behind annotations");
        map.X = 620; map.Y = 280; map.Width = 850; map.Height = 510; Surface.Refresh(map);
        SetTool(BoardTool.Arrow);
        var arrow = new BoardItem { Kind = "arrow", Color = "#C94F49", Size = 7, Points = [new(820, 650), new(1230, 470)] };
        var beforeArrow = lesson.Clone(); Surface.Add(arrow); Changed(beforeArrow);
        Check(Surface.Hit(new Point(1025, 560))?.Id == arrow.Id, "Arrow can be selected over a map");
        var blueArrow = new BoardItem { Kind = "arrow", Color = "#427CB3", Size = 6, Points = [new(1320, 680), new(1100, 720)] }; Surface.Add(blueArrow);
        var stroke = new BoardItem { Kind = "pen", Color = "#5F895A", Size = 5, Points = [new(750, 500), new(770, 450), new(800, 420), new(835, 410), new(870, 430)] };
        Surface.Add(stroke);
        Surface.Select(arrow); color = "#8D6DA8"; ChangeSelected(i => i.Color = color);
        Check(arrow.Color == "#8D6DA8", "Selected annotation changes color");
        Undo(); Check(lesson.Items.Single(i => i.Id == arrow.Id).Color == "#C94F49", "Undo restores the previous annotation color");
        Redo(); Check(lesson.Items.Single(i => i.Id == arrow.Id).Color == "#8D6DA8", "Redo restores the new annotation color");

        var beforeErase = lesson.Clone();
        EraseAt(new Point(1025, 560));
        Check(!lesson.Items.Any(i => i.Id == arrow.Id) && lesson.Items.Any(i => i.Kind == "image"), "Eraser removes an annotation while preserving the map");
        Changed(beforeErase); Undo();
        Check(lesson.Items.Any(i => i.Id == arrow.Id), "Undo restores erased annotation");
        var persisted = Path.Combine(directory, "Проверка урока.hboard");
        LessonFile.Save(persisted, lesson, UsedAssets(lesson));
        var loaded = LessonFile.Load(persisted);
        Check(loaded.Lesson.Items.Count == lesson.Items.Count && loaded.Assets.Count == 1 && loaded.Lesson.Items.Any(i => i.Id == originalTextId), "Lesson file round-trips Cyrillic text, lists, maps and strokes");
        var invalid = lesson.Clone(); invalid.Items[0].Width = double.NaN;
        var rejected = false; try { LessonFile.Validate(invalid); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Malformed lesson geometry is rejected");
        Surface.ExportPng(Path.Combine(directory, "export.png"), new Rect(0, 0, 1600, 1000));
        var exported = LessonFile.DecodeImage(File.ReadAllBytes(Path.Combine(directory, "export.png")));
        Check(exported.PixelWidth == 1600 && exported.PixelHeight == 1000, "PNG exports the requested region at original resolution");
        await AutosaveAsync();
        Check(File.Exists(AutosavePath) && LessonFile.Load(AutosavePath).Lesson.Items.Count == lesson.Items.Count, "Autosave persists the complete current lesson");
        await OpenLessonAsync(persisted, false);
        Check(lesson.Items.Count == loaded.Lesson.Items.Count && assets.Count == 1, "Saved lesson reopens in the application");
        SetTool(BoardTool.Select); Surface.Select(null);
        Fit();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        CaptureWindow(Path.Combine(directory, "02-board-with-materials.png"));

        SetCamera(-20000, 12000, .75);
        var anchor = new Point(430, 260); var world = Surface.ScreenToWorld(anchor); var screen = Surface.WorldToScreen(world);
        Check((screen - anchor).Length < .0001, "Camera converts screen and world coordinates in both directions");
        ZoomAt(1.8, anchor);
        Check((Surface.ScreenToWorld(anchor) - world).Length < .0001, "Zoom preserves the world point beneath the pointer");
        var savedX = cameraX; var savedY = cameraY; var savedZoom = zoom;
        var textX = lesson.Items.Single(i => i.Id == originalTextId).X;
        BeginPan(new Point(200, 200)); MovePan(new Point(560, 20)); EndPan();
        Check(Math.Abs(cameraX - (savedX - 360 / savedZoom)) < .0001 && Math.Abs(cameraY - (savedY + 180 / savedZoom)) < .0001, "Dragging the camera pans in both directions independent of zoom");
        Check(lesson.Items.Single(i => i.Id == originalTextId).X == textX, "Panning leaves object world positions unchanged");

        SetTool(BoardTool.Text); BeginEditor(null, new Point(-125000, -8400)); Editor.Text = "Материал далеко слева"; CommitEditor();
        var distantText = lesson.Items.Single(i => i.Text == "Материал далеко слева");
        Check(distantText.X == -125000 && distantText.Y == -8400, "Objects can be placed far outside the former sheet, including negative coordinates");
        Surface.Select(distantText); ToggleLock(); var lockedSize = distantText.Size;
        ChangeSelected(i => i.Size = 150); DeleteSelected(); EraseAt(new Point(distantText.X + 10, distantText.Y + 10)); BeginEditor(distantText, new Point());
        Check(distantText.Locked && lesson.Items.Contains(distantText) && distantText.Size == lockedSize && editing == null, "Pinned objects resist editing, style changes, deletion and the eraser");
        ToggleLock(); Check(!distantText.Locked, "Pinned objects can be unlocked explicitly");
        Surface.Remove(distantText);

        FocusRegion(new Rect(0, 0, 1600, 1000), false); AddFrame("Введение");
        var firstFrame = lesson.Frames[0];
        color = "#26352F";
        BeginEditor(null, new Point(2250, 100)); Editor.Text = "Ход событий"; CommitEditor();
        BeginEditor(null, new Point(2250, 210)); Editor.Text = "Карта и направления\nНаступление и контрнаступление"; CommitEditor();
        var secondMap = lesson.Items.First(i => i.Kind == "image").Clone(); secondMap.Id = Guid.NewGuid().ToString("N"); secondMap.X = 2310; secondMap.Y = 400; secondMap.Width = 1000; secondMap.Height = 600; lesson.Items.Insert(0, secondMap); Surface.Rebuild();
        Surface.Select(secondMap); ToggleLock();
        Surface.Add(new BoardItem { Kind = "arrow", Color = "#C94F49", Size = 8, Points = [new(2500, 820), new(3050, 550)] });
        FocusRegion(new Rect(2150, 30, 1300, 1000), false); AddFrame("Карта и направления");
        Check(lesson.Frames.Count == 2 && lesson.Frames[1].X > 1500, "Saved frames retain distinct regions of the same infinite board");
        GoToFrame(0, false);
        Check((new Point(VisibleRegion.X + VisibleRegion.Width / 2, VisibleRegion.Y + VisibleRegion.Height / 2) - new Point(firstFrame.X + firstFrame.Width / 2, firstFrame.Y + firstFrame.Height / 2)).Length < .001, "Selecting a frame restores its centre and scale");
        NextFrame(1); await Task.Delay(360);
        Check(activeFrame == 1 && !cameraTimer.IsEnabled && VisibleRegion.X > 1500, "Presentation navigation completes a smooth transition to the next frame");
        var itemCount = lesson.Items.Count; var frameToMove = lesson.Frames[1]; MoveFrame(frameToMove, -1);
        Check(lesson.Frames[0].Id == frameToMove.Id, "Frame order can be changed independently of board objects");
        MoveFrame(frameToMove, 1); RemoveFrame(frameToMove);
        Check(lesson.Items.Count == itemCount && lesson.Frames.Count == 1, "Removing a frame preserves every board object");
        Undo(); Check(lesson.Frames.Count == 2, "Undo restores a removed presentation frame");

        GoToFrame(0, false); var originalWidth = Viewport.ActualWidth;
        StartPresentation();
        Check(presenting && MainHeader.Visibility == Visibility.Collapsed && ToolColumn.ActualWidth == 0 && Viewport.ActualWidth > originalWidth, "Presentation hides editing panels and expands the working view");
        CaptureWindow(Path.Combine(directory, "04-presentation.png"));
        EndPresentation();
        Check(!presenting && MainHeader.Visibility == Visibility.Visible && ToolColumn.ActualWidth == 48, "Exiting presentation restores the compact editor");

        SetCamera(-5000, 2500, .8); StoreCamera();
        var modernPath = Path.Combine(directory, "Бесконечная доска.hboard"); LessonFile.Save(modernPath, lesson, UsedAssets(lesson));
        var modern = LessonFile.Load(modernPath);
        Check(modern.Lesson.Frames.Count == 2 && modern.Lesson.Items.Any(i => i.Locked) && modern.Lesson.View?.X == -5000 && modern.Lesson.View.Zoom == .8, "Lesson round-trips frames, pinned objects and the camera view");
        var legacy = lesson.Clone(); legacy.Version = 1; legacy.Frames.Clear(); legacy.View = null;
        var legacyPath = Path.Combine(directory, "Старый формат.hboard"); LessonFile.Save(legacyPath, legacy, UsedAssets(legacy));
        var upgraded = LessonFile.Load(legacyPath);
        Check(upgraded.Lesson.Version == 2 && upgraded.Lesson.Items.Count == lesson.Items.Count && upgraded.Lesson.View == null, "Files from version 1 open without losing their objects");

        Surface.ExportPng(Path.Combine(directory, "frame.png"), lesson.Frames[1].Bounds);
        Check(File.Exists(Path.Combine(directory, "frame.png")), "A saved presentation frame exports to PNG");
        using (var icon = Application.GetResourceStream(new Uri("pack://application:,,,/HistoryBoard;component/Assets/app.ico")).Stream)
        {
            var decoded = BitmapDecoder.Create(icon, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Check(decoded.Frames.Count == 7 && decoded.Frames.Any(f => f.PixelWidth == 16) && decoded.Frames.Any(f => f.PixelWidth == 256), "Application icon includes seven native Windows sizes, from 16 to 256 pixels");
        }
        GoToFrame(1, false); UpdateUi(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        CaptureWindow(Path.Combine(directory, "05-infinite-board-and-frames.png"));

        await UpdateServiceTests.RunAsync(directory, Check);
        await RunEditingFeatureTests(directory, Check);
        await RunLibraryFeatureTests(directory, Check);
        RunPaperFeatureTests(directory, Check);
        await RunShortcutFeatureTests(directory, Check);

        var performance = new Stopwatch(); performance.Start();
        var stressLesson = new Lesson();
        for (var n = 0; n < 2000; n++) stressLesson.Items.Add(new BoardItem { Kind = "pen", Size = 3, Points = Enumerable.Range(0, 12).Select(i => new BoardPoint(n % 80 * 18 + i, n / 80 * 25 + Math.Sin(i) * 4)).ToList() });
        Surface.SetLesson(stressLesson, new Dictionary<string, byte[]>());
        var sceneMilliseconds = performance.Elapsed.TotalMilliseconds;
        performance.Restart();
        for (var frame = 0; frame < 240; frame++) Surface.Preview(new BoardItem { Kind = "pen", Points = Enumerable.Range(0, 150).Select(i => new BoardPoint(i * 3, Math.Sin(i * .05 + frame) * 90 + 300)).ToList(), Size = 4 });
        var averageDrawMilliseconds = performance.Elapsed.TotalMilliseconds / 240;
        Check(Surface.Lesson.Items.Count == 2000, "Retained drawing scene supports 2,000 existing strokes");
        performance.Restart();
        for (var frame = 0; frame < 1000; frame++) Surface.SetCamera(-20000 + frame * 30, -8000 + frame * 12, .8);
        var panMilliseconds = performance.Elapsed.TotalMilliseconds / 1000;
        Check(Surface.Lesson.Items.Count == 2000, "Camera navigation retains the existing 2,000-stroke scene");
        var report = new { Passed = checks.Count, Checks = checks, CanvasAreaPercent = Math.Round(workspaceRatio * 100, 1), SceneBuildMilliseconds = Math.Round(sceneMilliseconds, 2), AveragePreviewUpdateMilliseconds = Math.Round(averageDrawMilliseconds, 3), AverageCameraUpdateMilliseconds = Math.Round(panMilliseconds, 4), Note = "CPU-side drawing preparation; not an end-to-end input latency or GPU frame-rate benchmark." };
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        autosaveTimer.Stop(); closing = true;
    }

    private void CaptureWindow(string path)
    {
        UpdateLayout();
        var content = (FrameworkElement)Content;
        var target = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); target.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(target)); using var file = File.Create(path); png.Save(file);
    }
}
