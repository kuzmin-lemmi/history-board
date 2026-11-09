using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HistoryBoard;

public partial class MainWindow
{
    private async Task RunTeachingFeatureTests(string directory, Action<bool, string> check)
    {
        var folder = Path.Combine(directory, "teaching-tests");
        Directory.CreateDirectory(folder);
        lesson = new Lesson(); assets = []; currentPath = null; undo.Clear(); redo.Clear(); activeFrame = -1;
        Surface.SetLesson(lesson, assets); LessonTitle.Text = lesson.Title; UpdateUi(); SetCamera(0, 0, 1);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // ---- Shapes and marker ----
        check(ShapeRect(new Point(10, 10), new Point(40, 100), true).Width == 90 && ShapeRect(new Point(10, 10), new Point(-30, 20), true) == new Rect(-30, 10, 40, 40), "Shift turns a dragged rectangle or oval into a square or circle in any direction");
        SetTool(BoardTool.Rectangle);
        int undoDepth() => undo.Count;
        var depth = undoDepth();
        draft = new BoardItem { Kind = "rect", X = 100, Y = 100, Width = 3, Height = 3, Color = color, Size = 4 }; gesture = true; gestureBefore = lesson.Clone();
        FinishGesture();
        check(lesson.Items.Count == 0 && undoDepth() == depth, "An accidental tiny shape is not added to the board");
        var rect = new BoardItem { Kind = "rect", X = 100, Y = 100, Width = 300, Height = 200, Color = "#C94F49", Size = 4 };
        draft = rect; gesture = true; gestureBefore = lesson.Clone(); FinishGesture();
        check(lesson.Items.Contains(rect) && undoDepth() == depth + 1, "A drawn rectangle is added as one undoable step");
        check(BoardSurface.ShapeHit(rect, new Point(101, 200)) && !BoardSurface.ShapeHit(rect, new Point(250, 200)), "An outlined shape is picked by its border, leaving the area inside it clickable");
        SetTool(BoardTool.Select); Surface.Select(rect); fillShapes = false; OnFill(this, new RoutedEventArgs());
        check(rect.Fill == "#40C94F49" && BoardSurface.ShapeHit(rect, new Point(250, 200)) && FillButton.Visibility == Visibility.Visible, "«Заливка» gives the selected shape a translucent fill of its own color");
        color = "#427CB3"; ChangeSelected(i => { i.Color = color; if (i.Fill != null) i.Fill = BoardSurface.FillFor(color); });
        check(rect.Color == "#427CB3" && rect.Fill == "#40427CB3", "Changing a filled shape's color recolors its fill as well");
        var oval = new BoardItem { Kind = "ellipse", X = 600, Y = 100, Width = 200, Height = 100, Color = "#5F895A", Size = 4 };
        Surface.Add(oval);
        check(BoardSurface.ShapeHit(oval, new Point(700, 101)) && !BoardSurface.ShapeHit(oval, new Point(700, 150)) && !BoardSurface.ShapeHit(oval, new Point(602, 102)), "An oval is picked along its curved outline only");

        SetTool(BoardTool.Marker); strokeSize = 4;
        var marker = new BoardItem { Kind = "marker", Color = "#D69A38", Size = MarkerSize, Points = [new(100, 400), new(500, 400)] };
        draft = marker; gesture = true; gestureBefore = lesson.Clone(); FinishGesture();
        check(lesson.Items.Contains(marker) && marker.Size == 20, "The marker draws a wide stroke, five times the pencil thickness");
        SetTool(BoardTool.Select); Surface.Select(marker); AdjustSize(1);
        check(marker.Size == 25 && strokeSize == 5, "Changing the marker size keeps its width proportional");
        var pixels = Pixels(Surface.RenderBitmap(new Rect(280, 390, 20, 20), 20, false), 10, 10);
        check(pixels.R > pixels.B && pixels.B > 120, "The marker is translucent: the paper stays visible beneath the highlight");

        // ---- Callouts ----
        SetTool(BoardTool.Callout);
        var tip = new Point(900, 600);
        draft = NewCallout(tip, tip); gesture = true; gestureBefore = lesson.Clone(); depth = undoDepth();
        FinishGesture();
        check(editing?.Kind == "callout" && Editor.Visibility == Visibility.Visible && editing.X > tip.X && editing.Y < tip.Y, "A click with the callout tool places a bubble beside the point and starts typing");
        Editor.Text = "Здесь была битва"; var callout = editing!; CommitEditor();
        check(lesson.Items.Contains(callout) && callout.Text == "Здесь была битва" && Math.Abs(callout.X + callout.Points[0].X - tip.X) < 1e-9 && Math.Abs(callout.Y + callout.Points[0].Y - tip.Y) < 1e-9 && undoDepth() == depth + 1,
            "A typed callout keeps pointing at the chosen place");
        check(Surface.Hit(new Point(callout.X + 5, callout.Y + 5)) == callout && Surface.Bounds(callout).Contains(tip), "The callout bubble is selectable and its bounds include the pointer");
        Undo(); check(!lesson.Items.Any(i => i.Kind == "callout"), "Undo removes a new callout"); Redo();
        // Undo and redo restore copies of the lesson, so look the objects up again.
        rect = lesson.Items.Single(i => i.Kind == "rect"); marker = lesson.Items.Single(i => i.Kind == "marker"); callout = lesson.Items.Single(i => i.Kind == "callout");
        draft = NewCallout(tip, new Point(1200, 300)); gesture = true; gestureBefore = lesson.Clone(); depth = undoDepth();
        FinishGesture(); Editor.Text = "  "; CommitEditor();
        check(lesson.Items.Count(i => i.Kind == "callout") == 1 && undoDepth() == depth, "An empty callout is discarded without an undo step");

        // ---- Timeline ----
        check(Timeline.Year("1812") == 1812 && Timeline.Year("22 июня 1941") == 1941 && Timeline.Year("753 до н. э.") == -753 && Timeline.Year("XIX век") == 1850 && Timeline.Year("17 в.") == 1650 && Timeline.Year("Смута") == null,
            "Timeline dates are read as years, including BC dates and centuries");
        var events = Timeline.Parse("1812 — Отечественная война\n1825: Восстание декабристов\n1941 - 1945 — Великая Отечественная война\n\nСмута");
        check(events.Count == 4 && events[0].Title == "Отечественная война" && events[1].Title == "Восстание декабристов" && events[2].Date == "1941 - 1945" && events[3].Title == "", "Timeline lines split into date and event with the usual separators");
        var scaled = Timeline.Arrange(new BoardItem { Kind = "timeline", Text = "2000 — В\n1800 — А\n1900 — Б", Width = 1000, Size = 20 });
        check(scaled.Proportional && scaled.Events.Select(e => e.Event.Title).SequenceEqual(["А", "Б", "В"]) && Math.Abs(scaled.Events[1].X - 500) < 1e-9, "Dated events are sorted and placed to scale");
        var even = Timeline.Arrange(new BoardItem { Kind = "timeline", Text = "Начало — а\nСередина — б\nКонец — в", Width = 1000, Size = 20 });
        check(!even.Proportional && Math.Abs(even.Events[1].X - 500) < 1e-9, "Events without years are spaced evenly in the written order");
        var crowded = Timeline.Arrange(new BoardItem { Kind = "timeline", Text = "1700 — а\n1701 — б\n1702 — в\n1900 — г", Width = 1000, Size = 20 });
        check(crowded.Events[0].Row != crowded.Events[1].Row && crowded.Events[^1].Row == 0, "Close events alternate rows so their labels do not overlap");
        check(NormalizeTimeline(" 1812 — а \r\n\r\n1825 — б ") == "1812 — а\n1825 — б", "Blank lines and spaces are removed from timeline text");
        SetCamera(0, 0, 1);
        var timeline = AddTimeline(Timeline.Example)!;
        var timelineBounds = Surface.Bounds(timeline);
        check(timeline.Kind == "timeline" && timelineBounds.Height > 100 && Surface.Selected == timeline, "A timeline is added in the middle of the current view and selected");
        var timelinePixels = RenderAlone(timeline);
        var axisRow = (int)(Timeline.Arrange(timeline).AxisY * timelinePixels.PixelHeight / timelineBounds.Height);
        var darkest = Enumerable.Range(axisRow - 4, 9).Min(y => Pixels(timelinePixels, (int)(timelinePixels.PixelWidth * .5), y).R);
        check(darkest < 140, "The timeline axis is drawn across the board");

        // ---- Maps: rotation and crop ----
        var map = await AddTestMap();
        var original = map.Clone(); var center = new Point(map.X + map.Width / 2, map.Y + map.Height / 2);
        RotateImage(map);
        check(map.Rotation == 90 && Math.Abs(map.Width - original.Height) < 1e-9 && Math.Abs(map.X + map.Width / 2 - center.X) < 1e-9, "Rotating a map swaps its sides around the same centre");
        var turned = RenderAlone(map);
        var top = Pixels(turned, turned.PixelWidth / 2, (int)(turned.PixelHeight * .1)); var bottom = Pixels(turned, turned.PixelWidth / 2, (int)(turned.PixelHeight * .9));
        check(top.R > top.B && bottom.B > bottom.R, "A map turned clockwise shows its left side at the top");
        Undo(); map = lesson.Items.Single(i => i.Kind == "image");
        rect = lesson.Items.Single(i => i.Kind == "rect"); marker = lesson.Items.Single(i => i.Kind == "marker"); timeline = lesson.Items.Single(i => i.Kind == "timeline");
        check(map.Rotation == 0 && map.Width == original.Width, "Undo restores the map orientation");
        ApplyCrop(map, new Rect(.5, 0, .5, 1));
        check(map.IsCropped && Math.Abs(map.Width - original.Width / 2) < 1e-6 && Math.Abs(map.Height - original.Height) < 1e-6, "Cropping keeps the scale of the remaining part of the map");
        var cropped = RenderAlone(map);
        var left = Pixels(cropped, (int)(cropped.PixelWidth * .05), cropped.PixelHeight / 2);
        check(left.B > left.R, "Only the chosen part of the map is shown after cropping");
        RotateImage(map);
        var both = RenderAlone(map);
        var bothTop = Pixels(both, both.PixelWidth / 2, (int)(both.PixelHeight * .1));
        check(map.Rotation == 90 && bothTop.B > bothTop.R && Math.Abs(map.Height - original.Width / 2) < 1e-6, "Crop and rotation combine");
        ApplyCrop(map, new Rect(0, 0, 1, 1));
        check(!map.IsCropped && Math.Abs(map.Height - original.Width) < 1e-6, "«Вся карта» restores the full picture");

        // ---- File format ----
        map.Step = 2; marker.Step = 1;
        var path = Path.Combine(folder, "Инструменты урока.hboard");
        LessonFile.Save(path, lesson, UsedAssets(lesson));
        var reopened = LessonFile.Load(path);
        var savedMap = reopened.Lesson.Items.Single(i => i.Kind == "image");
        check(reopened.Lesson.Version == 3 && savedMap.Rotation == 90 && savedMap.Step == 2 && reopened.Lesson.Items.Single(i => i.Kind == "rect").Fill == "#40427CB3" &&
            reopened.Lesson.Items.Single(i => i.Kind == "callout").Points.Count == 1 && reopened.Lesson.Items.Single(i => i.Kind == "timeline").Text == timeline.Text,
            "Shapes, markers, callouts, timelines, map rotation and reveal steps are saved in the lesson");
        var simplePath = Path.Combine(folder, "Простой урок.hboard");
        LessonFile.Save(simplePath, new Lesson { Items = [new BoardItem { Text = "Только текст" }] }, new Dictionary<string, byte[]>());
        check(LessonFile.Load(simplePath).Lesson.Version == 2, "Lessons without the new objects are still saved in the format version 1.2 can open");
        bool Rejected(Action<BoardItem> change)
        {
            var item = new BoardItem { Kind = "image", AssetId = Guid.NewGuid().ToString("N") }; change(item);
            try { LessonFile.Validate(new Lesson { Items = [item] }); return false; } catch (InvalidDataException) { return true; }
        }
        check(Rejected(i => i.Rotation = 45) && Rejected(i => { i.CropX = .6; i.CropWidth = .5; }) && Rejected(i => i.Step = 100) && Rejected(i => { i.Kind = "callout"; i.AssetId = null; }) && Rejected(i => i.Fill = "красный"),
            "Invalid rotation, crop, reveal step, callout pointer and fill are rejected when opening a file");

        // ---- Presentation: steps, pointer, spotlight, curtain ----
        SetCamera(0, 0, 1);
        var whole = Surface.ContentBounds(); whole.Inflate(40, 40);
        lesson.Frames.Add(new LessonFrame { Name = "Карта и пометки", X = whole.X, Y = whole.Y, Width = whole.Width, Height = whole.Height });
        lesson.Frames.Add(new LessonFrame { Name = "Пустой кадр", X = whole.Right + 5000, Y = whole.Y, Width = 1000, Height = 600 });
        RebuildFrames(); Surface.Redraw();
        check(Surface.ShowStepBadges, "Reveal steps are marked with numbered badges in the editor");
        StartPresentation(); await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var stepMap = lesson.Items.Single(i => i.Kind == "image"); var stepMarker = lesson.Items.Single(i => i.Kind == "marker");
        check(presenting && activeFrame == 0 && Surface.IsHidden(stepMap) && Surface.IsHidden(stepMarker) && !Surface.IsHidden(rect) && !Surface.ShowStepBadges && PresentationLabel.Text.Contains("0 из 2"),
            "In a presentation, stepped objects of the frame start hidden while the others are visible");
        PresentStep(1);
        check(activeFrame == 0 && !Surface.IsHidden(stepMarker) && Surface.IsHidden(stepMap), "The first «next» reveals step 1 and stays on the frame");
        PresentStep(1);
        check(activeFrame == 0 && !Surface.IsHidden(stepMap) && PresentationLabel.Text.Contains("2 из 2"), "The second «next» reveals step 2");
        PresentStep(1);
        check(activeFrame == 1, "After the last step «next» moves to the following frame");
        PresentStep(-1);
        check(activeFrame == 0 && !Surface.IsHidden(stepMap) && !Surface.IsHidden(stepMarker), "Going back returns to the previous frame fully revealed");
        PresentStep(-1);
        check(activeFrame == 0 && Surface.IsHidden(stepMap) && !Surface.IsHidden(stepMarker), "«Back» within a frame hides the last revealed step");

        SetPresentationTool(PresentationTool.Spotlight);
        Overlay.Pointer = new Point(Overlay.ActualWidth / 2, Overlay.ActualHeight / 2); Overlay.HasPointer = true; Overlay.InvalidateVisual(); Overlay.UpdateLayout();
        var overlay = RenderElement(Overlay);
        var lit = Pixels(overlay, overlay.PixelWidth / 2, overlay.PixelHeight / 2); var dark = Pixels(overlay, 5, 5);
        check(lit.A == 0 && dark.A > 150 && Surface.Cursor == Cursors.None, "The spotlight dims everything except a circle around the pointer");
        AdjustPresentationTool(120); check(Overlay.SpotlightRadius == 195, "The mouse wheel widens the spotlight");
        TogglePresentationTool(PresentationTool.Curtain); Overlay.CurtainTop = 100; Overlay.InvalidateVisual(); Overlay.UpdateLayout();
        overlay = RenderElement(Overlay);
        check(Pixels(overlay, 30, 50).A == 0 && Pixels(overlay, 30, Math.Min(overlay.PixelHeight - 1, 260)).A == 255, "The curtain covers the screen below its edge");
        TogglePresentationTool(PresentationTool.Pointer); Overlay.InvalidateVisual(); Overlay.UpdateLayout();
        overlay = RenderElement(Overlay);
        var dot = Pixels(overlay, (int)Overlay.Pointer.X, (int)Overlay.Pointer.Y);
        check(Overlay.Mode == PresentationTool.Pointer && dot.A == 255 && dot.R > 200 && Pixels(overlay, 5, 5).A == 0, "The pointer shows a red dot without covering the board");
        CaptureWindow(Path.Combine(directory, "13-presentation-tools.png"));
        TogglePresentationTool(PresentationTool.Pointer);
        check(Overlay.Mode == PresentationTool.None && Surface.Cursor == Cursors.Arrow, "Pressing the same tool again turns it off");
        var standard = ShortcutProfile.Default();
        check(standard.Resolve(Key.P, ModifierKeys.None, ShortcutContext.Presentation, false) == BoardCommand.PresentationPointer && standard.Resolve(Key.P, ModifierKeys.None, ShortcutContext.Editor, false) == BoardCommand.PenTool &&
            standard.Resolve(Key.S, ModifierKeys.None, ShortcutContext.Presentation, false) == BoardCommand.PresentationSpotlight && standard.Resolve(Key.B, ModifierKeys.None, ShortcutContext.Presentation, false) == BoardCommand.PresentationCurtain,
            "P, S and B switch the pointer, spotlight and curtain during a show without changing editor keys");
        var defaults = ShortcutProfile.Default();
        check(ShortcutProfile.Definitions.All(d => defaults.Assign(d.Command, defaults.Get(d.Command).ToArray()) == null), "The new tool shortcuts do not conflict with the existing ones");
        EndPresentation();
        check(!presenting && lesson.Items.All(i => !Surface.IsHidden(i)) && Surface.ShowStepBadges && Overlay.Visibility == Visibility.Collapsed, "Ending the show reveals every object again");

        // ---- PDF ----
        var pdfPath = Path.Combine(folder, "Кадры урока.pdf");
        ExportFramesPdf(pdfPath);
        var pdf = File.ReadAllBytes(pdfPath); var text = Encoding.Latin1.GetString(pdf);
        var xref = long.Parse(text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        check(text.StartsWith("%PDF-1.4") && text.TrimEnd().EndsWith("%%EOF") && text.Contains("/Count 2") && text.Substring((int)xref, 4) == "xref", "Frames export to a PDF with a valid cross-reference table, one page per frame");
        var streamStart = text.IndexOf("/DCTDecode", StringComparison.Ordinal);
        var dataStart = text.IndexOf("stream\n", streamStart, StringComparison.Ordinal) + 7;
        var lengthText = text[(text.IndexOf("/Length ", streamStart, StringComparison.Ordinal) + 8)..].Split(' ')[0];
        var length = int.Parse(lengthText);
        check(pdf[dataStart] == 0xFF && pdf[dataStart + 1] == 0xD8 && text.Substring(dataStart + length, 10) == "\nendstream", "PDF pages embed the rendered frames as JPEG pictures");
        var offsets = text[(int)xref..].Split('\n').Skip(3).TakeWhile(l => l.EndsWith(" n ")).Select(l => int.Parse(l[..10])).ToList();
        check(offsets.Count == 3 + 2 * 3 && offsets.Select((o, i) => text.Substring(o, $"{i + 1} 0 obj".Length) == $"{i + 1} 0 obj").All(ok => ok), "Every PDF object is listed at its exact position");
        check(text.Contains(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(lesson.Title))), "The PDF keeps the Cyrillic lesson title");

        Surface.Select(timeline); SetTool(BoardTool.Select); Fit(); UpdateUi(); await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        CaptureWindow(Path.Combine(directory, "12-teaching-tools.png"));
        check(toolButtons.Count == 11 && toolButtons.Values.Last().TranslatePoint(new Point(0, toolButtons.Values.Last().ActualHeight), ToolRail).Y <= ToolRail.ActualHeight - 30,
            "All eleven tools fit in the tool rail above the help button");
    }

