using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HistoryBoard;

// Each object owns a retained DrawingVisual. Moving the mouse redraws only the active
// object; existing maps, text and strokes stay in WPF's composition tree.
public sealed class BoardSurface : FrameworkElement
{
    private readonly VisualCollection visuals;
    private readonly ContainerVisual world = new();
    private readonly MatrixTransform camera = new();
    private readonly Dictionary<string, DrawingVisual> objects = [];
    private readonly Dictionary<string, BitmapSource> images = [];
    private readonly DrawingVisual selection = new();
    private readonly DrawingVisual preview = new();
    public Lesson Lesson { get; private set; } = new();
    private readonly List<BoardItem> selectedItems = [];
    public IReadOnlyList<BoardItem> SelectedItems => selectedItems;
    public BoardItem? Selected => selectedItems.FirstOrDefault();
    public bool SelectionLocked => selectedItems.Any(i => i.Locked);
    private Rect marquee = Rect.Empty;
    public Transform CameraTransform => camera;
    public Point ScreenToWorld(Point point) => camera.Inverse.Transform(point);
    public Point WorldToScreen(Point point) => camera.Transform(point);
    public void SetCamera(double x, double y, double zoom) { camera.Matrix = new Matrix(zoom, 0, 0, zoom, -x * zoom, -y * zoom); DrawSelection(); InvalidateVisual(); }
    public Rect SelectedBounds { get { var bounds = Rect.Empty; foreach (var item in selectedItems) bounds.Union(Bounds(item)); return bounds; } }
    protected override int VisualChildrenCount => visuals.Count;
    protected override Visual GetVisualChild(int index) => visuals[index];

    public BoardSurface()
    {
        visuals = new VisualCollection(this) { world };
        world.Transform = camera;
        world.Children.Add(preview); world.Children.Add(selection);
        Focusable = true;
        ClipToBounds = true;
    }

    public void SetLesson(Lesson lesson, IReadOnlyDictionary<string, byte[]> assets)
    {
        var loaded = new Dictionary<string, BitmapSource>();
        foreach (var pair in assets) loaded[pair.Key] = images.TryGetValue(pair.Key, out var cached) ? cached : LessonFile.DecodeImage(pair.Value);
        Lesson = lesson;
        images.Clear(); foreach (var pair in loaded) images[pair.Key] = pair.Value;
        Rebuild();
    }

    public void AddImage(string id, BitmapSource image) => images[id] = image;

    public void Rebuild()
    {
        world.Children.Clear(); objects.Clear();
        foreach (var item in Lesson.Items)
        {
            var visual = new DrawingVisual();
            objects[item.Id] = visual; world.Children.Add(visual); DrawItem(visual, item);
        }
        world.Children.Add(preview); world.Children.Add(selection);
        selectedItems.Clear(); marquee = Rect.Empty;
        using (preview.RenderOpen()) { }
        DrawSelection();
        InvalidateVisual();
    }

    public void Add(BoardItem item)
    {
        Lesson.Items.Add(item);
        var visual = new DrawingVisual(); objects[item.Id] = visual;
        world.Children.Insert(world.Children.Count - 2, visual); DrawItem(visual, item);
        InvalidateVisual();
    }

    public void Refresh(BoardItem item)
    {
        if (objects.TryGetValue(item.Id, out var visual)) DrawItem(visual, item);
        if (selectedItems.Contains(item)) DrawSelection();
    }

    public void RefreshMany(IEnumerable<BoardItem> items)
    {
        foreach (var item in items)
            if (objects.TryGetValue(item.Id, out var visual)) DrawItem(visual, item);
        DrawSelection();
    }

    public void Remove(BoardItem item)
    {
        Lesson.Items.Remove(item);
        if (objects.Remove(item.Id, out var visual)) world.Children.Remove(visual);
        selectedItems.Remove(item); DrawSelection();
        InvalidateVisual();
    }

    public void RemoveMany(IEnumerable<BoardItem> items)
    {
        var removed = items.ToHashSet();
        Lesson.Items.RemoveAll(removed.Contains);
        foreach (var item in removed)
            if (objects.Remove(item.Id, out var visual)) world.Children.Remove(visual);
        selectedItems.RemoveAll(removed.Contains); DrawSelection(); InvalidateVisual();
    }

