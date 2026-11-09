using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HistoryBoard;

public partial class MainWindow
{
    private double cameraX, cameraY;
    private int viewRevision, autosavedViewRevision = -1;
    private bool panning, spaceHeld, presenting, presentationWasFullscreen;
    private Point panStart;
    private double panStartX, panStartY;
    private int activeFrame = -1, lastFrame = -1;
    private readonly Dictionary<string, Image> frameImages = [];
    private readonly Dictionary<string, (string Key, BitmapSource Image)> frameThumbnails = [];
    private DispatcherTimer? thumbnailTimer;

    // Thumbnails are rendered shortly after editing pauses, so typing and drawing stay smooth.
    private void ScheduleFrameThumbnails()
    {
        if (thumbnailTimer == null)
        {
            thumbnailTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
            thumbnailTimer.Tick += (_, _) => { if (editing != null || gesture || panning) return; thumbnailTimer.Stop(); RefreshFrameThumbnails(); };
        }
        thumbnailTimer.Stop(); thumbnailTimer.Start();
    }

    internal void RefreshFrameThumbnails()
    {
        foreach (var frame in lesson.Frames.Take(80))
        {
            var key = $"{revision}|{frame.X}|{frame.Y}|{frame.Width}|{frame.Height}|{lesson.Paper}";
            if (!frameThumbnails.TryGetValue(frame.Id, out var cached) || cached.Key != key)
            {
                try { cached = (key, Surface.RenderBitmap(frame.Bounds, 128, true)); frameThumbnails[frame.Id] = cached; }
                catch { continue; }
            }
            if (frameImages.TryGetValue(frame.Id, out var image)) image.Source = cached.Image;
        }
    }

    private void OnUpdateFrame(object sender, RoutedEventArgs e)
    {
        if (lastFrame < 0 || lastFrame >= lesson.Frames.Count) return;
        CommitEditor(); FinishGesture(); RecaptureFrame(lesson.Frames[lastFrame]); Surface.Focus();
    }
    private readonly Dictionary<string, Button> frameButtons = [];
    private readonly DispatcherTimer cameraTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private long cameraAnimationStart;
    private BoardView animationFrom = new(), animationTo = new();
    private Rect VisibleRegion => new(cameraX, cameraY, Math.Max(1, Viewport.ActualWidth) / zoom, Math.Max(1, Viewport.ActualHeight) / zoom);

    private void StoreCamera() => lesson.View = new BoardView { X = cameraX, Y = cameraY, Zoom = zoom };

