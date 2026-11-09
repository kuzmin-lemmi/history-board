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
        shortcuts = ShortcutProfile.Load(SettingsPath);
        BuildTools(); BuildPalette();
        ApplyShortcutHints();
        Surface.SetLesson(lesson, assets);
        EditLayer.RenderTransform = Surface.CameraTransform;
        ready = true;
        SetTool(BoardTool.Text); UpdateUi();
        Loaded += (_, _) => { RestoreCamera(); if (restore) { RestoreAutosave(); Dispatcher.BeginInvoke(new Action(() => OnLibrary(this, new RoutedEventArgs()))); } };
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
        UpdateSelectionUi();
        RebuildFrames();
        Title = $"{lesson.Title}{(revision != savedRevision ? " •" : "")} — Доска истории";
    }

    private void UpdateSelectionUi()
    {
        var selected = Surface.Selected;
        SelectedLabel.Text = Surface.SelectedItems.Count > 1 ? $"Выбрано: {Surface.SelectedItems.Count}" : selected == null ? "" : selected.Locked ? "Объект закреплён" : selected.Kind switch { "image" => "Карта", "text" => "Надпись", _ => "Пометка" };
        LockButton.IsEnabled = selected != null;
        LockButton.Content = Surface.SelectionLocked ? "Открепить" : "Закрепить";
        BackButton.Visibility = Surface.SelectedItems.Count == 1 && selected?.Kind == "image" && !Surface.SelectionLocked ? Visibility.Visible : Visibility.Collapsed;
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
        var items = editing != null ? new[] { editing } : Surface.SelectedItems.ToArray();
        if (items.Length == 0 || items.Any(i => i.Locked)) return;
        var targets = items.Where(i => i.Kind != "image").ToArray();
        if (targets.Length == 0) return;
        var before = lesson.Clone(); foreach (var item in targets) change(item); Surface.RefreshMany(targets);
        if (editing != null) StyleEditor(); else Changed(before);
    }

    private Point BoardPosition(MouseEventArgs e)
    {
        return Surface.ScreenToWorld(e.GetPosition(Surface));
    }

    private void OnBoardDown(object sender, MouseButtonEventArgs e)
    {
        if (presenting || touchNavigating) return;
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
            resizing = Surface.SelectedItems.Count == 1 && selected?.Kind == "image" && !rect.IsEmpty && (point - new Point(rect.Right + 7 / zoom, rect.Bottom + 7 / zoom)).Length < 14 / zoom;
            if (resizing) hit = selected;
            if (hit == null)
            {
                marqueeStartSelection = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Surface.SelectedItems.ToArray() : [];
                Surface.SelectMany(marqueeStartSelection); selectingArea = true;
                gestureStart = point; gesture = true; Surface.CaptureMouse(); UpdateUi(); e.Handled = true; return;
            }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && !resizing) { Surface.ToggleSelection(hit); UpdateUi(); e.Handled = true; return; }
            if (!Surface.SelectedItems.Contains(hit)) Surface.Select(hit);
            if (Surface.SelectionLocked) { CopyProperties(hit); UpdateUi(); Status.Text = "Выделение содержит закреплённые объекты. Сначала открепите их."; return; }
            if (hit.Kind == "text" && e.ClickCount == 2) { BeginEditor(hit, point); return; }
            CopyProperties(hit);
            moving = hit; moveOriginal = hit.Clone();
            moveOriginals = Surface.SelectedItems.ToDictionary(i => i.Id, i => i.Clone());
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
        if (selectingArea)
        {
            var area = new Rect(gestureStart, point);
            SelectArea(area); return;
        }
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
                MoveSelection(delta);
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
        if (selectingArea)
        {
            selectingArea = false; Surface.ShowMarquee(Rect.Empty); marqueeStartSelection = [];
            Surface.ReleaseMouseCapture(); UpdateUi(); return;
        }
        if (draft != null)
        {
            if (draft.Kind == "pen" || draft.Points.Count > 1 && (draft.Points[^1].ToPoint() - draft.Points[0].ToPoint()).Length > 4)
            { Surface.Add(draft); changed = true; }
            draft = null; Surface.Preview(null);
        }
        else if (moving != null && moveOriginal != null)
            changed = moving.X != moveOriginal.X || moving.Y != moveOriginal.Y || moving.Width != moveOriginal.Width;
        else if (tool == BoardTool.Eraser) changed = gestureBefore?.Items.Count != lesson.Items.Count;
        moving = null; moveOriginal = null; moveOriginals.Clear();
        Surface.ReleaseMouseCapture();
        if (changed && gestureBefore != null) Changed(gestureBefore);
        gestureBefore = null; UpdateUi();
    }

    private void EraseAt(Point point)
    {
        var item = Surface.Hit(point, false); if (item == null) return;
        var group = Surface.GroupMembers(item).ToArray();
        if (group.Any(i => i.Locked || i.Kind == "image")) return;
        Surface.RemoveMany(group);
    }

    private void BeginEditor(BoardItem? item, Point point)
    {
        if (item != null && Surface.GroupMembers(item).Any(i => i.Locked)) { Surface.Select(item); UpdateUi(); Status.Text = "Эта надпись закреплена. Сначала нажмите «Открепить»."; return; }
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
        CommitEditor(); FinishGesture(); var items = Surface.SelectedItems.ToArray(); if (items.Length == 0 || Surface.SelectionLocked) return;
        var before = lesson.Clone(); Surface.RemoveMany(items); Changed(before);
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
            if (activeLesson != lesson) { Status.Text = "Урок изменился. Добавьте карту ещё раз."; return; }
            await AddImageBytesAsync(bytes);
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
    private async Task<bool> AutosaveAsync()
    {
        autosaveTimer.Stop();
        if (editing != null || gesture || panning) { autosaveTimer.Start(); return false; }
        if (autosavedRevision == revision && autosavedViewRevision == viewRevision) return true;
        StoreCamera();
        var snapshot = lesson.Clone(); var data = UsedAssets(snapshot); var value = revision; var viewValue = viewRevision;
        var source = currentPath; var dirty = HasUnsavedChanges;
        try
        {
            await saveGate.WaitAsync();
            try
            {
                await Task.Run(() => { LessonFile.Save(AutosavePath, snapshot, data, true); KeepAutosaveHistory(); });
                // Saving to the lesson file may have finished while the autosave was running.
                WriteSessionInfo(source, dirty && !(currentPath == source && savedRevision >= value));
            }
            finally { saveGate.Release(); }
            if (revision == value && viewRevision == viewValue) { autosavedRevision = value; autosavedViewRevision = viewValue; Status.Text = "●  Автосохранено на компьютере"; }
            else ScheduleAutosave();
            return true;
        }
        catch (Exception ex) { Status.Text = "Автосохранение недоступно: " + ex.Message + " Сохраните урок через Ctrl+S."; return false; }
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
        var activeLesson = lesson; var snapshot = lesson.Clone(); var data = UsedAssets(snapshot); var value = revision;
        var preview = CaptureLessonPreview();
        try
        {
            await saveGate.WaitAsync();
            try { await Task.Run(() => LessonFile.Save(path, snapshot, data, true)); } finally { saveGate.Release(); }
            if (ReferenceEquals(activeLesson, lesson))
            {
                currentPath = path; savedRevision = value;
                // Keep the autosave linked to this file, so the next start knows it is saved.
                if (autosavedRevision == revision && autosavedViewRevision == viewRevision) WriteSessionInfo(currentPath, HasUnsavedChanges); else ScheduleAutosave();
            }
            UpdateUi(); Status.Text = "●  Урок сохранён: " + Path.GetFileName(path); RememberLesson(path, snapshot, preview); return true;
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
            UpdateUi(); RestoreCamera(); ScheduleAutosave(); Status.Text = "●  Урок открыт: " + Path.GetFileName(path); RememberLesson(path, lesson, CaptureLessonPreview());
        }
        catch (Exception ex) { if (!await OpenBackupAfterFailureAsync(path, ex)) Error("Не удалось открыть урок", ex); }
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
    private void OnHelp(object sender, RoutedEventArgs e) => ShowShortcutHelp();

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
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var textInput = Keyboard.FocusedElement is TextBox;
        if (key == Key.Escape)
        {
            if (presenting) { EndPresentation(); e.Handled = true; }
            else if (!textInput) { if (fullscreen) ToggleFullscreen(); else { Surface.Select(null); UpdateUi(); } e.Handled = true; }
            return;
        }
        if (!presenting && !textInput && key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        { spaceHeld = true; Surface.Cursor = Cursors.Hand; e.Handled = true; return; }
        var wasPresenting = presenting;
        var matches = shortcuts.Resolve(key, Keyboard.Modifiers, presenting ? ShortcutContext.Presentation : ShortcutContext.Editor, textInput) != null;
        e.Handled = matches || wasPresenting;
        if (matches) await HandleShortcutAsync(key, Keyboard.Modifiers, textInput);
    }

    private bool closing;
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true; EndPan(); CommitEditor(); FinishGesture(); CommitTitle(); StopCameraAnimation(); autosaveTimer.Stop();
        if (!await ConfirmCloseAsync()) return;
        if (!await AutosaveAsync() && HasUnsavedChanges &&
            MessageBox.Show(this, "Автосохранение не удалось, несохранённые изменения будут потеряны. Всё равно закрыть доску?", "Доска истории", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        closing = true; Close();
    }
}
