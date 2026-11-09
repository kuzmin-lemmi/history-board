using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShapePath = System.Windows.Shapes.Path;
using ShapeRectangle = System.Windows.Shapes.Rectangle;

namespace HistoryBoard;

// Shapes, marker, callouts, timelines, map crop/rotation and presentation reveal steps.
public partial class MainWindow
{
    private const double MarkerFactor = 5;
    private double MarkerSize => Math.Min(150, strokeSize * MarkerFactor);
    private bool StrokeContext => tool is BoardTool.Pen or BoardTool.Arrow or BoardTool.Marker or BoardTool.Rectangle or BoardTool.Ellipse ||
        editing == null && Surface.Selected is { } selected && BoardItem.IsStroke(selected.Kind);

    internal static Rect ShapeRect(Point start, Point end, bool square)
    {
        var delta = end - start;
        if (square)
        {
            var side = Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y));
            delta = new Vector(Math.Sign(delta.X == 0 ? 1 : delta.X) * side, Math.Sign(delta.Y == 0 ? 1 : delta.Y) * side);
        }
        return new Rect(start, start + delta);
    }

    private void OnFill(object sender, RoutedEventArgs e)
    {
        fillShapes = !fillShapes;
        ChangeSelected(i => { if (i.Kind is "rect" or "ellipse") i.Fill = fillShapes ? BoardSurface.FillFor(i.Color) : null; });
        UpdateProperties();
    }

    // Callouts: the press marks the place the callout points to, the release places the bubble.
    private BoardItem NewCallout(Point tip, Point bubble) => new()
    {
        Kind = "callout", X = bubble.X, Y = bubble.Y, Width = 320, Size = Math.Clamp(Math.Round(fontSize * .75), 14, 120), Color = color, Bold = bold, Serif = serif,
        Points = [new(tip.X - bubble.X, tip.Y - bubble.Y)]
    };

    private static void MoveCalloutDraft(BoardItem draft, Point tip, Point bubble)
    {
        draft.X = bubble.X; draft.Y = bubble.Y; draft.Points[0] = new(tip.X - bubble.X, tip.Y - bubble.Y);
    }

    internal void PlaceCallout(BoardItem callout)
    {
        var tip = new Point(callout.X + callout.Points[0].X, callout.Y + callout.Points[0].Y);
        if (callout.Points[0].ToPoint() is var offset && new Vector(offset.X, offset.Y).Length < 30 / zoom)
            MoveCalloutDraft(callout, tip, new Point(tip.X + 70 / zoom, tip.Y - 140 / zoom));
        BeginEditor(null, new Point(callout.X, callout.Y), callout);
    }

    // Timelines are edited as plain lines "дата — событие" in a small dialog.
    private string? TimelineDialog(string text, bool creating)
    {
        var window = new Window { Owner = this, Title = creating ? "Новая лента времени" : "Лента времени", Width = 520, Height = 470, MinWidth = 420, MinHeight = 340, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = BoardSurface.Brush("#FAF7EF") };
        var root = new DockPanel { Margin = new Thickness(20) };
        var hint = new TextBlock { Text = "Каждое событие — с новой строки: «дата — название».\nДаты с годами («1812», «22 июня 1941», «753 до н. э.», «XIX век») расставляются в масштабе, остальные — равномерно.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = BoardSurface.Brush("#5F6B62"), Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = creating ? "Добавить ленту" : "Сохранить", Padding = new Thickness(14, 7, 14, 7), Background = BoardSurface.Brush("#58725B"), Foreground = Brushes.White };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var input = new TextBox { Text = text.Replace("\n", Environment.NewLine), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 14, Padding = new Thickness(8), MaxLength = 20000 };
        root.Children.Add(input); window.Content = root;
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? NormalizeTimeline(input.Text) : null;
    }

    internal static string NormalizeTimeline(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    private void OnAddTimeline(object sender, RoutedEventArgs e)
    {
        if (presenting) return;
        CommitEditor(); FinishGesture();
        if (TimelineDialog(Timeline.Example, true) is { } text) AddTimeline(text);
    }

    internal BoardItem? AddTimeline(string text)
    {
        if (lesson.Items.Count >= 20000) { Status.Text = "В уроке может быть до 20 000 объектов."; return null; }
        var visible = VisibleRegion;
        var before = lesson.Clone();
        var item = new BoardItem
        {
            Kind = "timeline", Text = NormalizeTimeline(text), Width = Math.Clamp(visible.Width * .8, 400, 100000), Height = 1,
            Size = Math.Clamp(Math.Round(22 / Math.Max(zoom, .25)), 12, 160), Color = color, Serif = serif
        };
        item.X = visible.X + (visible.Width - item.Width) / 2; item.Y = visible.Y + visible.Height * .3;
        Surface.Add(item); SetTool(BoardTool.Select); Surface.Select(item); Changed(before);
        Status.Text = "● Лента времени добавлена. Двойной клик — изменить события, уголок — длина.";
        return item;
    }

    private void EditTimeline(BoardItem item)
    {
        if (Surface.GroupMembers(item).Any(i => i.Locked)) { Status.Text = "Лента закреплена. Сначала нажмите «Открепить»."; return; }
        if (TimelineDialog(item.Text, false) is not { } text || text == item.Text) return;
        var before = lesson.Clone(); item.Text = text; Surface.Refresh(item); Changed(before);
    }

    // Maps: rotation in 90-degree steps around the centre, crop by selecting a region of the source picture.
    private BoardItem? SelectedImage => Surface.SelectedItems.Count == 1 && Surface.Selected is { Kind: "image", Locked: false } image ? image : null;

    private void OnRotate(object sender, RoutedEventArgs e) { if (SelectedImage is { } image) RotateImage(image); }

    internal void RotateImage(BoardItem item)
    {
        CommitEditor(); FinishGesture();
        var before = lesson.Clone();
        var center = new Point(item.X + item.Width / 2, item.Y + item.Height / 2);
        (item.Width, item.Height) = (item.Height, item.Width);
        item.X = center.X - item.Width / 2; item.Y = center.Y - item.Height / 2;
        item.Rotation = (item.Rotation + 90) % 360;
        Surface.Refresh(item); Changed(before);
    }

    internal void ApplyCrop(BoardItem item, Rect crop)
    {
        crop = Rect.Intersect(crop, new Rect(0, 0, 1, 1));
        if (crop.IsEmpty || crop.Width < .01 || crop.Height < .01) return;
        var before = lesson.Clone();
        var turned = item.Rotation is 90 or 270;
        var contentWidth = (turned ? item.Height : item.Width) * crop.Width / item.CropWidth;
        var contentHeight = (turned ? item.Width : item.Height) * crop.Height / item.CropHeight;
        item.CropX = crop.X; item.CropY = crop.Y; item.CropWidth = crop.Width; item.CropHeight = crop.Height;
        (item.Width, item.Height) = turned ? (contentHeight, contentWidth) : (contentWidth, contentHeight);
        Surface.Refresh(item); Changed(before);
        Status.Text = item.IsCropped ? "● Карта кадрирована. Исходное изображение сохранено — кадр можно изменить." : "● Показана вся карта";
    }

    private void OnCrop(object sender, RoutedEventArgs e)
    {
        if (SelectedImage is not { AssetId: { } asset } item || !assets.TryGetValue(asset, out var bytes)) return;
        CommitEditor(); FinishGesture();
        if (CropDialog(LessonFile.DecodeImage(bytes), new Rect(item.CropX, item.CropY, item.CropWidth, item.CropHeight)) is { } crop) ApplyCrop(item, crop);
    }

    private Rect? CropDialog(BitmapSource source, Rect current)
    {
        var window = new Window { Owner = this, Title = "Кадрирование карты", Width = 900, Height = 680, MinWidth = 520, MinHeight = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = BoardSurface.Brush("#FAF7EF") };
        var root = new DockPanel { Margin = new Thickness(18) };
        var hint = new TextBlock { Text = "Обведите мышью часть карты, которую нужно оставить. Исходное изображение не изменяется.", FontSize = 12, Foreground = BoardSurface.Brush("#5F6B62"), Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var whole = new Button { Content = "Вся карта", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "Кадрировать", Padding = new Thickness(14, 7, 14, 7), Background = BoardSurface.Brush("#58725B"), Foreground = Brushes.White };
        buttons.Children.Add(whole); buttons.Children.Add(cancel); buttons.Children.Add(ok);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var holder = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var picture = new Image { Source = source, Stretch = Stretch.Uniform };
        var layer = new Canvas { Background = Brushes.Transparent, Cursor = Cursors.Cross };
        var shade = new ShapePath { Fill = BoardSurface.Brush("#9926352F"), IsHitTestVisible = false };
        var frame = new ShapeRectangle { Stroke = Brushes.White, StrokeThickness = 2, StrokeDashArray = [4, 3], IsHitTestVisible = false };
        layer.Children.Add(shade); layer.Children.Add(frame);
        holder.Children.Add(picture); holder.Children.Add(layer); root.Children.Add(holder); window.Content = root;
        var selection = current; Point? start = null;
        void Show()
        {
            var size = new Size(picture.ActualWidth, picture.ActualHeight);
            var area = new Rect(selection.X * size.Width, selection.Y * size.Height, selection.Width * size.Width, selection.Height * size.Height);
            Canvas.SetLeft(frame, area.X); Canvas.SetTop(frame, area.Y); frame.Width = area.Width; frame.Height = area.Height;
            shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(size)), new RectangleGeometry(area));
        }
        Point Normalized(MouseEventArgs e)
        {
            var p = e.GetPosition(picture);
            return new Point(Math.Clamp(p.X / Math.Max(1, picture.ActualWidth), 0, 1), Math.Clamp(p.Y / Math.Max(1, picture.ActualHeight), 0, 1));
        }
        picture.SizeChanged += (_, _) => Show();
        layer.MouseLeftButtonDown += (_, e) => { start = Normalized(e); layer.CaptureMouse(); };
        layer.MouseMove += (_, e) => { if (start is { } s) { var r = new Rect(s, Normalized(e)); if (r.Width > .005 && r.Height > .005) { selection = r; Show(); } } };
        layer.MouseLeftButtonUp += (_, _) => { start = null; layer.ReleaseMouseCapture(); };
        whole.Click += (_, _) => { selection = new Rect(0, 0, 1, 1); Show(); };
        ok.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true ? selection : null;
    }

    // Reveal steps: objects with a step appear one click after another while their frame is shown.
    internal void SetStep(int step)
    {
        CommitEditor(); FinishGesture();
        var items = Surface.SelectedItems.ToArray();
        if (items.Length == 0) return;
        if (Surface.SelectionLocked) { Status.Text = "Выделение содержит закреплённые объекты. Сначала открепите их."; return; }
        if (items.All(i => i.Step == step)) return;
        var before = lesson.Clone();
        foreach (var item in items) item.Step = Math.Clamp(step, 0, 99);
        Surface.Redraw(); Changed(before);
        Status.Text = step == 0 ? "● Объекты видны сразу" : $"● В показе объекты появятся на шаге {step} — после {step}-го нажатия «→» в своём кадре";
    }

    private MenuItem StepsMenu()
    {
        var selected = Surface.SelectedItems;
        var menu = new MenuItem { Header = "Появление в показе", IsEnabled = selected.Count > 0 && !Surface.SelectionLocked };
        var current = selected.Select(i => i.Step).Distinct().Count() == 1 ? selected[0].Step : -1;
        void Add(string title, int step)
        {
            var entry = new MenuItem { Header = title, IsCheckable = true, IsChecked = current == step };
            entry.Click += (_, _) => SetStep(step); menu.Items.Add(entry);
        }
        Add("Видно сразу", 0);
        var next = Math.Min(99, lesson.Items.Select(i => i.Step).DefaultIfEmpty(0).Max() + 1);
        menu.Items.Add(new Separator());
        for (var step = 1; step <= Math.Min(9, Math.Max(3, next)); step++) Add($"Шаг {step}", step);
        if (next > 9) Add($"Шаг {next} (новый)", next);
        return menu;
    }
}
