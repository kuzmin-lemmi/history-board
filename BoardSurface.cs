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
    public BoardItem? Selected { get; private set; }
    public Transform CameraTransform => camera;
    public Point ScreenToWorld(Point point) => camera.Inverse.Transform(point);
    public Point WorldToScreen(Point point) => camera.Transform(point);
    public void SetCamera(double x, double y, double zoom) => camera.Matrix = new Matrix(zoom, 0, 0, zoom, -x * zoom, -y * zoom);
    public Rect SelectedBounds => Selected is null ? Rect.Empty : Bounds(Selected);
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
        Selected = null;
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
        if (Selected == item) DrawSelection();
    }

    public void Remove(BoardItem item)
    {
        Lesson.Items.Remove(item);
        if (objects.Remove(item.Id, out var visual)) world.Children.Remove(visual);
        if (Selected == item) Select(null);
        InvalidateVisual();
    }

    public void Select(BoardItem? item) { Selected = item; DrawSelection(); }
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
            if (item.Kind is "text" or "image")
            {
                var bounds = Bounds(item); bounds.Inflate(5, 5);
                if (bounds.Contains(point)) return item;
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
            dc.DrawImage(image, new Rect(0, 0, item.Width, item.Height));
        else if (item.Kind == "text") dc.DrawText(Format(item), new Point(0, 0));
        else if (item.Points.Count > 0)
        {
            var brush = Brush(item.Color);
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

    private void DrawSelection()
    {
        using var dc = selection.RenderOpen();
        if (Selected == null) return;
        var rect = Bounds(Selected); rect.Inflate(7, 7);
        var pen = new Pen(Brush(Selected.Locked ? "#9A8063" : "#58725B"), 1.5) { DashStyle = Selected.Locked ? DashStyles.Solid : DashStyles.Dash };
        dc.DrawRectangle(null, pen, rect);
        if (Selected.Kind == "image" && !Selected.Locked)
            dc.DrawRoundedRectangle(Brushes.White, new Pen(Brush("#58725B"), 2), new Rect(rect.Right - 7, rect.Bottom - 7, 14, 14), 3, 3);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
    }

    public Rect ContentBounds()
    {
        var bounds = Rect.Empty;
        foreach (var item in Lesson.Items) bounds.Union(Bounds(item));
        return bounds;
    }

    public void ExportPng(string path, Rect? region = null)
    {
        var area = region ?? ContentBounds();
        if (area.IsEmpty) area = new Rect(0, 0, Lesson.Width, Lesson.Height);
        else if (region == null) area.Inflate(32, 32);
        var scale = Math.Min(1, Math.Min(8192 / area.Width, 8192 / area.Height));
        scale = Math.Min(scale, Math.Sqrt(32_000_000 / (area.Width * area.Height)));
        var width = Math.Max(1, (int)Math.Ceiling(area.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(area.Height * scale));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, -area.X * scale, -area.Y * scale));
            foreach (var item in Lesson.Items)
            {
                var obj = new DrawingVisual(); DrawItem(obj, item);
                dc.DrawDrawing(obj.Drawing);
            }
            dc.Pop();
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); target.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
        using var file = File.Create(path); encoder.Save(file);
    }

    public static SolidColorBrush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
}