    private void SetCamera(double x, double y, double scale, bool track = true)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(scale)) return;
        var nextX = Math.Clamp(x, -999_000_000, 999_000_000);
        var nextY = Math.Clamp(y, -999_000_000, 999_000_000);
        var nextZoom = Math.Clamp(scale, .02, 8);
        var changed = cameraX != nextX || cameraY != nextY || zoom != nextZoom;
        cameraX = nextX; cameraY = nextY; zoom = nextZoom; fitting = false;
        Surface.SetCamera(cameraX, cameraY, zoom);
        ZoomValue.Text = $"{zoom * 100:0}%";
        // Far from the normal scale the percentage turns into a visible "back to 100%" button.
        ZoomResetButton.Background = Math.Abs(zoom - 1) > .05 ? BoardSurface.Brush("#E6EDDF") : Brushes.Transparent;
        StoreCamera();
        if (changed) viewRevision++;
        if (track && changed)
        {
            activeFrame = -1; UpdateFrameSelection(); ScheduleAutosave();
        }
    }

    private void RestoreCamera()
    {
        StopCameraAnimation();
        var view = lesson.View;
        if (view != null) SetCamera(view.X, view.Y, view.Zoom);
        else Fit();
    }

    private void ZoomAt(double value, Point screenAnchor)
    {
        StopCameraAnimation();
        var worldAnchor = Surface.ScreenToWorld(screenAnchor);
        var scale = Math.Clamp(value, .02, 8);
        SetCamera(worldAnchor.X - screenAnchor.X / scale, worldAnchor.Y - screenAnchor.Y / scale, scale);
    }

    private void FocusRegion(Rect region, bool animate)
    {
        if (Viewport.ActualWidth < 1 || Viewport.ActualHeight < 1 || region.IsEmpty) return;
        var targetZoom = Math.Clamp(Math.Min(Viewport.ActualWidth / region.Width, Viewport.ActualHeight / region.Height), .02, 8);
        var target = new BoardView { X = region.X + region.Width / 2 - Viewport.ActualWidth / (2 * targetZoom), Y = region.Y + region.Height / 2 - Viewport.ActualHeight / (2 * targetZoom), Zoom = targetZoom };
        StopCameraAnimation();
        if (!animate) { SetCamera(target.X, target.Y, target.Zoom, false); ScheduleAutosave(); return; }
        animationFrom = new BoardView { X = cameraX, Y = cameraY, Zoom = zoom }; animationTo = target;
        cameraAnimationStart = Stopwatch.GetTimestamp();
        cameraTimer.Tick -= OnCameraAnimationTick; cameraTimer.Tick += OnCameraAnimationTick;
        cameraTimer.Start();
    }

    private void OnCameraAnimationTick(object? sender, EventArgs e)
    {
        var t = Math.Clamp(Stopwatch.GetElapsedTime(cameraAnimationStart).TotalMilliseconds / 280, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        var scale = Math.Exp(Math.Log(animationFrom.Zoom) + (Math.Log(animationTo.Zoom) - Math.Log(animationFrom.Zoom)) * eased);
        // Interpolate camera centres, so differing frame sizes do not produce a sideways zoom.
        var halfWidth = Viewport.ActualWidth / 2; var halfHeight = Viewport.ActualHeight / 2;
        var cx = Lerp(animationFrom.X + halfWidth / animationFrom.Zoom, animationTo.X + halfWidth / animationTo.Zoom, eased);
        var cy = Lerp(animationFrom.Y + halfHeight / animationFrom.Zoom, animationTo.Y + halfHeight / animationTo.Zoom, eased);
        SetCamera(cx - halfWidth / scale, cy - halfHeight / scale, scale, false);
        if (t >= 1) { cameraTimer.Stop(); ScheduleAutosave(); }
    }
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private void StopCameraAnimation() { if (!cameraTimer.IsEnabled) return; cameraTimer.Stop(); ScheduleAutosave(); }

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (presenting) { BeginCurtainDrag(e); e.Handled = true; return; }
        if (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Left && spaceHeld)
        { BeginPan(e.GetPosition(Viewport)); e.Handled = true; }
    }
    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (curtainDragging) { EndCurtainDrag(); e.Handled = true; return; }
        if (panning && e.ChangedButton is MouseButton.Left or MouseButton.Middle) { EndPan(); e.Handled = true; }
    }

    private void BeginPan(Point screen)
    {
        CommitEditor(); FinishGesture(); StopCameraAnimation();
        panning = true; panStart = screen; panStartX = cameraX; panStartY = cameraY;
        Surface.Focus(); Surface.Cursor = Cursors.ScrollAll; Surface.CaptureMouse();
    }
    private void MovePan(Point screen)
    {
        if (!panning) return;
        var delta = screen - panStart;
        SetCamera(panStartX - delta.X / zoom, panStartY - delta.Y / zoom, zoom, false);
        activeFrame = -1; UpdateFrameSelection();
    }
    private void EndPan()
    {
        if (!panning) return;
        panning = false; Surface.ReleaseMouseCapture();
        RestoreToolCursor(); ScheduleAutosave();
    }
    private void RestoreToolCursor() => Surface.Cursor = spaceHeld || tool == BoardTool.Hand ? Cursors.Hand : tool is BoardTool.Text or BoardTool.List ? Cursors.IBeam : tool == BoardTool.Select ? Cursors.Arrow : tool == BoardTool.Eraser ? Cursors.No : Cursors.Cross;
    private void OnKeyUp(object sender, KeyEventArgs e) { if (e.Key == Key.Space) { spaceHeld = false; if (!panning) RestoreToolCursor(); } }
    private void OnDeactivated(object? sender, EventArgs e) { spaceHeld = false; touches.Clear(); touchNavigating = false; EndPan(); FinishGesture(); RestoreToolCursor(); }

    private void ToggleLock()
    {
        CommitEditor(); FinishGesture(); var items = Surface.SelectedItems.ToArray(); if (items.Length == 0) return;
        var locked = !Surface.SelectionLocked;
        var before = lesson.Clone(); foreach (var item in items) item.Locked = locked; Surface.RefreshMany(items); Changed(before);
        Status.Text = locked ? "● Выделение закреплено на доске" : "● Выделение можно перемещать и редактировать";
    }
    private void OnLock(object sender, RoutedEventArgs e) => ToggleLock();

    // Returns to the normal 1:1 scale around the centre of the current view.
    internal void ResetZoom(bool animate = true)
    {
        if (Viewport.ActualWidth < 1) return;
        var view = VisibleRegion; var center = new Point(view.X + view.Width / 2, view.Y + view.Height / 2);
        FocusRegion(new Rect(center.X - Viewport.ActualWidth / 2, center.Y - Viewport.ActualHeight / 2, Viewport.ActualWidth, Viewport.ActualHeight), animate);
        Status.Text = "● Обычный масштаб 100%";
    }
    private void OnZoomReset(object sender, RoutedEventArgs e) { ResetZoom(); Surface.Focus(); }

    private void AddFrame(string? name = null)
    {
        CommitEditor(); FinishGesture(); StopCameraAnimation();
        if (lesson.Frames.Count >= 500) { Status.Text = "В одном уроке можно сохранить до 500 кадров."; return; }
        var before = lesson.Clone(); var region = VisibleRegion;
        var frame = new LessonFrame { Name = name ?? $"Кадр {lesson.Frames.Count + 1}", X = region.X, Y = region.Y, Width = region.Width, Height = region.Height };
        lesson.Frames.Add(frame); activeFrame = lastFrame = lesson.Frames.Count - 1; Changed(before);
        Status.Text = $"● Кадр {lesson.Frames.Count} сохранён — рамка на доске показывает, что увидят ученики. Переместитесь к следующему материалу и снова нажмите «＋ Кадр».";
        Surface.Focus();
    }
    private void OnAddFrame(object sender, RoutedEventArgs e) => AddFrame();

    private void RebuildFrames()
    {
        if (activeFrame >= lesson.Frames.Count) activeFrame = lesson.Frames.Count - 1;
        FrameButtons.Children.Clear(); frameButtons.Clear(); frameImages.Clear();
        FrameHint.Visibility = lesson.Frames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < lesson.Frames.Count; i++)
        {
            var frame = lesson.Frames[i]; var index = i;
            var thumbnail = new Image { Width = 64, Height = 38, Stretch = Stretch.Uniform, Source = frameThumbnails.TryGetValue(frame.Id, out var cached) ? cached.Image : null };
            var face = new StackPanel { Orientation = Orientation.Horizontal };
            face.Children.Add(new Border { Child = thumbnail, BorderBrush = BoardSurface.Brush("#DCE3D4"), BorderThickness = new Thickness(1), Background = BoardSurface.Brush("#FAF7EF"), Margin = new Thickness(0, 0, 7, 0) });
            face.Children.Add(new TextBlock { Text = $"{i + 1}  {frame.Name}", VerticalAlignment = VerticalAlignment.Center, MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis });
            frameImages[frame.Id] = thumbnail;
            var button = new Button { Content = face, FontSize = 11, Padding = new Thickness(4, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Перейти к кадру. Двойной клик — название. Правая кнопка — порядок, обновление, удаление." };
            button.MouseEnter += (_, _) => { Surface.HighlightedFrame = index; Surface.Redraw(); };
            button.MouseLeave += (_, _) => { Surface.HighlightedFrame = -1; Surface.Redraw(); };
            button.Click += (_, _) => GoToFrame(index);
            button.MouseDoubleClick += (_, e) => { RenameFrame(frame); e.Handled = true; };
            var menu = new ContextMenu();
            void Menu(string label, Action action, bool enabled = true) { var item = new MenuItem { Header = label, IsEnabled = enabled }; item.Click += (_, _) => action(); menu.Items.Add(item); }
            Menu("Переименовать", () => RenameFrame(frame));
            Menu("Запомнить текущий вид в этот кадр", () => RecaptureFrame(frame));
            Menu("Передвинуть влево", () => MoveFrame(frame, -1), i > 0);
            Menu("Передвинуть вправо", () => MoveFrame(frame, 1), i < lesson.Frames.Count - 1);
            menu.Items.Add(new Separator()); Menu("Удалить кадр (объекты останутся)", () => RemoveFrame(frame));
            button.ContextMenu = menu; frameButtons[frame.Id] = button; FrameButtons.Children.Add(button);
        }
        UpdateFrameSelection(); ScheduleFrameThumbnails();
    }

    private void UpdateFrameSelection()
    {
        FrameCounter.Text = lesson.Frames.Count == 0 ? "0 кадров" : $"{(activeFrame >= 0 ? (activeFrame + 1).ToString() : "—")} / {lesson.Frames.Count}";
        for (var i = 0; i < lesson.Frames.Count; i++)
            if (frameButtons.TryGetValue(lesson.Frames[i].Id, out var button)) button.Background = i == activeFrame ? BoardSurface.Brush("#DDE8D3") : BoardSurface.Brush("#F0F3EB");
        if (activeFrame >= 0 && activeFrame < lesson.Frames.Count) PresentationLabel.Text = $"{activeFrame + 1} / {lesson.Frames.Count}   {lesson.Frames[activeFrame].Name}";
        if (lastFrame >= lesson.Frames.Count) lastFrame = -1;
        // After moving away from a frame, offer to store the new view in it.
        UpdateFrameButton.Visibility = !presenting && activeFrame < 0 && lastFrame >= 0 ? Visibility.Visible : Visibility.Collapsed;
        if (lastFrame >= 0) UpdateFrameButton.Content = $"↻ Обновить кадр {lastFrame + 1}";
        UpdateFrameButton.ToolTip = lastFrame >= 0 ? $"Запомнить текущий вид в кадр «{lesson.Frames[lastFrame].Name}»" : null;
        TrayPresentButton.IsEnabled = lesson.Frames.Count > 0;
        if (Surface.ActiveFrame != activeFrame) { Surface.ActiveFrame = activeFrame; Surface.Redraw(); }
    }

    private void GoToFrame(int index, bool animate = true)
    {
        if (index < 0 || index >= lesson.Frames.Count) return;
        CommitEditor(); FinishGesture(); EndPan();
        activeFrame = lastFrame = index; Surface.Select(null); FocusRegion(lesson.Frames[index].Bounds, animate);
        UpdateFrameSelection();
        if (presenting) ApplyReveal();
        if (frameButtons.TryGetValue(lesson.Frames[index].Id, out var button)) button.BringIntoView();
        Surface.Focus();
    }
    private void NextFrame(int direction)
    {
        if (lesson.Frames.Count == 0) return;
        GoToFrame(activeFrame < 0 ? direction > 0 ? 0 : lesson.Frames.Count - 1 : Math.Clamp(activeFrame + direction, 0, lesson.Frames.Count - 1));
    }
    private void OnPreviousFrame(object sender, RoutedEventArgs e) => PresentStep(-1);
    private void OnNextFrame(object sender, RoutedEventArgs e) => PresentStep(1);

    private void RenameFrame(LessonFrame frame)
    {
        StopCameraAnimation();
        var prompt = new Window { Owner = this, Title = "Название кадра", Width = 380, Height = 175, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = BoardSurface.Brush("#FAFBF7") };
        var panel = new StackPanel { Margin = new Thickness(20) };
        var input = new TextBox { Text = frame.Name, MaxLength = 100, Padding = new Thickness(7), Margin = new Thickness(0, 0, 0, 14) };
        var ok = new Button { Content = "Сохранить название", IsDefault = true, Background = BoardSurface.Brush("#58725B"), Foreground = Brushes.White, Padding = new Thickness(12, 7, 12, 7), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => prompt.DialogResult = true;
        panel.Children.Add(input); panel.Children.Add(ok); prompt.Content = panel;
        prompt.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(input.Text)) { var before = lesson.Clone(); frame.Name = input.Text.Trim(); Changed(before); }
    }
    private void RecaptureFrame(LessonFrame frame)
    {
        StopCameraAnimation(); var before = lesson.Clone(); var view = VisibleRegion;
        frame.X = view.X; frame.Y = view.Y; frame.Width = view.Width; frame.Height = view.Height;
        activeFrame = lastFrame = lesson.Frames.IndexOf(frame); Changed(before);
        Status.Text = $"● Кадр {activeFrame + 1} теперь показывает текущий вид";
    }
    private void MoveFrame(LessonFrame frame, int delta)
    {
        var index = lesson.Frames.IndexOf(frame); var target = index + delta;
        if (index < 0 || target < 0 || target >= lesson.Frames.Count) return;
        var before = lesson.Clone(); lesson.Frames.RemoveAt(index); lesson.Frames.Insert(target, frame); activeFrame = lastFrame = target; Changed(before);
    }
    private void RemoveFrame(LessonFrame frame)
    {
        if (!lesson.Frames.Contains(frame)) return;
        var before = lesson.Clone(); lesson.Frames.Remove(frame); activeFrame = lastFrame = -1; frameThumbnails.Remove(frame.Id); Changed(before);
    }

    private void StartPresentation()
    {
        if (presenting) return;
        CommitEditor(); FinishGesture(); EndPan(); StopCameraAnimation();
        if (lesson.Frames.Count == 0) AddFrame();
        presenting = true; presentationWasFullscreen = fullscreen; revealStep = 0;
        Surface.Select(null); Surface.ShowStepBadges = false; Surface.ShowFrames = false; Surface.Redraw();
        Overlay.Visibility = Visibility.Visible; Overlay.HasPointer = false; SetPresentationTool(PresentationTool.None);
        MainHeader.Visibility = PropertyBar.Visibility = ToolRail.Visibility = FramesTray.Visibility = StatusBar.Visibility = ZoomControls.Visibility = Visibility.Collapsed;
        ToolColumn.Width = new GridLength(0); PresentationControls.Visibility = Visibility.Visible;
        if (!fullscreen) ToggleFullscreen();
        UpdateLayout(); GoToFrame(activeFrame >= 0 ? activeFrame : 0, false);
    }
    private void EndPresentation()
    {
        if (!presenting) return;
        StopCameraAnimation(); SetPresentationTool(PresentationTool.None); EndCurtainDrag(); presenting = false;
        Overlay.Visibility = Visibility.Collapsed; ShowAllSteps(); Surface.ShowStepBadges = true; Surface.ShowFrames = true; Surface.Redraw(); RestoreToolCursor();
        MainHeader.Visibility = PropertyBar.Visibility = ToolRail.Visibility = FramesTray.Visibility = StatusBar.Visibility = ZoomControls.Visibility = Visibility.Visible;
        ToolColumn.Width = new GridLength(48); PresentationControls.Visibility = Visibility.Collapsed;
        if (fullscreen != presentationWasFullscreen) ToggleFullscreen();
        UpdateLayout(); GoToFrame(activeFrame >= 0 ? activeFrame : 0, false); UpdateUi();
    }
    private void OnPresent(object sender, RoutedEventArgs e) => StartPresentation();
    private void OnEndPresentation(object sender, RoutedEventArgs e) => EndPresentation();
}