    private async Task<BoardItem> AddTestMap()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(BoardSurface.Brush("#C94F49"), null, new Rect(0, 0, 100, 100));
            dc.DrawRectangle(BoardSurface.Brush("#427CB3"), null, new Rect(100, 0, 100, 100));
        }
        var bitmap = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var memory = new MemoryStream(); encoder.Save(memory);
        await AddImageBytesAsync(memory.ToArray());
        return lesson.Items.First(i => i.Kind == "image");
    }

    // Renders one object on its own, so overlapping objects do not affect pixel checks.
    private BitmapSource RenderAlone(BoardItem item)
    {
        var probe = new BoardSurface();
        var data = item.AssetId != null ? new Dictionary<string, byte[]> { [item.AssetId] = assets[item.AssetId] } : new Dictionary<string, byte[]>();
        probe.SetLesson(new Lesson { Paper = "white", Items = [item.Clone()] }, data);
        return probe.RenderBitmap(probe.Bounds(item), 400, true);
    }

    private static (byte B, byte G, byte R, byte A) Pixels(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0).CopyPixels(new Int32Rect(Math.Clamp(x, 0, bitmap.PixelWidth - 1), Math.Clamp(y, 0, bitmap.PixelHeight - 1), 1, 1), pixel, 4, 0);
        return (pixel[0], pixel[1], pixel[2], pixel[3]);
    }

    private static BitmapSource RenderElement(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)element.ActualWidth), Math.Max(1, (int)element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); return bitmap;
    }
}
