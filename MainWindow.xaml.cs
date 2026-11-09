using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HistoryBoard;

public partial class MainWindow : Window
{
    private Lesson lesson = new();
    private Dictionary<string, byte[]> assets = [];
    private readonly List<Lesson> undo = [], redo = [];
    private BoardTool tool = BoardTool.Text;
    private readonly Dictionary<BoardTool, Button> toolButtons = [];
    private readonly Dictionary<string, Button> colorButtons = [];
    private string color = "#26352F";
    private double fontSize = 32, strokeSize = 4, zoom = 1;
    private bool bold, serif, fitting, ready, syncing, fullscreen;
    private string? currentPath;
    private int revision, savedRevision, autosavedRevision = -1;
    private readonly DispatcherTimer autosaveTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private BoardItem? editing, draft, moving, moveOriginal;
    private Lesson? editBefore, gestureBefore;
    private Point gestureStart;
    private bool gesture, resizing;
    private string AutosavePath => Path.Combine(App.DataDirectory, "last-lesson.hboard");

    public MainWindow(bool restore = true)
    {
        InitializeComponent();
        BuildTools(); BuildPalette();
        Surface.SetLesson(lesson, assets);
        EditLayer.RenderTransform = Surface.CameraTransform;
        ready = true;
        SetTool(BoardTool.Text); UpdateUi();
        Loaded += (_, _) => { RestoreCamera(); if (restore) RestoreAutosave(); };
        autosaveTimer.Tick += async (_, _) => await AutosaveAsync();
    }