    public void Select(BoardItem? item) => SelectMany(item == null ? [] : [item]);
    public IEnumerable<BoardItem> GroupMembers(BoardItem item) => item.GroupId == null ? [item] : Lesson.Items.Where(i => i.GroupId == item.GroupId);
    public void SelectMany(IEnumerable<BoardItem> items)
    {
        var candidates = items.ToHashSet();
        var groups = candidates.Where(i => i.GroupId != null).Select(i => i.GroupId!).ToHashSet();
        selectedItems.Clear();
        selectedItems.AddRange(Lesson.Items.Where(i => candidates.Contains(i) || i.GroupId != null && groups.Contains(i.GroupId)));
        DrawSelection();
    }
    public void ToggleSelection(BoardItem item)
    {
        var group = GroupMembers(item).ToArray();
        SelectMany(group.Any(selectedItems.Contains) ? selectedItems.Except(group).ToArray() : selectedItems.Concat(group).ToArray());
    }
    public void ShowMarquee(Rect rect) { marquee = rect; DrawSelection(); }
    public void Preview(BoardItem? item) { if (item == null) { using (preview.RenderOpen()) { } } else DrawItem(preview, item); }
    public void HideItem(BoardItem item, bool hide) { if (objects.TryGetValue(item.Id, out var v)) v.Opacity = hide ? 0 : 1; }

    public static FormattedText Format(BoardItem item)
    {
        var text = item.Numbered ? string.Join("\n", item.Text.Split('\n').Select((line, n) => $"{n + 1}.  {line}")) : item.Text;
        var typeface = new Typeface(new FontFamily(item.Serif ? "Georgia" : "Segoe UI"), FontStyles.Normal, item.Bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        var result = new FormattedText(string.IsNullOrEmpty(text) ? " " : text, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight, typeface, item.Size, Brush(item.Color), 1.0)
        { MaxTextWidth = Math.Max(30, item.Width), LineHeight = item.Size * 1.4, Trimming = TextTrimming.None };
        return result;
    }

    public Rect Bounds(BoardItem item)
    {
        if (item.Kind == "image") return new Rect(item.X, item.Y, item.Width, item.Height);
        if (item.Kind is "rect" or "ellipse")
        {
            var shape = new Rect(item.X, item.Y, item.Width, item.Height); shape.Inflate(item.Size / 2 + 2, item.Size / 2 + 2); return shape;
        }
        if (item.Kind == "timeline") return new Rect(item.X, item.Y, item.Width, Timeline.Arrange(item).Height);
        if (item.Kind == "callout")
        {
            var bubble = CalloutBubble(item);
            if (item.Points.Count > 0) bubble.Union(new Rect(item.X + item.Points[0].X - 4, item.Y + item.Points[0].Y - 4, 8, 8));
            return bubble;
        }
        if (item.Kind == "text")
        {
            var text = Format(item);
            return new Rect(item.X, item.Y, Math.Max(30, text.WidthIncludingTrailingWhitespace), Math.Max(item.Size * 1.4, text.Height));
        }
        if (item.Points.Count == 0) return new Rect(item.X, item.Y, 1, 1);
        var rect = new Rect(new Point(item.Points.Min(p => p.X) + item.X, item.Points.Min(p => p.Y) + item.Y),
            new Point(item.Points.Max(p => p.X) + item.X, item.Points.Max(p => p.Y) + item.Y));
        rect.Inflate(Math.Max(8, item.Size * 2), Math.Max(8, item.Size * 2));
        return rect;
    }

    public BoardItem? Hit(Point point, bool includeImages = true)
    {
        for (var n = Lesson.Items.Count - 1; n >= 0; n--)
        {
            var item = Lesson.Items[n];
            if (!includeImages && item.Kind == "image") continue;
            if (item.Kind is "text" or "image" or "timeline")
            {
                var bounds = Bounds(item); bounds.Inflate(5, 5);
                if (bounds.Contains(point)) return item;
            }
            else if (item.Kind == "callout")
            {
                var bubble = CalloutBubble(item); bubble.Inflate(4, 4);
                if (bubble.Contains(point)) return item;
            }
            else if (item.Kind is "rect" or "ellipse")
            {
                if (ShapeHit(item, point)) return item;
            }
            else
            {
                var local = new Point(point.X - item.X, point.Y - item.Y);
                for (var i = 1; i < item.Points.Count; i++)
                    if (DistanceToSegment(local, item.Points[i - 1].ToPoint(), item.Points[i].ToPoint()) < Math.Max(9, item.Size + 4)) return item;
                if (item.Points.Count == 1 && (local - item.Points[0].ToPoint()).Length < Math.Max(9, item.Size)) return item;
            }
        }
        return null;
    }

    public static bool Resizable(BoardItem item) => item.Kind is "image" or "rect" or "ellipse" or "timeline";

    // Outlined shapes are picked by their border, so the map inside a circled area stays clickable.
    public static bool ShapeHit(BoardItem item, Point point)
    {
        var rect = new Rect(item.X, item.Y, item.Width, item.Height);
        var tolerance = Math.Max(9, item.Size / 2 + 6);
        if (item.Kind == "rect")
        {
            var outer = rect; outer.Inflate(tolerance, tolerance);
            if (!outer.Contains(point)) return false;
            if (item.Fill != null) return true;
            var inner = rect;
            if (inner.Width <= 2 * tolerance || inner.Height <= 2 * tolerance) return true;
            inner.Inflate(-tolerance, -tolerance);
            return !inner.Contains(point);
        }
        var a = Math.Max(1, item.Width / 2); var b = Math.Max(1, item.Height / 2);
        var dx = (point.X - (item.X + a)) / a; var dy = (point.Y - (item.Y + b)) / b;
        var r = Math.Sqrt(dx * dx + dy * dy);
        return item.Fill != null ? r <= 1 + tolerance / Math.Min(a, b) : Math.Abs(r - 1) * Math.Min(a, b) <= tolerance;
    }

    public static double CalloutPadding(BoardItem item) => Math.Max(10, item.Size * .45);
    public static Rect CalloutBubble(BoardItem item)
    {
        var text = Format(item); var pad = CalloutPadding(item);
        return new Rect(item.X - pad, item.Y - pad, Math.Max(30, text.WidthIncludingTrailingWhitespace) + 2 * pad, Math.Max(item.Size * 1.4, text.Height) + 2 * pad);
    }

    public static SolidColorBrush Translucent(string color, byte alpha)
    {
        var value = (Color)ColorConverter.ConvertFromString(color); value.A = alpha;
        var brush = new SolidColorBrush(value); brush.Freeze(); return brush;
    }
    public static string FillFor(string color)
    {
        var value = (Color)ColorConverter.ConvertFromString(color);
        return $"#40{value.R:X2}{value.G:X2}{value.B:X2}";
    }

    public static double DistanceToSegment(Point p, Point a, Point b)
    {
        var d = b - a;
        if (d.LengthSquared < 0.001) return (p - a).Length;
        var t = Math.Clamp(Vector.Multiply(p - a, d) / d.LengthSquared, 0, 1);
        return (p - (a + d * t)).Length;
    }

    private void DrawItem(DrawingVisual visual, BoardItem item)
    {
        using var dc = visual.RenderOpen();
        dc.PushTransform(new TranslateTransform(item.X, item.Y));
        if (item.Kind == "image" && item.AssetId != null && images.TryGetValue(item.AssetId, out var image))
            DrawImage(dc, item, image);
        else if (item.Kind == "text") dc.DrawText(Format(item), new Point(0, 0));
        else if (item.Kind is "rect" or "ellipse")
        {
            var outline = new Pen(Brush(item.Color), item.Size) { LineJoin = PenLineJoin.Round }; outline.Freeze();
            var fill = item.Fill != null ? Brush(item.Fill) : null;
            var rect = new Rect(0, 0, item.Width, item.Height);
            if (item.Kind == "rect") dc.DrawRoundedRectangle(fill, outline, rect, Math.Min(8, item.Size), Math.Min(8, item.Size));
            else dc.DrawEllipse(fill, outline, new Point(item.Width / 2, item.Height / 2), item.Width / 2, item.Height / 2);
        }
        else if (item.Kind == "callout") DrawCallout(dc, item);
        else if (item.Kind == "timeline") Timeline.Draw(dc, item);
        else if (item.Points.Count > 0)
        {
            // A marker is a wide translucent stroke that keeps the map beneath it readable.
            var brush = item.Kind == "marker" ? Translucent(item.Color, 0x66) : Brush(item.Color);
            var pen = new Pen(brush, item.Size) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; pen.Freeze();
            if (item.Points.Count == 1) dc.DrawEllipse(brush, null, item.Points[0].ToPoint(), item.Size / 2, item.Size / 2);
            else
            {
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(item.Points[0].ToPoint(), false, false);
                    context.PolyLineTo(item.Points.Skip(1).Select(p => p.ToPoint()).ToArray(), true, false);
                }
                geometry.Freeze(); dc.DrawGeometry(null, pen, geometry);
                if (item.Kind == "arrow")
                {
                    var end = item.Points[^1].ToPoint();
                    var direction = end - item.Points[^2].ToPoint();
                    if (direction.Length > 1)
                    {
                        var headSize = Math.Min(Math.Max(18, item.Size * 4.0), direction.Length * .6);
                        direction.Normalize(); var normal = new Vector(-direction.Y, direction.X);
                        var head = new StreamGeometry();
                        using (var c = head.Open()) { c.BeginFigure(end, true, true); c.LineTo(end - direction * headSize + normal * headSize * .46, true, false); c.LineTo(end - direction * headSize - normal * headSize * .46, true, false); }
                        head.Freeze(); dc.DrawGeometry(brush, null, head);
                    }
                }
            }
        }
        dc.Pop();
    }