    private void BuildTools()
    {
        (BoardTool Tool, string Icon, string Label, string Shortcut)[] list =
        [ (BoardTool.Select, "↖", "Выбор", "V"), (BoardTool.Hand, "✥", "Рука — двигать доску", "H / Пробел"), (BoardTool.Text, "Т", "Текст", "T"), (BoardTool.List, "≡", "Список", "L"),
          (BoardTool.Pen, "✎", "Карандаш", "P"), (BoardTool.Arrow, "↗", "Стрелка", "A"), (BoardTool.Eraser, "⌫", "Ластик", "E") ];
        foreach (var item in list)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = item.Icon, FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center });
            var button = new Button { Content = content, ToolTip = item.Label + " · " + item.Shortcut, Height = 36, Padding = new Thickness(2), Margin = new Thickness(0, 0, 0, 4) };
            AutomationProperties.SetName(button, item.Label);
            var value = item.Tool;
            button.Click += (_, _) => SetTool(value);
            toolButtons[item.Tool] = button; Tools.Children.Add(button);
        }
    }

    private void BuildPalette()
    {
        (string Color, string Name)[] colors = [("#26352F", "Графитовый"), ("#C94F49", "Красный"), ("#427CB3", "Синий"), ("#5F895A", "Зелёный"), ("#D69A38", "Золотой"), ("#8D6DA8", "Фиолетовый")];
        foreach (var value in colors)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 15, Height = 15, Fill = BoardSurface.Brush(value.Color) };
            var button = new Button { Content = dot, Padding = new Thickness(4), Margin = new Thickness(1, 0, 1, 0), BorderThickness = new Thickness(1), ToolTip = value.Name };
            AutomationProperties.SetName(button, value.Name);
            button.Click += (_, _) => { color = value.Color; ChangeSelected(i => i.Color = color); UpdateProperties(); };
            colorButtons[value.Color] = button; Palette.Children.Add(button);
        }
    }

    private void SetTool(BoardTool value)
    {
        CommitEditor(); FinishGesture(); tool = value;
        if (value is not BoardTool.Select) Surface.Select(null);
        foreach (var pair in toolButtons)
        {
            pair.Value.Background = pair.Key == tool ? BoardSurface.Brush("#E6EDDF") : Brushes.Transparent;
            pair.Value.Foreground = pair.Key == tool ? BoardSurface.Brush("#48633F") : BoardSurface.Brush("#7B827C");
        }
        Surface.Cursor = value switch { BoardTool.Text or BoardTool.List => Cursors.IBeam, BoardTool.Select => Cursors.Arrow, BoardTool.Hand => Cursors.Hand, BoardTool.Eraser => Cursors.No, _ => Cursors.Cross };
        ToolHelp.Text = value switch
        {
            BoardTool.Text => "Клик — текст · Колёсико — шрифт · Пробел + мышь — двигать доску",
            BoardTool.List => "Enter — следующий пункт · Esc — закончить ввод",
            BoardTool.Pen => "Мышь — рисовать · Колёсико — толщина · Пробел — двигать доску",
            BoardTool.Arrow => "Протяните стрелку · Shift — выравнивание · Пробел — двигать доску",
            BoardTool.Eraser => "Ластик удаляет пометку целиком · Закреплённые объекты защищены",
            BoardTool.Hand => "Перетаскивайте поле · Колёсико — вверх/вниз · Shift — влево/вправо",
            _ => "Перетаскивайте объекты · Двойной клик — текст · Пробел — двигать доску"
        };
        UpdateProperties(); UpdateUi(); Surface.Focus();
    }

    private void UpdateProperties()
    {
        if (!ready) return;
        syncing = true;
        var stroke = tool is BoardTool.Pen or BoardTool.Arrow || Surface.Selected?.Kind is "pen" or "arrow";
        SizeLabel.Text = stroke ? "Толщина" : "Шрифт";
        SizeValue.Text = (stroke ? strokeSize : fontSize).ToString("0");
        FontChoice.IsEnabled = !stroke;
        FontChoice.SelectedIndex = serif ? 1 : 0;
        BoldButton.IsEnabled = !stroke;
        BoldButton.Background = bold ? BoardSurface.Brush("#E6EDDF") : Brushes.Transparent;
        foreach (var pair in colorButtons) pair.Value.BorderBrush = pair.Key == color ? BoardSurface.Brush(pair.Key) : Brushes.Transparent;
        syncing = false;
    }

    private void UpdateUi()
    {
        if (!ready) return;
        EmptyHint.Visibility = lesson.Items.Count == 0 && editing == null && draft == null ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.IsEnabled = undo.Count > 0; RedoButton.IsEnabled = redo.Count > 0;
        var selected = Surface.Selected;
        SelectedLabel.Text = selected == null ? "" : selected.Locked ? "Объект закреплён" : selected.Kind switch { "image" => "Карта", "text" => "Надпись", _ => "Пометка" };
        LockButton.IsEnabled = selected != null;
        LockButton.Content = selected?.Locked == true ? "Открепить" : "Закрепить";
        BackButton.Visibility = selected?.Kind == "image" && !selected.Locked ? Visibility.Visible : Visibility.Collapsed;
        RebuildFrames();
        Title = $"{lesson.Title}{(revision != savedRevision ? " •" : "")} — Доска истории";
    }

    private void Changed(Lesson before)
    {
        undo.Add(before); if (undo.Count > 60) undo.RemoveAt(0);
        redo.Clear(); revision++;
        Status.Text = "●  Изменения на доске";
        autosaveTimer.Stop(); autosaveTimer.Start(); UpdateUi();
    }

    private void ChangeSelected(Action<BoardItem> change)
    {
        var item = editing ?? Surface.Selected;
        if (item == null || item.Kind == "image" || item.Locked) return;
        var before = lesson.Clone(); change(item); Surface.Refresh(item);
        if (editing != null) StyleEditor(); else Changed(before);
    }

    private Point BoardPosition(MouseEventArgs e)
    {
        return Surface.ScreenToWorld(e.GetPosition(Surface));
    }

    private void OnBoardDown(object sender, MouseButtonEventArgs e)
    {
        if (presenting) return;
        StopCameraAnimation();
        if (tool == BoardTool.Hand) { BeginPan(e.GetPosition(Viewport)); e.Handled = true; return; }
        CommitEditor();
        var point = BoardPosition(e); var hit = Surface.Hit(point);
        if (tool is BoardTool.Text or BoardTool.List)
        {
            BeginEditor(hit?.Kind == "text" ? hit : null, point); e.Handled = true; return;
        }
        Surface.Focus();
        if (tool == BoardTool.Select)
        {
            // The resize handle extends slightly beyond the image itself.
            var selected = Surface.Selected;
            var rect = Surface.SelectedBounds;
            resizing = selected?.Kind == "image" && !rect.IsEmpty && (point - new Point(rect.Right + 7, rect.Bottom + 7)).Length < 20 / zoom;
            if (resizing) hit = selected;
            Surface.Select(hit);
            if (hit == null) { UpdateUi(); return; }
            if (hit.Locked) { CopyProperties(hit); UpdateUi(); Status.Text = "Объект закреплён. Нажмите «Открепить», чтобы изменить его."; return; }
            if (hit.Kind == "text" && e.ClickCount == 2) { BeginEditor(hit, point); return; }
            CopyProperties(hit);
            moving = hit; moveOriginal = hit.Clone();
        }
        else if (tool is BoardTool.Pen or BoardTool.Arrow)
        {
            Surface.Select(null);
            draft = new BoardItem { Kind = tool == BoardTool.Pen ? "pen" : "arrow", Color = color, Size = strokeSize, Points = [new(point.X, point.Y)] };
            Surface.Preview(draft);
        }
        gestureBefore = lesson.Clone(); gestureStart = point; gesture = true;
        if (tool == BoardTool.Eraser) EraseAt(point);
        Surface.CaptureMouse(); UpdateUi(); e.Handled = true;
    }

    private void OnBoardMove(object sender, MouseEventArgs e)
    {
        if (panning) { MovePan(e.GetPosition(Viewport)); return; }
        if (presenting) return;
        var point = BoardPosition(e);
        PositionLabel.Text = $"{point.X:0}, {point.Y:0}";
        if (!gesture) return;
        if (draft != null)
        {
            if (draft.Kind == "arrow")
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    var delta = point - gestureStart; var angle = Math.Round(Math.Atan2(delta.Y, delta.X) / (Math.PI / 4)) * Math.PI / 4;
                    point = gestureStart + new Vector(Math.Cos(angle) * delta.Length, Math.Sin(angle) * delta.Length);
                }
                if (draft.Points.Count == 1) draft.Points.Add(new(point.X, point.Y)); else draft.Points[1] = new(point.X, point.Y);
            }
            else if ((point - draft.Points[^1].ToPoint()).Length > .8 / zoom) draft.Points.Add(new(point.X, point.Y));
            Surface.Preview(draft);
        }
        else if (moving != null && moveOriginal != null)
        {
            var delta = point - gestureStart;
            if (resizing)
            {
                var ratio = moveOriginal.Height / moveOriginal.Width;
                var width = Math.Max(60, moveOriginal.Width + delta.X);
                width = Math.Min(width, 100000 / Math.Max(1, ratio));
                moving.Width = width; moving.Height = width * ratio;
            }
            else
            {
                moving.X = moveOriginal.X + delta.X;
                moving.Y = moveOriginal.Y + delta.Y;
            }
            Surface.Refresh(moving);
        }
        else if (tool == BoardTool.Eraser) EraseAt(point);
    }

    private void OnBoardUp(object sender, MouseButtonEventArgs e) { EndPan(); FinishGesture(); e.Handled = true; }
    private void OnLostCapture(object sender, MouseEventArgs e) { EndPan(); FinishGesture(); }

    private void FinishGesture()
    {
        if (!gesture) return;
        gesture = false; var changed = false;
        if (draft != null)
        {
            if (draft.Kind == "pen" || draft.Points.Count > 1 && (draft.Points[^1].ToPoint() - draft.Points[0].ToPoint()).Length > 4)
            { Surface.Add(draft); changed = true; }
            draft = null; Surface.Preview(null);
        }
        else if (moving != null && moveOriginal != null)
            changed = moving.X != moveOriginal.X || moving.Y != moveOriginal.Y || moving.Width != moveOriginal.Width;
        else if (tool == BoardTool.Eraser) changed = gestureBefore?.Items.Count != lesson.Items.Count;
        moving = null; moveOriginal = null;
        Surface.ReleaseMouseCapture();
        if (changed && gestureBefore != null) Changed(gestureBefore);
        gestureBefore = null; UpdateUi();
    }

    private void EraseAt(Point point) { var item = Surface.Hit(point, false); if (item != null && !item.Locked) Surface.Remove(item); }

    private void BeginEditor(BoardItem? item, Point point)
    {
        if (item?.Locked == true) { Surface.Select(item); UpdateUi(); Status.Text = "Эта надпись закреплена. Сначала нажмите «Открепить»."; return; }
        editBefore = lesson.Clone();
        editing = item ?? new BoardItem { Kind = "text", X = point.X, Y = point.Y, Width = Math.Clamp(Viewport.ActualWidth * .8 / zoom, 600, 1200), Size = fontSize, Color = color, Bold = bold, Serif = serif, Numbered = tool == BoardTool.List };
        if (item == null) Surface.Add(editing);
        CopyProperties(editing); Surface.Select(null); Surface.HideItem(editing, true);
        Editor.Text = editing.Text;
        Canvas.SetLeft(Editor, editing.X); Canvas.SetTop(Editor, editing.Y);
        Editor.Width = editing.Width;
        StyleEditor();
        EditLayer.IsHitTestVisible = true; Editor.Visibility = Visibility.Visible;
        UpdateUi(); Editor.Focus(); Editor.CaretIndex = Editor.Text.Length;
    }

    private void StyleEditor()
    {
        if (editing == null) return;
        Editor.FontSize = editing.Size;
        Editor.FontFamily = new FontFamily(editing.Serif ? "Georgia" : "Segoe UI");
        Editor.FontWeight = editing.Bold ? FontWeights.SemiBold : FontWeights.Normal;
        Editor.Foreground = BoardSurface.Brush(editing.Color);
        Editor.MinHeight = editing.Size * 1.5 + 4;
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (editing == null) return;
        editing.Text = Editor.Text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private void OnEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitEditor();
    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        { CommitEditor(); Surface.Focus(); e.Handled = true; }
    }

    private void CommitEditor()
    {
        if (editing == null) return;
        var item = editing; var before = editBefore!;
        editing = null; editBefore = null;
        Editor.Visibility = Visibility.Collapsed; EditLayer.IsHitTestVisible = false;
        if (string.IsNullOrWhiteSpace(item.Text)) Surface.Remove(item);
        else { Surface.HideItem(item, false); Surface.Refresh(item); Surface.Select(item); }
        var old = before.Items.FirstOrDefault(i => i.Id == item.Id);
        var exists = lesson.Items.Contains(item);
        if (exists != (old != null) || exists && old != null && (old.Text != item.Text || old.Size != item.Size || old.Color != item.Color || old.Bold != item.Bold || old.Serif != item.Serif)) Changed(before);
        UpdateUi();
    }

    private void CopyProperties(BoardItem item)
    {
        color = item.Color;
        if (item.Kind == "text") { fontSize = item.Size; bold = item.Bold; serif = item.Serif; }
        else if (item.Kind is "pen" or "arrow") strokeSize = item.Size;
        UpdateProperties();
    }

    private void AdjustSize(double step)
    {
        var stroke = tool is BoardTool.Pen or BoardTool.Arrow || Surface.Selected?.Kind is "pen" or "arrow";
        if (stroke) strokeSize = Math.Clamp(strokeSize + step, 1, 30); else fontSize = Math.Clamp(fontSize + step * 2, 12, 160);
        ChangeSelected(i => i.Size = i.Kind is "pen" or "arrow" ? strokeSize : fontSize);
        UpdateProperties();
    }
    private void OnSizeDown(object sender, RoutedEventArgs e) => AdjustSize(-1);
    private void OnSizeUp(object sender, RoutedEventArgs e) => AdjustSize(1);
    private void OnBold(object sender, RoutedEventArgs e) { bold = !bold; ChangeSelected(i => i.Bold = bold); UpdateProperties(); }
    private void OnFontChanged(object sender, SelectionChangedEventArgs e) { if (!ready || syncing) return; serif = FontChoice.SelectedIndex == 1; ChangeSelected(i => i.Serif = serif); }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (presenting) { e.Handled = true; return; }
        StopCameraAnimation();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ZoomAt(zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), e.GetPosition(Viewport));
        else if (tool is BoardTool.Text or BoardTool.List or BoardTool.Pen or BoardTool.Arrow || editing != null) AdjustSize(e.Delta > 0 ? 1 : -1);
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) SetCamera(cameraX - e.Delta / zoom, cameraY, zoom);
        else SetCamera(cameraX, cameraY - e.Delta / zoom, zoom);
        e.Handled = true;
    }

    private void Fit()
    {
        if (!ready || Viewport.ActualWidth < 100 || Viewport.ActualHeight < 100) return;
        var bounds = Surface.ContentBounds();
        if (bounds.IsEmpty) SetCamera(0, 0, 1);
        else { bounds.Inflate(48 / zoom, 48 / zoom); FocusRegion(bounds, false); }
        fitting = true;
    }
    private void SetZoom(double value, bool fit = false)
    {
        ZoomAt(value, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2)); fitting = fit;
    }
    private void OnViewportChanged(object sender, SizeChangedEventArgs e)
    {
        if (!ready) return;
        if (presenting && activeFrame >= 0 && activeFrame < lesson.Frames.Count) GoToFrame(activeFrame, false);
        else if (fitting) Fit();
    }
    private void OnFit(object sender, RoutedEventArgs e) => Fit();
    private void OnZoomIn(object sender, RoutedEventArgs e) => SetZoom(zoom * 1.2);
    private void OnZoomOut(object sender, RoutedEventArgs e) => SetZoom(zoom / 1.2);

    private void Undo()
    {
        CommitEditor(); FinishGesture(); if (undo.Count == 0) return;
        redo.Add(lesson.Clone()); lesson = undo[^1]; undo.RemoveAt(undo.Count - 1);
        Surface.SetLesson(lesson, UsedAssets(lesson)); LessonTitle.Text = lesson.Title;
        StoreCamera();
        revision++; UpdateUi(); ScheduleAutosave();
    }
    private void Redo()
    {
        CommitEditor(); FinishGesture(); if (redo.Count == 0) return;
        undo.Add(lesson.Clone()); lesson = redo[^1]; redo.RemoveAt(redo.Count - 1);
        Surface.SetLesson(lesson, UsedAssets(lesson)); LessonTitle.Text = lesson.Title;
        StoreCamera();
        revision++; UpdateUi(); ScheduleAutosave();
    }
    private void OnUndo(object sender, RoutedEventArgs e) => Undo();
    private void OnRedo(object sender, RoutedEventArgs e) => Redo();
    private void DeleteSelected()
    {
        CommitEditor(); var item = Surface.Selected; if (item == null || item.Locked) return;
        var before = lesson.Clone(); Surface.Remove(item); Changed(before);
    }
    private void OnDelete(object sender, RoutedEventArgs e) => DeleteSelected();
    private void OnSendBack(object sender, RoutedEventArgs e)
    {
        var item = Surface.Selected; if (item == null || item.Locked) return;
        var before = lesson.Clone(); lesson.Items.Remove(item); lesson.Items.Insert(0, item); Surface.Rebuild(); Surface.Select(item); Changed(before);
    }

    private async void OnAddMap(object sender, RoutedEventArgs e)
    {
        CommitEditor();
        var dialog = new OpenFileDialog { Title = "Добавить карту", Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|Все файлы|*.*" };
        if (dialog.ShowDialog(this) == true) await AddMapAsync(dialog.FileName);
    }
    internal async Task AddMapAsync(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 40 * 1024 * 1024) throw new InvalidDataException("Используйте карту размером до 40 МБ.");
            var activeLesson = lesson;
            Status.Text = "Загружаем карту…";
            var bytes = await File.ReadAllBytesAsync(path);
            var image = await Task.Run(() => LessonFile.DecodeImage(bytes));
            if (activeLesson != lesson) { Status.Text = "Урок изменился. Добавьте карту ещё раз."; return; }
            CommitEditor(); FinishGesture();
            var before = lesson.Clone(); var id = Guid.NewGuid().ToString("N"); assets[id] = bytes;
            var visible = VisibleRegion;
            var factor = Math.Min(visible.Width * .82 / image.PixelWidth, visible.Height * .82 / image.PixelHeight);
            var item = new BoardItem { Kind = "image", AssetId = id, Width = image.PixelWidth * factor, Height = image.PixelHeight * factor };
            item.X = visible.X + (visible.Width - item.Width) / 2; item.Y = visible.Y + (visible.Height - item.Height) / 2;
            Surface.AddImage(id, image); lesson.Items.Insert(0, item); Surface.Rebuild();
            SetTool(BoardTool.Select); Surface.Select(item); Changed(before);
            Status.Text = "●  Карта добавлена. Выберите карандаш или стрелку, чтобы рисовать поверх.";
        }
        catch (Exception ex) { Error("Не удалось открыть карту", ex); }
    }
    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (presenting) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (paths.Length == 0) return;
        if (Path.GetExtension(paths[0]).Equals(".hboard", StringComparison.OrdinalIgnoreCase)) await OpenLessonAsync(paths[0]);
        else await AddMapAsync(paths[0]);
    }

    private void OnTitleChanged(object sender, RoutedEventArgs e) => CommitTitle();
    private void OnTitleKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { CommitTitle(); Surface.Focus(); e.Handled = true; } }
    private void CommitTitle()
    {
        if (!ready) return;
        var value = string.IsNullOrWhiteSpace(LessonTitle.Text) ? "Новый урок" : LessonTitle.Text.Trim();
        if (value == lesson.Title) return;
        var before = lesson.Clone(); lesson.Title = value; LessonTitle.Text = value; Changed(before);
    }
    private Dictionary<string, byte[]> UsedAssets(Lesson snapshot) => snapshot.Items.Where(i => i.AssetId != null).Select(i => i.AssetId!).Distinct().ToDictionary(id => id, id => assets[id]);

    private void ScheduleAutosave() { autosaveTimer.Stop(); autosaveTimer.Start(); }
    private async Task AutosaveAsync()
    {
        autosaveTimer.Stop();
        if (editing != null || gesture || panning) { autosaveTimer.Start(); return; }
        if (autosavedRevision == revision && autosavedViewRevision == viewRevision) return;
        StoreCamera();
        var snapshot = lesson.Clone(); var data = UsedAssets(snapshot); var value = revision; var viewValue = viewRevision;
        try
        {
            await saveGate.WaitAsync();
            try { await Task.Run(() => LessonFile.Save(AutosavePath, snapshot, data)); } finally { saveGate.Release(); }
            if (revision == value && viewRevision == viewValue) { autosavedRevision = value; autosavedViewRevision = viewValue; Status.Text = "●  Автосохранено на компьютере"; }
            else ScheduleAutosave();
        }
        catch { Status.Text = "Автосохранение недоступно. Сохраните урок через Ctrl+S."; }
    }
    private void RestoreAutosave()
    {
        if (!File.Exists(AutosavePath)) return;
        try
        {
            var result = LessonFile.Load(AutosavePath);
            lesson = result.Lesson; assets = result.Assets;
            Surface.SetLesson(lesson, assets); LessonTitle.Text = lesson.Title;
            RestoreCamera(); autosavedRevision = revision; autosavedViewRevision = viewRevision; UpdateUi(); Status.Text = "●  Восстановлен последний урок";
        }
        catch { Status.Text = "Последний урок не восстановлен. Можно открыть сохранённый файл."; }
    }

    private async Task<bool> SaveLessonAsync(bool saveAs = false)
    {
        CommitEditor(); FinishGesture(); CommitTitle(); StopCameraAnimation(); StoreCamera();
        var path = currentPath;
        if (path == null || saveAs)
        {
            var dialog = new SaveFileDialog { Title = "Сохранить урок с картами и пометками", Filter = "Урок на доске (*.hboard)|*.hboard", FileName = SafeName(lesson.Title) + ".hboard", DefaultExt = ".hboard" };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }
        var snapshot = lesson.Clone(); var data = UsedAssets(snapshot); var value = revision;
        try
        {
            await saveGate.WaitAsync();
            try { await Task.Run(() => LessonFile.Save(path, snapshot, data)); } finally { saveGate.Release(); }
            currentPath = path; savedRevision = value; UpdateUi(); Status.Text = "●  Урок сохранён: " + Path.GetFileName(path); return true;
        }
        catch (Exception ex) { Error("Не удалось сохранить урок", ex); return false; }
    }
    private async void OnSave(object sender, RoutedEventArgs e) => await SaveLessonAsync();
    private async Task<bool> ConfirmReplace()
    {
        CommitEditor(); FinishGesture(); CommitTitle();
        if (revision == savedRevision) return true;
        var result = MessageBox.Show(this, "Сохранить текущий урок в файл перед продолжением?", "Доска истории", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.No || result == MessageBoxResult.Yes && await SaveLessonAsync();
    }
    private async void OnNew(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmReplace()) return;
        StopCameraAnimation(); lesson = new Lesson(); assets = []; currentPath = null; undo.Clear(); redo.Clear(); activeFrame = -1;
        revision++; savedRevision = revision; Surface.SetLesson(lesson, assets); LessonTitle.Text = lesson.Title;
        SetTool(BoardTool.Text); RestoreCamera(); ScheduleAutosave(); Status.Text = "●  Новая бесконечная доска";
    }
    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Открыть урок", Filter = "Урок на доске (*.hboard)|*.hboard" };
        if (dialog.ShowDialog(this) == true) await OpenLessonAsync(dialog.FileName);
    }
    internal async Task OpenLessonAsync(string path, bool confirm = true)
    {
        if (confirm && !await ConfirmReplace()) return;
        try
        {
            var result = await Task.Run(() => LessonFile.Load(path));
            StopCameraAnimation(); lesson = result.Lesson; assets = result.Assets; currentPath = path; undo.Clear(); redo.Clear(); activeFrame = -1;
            revision++; savedRevision = revision; Surface.SetLesson(lesson, assets); LessonTitle.Text = lesson.Title;
            UpdateUi(); RestoreCamera(); ScheduleAutosave(); Status.Text = "●  Урок открыт: " + Path.GetFileName(path);
        }
        catch (Exception ex) { Error("Не удалось открыть урок", ex); }
    }
    private void OnExport(object sender, RoutedEventArgs e)
    {
        CommitEditor(); FinishGesture();
        var menu = new ContextMenu();
        var visible = new MenuItem { Header = "Текущий вид" }; visible.Click += (_, _) => ExportRegion(VisibleRegion); menu.Items.Add(visible);
        var all = new MenuItem { Header = "Все объекты доски" }; all.Click += (_, _) => ExportRegion(null); menu.Items.Add(all);
        var frame = new MenuItem { Header = "Выбранный кадр", IsEnabled = activeFrame >= 0 && activeFrame < lesson.Frames.Count };
        frame.Click += (_, _) => { if (activeFrame >= 0 && activeFrame < lesson.Frames.Count) ExportRegion(lesson.Frames[activeFrame].Bounds); }; menu.Items.Add(frame);
        menu.PlacementTarget = (UIElement)sender; menu.IsOpen = true;
    }
    private void ExportRegion(Rect? region)
    {
        var dialog = new SaveFileDialog { Title = "Сохранить изображение доски", Filter = "Изображение PNG|*.png", FileName = SafeName(lesson.Title) + ".png" };
        if (dialog.ShowDialog(this) != true) return;
        try { Surface.ExportPng(dialog.FileName, region); Status.Text = "●  Изображение доски сохранено"; } catch (Exception ex) { Error("Не удалось сохранить PNG", ex); }
    }

    private static string SafeName(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private void Error(string title, Exception ex) { Status.Text = title; MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    private void OnHelp(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "БЕСКОНЕЧНАЯ ДОСКА\nПишите и размещайте карты в любом месте. Пробел + левая кнопка мыши, средняя кнопка или инструмент «Рука» (H) перемещают поле. Колёсико в режиме выбора двигает вид вверх/вниз; Shift + колёсико — влево/вправо. Ctrl + колёсико масштабирует вокруг курсора. Ctrl+0 показывает все объекты.\n\nТЕКСТ И РИСОВАНИЕ\nКликните инструментом «Текст» или «Список» и пишите. Колёсико меняет размер шрифта, Enter — новая строка, Esc — закончить ввод. Карты можно перетащить в окно. Карандаш и стрелка рисуют поверх карты; Shift выравнивает стрелку.\n\nЗАКРЕПЛЕНИЕ\nИнструмент «Выбор» (V) перемещает объекты. Двойной клик редактирует надпись, уголок карты меняет её размер. Кнопка «Закрепить» (Ctrl+L) защищает объект от случайного перемещения, редактирования и удаления.\n\nКАДРЫ И ПОКАЗ\nНайдите нужный вид и нажмите «＋ Кадр» (Ctrl+Enter). Повторите для других участков. Клик по кадру возвращает его вид; двойной клик меняет название. Правая кнопка — порядок, обновление вида и удаление кадра. F5 запускает показ, стрелки или Page Up/Down переключают кадры, Esc завершает показ.\n\nСОХРАНЕНИЕ\nCtrl+S сохраняет урок с картами и кадрами в .hboard, Ctrl+Shift+S — копию. Последний урок и положение камеры восстанавливаются автоматически. Меню PNG экспортирует текущий вид, выбранный кадр или все объекты. F11 — весь экран. Ctrl+Z / Ctrl+Y — отменить / повторить.",
        "Как пользоваться доской", MessageBoxButton.OK, MessageBoxImage.Information);

    private WindowState previousState;
    private void ToggleFullscreen()
    {
        if (!fullscreen) { previousState = WindowState; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized; }
        else { WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize; WindowState = previousState; }
        fullscreen = !fullscreen;
    }
    private void OnFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (presenting)
        {
            e.Handled = true;
            if (e.Key is Key.Escape or Key.F5) EndPresentation();
            else if (e.Key is Key.Right or Key.PageDown or Key.Space) NextFrame(1);
            else if (e.Key is Key.Left or Key.PageUp or Key.Back) NextFrame(-1);
            else if (e.Key == Key.Home) GoToFrame(0);
            else if (e.Key == Key.End) GoToFrame(lesson.Frames.Count - 1);
            return;
        }
        if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; return; }
        if (e.Key == Key.F5 && Keyboard.FocusedElement is not TextBox) { StartPresentation(); e.Handled = true; return; }
        if (e.Key == Key.Escape && editing == null) { if (fullscreen) ToggleFullscreen(); else { CommitEditor(); Surface.Select(null); UpdateUi(); } return; }
        if (ctrl && e.Key == Key.S) { e.Handled = true; await SaveLessonAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); return; }
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Space) { spaceHeld = true; Surface.Cursor = Cursors.Hand; e.Handled = true; return; }
        if (!ctrl && e.Key is Key.PageDown or Key.PageUp) { NextFrame(e.Key == Key.PageDown ? 1 : -1); e.Handled = true; return; }
        if (ctrl)
        {
            e.Handled = true;
            switch (e.Key)
            {
                case Key.Z: Undo(); break; case Key.Y: Redo(); break; case Key.O: OnOpen(this, e); break;
                case Key.N: OnNew(this, e); break; case Key.M: OnAddMap(this, e); break; case Key.D0: Fit(); break;
                case Key.B: OnBold(this, e); break; default: e.Handled = false; break;
                case Key.L: ToggleLock(); break; case Key.Enter: AddFrame(); break;
            }
            return;
        }
        var selectedTool = e.Key switch { Key.V => BoardTool.Select, Key.H => BoardTool.Hand, Key.T => BoardTool.Text, Key.L => BoardTool.List, Key.P => BoardTool.Pen, Key.A => BoardTool.Arrow, Key.E => BoardTool.Eraser, _ => (BoardTool?)null };
        if (selectedTool != null) { SetTool(selectedTool.Value); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
    }

    private bool closing;
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true; EndPan(); CommitEditor(); FinishGesture(); CommitTitle(); StopCameraAnimation(); autosaveTimer.Stop();
        await AutosaveAsync();
        closing = true; Close();
    }
}