    // Crop selects part of the source picture; rotation turns that part in 90-degree steps
    // inside the item's rectangle, whose width and height are already swapped for 90 and 270.
    private static void DrawImage(DrawingContext dc, BoardItem item, BitmapSource image)
    {
        var turned = item.Rotation is 90 or 270;
        var content = turned ? new Size(item.Height, item.Width) : new Size(item.Width, item.Height);
        var box = new Rect(item.Width / 2 - content.Width / 2, item.Height / 2 - content.Height / 2, content.Width, content.Height);
        dc.PushTransform(new RotateTransform(item.Rotation, item.Width / 2, item.Height / 2));
        dc.PushClip(new RectangleGeometry(box));
        var full = new Size(content.Width / item.CropWidth, content.Height / item.CropHeight);
        dc.DrawImage(image, new Rect(box.X - item.CropX * full.Width, box.Y - item.CropY * full.Height, full.Width, full.Height));
        dc.Pop(); dc.Pop();
    }

    private static void DrawCallout(DrawingContext dc, BoardItem item)
    {
        var bubble = CalloutBubble(item); bubble.Offset(-item.X, -item.Y);
        var tip = item.Points.Count > 0 ? item.Points[0].ToPoint() : new Point(bubble.Left, bubble.Bottom + 40);
        var center = new Point(bubble.X + bubble.Width / 2, bubble.Y + bubble.Height / 2);
        var direction = tip - center;
        var shape = (Geometry)new RectangleGeometry(bubble, 10, 10);
        if (!bubble.Contains(tip) && direction.Length > 1)
        {
            direction.Normalize();
            var normal = new Vector(-direction.Y, direction.X) * Math.Min(16, Math.Min(bubble.Width, bubble.Height) * .25);
            var tail = new StreamGeometry();
            using (var c = tail.Open()) { c.BeginFigure(center + normal, true, true); c.LineTo(tip, true, false); c.LineTo(center - normal, true, false); }
            shape = new CombinedGeometry(GeometryCombineMode.Union, shape, tail);
        }
        shape.Freeze();
        var border = new Pen(Brush(item.Color), Math.Max(1.5, item.Size * .07)) { LineJoin = PenLineJoin.Round }; border.Freeze();
        dc.DrawGeometry(Brush("#F7FFFFFF"), border, shape);
        dc.DrawText(Format(item), new Point(0, 0));
    }

    public bool ShowStepBadges { get; set; } = true;
    public bool IsHidden(BoardItem item) => objects.TryGetValue(item.Id, out var visual) && visual.Opacity == 0;
    public void Redraw() => DrawSelection();

    private void DrawSelection()
    {
        using var dc = selection.RenderOpen();
        var scale = Math.Max(.02, camera.Matrix.M11);
        if (!marquee.IsEmpty) dc.DrawRectangle(Brush("#1458725B"), new Pen(Brush("#58725B"), 1 / scale), marquee);
        if (ShowStepBadges)
            foreach (var stepped in Lesson.Items.Where(i => i.Step > 0))
            {
                // Numbered badges show the reveal order in the editor; they never appear in exports or the show.
                var corner = Bounds(stepped).TopLeft; var radius = 11 / scale;
                dc.DrawEllipse(Brush("#D69A38"), new Pen(Brushes.White, 1.5 / scale), corner, radius, radius);
                var label = new FormattedText(stepped.Step.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12 / scale, Brushes.White, 1.0);
                dc.DrawText(label, new Point(corner.X - label.Width / 2, corner.Y - label.Height / 2));
            }
        if (Selected == null) return;
        var rect = SelectedBounds; rect.Inflate(7 / scale, 7 / scale);
        var pen = new Pen(Brush(SelectionLocked ? "#9A8063" : "#58725B"), 1.5 / scale) { DashStyle = SelectionLocked ? DashStyles.Solid : DashStyles.Dash };
        dc.DrawRectangle(null, pen, rect);
        if (selectedItems.Count == 1 && Resizable(Selected) && !SelectionLocked)
            dc.DrawRoundedRectangle(Brushes.White, new Pen(Brush("#58725B"), 2 / scale), new Rect(rect.Right - 7 / scale, rect.Bottom - 7 / scale, 14 / scale, 14 / scale), 3 / scale, 3 / scale);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.PushTransform(camera);
        var area = new Rect(ScreenToWorld(new Point()), ScreenToWorld(new Point(ActualWidth, ActualHeight)));
        DrawPaper(dc, area, Math.Max(.02, camera.Matrix.M11));
        dc.Pop();
    }

    private void DrawPaper(DrawingContext dc, Rect area, double scale)
    {
        dc.DrawRectangle(Brush(Lesson.Paper == "white" ? "#FFFFFF" : "#FAF7EF"), null, area);
        if (Lesson.Paper is "plain" or "white") return;
        var spacing = 24.0;
        while (spacing * scale < 12) spacing *= 5;
        var tile = new DrawingVisual();
        using (var pattern = tile.RenderOpen())
        {
            pattern.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, spacing, spacing));
            var ink = Brush("#DEDCCF");
            var pen = new Pen(ink, .8 / scale);
            if (Lesson.Paper == "dots") pattern.DrawEllipse(Brush("#C2C8B8"), null, new Point(spacing / 2, spacing / 2), 1.1 / scale, 1.1 / scale);
            else
            {
                pattern.DrawLine(pen, new Point(0, 0), new Point(spacing, 0));
                if (Lesson.Paper == "grid") pattern.DrawLine(pen, new Point(0, 0), new Point(0, spacing));
            }
        }
        var brush = new DrawingBrush(tile.Drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, ViewboxUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, spacing, spacing), Viewbox = new Rect(0, 0, spacing, spacing) };
        brush.Freeze(); dc.DrawRectangle(brush, null, area);
    }

    public Rect ContentBounds()
    {
        var bounds = Rect.Empty;
        foreach (var item in Lesson.Items) bounds.Union(Bounds(item));
        return bounds;
    }

    public void ExportPng(string path, Rect? region = null, int maxSide = 8192)
        => File.WriteAllBytes(path, RenderPng(region, maxSide));

    public byte[] RenderPng(Rect? region = null, int maxSide = 8192)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(RenderBitmap(region, maxSide, false)));
        using var memory = new MemoryStream(); encoder.Save(memory); return memory.ToArray();
    }

    public (byte[] Jpeg, int Width, int Height) RenderJpeg(Rect region, int maxSide, int quality = 88)
    {
        var bitmap = RenderBitmap(region, maxSide, true);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0)));
        using var memory = new MemoryStream(); encoder.Save(memory); return (memory.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight);
    }

    public BitmapSource RenderBitmap(Rect? region, int maxSide, bool allowUpscale)
    {
        var area = region ?? ContentBounds();
        if (area.IsEmpty) area = new Rect(0, 0, Lesson.Width, Lesson.Height);
        else if (region == null) area.Inflate(32, 32);
        var scale = Math.Min(maxSide / area.Width, maxSide / area.Height);
        if (!allowUpscale) scale = Math.Min(1, scale);
        scale = Math.Min(scale, Math.Sqrt(32_000_000 / (area.Width * area.Height)));
        var width = Math.Max(1, (int)Math.Ceiling(area.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(area.Height * scale));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brush(Lesson.Paper == "white" ? "#FFFFFF" : "#FAF7EF"), null, new Rect(0, 0, width, height));
            dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, -area.X * scale, -area.Y * scale));
            DrawPaper(dc, area, scale);
            foreach (var item in Lesson.Items)
            {
                var obj = new DrawingVisual(); DrawItem(obj, item);
                dc.DrawDrawing(obj.Drawing);
            }
            dc.Pop();
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); target.Render(visual);
        target.Freeze(); return target;
    }

    public static SolidColorBrush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
}
